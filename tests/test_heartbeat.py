"""心跳与超时测试（设计 §2.4）。

覆盖：
    1. 正常心跳（mockhost 在线）→ 连续 ping 全部成功，连接不被判死；
    2. 宿主收包但不回包（--silent）→ 连续 missLimit 次超时后判连接死亡（W1003）；
    3. 请求超时 → MCP 侧 PipeTimeout 携带 opId（供 task_reconcile 对账）；
    4. 连接断开后 call 立刻失败且不再挂起。
"""

from __future__ import annotations

import json
import time

import harness


def run() -> dict:
    suite = harness.Suite("heartbeat")

    with harness.sandbox("heartbeat"):
        # ---------- 1. 正常心跳 ----------
        def healthy_heartbeat():
            with harness.mock_server() as (server, _host):
                client = harness.connect(server, heartbeat=True,
                                         heartbeat_interval_ms=200,
                                         heartbeat_miss_limit=3)
                try:
                    time.sleep(0.9)          # 约 4 个心跳周期
                    suite.is_false(client.closed, "宿主在线时连接保持存活")
                    suite.ok(client.ping(timeout_ms=1000), "心跳 ping 成功")
                finally:
                    client.close()

        # ---------- 2. 宿主不回包 → 判死 ----------
        def silent_host_dies():
            with harness.mock_server(silent=True) as (server, _host):
                client = harness.connect(server, heartbeat=False)
                client._heartbeat_enabled = True
                client._heartbeat_interval_ms = 200
                client._heartbeat_miss_limit = 2
                client._start_heartbeat()
                deadline = time.time() + 8.0
                while time.time() < deadline and not client.closed:
                    time.sleep(0.05)
                suite.is_true(client.closed, "连续 2 次心跳失败后判连接死亡")
                error = client.last_protocol_error or {}
                suite.eq(error.get("code"), "W1003", "判死原因记为 W1003 heartbeat_timeout")
                client.close()

        # ---------- 3. 请求超时携带 opId ----------
        def call_timeout():
            import pipeclient
            with harness.mock_server(silent=True) as (server, _host):
                client = harness.connect(server, heartbeat=False)
                op_id = None
                try:
                    client.call("sys_ping", {}, timeout_ms=600)
                    suite.fail("silent 宿主下 call 应当超时")
                except pipeclient.PipeTimeout as exc:
                    op_id = exc.op_id
                    suite.is_true(bool(op_id), "PipeTimeout 携带 opId")
                    suite.matches(op_id, r"^op-[0-9T:A-Za-z-]{8,64}$", "opId 形状合法")
                finally:
                    client.close()
                suite.is_true(bool(op_id), "超时 opId 可用于 task_reconcile 对账")

        # ---------- 4. 断连后 call 立刻失败 ----------
        def closed_connection_fails_fast():
            import pipeclient
            with harness.mock_server() as (server, _host):
                client = harness.connect(server)
                client.close()
                started = time.time()
                try:
                    client.call("sys_ping", {}, timeout_ms=3000)
                    suite.fail("关闭后的连接不应还能调用")
                except (pipeclient.PipeError, pipeclient.FrameError) as exc:
                    elapsed = time.time() - started
                    suite.is_true(elapsed < 2.0,
                                  "关闭后调用立刻失败（不挂起）", "耗时 %.2fs" % elapsed)
                    suite.is_true(isinstance(exc, pipeclient.PipeError),
                                  "抛出 PipeError 家族异常", repr(exc))

        for label, func in (
            ("正常心跳", healthy_heartbeat),
            ("心跳判死", silent_host_dies),
            ("请求超时", call_timeout),
            ("断连快速失败", closed_connection_fails_fast),
        ):
            suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
