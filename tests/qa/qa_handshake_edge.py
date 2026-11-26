"""tests/qa/qa_handshake_edge.py —— 握手边角（QA 独立复核）。

覆盖工程师 test_handshake.py 之外/更细的判定点：
  1. 同实例第二个**连接**（不是同连接第二个 hello）→ E1003 session_busy + bye + 断开，
     且**第一个连接不受影响**（这是「单会话不误伤已有会话」的关键）；
  2. bye 之后再复用旧 sessionToken → 必须 E4001（令牌随连接生命周期失效）；
  3. protocolVersion 兼容矩阵：1.9 允许、0.9 拒绝、1 / 1.0.0 / "" / "abc" 拒绝（E4001）；
     并核对设计 §2.3 承诺的 W1002 client_ahead 告警是否真的出现在线上；
  4. hello 缺必填字段：缺 protocolVersion / 缺 clientInfo / 缺 clientNonce（契约见
     protocol/handshake.schema.json#/$defs/Hello 的 required）；
  5. requestedFeatures 里有未知能力 → 进 missingFeatures，且握手仍成功。
"""

from __future__ import annotations

import json
import sys

import qa_common as qa


def hello_frame(version="1.0", client_info=None, nonce="0123456789abcdef",
                features=None, include_version=True, include_nonce=True):
    """按契约手搓 hello 帧，便于逐个删字段。"""
    frame = {"kind": "hello"}
    if include_version:
        frame["protocolVersion"] = version
    if client_info is not None:
        frame["clientInfo"] = client_info
    if include_nonce:
        frame["clientNonce"] = nonce
    if features is not None:
        frame["requestedFeatures"] = features
    return frame


