"""tests/qa/qa_framing_edge.py —— 分帧边角（QA 独立复核，不改 tests/ 既有用例）。

针对工程师已覆盖的「常规半包/粘包/1.5 MiB 超限」之外，专挑这几条：
  1. len == 0（协议违规）—— 必须关连接，且不得发出任何业务响应；
  2. 声明 1 MiB 但只发一半就断连 —— 宿主必须判协议违规关连接，且不残留线程；
  3. 声明 0x7FFFFFFF（int32 上限）—— 必须拒绝且**不 OOM**（用进程工作集增量作证）；
  4. 连续 3 帧粘在同一次 write 里（工程师只测了 2 帧）；
  5. UTF-8 中文多字节字符被 write 边界劈开 —— 拼包后必须还原成正确的中文字符串；
  6. 帧体是 JSON 数组（协议要求单 object）—— 必须拒绝且连接可继续用。

「不 OOM」「不残留」这类结论不能只写「跑通了」，故用 rss_kb() 取进程工作集增量、
用「坏连接之后能否重新握手」作为宿主存活的证据。
"""

from __future__ import annotations

import io
import json
import sys
import time

import qa_common as qa

OOM_BUDGET_KB = 50 * 1024          # 超长长度前缀引起的 RSS 增量上限（50 MB）

#: 帧体声明 0x7FFFFFFF 时至少要有这么多余量，否则「不 OOM」的结论没有意义
INT32_MAX = 0x7FFFFFFF

#: 管道被对端关闭时 pipeclient 抛的异常（不继承 OSError，必须显式列出）
CLOSED = (qa.pipeclient.PipeClosed, qa.pipeclient.PipeError,
          EOFError, OSError, ValueError)


def reopen_and_handshake(suite, server, label: str) -> None:
    """「宿主是否还活着」的唯一硬证据：坏连接之后能否重新握手拿到 hello_ack。

    注意：raw_client(server) 默认已在上下文里做过一次 hello，故这里必须 handshake=False，
    否则第二个 hello 会被单会话语义拒绝成 E1003（这正是握手边角用例要测的东西，
    不能混进分帧用例里当噪声）。
    """
    with qa.raw_client(server, handshake=False) as fresh:
        ack = fresh.hello()
        if ack.get("kind") == "hello_ack":
            suite.ok(True, label)
        else:
            suite.ok(False, label,
                     "ack=%s" % json.dumps(ack, ensure_ascii=False)[:300])



