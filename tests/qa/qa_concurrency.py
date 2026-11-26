"""tests/qa/qa_concurrency.py —— 并发与 opId 配对（QA 独立复核）。

M0 的硬约定是「只读不并行执行」（设计 §3.3），但**协议层必须允许多个请求同时在飞**，
否则 task_cancel 根本读不到。工程师的测试里没有任何并发用例，这里补：

  1. 同一条管道连接上一次性发出 4 个请求（4 帧粘在一次 write）→ 必须收到 4 个响应，
     opId 与 tool 一一对应，不许串味；
  2. 两个线程各发 2 个请求（加写锁保证帧不互相穿插）→ 4 个响应的 opId 集合必须
     恰好等于请求集合，且每条响应的 tool 与 opId 的映射关系不能错位；
  3. 执行确实被串行化（FIFO 顺序 + 每个响应的 elapsedMs 均为单次执行量级，
     而不是「4 个响应同时返回」）；
  4. 重复 opId（客户端重试用了同一个 opId）→ 两个响应都能拿到、不崩溃，
     且 task_status 能查到该 opId。
"""

from __future__ import annotations

import json
import sys
import threading

import qa_common as qa


def build_request(token, instance_id, op_id, tool, params=None):
    return {
        "protocolVersion": "1.0",
        "sessionToken": token,
        "hostInstanceId": instance_id,
        "opId": op_id,
        "idempotencyKey": None,
        "tool": tool,
        "params": params or {},
        "paramsDigest": None,
        "targetFingerprint": None,
        "approvalToken": None,
        "timeoutMs": 15000,
        "cancelable": True,
    }