def run() -> dict:
    suite = qa.harness.Suite("qa_handshake_edge")
    info = {"name": "qa-raw-client", "version": "0.0.1"}

    with qa.harness.sandbox("qa-handshake") as _tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)

            # ---------------- 1. 第二个连接 ----------------
            def second_connection():
                with qa.raw_client(server) as first:
                    suite.is_true(bool(first.token), "第一连接握手脚本拿到 token")
                    with qa.raw_client(server, handshake=False) as second:
                        second.send_frame(hello_frame(client_info=info))
                        busy = second.read_json()
                        error = busy.get("error") or {}
                        suite.eq(error.get("code"), "E1003",
                                 "同实例第二连接 hello → E1003（单会话语义）")
                        suite.eq(error.get("detail"), "session_busy",
                                 "E1003 detail=session_busy")
                        suite.is_true(busy.get("ok") is False,
                                      "第二连接 hello_ack 不是成功态")
                        bye = second.read_json()
                        suite.eq(bye.get("kind"), "bye",
                                 "第二连接被拒后收到 bye（设计 §2.8：并关闭）")
                        suite.eq(bye.get("detail"), "session_busy", "bye.detail=session_busy")
                    # 关键：第一连接必须还能用（拒绝第二连接不能误伤已有会话）
                    still = first.request("sys_ping")
                    suite.is_true(still.get("ok") is True,
                                  "第二连接被拒后，第一连接的 sys_ping 仍成功",
                                  qa.describe_error(still))

            suite.case("单会话：第二连接", second_connection)

            # ---------------- 2. bye 之后复用旧 token ----------------
            def reuse_after_bye():
                client = qa.harness.RawClient(server.pipe_name)
                old_token = None
                try:
                    ack = client.hello()
                    old_token = ack.get("sessionToken")
                    client.send_frame({"kind": "bye", "reason": "qa_done"})
                    qa_time_sleep(0.3)
                finally:
                    client.close()
                # 新连接：用旧 token 直接发请求（跳过 hello）
                with qa.raw_client(server, handshake=False) as fresh:
                    reuse = fresh.request("sys_ping", token=old_token)
                    error = reuse.get("error") or {}
                    suite.eq(error.get("code"), "E4001",
                             "bye 后复用旧 token → E4001（令牌随连接失效）")
                    # errors.json#E4001 的 detailExamples 只承诺 major_mismatch / session_expired；
                    # C# 宿主对「未握手」额外细化为 handshake_required，mockhost 归入 session_expired。
                    # 二者都在契约内，故只断言 detail 属于这两种之一（不过度约束实现）。
                    detail = str(error.get("detail") or "")
                    suite.ok(detail in ("session_expired", "handshake_required"),
                             "未握手即请求 → detail 为 session_expired 或 handshake_required",
                             "实际 detail=%r" % (detail,))
                    # 重新握手后，再用旧 token 也必须被拒
                    ack = fresh.hello()
                    suite.eq(ack.get("kind"), "hello_ack", "新连接可重新握手")
                    stale = fresh.request("sys_ping", token=old_token)
                    suite.eq((stale.get("error") or {}).get("code"), "E4001",
                             "新会话里用旧 token → E4001")
                    suite.eq((stale.get("error") or {}).get("detail"), "session_expired",
                             "旧 token 的 detail 必须是 session_expired")

            suite.case("bye 后复用旧 token", reuse_after_bye)

            # ---------------- 3. 版本兼容矩阵 ----------------
            def version_matrix():
                # 3.1 次版本超前（1.9）：允许连接
                with qa.raw_client(server, handshake=False) as client:
                    client.send_frame(hello_frame(version="1.9", client_info=info))
                    ack = client.read_json()
                    suite.eq(ack.get("kind"), "hello_ack",
                             "1.9（次版本超前）允许连接")
                    suite.is_false(ack.get("writeEnabled"),
                                   "1.9 连接 writeEnabled 仍为 false")
                    # 设计 §2.3：客户端次版本超前必须告警 W1002（线上可观测）
                    warnings = ack.get("warnings")
                    w1002 = any(isinstance(item, dict) and item.get("code") == "W1002"
                                for item in (warnings or []))
                    post = client.request("sys_ping")
                    post_codes = [item.get("code") for item in (post.get("warnings") or [])
                                  if isinstance(item, dict)]
                    suite.ok(w1002 or "W1002" in post_codes,
                             "次版本超前：W1002 client_ahead 在线上可观测（hello_ack 或后续响应）",
                             "hello_ack.warnings=%r；sys_ping.warnings=%r"
                             % (warnings, post.get("warnings")))

                # 3.2 主版本落后（0.9）：拒绝 + bye + 断开
                with qa.raw_client(server, handshake=False) as client:
                    client.send_frame(hello_frame(version="0.9", client_info=info))
                    reject = client.read_json()
                    suite.eq((reject.get("error") or {}).get("code"), "E4001",
                             "0.9（主版本不符）→ E4001")
                    bye = client.read_json()
                    suite.eq(bye.get("kind"), "bye", "0.9 被拒后收到 bye")
                    closed = False
                    try:
                        client.read_json(timeout=1.5)
                    except (qa.pipeclient.PipeClosed, qa.pipeclient.PipeError,
                            EOFError, OSError, ValueError):
                        closed = True
                    except TimeoutError:
                        closed = False
                    suite.is_true(closed, "0.9 被拒后连接被关闭")

                # 3.3 畸形版本号逐个验证
                for bad in ("1", "1.0.0", "", "abc", "1.x", " 1.0", "1.-1"):
                    with qa.raw_client(server, handshake=False) as client:
                        client.send_frame(hello_frame(version=bad, client_info=info))
                        frame = client.read_json()
                        code = (frame.get("error") or {}).get("code")
                        suite.eq(code, "E4001",
                                 "畸形 protocolVersion=%r → E4001" % (bad,))

            suite.case("版本矩阵", version_matrix)

            # ---------------- 4. hello 缺必填字段 ----------------
            def missing_fields():
                # 4.1 缺 protocolVersion
                with qa.raw_client(server, handshake=False) as client:
                    client.send_frame(hello_frame(client_info=info, include_version=False))
                    frame = client.read_json()
                    suite.eq((frame.get("error") or {}).get("code"), "E4001",
                             "hello 缺 protocolVersion → E4001")

                # 4.2 缺 clientInfo（handshake.schema.json required 含 clientInfo）
                with qa.raw_client(server, handshake=False) as client:
                    client.send_frame(hello_frame(client_info=None))
                    frame = client.read_json()
                    code = (frame.get("error") or {}).get("code")
                    field = (frame.get("error") or {}).get("field")
                    suite.ok(code in ("E1001", "E4001"),
                             "hello 缺 clientInfo → 拒绝（E1001/E4001）",
                             "实际 kind=%r code=%r field=%r"
                             % (frame.get("kind"), code, field))

                # 4.3 缺 clientNonce（契约 required 含 clientNonce）
                with qa.raw_client(server, handshake=False) as client:
                    client.send_frame(hello_frame(client_info=info, include_nonce=False))
                    frame = client.read_json()
                    code = (frame.get("error") or {}).get("code")
                    suite.ok(code in ("E1001", "E4001"),
                             "hello 缺 clientNonce → 拒绝（E1001/E4001）",
                             "实际 kind=%r code=%r" % (frame.get("kind"), code))

                # 4.4 clientNonce 太短（契约 16–64 hex）
                with qa.raw_client(server, handshake=False) as client:
                    client.send_frame(hello_frame(client_info=info, nonce="abcd"))
                    frame = client.read_json()
                    code = (frame.get("error") or {}).get("code")
                    suite.ok(code in ("E1001", "E4001"),
                             "clientNonce 长度 4（<16）→ 拒绝",
                             "实际 kind=%r code=%r" % (frame.get("kind"), code))

            suite.case("hello 缺字段", missing_fields)

            # ---------------- 5. 未知能力进 missingFeatures ----------------
            def unknown_feature():
                with qa.raw_client(server, handshake=False) as client:
                    client.send_frame(hello_frame(
                        client_info=info,
                        features=["preview", "quantum_teleport", "read_overview"]))
                    ack = client.read_json()
                    suite.eq(ack.get("kind"), "hello_ack",
                             "含未知能力时握手仍成功")
                    missing = ack.get("missingFeatures") or []
                    suite.contains(missing, "quantum_teleport",
                                   "未知能力进 missingFeatures")
                    suite.ok("preview" not in missing,
                             "已知能力不进 missingFeatures", "missing=%r" % (missing,))
                    suite.ok("read_overview" not in missing,
                             "工具名不被误报为缺失能力", "missing=%r" % (missing,))

            suite.case("missingFeatures", unknown_feature)

    return suite.report()


def qa_time_sleep(seconds: float) -> None:
    import time
    time.sleep(seconds)


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
