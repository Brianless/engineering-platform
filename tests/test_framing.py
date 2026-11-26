"""分帧与协议边界测试（设计 §2.1）。

覆盖：
    1. 半包：长度头与载荷分两次写，宿主必须拼包后再处理；
    2. 粘包：两帧写进一次 write，宿主必须逐帧响应且顺序正确；
    3. 超长帧：len > 1 MiB → 回 E1001 detail=message_too_large（opId=null），
       并且连接**保持可用**（这是最容易实现错的一条）；
    4. len == 0 → 协议违规，连接关闭（不发响应）；
    5. 非 JSON 载荷 → E1001 invalid_json，连接仍可用；
    6. 规范化编码：ensure_ascii=False（中文不转义），长度前缀为小端 uint32。
"""

from __future__ import annotations

import json
import struct

import harness


def run() -> dict:
    suite = harness.Suite("framing")

    with harness.sandbox("framing"):
        with harness.mock_server() as (server, host):
            limit = host.capabilities()["messageLimitBytes"]

            # ---------- 1. 半包 ----------
            def half_packet():
                with harness.raw_client(server, handshake=False) as client:
                    frame = client._pipeclient.encode_frame({
                        "kind": "hello",
                        "protocolVersion": "1.0",
                        "clientInfo": {"name": "half-packet", "version": "0.1.0"},
                        "clientNonce": "abcdef0123456789",
                    })
                    head, body = frame[:4], frame[4:]
                    client.send_bytes(head)
                    client.send_bytes(body[: len(body) // 2])   # 半个载荷
                    client.send_bytes(body[len(body) // 2:])    # 补齐
                    ack = client.read_json()
                    suite.eq(ack.get("kind"), "hello_ack", "半包：宿主拼包后完成握手")
                    suite.eq(len(ack.get("sessionToken") or ""), 64, "半包：sessionToken 为 32 字节 hex")

            # ---------- 2. 粘包 ----------
            def sticky_packet():
                with harness.raw_client(server) as client:
                    a = client._pipeclient.encode_frame({
                        "protocolVersion": "1.0", "sessionToken": client.token,
                        "hostInstanceId": client.instance_id, "opId": "op-sticky-0001",
                        "idempotencyKey": None, "tool": "sys_ping", "params": {},
                        "paramsDigest": None, "targetFingerprint": None,
                        "approvalToken": None, "timeoutMs": 5000, "cancelable": True})
                    b = client._pipeclient.encode_frame({
                        "protocolVersion": "1.0", "sessionToken": client.token,
                        "hostInstanceId": client.instance_id, "opId": "op-sticky-0002",
                        "idempotencyKey": None, "tool": "sys_version", "params": {},
                        "paramsDigest": None, "targetFingerprint": None,
                        "approvalToken": None, "timeoutMs": 5000, "cancelable": True})
                    client.send_bytes(a + b)          # 一次 write 两帧
                    first = client.read_json()
                    second = client.read_json()
                    suite.eq(first.get("opId"), "op-sticky-0001", "粘包：第一帧按顺序响应")
                    suite.eq(second.get("opId"), "op-sticky-0002", "粘包：第二帧按顺序响应")
                    suite.is_true(first.get("ok"), "粘包：sys_ping 成功")

            # ---------- 3. 超长帧 ----------
            def oversize_frame():
                with harness.raw_client(server) as client:
                    declared = limit + 1024
                    client.send_oversize_frame(declared)
                    error_frame = client.read_json(timeout=8.0)
                    suite.is_true(error_frame.get("error"), "超长帧：返回错误帧而非静默丢弃")
                    suite.eq((error_frame.get("error") or {}).get("code"), "E1001",
                             "超长帧：错误码 E1001")
                    suite.contains((error_frame.get("error") or {}).get("detail") or "",
                                   "message_too_large", "超长帧：detail=message_too_large")
                    suite.ok(error_frame.get("opId") is None, "超长帧：opId 为 null",
                             "actual=%r" % (error_frame.get("opId"),))
                    # 关键断言：连接必须还能用（流没有错位）
                    response = client.request("sys_ping")
                    suite.is_true(response.get("ok"), "超长帧之后同一连接的 sys_ping 仍成功",
                                  json.dumps(response.get("error"), ensure_ascii=False))

            # ---------- 4. len == 0 ----------
            def zero_length():
                with harness.raw_client(server) as client:
                    client.send_bytes(struct.pack("<I", 0))
                    closed = client.expect_silence(timeout=1.0)
                    suite.is_true(closed, "len==0：宿主关闭连接且不发响应")

            # ---------- 5. 非 JSON ----------
            def invalid_json():
                with harness.raw_client(server) as client:
                    client.send_bytes(struct.pack("<I", 5) + b"{bad}")
                    frame = client.read_json()
                    suite.eq((frame.get("error") or {}).get("code"), "E1001",
                             "非 JSON：E1001")
                    suite.contains((frame.get("error") or {}).get("detail") or "",
                                   "invalid_json", "非 JSON：detail=invalid_json")
                    response = client.request("sys_ping")
                    suite.is_true(response.get("ok"), "非 JSON 之后连接仍可用")

            # ---------- 6. 编码规范 ----------
            def encoding_rules():
                import mock_host
                frame = mock_host.encode_frame({"a": "中文", "b": 1})
                declared = struct.unpack("<I", frame[:4])[0]
                suite.eq(declared, len(frame) - 4, "编码：长度前缀 = 载荷字节数（小端 uint32）")
                suite.is_true(b"\xe4\xb8\xad\xe6\x96\x87" in frame,
                              "编码：中文以 UTF-8 原样写出（不转义成 \\uXXXX）")

            for label, func in (
                ("半包", half_packet),
                ("粘包", sticky_packet),
                ("超长帧", oversize_frame),
                ("零长度", zero_length),
                ("非 JSON", invalid_json),
                ("编码规范", encoding_rules),
            ):
                suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