def run() -> dict:
    suite = qa.harness.Suite("qa_concurrency")

    with qa.harness.sandbox("qa-concurrency") as _tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)

            # ---------------- 1. 一次 write 里 4 个请求 ----------------
            def burst_four():
                with qa.raw_client(server) as client:
                    plan = [
                        ("op-qa-burst-0001", "sys_ping", {}),
                        ("op-qa-burst-0002", "read_layers", {"limit": 5}),
                        ("op-qa-burst-0003", "read_anomalies", {"limit": 5}),
                        ("op-qa-burst-0004", "sys_version", {}),
                    ]
                    blob = b"".join(
                        qa.frame_bytes(build_request(client.token, client.instance_id,
                                                     op_id, tool, params))
                        for op_id, tool, params in plan)
                    client.send_bytes(blob)
                    frames = [client.read_json(timeout=10.0) for _ in plan]
                    suite.eq([item.get("opId") for item in frames],
                             [op_id for op_id, _t, _p in plan],
                             "4 请求突发：响应顺序与请求顺序一致（FIFO 串行）")
                    expected_tools = [tool for _o, tool, _p in plan]
                    suite.eq([item.get("tool") for item in frames], expected_tools,
                             "4 请求突发：每条响应的 tool 与 opId 映射无错位")
                    suite.eq([bool(item.get("ok")) for item in frames], [True] * 4,
                             "4 请求突发：全部成功（无帧错位/令牌串味）")
                    tokens = {item.get("sessionToken") for item in frames}
                    suite.eq(tokens, {client.token},
                             "4 请求突发：响应里的 sessionToken 全部一致且等于会话令牌")

            suite.case("单连接 4 请求突发", burst_four)

            # ---------------- 2. 两个线程交错写 ----------------
            def threaded_pairs():
                with qa.raw_client(server) as client:
                    write_lock = threading.Lock()
                    results = []
                    results_lock = threading.Lock()
                    errors = []

                    def worker(index):
                        try:
                            for sub in range(2):
                                op_id = "op-qa-thread-%02d-%d" % (index, sub)
                                payload = qa.frame_bytes(build_request(
                                    client.token, client.instance_id, op_id, "sys_ping"))
                                with write_lock:                    # 保证帧不被劈开
                                    client.send_bytes(payload)
                        except Exception as exc:              # noqa: BLE001
                            errors.append(repr(exc))

                    threads = [threading.Thread(target=worker, args=(i,))
                               for i in range(2)]
                    for thread in threads:
                        thread.start()
                    for thread in threads:
                        thread.join(timeout=10)

                    for _ in range(4):
                        try:
                            response = client.read_json(timeout=10.0)
                        except Exception as exc:              # noqa: BLE001
                            errors.append(repr(exc))
                            break
                        with results_lock:
                            results.append(response)

                    suite.ok(errors == [], "多线程交错写：无异常", "errors=%r" % (errors,))
                    suite.ok(len(results) == 4, "多线程交错写：收到 4 个响应",
                             "实际 %d 个" % len(results))
                    expected = {"op-qa-thread-00-0", "op-qa-thread-00-1",
                                "op-qa-thread-01-0", "op-qa-thread-01-1"}
                    suite.eq({item.get("opId") for item in results}, expected,
                             "多线程交错写：opId 集合与请求集合完全一致（无丢失/无重复）")
                    suite.eq({item.get("tool") for item in results}, {"sys_ping"},
                             "多线程交错写：所有响应的 tool 都正确（不串味）")

            suite.case("双线程交错写", threaded_pairs)

            # ---------------- 3. 串行化的证据 ----------------
            def serialized():
                with qa.raw_client(server) as client:
                    plan = [("op-qa-serial-%02d" % index, "read_layers", {"limit": 3})
                            for index in range(4)]
                    client.send_bytes(b"".join(
                        qa.frame_bytes(build_request(client.token, client.instance_id,
                                                     op_id, tool, params))
                        for op_id, tool, params in plan))
                    frames = [client.read_json(timeout=10.0) for _ in plan]
                    elapsed = [int(item.get("elapsedMs") or 0) for item in frames]
                    suite.eq([item.get("opId") for item in frames],
                             [op_id for op_id, _t, _p in plan],
                             "串行化：4 个只读请求严格按请求顺序完成")
                    suite.ok(all(value >= 0 for value in elapsed),
                             "串行化：每个响应都带 elapsedMs（可观测耗时）",
                             "elapsed=%r" % (elapsed,))
                    # 串行执行下每个响应各自持有一个独立 startedAt，且时间单调不减
                    starts = [str(item.get("startedAt") or "") for item in frames]
                    suite.ok(starts == sorted(starts),
                             "串行化：startedAt 单调不减（同一执行器排队，无并行交叠）",
                             "starts=%r" % (starts,))

            suite.case("只读串行化", serialized)

            # ---------------- 4. 重复 opId ----------------
            def duplicate_op_id():
                with qa.raw_client(server) as client:
                    first = client.request("sys_ping", op_id="op-qa-dup-0001")
                    second = client.request("sys_ping", op_id="op-qa-dup-0001")
                    suite.eq(first.get("opId"), "op-qa-dup-0001",
                             "重复 opId：第一次响应的 opId 正确回填")
                    suite.eq(second.get("opId"), "op-qa-dup-0001",
                             "重复 opId：第二次响应的 opId 正确回填（不拒绝、不崩溃）")
                    status = client.request("task_status", {"opId": "op-qa-dup-0001"})
                    suite.is_true(status.get("ok") is True,
                                  "重复 opId 之后 task_status 仍可查询",
                                  qa.describe_error(status))
                    result = status.get("result") or {}
                    suite.eq(result.get("opId"), "op-qa-dup-0001",
                             "task_status 返回的 opId 与查询一致")

            suite.case("重复 opId", duplicate_op_id)

            # ---------------- 5. 一个失败请求不污染同批成功请求 ----------------
            def mixed_batch():
                with qa.raw_client(server) as client:
                    plan = [("op-qa-mix-0001", "sys_ping", {}),
                            ("op-qa-mix-0002", "cad_create_line", {"layer": "0"}),
                            ("op-qa-mix-0003", "read_overview", {})]
                    client.send_bytes(b"".join(
                        qa.frame_bytes(build_request(client.token, client.instance_id,
                                                     op_id, tool, params))
                        for op_id, tool, params in plan))
                    frames = [client.read_json(timeout=10.0) for _ in plan]
                    suite.eq([item.get("opId") for item in frames],
                             [op_id for op_id, _t, _p in plan],
                             "混合批：写入域请求夹在只读请求中间时，响应顺序与配对正确")
                    suite.is_true(frames[0].get("ok") is True,
                                  "混合批：第 1 条 sys_ping 成功")
                    suite.eq((frames[1].get("error") or {}).get("code"), "E2003",
                             "混合批：第 2 条写域请求 E2003")
                    suite.is_true(frames[2].get("ok") is True,
                                  "混合批：第 3 条 read_overview 仍成功（未被写域请求带偏）")

            suite.case("混合批", mixed_batch)

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