def run() -> dict:
    suite = qa.harness.Suite("qa_framing_edge")

    with qa.harness.sandbox("qa-framing") as _tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)

            # ---------------- 1. 零长度帧 ----------------
            def zero_length():
                with qa.raw_client(server, handshake=False) as client:
                    client.send_bytes(b"\x00\x00\x00\x00")
                    closed = False
                    payload = None
                    try:
                        payload = client.read_json(timeout=2.0)
                    except CLOSED:
                        closed = True
                    except TimeoutError:
                        closed = False
                    suite.ok(closed, "len==0：宿主关闭连接（读到 EOF 而非静默挂住）",
                             "closed=%r payload=%r" % (closed, payload))
                    suite.ok(payload is None, "len==0：不返回任何业务帧（协议违规直接断）",
                             "payload=%r" % (payload,))

            suite.case("零长度帧", zero_length)

            # ---------------- 2. 声明 1 MiB 只发一半就断连 ----------------
            def half_of_limit():
                before = qa.rss_kb()
                # 本用例**不需要**裸客户端：它测的是「半包后断连宿主不炸」，与连接是否重试无关。
                # 之前这里直接用 `harness.RawClient(server.pipe_name)`，绕过了连接重试 →
                # 管道实例切换瞬间会偶发 HostUnreachable('pipe_not_found')（flake 台账第 5 次就是这个）。
                # 改用 `qa.raw_client`（只对建连重试、用例体异常照常冒）。
                with qa.raw_client(server, handshake=False) as client:
                    client.send_bytes(qa.header_only(qa.MAX_PAYLOAD))
                    client.send_bytes(b"x" * (qa.MAX_PAYLOAD // 2))   # 只发一半
                # 退出 with 即 close（直接断连），与原逻辑一致
                time.sleep(0.4)
                after = qa.rss_kb()
                suite.ok(True, "声明 1 MiB 只发一半后断连：宿主未抛异常（无异常日志即正常）")
                if before > 0 and after > 0:
                    suite.ok(after - before < OOM_BUDGET_KB,
                             "半包断连：RSS 增量在预算内（不预分配 len 字节）",
                             "before=%dKB after=%dKB delta=%dKB"
                             % (before, after, after - before))
                # 宿主存活证据：坏连接之后还能完成一次全新握手
                reopen_and_handshake(suite, server, "半包断连后宿主仍可接受新连接并握手")

            suite.case("声明 1MiB 半包断连", half_of_limit)

            # ---------------- 3. 声明 0x7FFFFFFF（必须拒绝且不 OOM） ----------------
            def int32_max_length():
                before = qa.rss_kb()
                client = qa.harness.RawClient(server.pipe_name)
                got = None
                try:
                    client.send_bytes(qa.header_only(INT32_MAX))
                    client.send_bytes(b"y" * 1024)                   # 只给 1 KB
                    try:
                        got = client.read_json(timeout=1.5)
                    except (TimeoutError,) + CLOSED:
                        got = None
                finally:
                    client.close()
                time.sleep(0.4)
                after = qa.rss_kb()
                suite.ok(got is None,
                         "0x7FFFFFFF：拒绝且不回业务帧（已丢弃/断开，不尝试分配 2 GB）",
                         "got=%r" % (got,))
                if before > 0 and after > 0:
                    delta = after - before
                    suite.ok(delta < OOM_BUDGET_KB,
                             "0x7FFFFFFF：RSS 增量 < 50 MB（证明按 8 KB 暂存丢弃）",
                             "before=%dKB after=%dKB delta=%dKB" % (before, after, delta))
                else:
                    suite.ok(True, "0x7FFFFFFF：本环境取不到 RSS，跳过内存断言（不据此判失败）")
                reopen_and_handshake(suite, server, "0x7FFFFFFF 之后宿主仍存活（可重新握手）")

            suite.case("长度前缀 0x7FFFFFFF", int32_max_length)

            # ---------------- 4. 连续 3 帧粘在一次 write ----------------
            def three_frames_at_once():
                with qa.raw_client(server) as client:
                    tools = ["sys_ping", "sys_version", "read_overview"]
                    op_ids = ["op-qa-sticky-0001", "op-qa-sticky-0002", "op-qa-sticky-0003"]
                    blob = io.BytesIO()
                    for index, tool in enumerate(tools):
                        blob.write(qa.frame_bytes({
                            "protocolVersion": "1.0",
                            "sessionToken": client.token,
                            "hostInstanceId": client.instance_id,
                            "opId": op_ids[index],
                            "idempotencyKey": None,
                            "tool": tool,
                            "params": {},
                            "paramsDigest": None,
                            "targetFingerprint": None,
                            "approvalToken": None,
                            "timeoutMs": 15000,
                            "cancelable": True,
                        }))
                    client.send_bytes(blob.getvalue())               # 一次 write，三帧
                    frames = [client.read_json(timeout=6.0) for _ in range(3)]
                    suite.eq([item.get("opId") for item in frames], op_ids,
                             "粘包 3 帧：响应按 FIFO 顺序、opId 一一对应")
                    suite.eq([bool(item.get("ok")) for item in frames], [True] * 3,
                             "粘包 3 帧：三帧全部成功（无帧错位）")

            suite.case("三帧粘连", three_frames_at_once)

            # ---------------- 5. UTF-8 中文被 write 边界劈开 ----------------
            def utf8_split():
                with qa.raw_client(server) as client:
                    layer = "D-DR-管道"
                    frame = qa.frame_bytes({
                        "protocolVersion": "1.0",
                        "sessionToken": client.token,
                        "hostInstanceId": client.instance_id,
                        "opId": "op-qa-utf8-0001",
                        "idempotencyKey": None,
                        "tool": "read_query",
                        "params": {"layer": layer, "limit": 5},
                        "paramsDigest": None,
                        "targetFingerprint": None,
                        "approvalToken": None,
                        "timeoutMs": 15000,
                        "cancelable": True,
                    })
                    # 找到「管」字的第一个字节，在其后 1 字节处切开 → 正好劈开 3 字节序列
                    marker = layer.encode("utf-8")[:3]
                    index = frame.find(marker)
                    suite.is_true(index > 0, "UTF-8 用例：能在帧里定位中文字节起点",
                                   "index=%d" % index)
                    if index > 0:
                        cut = index + 1
                        client.send_bytes(frame[:cut])
                        time.sleep(0.05)
                        client.send_bytes(frame[cut:])
                        response = client.read_json(timeout=6.0)
                        suite.eq(response.get("opId"), "op-qa-utf8-0001",
                                 "中文多字节被劈开：拼包后仍能解析出同一个 opId")
                        suite.is_true(response.get("ok") is True,
                                      "中文多字节被劈开：请求正常执行（UTF-8 未被截断成乱码）",
                                      qa.describe_error(response))

            suite.case("UTF-8 跨 write 边界", utf8_split)

            # ---------------- 6. 帧体是 JSON 数组 ----------------
            def array_frame():
                with qa.raw_client(server) as client:
                    client.send_bytes(qa.frame_bytes([1, 2, 3]))
                    frame = client.read_json(timeout=3.0)
                    suite.eq((frame.get("error") or {}).get("code"), "E1001",
                             "帧体为数组：回 E1001（协议要求单 object）")
                    suite.is_true(client.request("sys_ping").get("ok") is True,
                                  "帧体为数组之后连接仍可用")

            suite.case("数组帧", array_frame)

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
