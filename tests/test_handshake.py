"""握手与请求门禁测试（设计 §2.2 / §2.3）。

覆盖：
    1. hello → hello_ack 字段完整性（含 capabilities 8 个必填项与 gaps）；
    2. 主版本不匹配 → E4001 + bye，连接关闭；
    3. 次版本高于宿主 → 允许连接（W1002 场景），writeEnabled 恒 false；
    4. 未知 sessionToken → E4001 detail=session_expired；
    5. approvalToken 非 null → E1006；
    6. opId 形状非法 → E1001 field=opId；
    7. 单会话：已有活动会话时新 hello → E1003 session_busy；
    8. 请求缺 params / 参数越界 → E1001 且 field 指到具体入参。
"""

from __future__ import annotations

import json

import harness


def run() -> dict:
    suite = harness.Suite("handshake")

    with harness.sandbox("handshake"):
        with harness.mock_server() as (server, host):

            # ---------- 1. 正常握手 ----------
            def normal_handshake():
                with harness.raw_client(server, handshake=False) as client:
                    ack = client.hello()
                    suite.eq(ack.get("kind"), "hello_ack", "hello_ack kind 正确")
                    suite.eq(ack.get("protocolVersion"), "1.0", "hello_ack 协议版本 1.0")
                    suite.eq(ack.get("clientNonceEcho"), "0" * 16, "echo 回 clientNonce")
                    suite.matches(ack.get("hostInstanceId") or "", r"^mock-[0-9a-f]{8}$",
                                  "hostInstanceId 形状 mock-<8hex>")
                    suite.eq(len(ack.get("sessionToken") or ""), 64, "sessionToken 64 hex")
                    suite.matches(ack.get("serverTimeUtc") or "",
                                  r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
                                  "serverTimeUtc 为毫秒精度 UTC")
                    suite.is_false(ack.get("writeEnabled"), "writeEnabled 恒为 false")

                    # 第二轮新增的宿主身份字段（Civil 3D 探测结果，不是猜的）
                    suite.eq(ack.get("hostProduct"), "AutoCAD",
                             "hello_ack.hostProduct=AutoCAD（mock 档无 Civil 3D）")
                    suite.matches(ack.get("hostRelease") or "", r"^R[0-9]+\.[0-9]+$",
                                  "hello_ack.hostRelease 形如 R24.1")
                    suite.matches(ack.get("hostVersion") or "", r"^[0-9]+\.[0-9]+\.[0-9]+",
                                  "hostVersion 仍是插件版本号（未与 CAD 版本混用）")

                    caps = ack.get("capabilities") or {}
                    for key in ("domains", "writeEnabled", "civilApi", "preview",
                                "messageLimitBytes", "heartbeat", "cancellation",
                                "toolCatalogHash"):
                        suite.ok(key in caps, "capabilities 必填项存在：%s" % key)
                    suite.eq(caps.get("domains"), ["sys", "read", "task"],
                             "capabilities.domains 只含只读域")
                    suite.eq(caps.get("messageLimitBytes"), 1048576, "messageLimitBytes=1 MiB")
                    suite.eq((caps.get("heartbeat") or {}).get("intervalMs"), 5000,
                             "heartbeat.intervalMs=5000")
                    suite.eq((caps.get("heartbeat") or {}).get("missLimit"), 3,
                             "heartbeat.missLimit=3")
                    suite.eq((caps.get("preview") or {}).get("renderMode"), "schematic",
                             "preview.renderMode=schematic（非出图）")
                    suite.is_false((caps.get("civilApi") or {}).get("available"),
                                   "civilApi.available=false（mock 档无 Civil 3D）")
                    suite.eq((caps.get("civilApi") or {}).get("product"), "AutoCAD",
                             "civilApi.product=AutoCAD")
                    suite.is_false((caps.get("civilApi") or {}).get("writeSupported"),
                                   "civilApi.writeSupported=false（M0 无 Civil 写 API）")
                    suite.matches(caps.get("toolCatalogHash") or "",
                                  r"^sha256:[0-9a-f]{16}$", "toolCatalogHash 形状")

                    gaps = ack.get("gaps") or []
                    suite.eq(len(gaps), 1, "gaps 上报 1 条 Civil 3D 缺口")
                    suite.eq(gaps[0].get("status"), "absent", "gap.status=absent")
                    suite.contains(gaps[0].get("capability") or "", "civil.",
                                   "gap.capability 指向 civil.*")

            # ---------- 2. 主版本不匹配 ----------
            def major_mismatch():
                with harness.raw_client(server, handshake=False) as client:
                    ack = client.hello("2.0")
                    suite.eq((ack.get("error") or {}).get("code"), "E4001",
                             "主版本 2.0 → E4001")
                    goodbye = client.read_json()
                    suite.eq(goodbye.get("kind"), "bye", "主版本不匹配后发 bye")
                    suite.eq(goodbye.get("reason"), "protocol_violation",
                             "bye.reason=protocol_violation")
                    suite.is_true(client.expect_silence(timeout=1.0),
                                  "主版本不匹配后连接关闭")

            # ---------- 3. 次版本高于宿主 ----------
            def minor_ahead():
                with harness.raw_client(server, handshake=False) as client:
                    ack = client.hello("1.9")
                    suite.eq(ack.get("kind"), "hello_ack", "次版本 1.9 允许连接")
                    suite.is_false(ack.get("writeEnabled"), "降级连接 writeEnabled 仍为 false")
                    # D3：W1002 必须真的上线（以前只构造、只写日志，客户端永远看不到）
                    codes = [item.get("code") for item in (ack.get("warnings") or [])]
                    suite.contains(codes, "W1002",
                                   "次版本领先 → hello_ack.warnings 含 W1002")
                    suite.is_true(all(isinstance(item, dict) and item.get("message")
                                      for item in (ack.get("warnings") or [])),
                                  "W1002 是结构化告警对象（含 code + message）")

            # ---------- 3b. hello 必填字段（D1）----------
            def hello_required_fields():
                good_info = {"name": "gate-client", "version": "0.1.0"}

                def raw_hello(frame):
                    with harness.raw_client(server, handshake=False) as client:
                        client.send_frame(frame)
                        return client.read_json()

                cases = [
                    ("缺 clientInfo",
                     {"kind": "hello", "protocolVersion": "1.0",
                      "clientNonce": "0123456789abcdef"},
                     "clientInfo_missing", "clientInfo"),
                    ("clientInfo 缺 version",
                     {"kind": "hello", "protocolVersion": "1.0",
                      "clientInfo": {"name": "gate-client"},
                      "clientNonce": "0123456789abcdef"},
                     "clientInfo.version_missing", "clientInfo.version"),
                    ("clientInfo 缺 name",
                     {"kind": "hello", "protocolVersion": "1.0",
                      "clientInfo": {"version": "0.1.0"},
                      "clientNonce": "0123456789abcdef"},
                     "clientInfo.name_missing", "clientInfo.name"),
                    ("缺 clientNonce",
                     {"kind": "hello", "protocolVersion": "1.0",
                      "clientInfo": good_info},
                     "clientNonce_missing", "clientNonce"),
                    ("clientNonce 长度 4",
                     {"kind": "hello", "protocolVersion": "1.0",
                      "clientInfo": good_info, "clientNonce": "abcd"},
                     "clientNonce_invalid", "clientNonce"),
                    ("clientNonce 非十六进制（长度够）",
                     {"kind": "hello", "protocolVersion": "1.0",
                      "clientInfo": good_info, "clientNonce": "zzzzzzzzzzzzzzzz"},
                     "clientNonce_invalid", "clientNonce"),
                ]
                for label, frame, detail, field in cases:
                    response = raw_hello(frame)
                    error = response.get("error") or {}
                    suite.eq(error.get("code"), "E1001", "%s → E1001" % label)
                    suite.eq(error.get("detail"), detail, "%s → detail=%s" % (label, detail))
                    suite.eq(error.get("field"), field, "%s → field=%s" % (label, field))
                    suite.is_true(not response.get("sessionToken"),
                                  "%s 不签发 sessionToken" % label)

                # 合法帧仍能握手（防止「一律拒绝」的假通过）
                ok = raw_hello({"kind": "hello", "protocolVersion": "1.0",
                                "clientInfo": good_info,
                                "clientNonce": "0123456789abcdef"})
                suite.eq(ok.get("kind"), "hello_ack", "字段齐全的 hello 仍正常握手")

            # ---------- 4-6. 请求门禁 ----------
            def token_and_gates():
                with harness.raw_client(server) as client:
                    bad_token = client.request("sys_ping", token="0" * 64)
                    suite.eq((bad_token.get("error") or {}).get("code"), "E4001",
                             "未知 sessionToken → E4001")
                    suite.eq((bad_token.get("error") or {}).get("detail"), "session_expired",
                             "未知 sessionToken → detail=session_expired")

                    approval = client.request("sys_ping", approval_token="legacy-token")
                    suite.eq((approval.get("error") or {}).get("code"), "E1006",
                             "approvalToken 非 null → E1006")

                    bad_op = client.request("sys_ping", op_id="bad-opid")
                    suite.eq((bad_op.get("error") or {}).get("code"), "E1001",
                             "opId 形状非法 → E1001")
                    suite.eq((bad_op.get("error") or {}).get("field"), "opId",
                             "opId 非法 → field=opId")

            # ---------- 7. 单会话 ----------
            def single_session():
                first = harness.connect(server)
                try:
                    with harness.raw_client(server, handshake=False) as second:
                        busy = second.hello()
                        suite.eq((busy.get("error") or {}).get("code"), "E1003",
                                 "已有活动会话时新 hello → E1003")
                        suite.eq((busy.get("error") or {}).get("detail"), "session_busy",
                                 "session_busy detail 正确")
                finally:
                    first.close()

            # ---------- 8. 参数门禁 ----------
            def params_gates():
                client = harness.connect(server)
                try:
                    unknown = harness.call_err(suite, client, "read_layers",
                                               {"nonsense": 1}, "未知参数")
                    suite.eq(unknown.get("field"), "params.nonsense",
                             "未知参数 → field=params.nonsense")

                    out_of_range = harness.call_err(suite, client, "read_query",
                                                   {"limit": 500}, "limit 越界")
                    suite.eq(out_of_range.get("code"), "E1001", "limit 越界 → E1001")
                    suite.eq(out_of_range.get("field"), "params.limit",
                             "limit 越界 → field=params.limit")
                    suite.is_true(out_of_range.get("retriable"),
                                  "参数类错误 retriable=true（修正后可换 opId 重试）")

                    wrong_type = harness.call_err(suite, client, "read_preview",
                                                 {"width": "1200"}, "width 类型错误")
                    suite.eq(wrong_type.get("field"), "params.width",
                             "width 类型错误 → field=params.width")

                    bad_handle = harness.call_err(suite, client, "read_describe",
                                                 {"handle": "ZZZZ"}, "handle 非十六进制")
                    suite.eq(bad_handle.get("field"), "params.handle",
                             "handle 非十六进制 → field=params.handle")

                    ok_ping = client.call("sys_ping", {})
                    suite.is_true(ok_ping.get("ok"), "门禁拒绝后连接仍可正常调用")
                finally:
                    client.close()

            # ---------- 9. 写域大小写（D6）----------
            def write_domain_case_insensitive():
                client = harness.connect(server)
                try:
                    variants = ("cad_create_line", "CAD_CREATE_LINE", "Cad_Create_Line",
                                "cad_CREATE_line", "civil_Create_Profile",
                                "FILE_WRITE_TEXT", "Skill_Run_Lisp")
                    for name in variants:
                        error = harness.call_err(suite, client, name, {},
                                                 "写域 %s" % name)
                        suite.eq(error.get("code"), "E2003",
                                 "%s 必须 E2003（写通道关闭），不能退化成 E2001" % name)
                        suite.eq(error.get("detail"), "write_channel_disabled_m0",
                                 "%s detail=write_channel_disabled_m0" % name)
                        suite.eq(error.get("field"), "tool", "%s field=tool" % name)

                    # 拼错的只读名仍应是 E2001（两类错误语义不能混）
                    typo = harness.call_err(suite, client, "read_overveiw", {},
                                            "拼错的只读工具名")
                    suite.eq(typo.get("code"), "E2001",
                             "拼错的只读名 → E2001（不是写域）")
                finally:
                    client.close()

            for label, func in (
                ("正常握手", normal_handshake),
                ("主版本不匹配", major_mismatch),
                ("次版本领先", minor_ahead),
                ("hello 必填字段", hello_required_fields),
                ("令牌与门禁", token_and_gates),
                ("单会话", single_session),
                ("参数门禁", params_gates),
                ("写域大小写", write_domain_case_insensitive),
            ):
                suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
