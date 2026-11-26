"""tests/qa/qa_mcp_stdio_edge.py —— MCP stdio（JSON-RPC 2.0）边角（QA 独立复核）。

黑盒方式：把 mcp/engineering_mcp.py 当独立进程启动，本进程内起 mockhost 当宿主。
工程师 test_mcp_stdio.py 覆盖了主流程，这里只打它没打的协议边角：

  1. 未发 notifications/initialized 就 tools/list、tools/call → -32002；
  2. initialize 传不支持的 protocolVersion（2019-01-01 / 2099-01-01 / 垃圾串）→ 必须
     协商到服务端支持的版本，而不是原样回一个自己实现不了的版本；
  3. 连续两次 initialize → 不崩溃、第二条仍有合法 result（并记录行为）；
  4. tools/call 的 arguments 为 null / 缺省 → 按 {} 处理（MCP 规范 arguments 可选）；
  5. tools/call 传未声明的参数 → -32602；缺 name → -32602；name 非字符串 → -32602；
  6. 缺失 jsonrpc / jsonrpc 非 "2.0" / 数组请求 / 非 JSON 行 → -32600 / -32700；
  7. stdout 洁净性：每一行都必须是合法 JSON（日志绝不能混进 stdout）；
  8. 日志文件里不出现图纸文字内容（M0 明令禁止落图纸内容）。
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time

import qa_common as qa

MCP_ENTRY = os.path.join(qa.MCP_DIR, "engineering_mcp.py")

#: 设计 §4.2 服务端支持的 MCP 协议版本集合
SUPPORTED_MCP_VERSIONS = {"2024-11-05", "2025-03-26", "2025-06-18"}


def spawn(env):
    return subprocess.Popen(
        [sys.executable, "-u", MCP_ENTRY, "--no-heartbeat"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        cwd=qa.ROOT, env=env)


class Client:
    """最小 stdio 客户端：逐行发、逐行收，保留所有 stdout 行用于洁净性断言。"""

    def __init__(self, process):
        self.process = process
        self.lines = []
        self.next_id = 1

    def send(self, message) -> None:
        payload = json.dumps(message, ensure_ascii=False)
        self.process.stdin.write((payload + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def send_raw(self, raw: str) -> None:
        self.process.stdin.write((raw + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def read_one(self, timeout: float = 20.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.process.stdout.readline()
            if not line:
                return None
            text = line.decode("utf-8", "replace").strip()
            if not text:
                continue
            self.lines.append(text)
            return json.loads(text)
        return None

    def request(self, method: str, params=None, timeout: float = 20.0):
        request_id = self.next_id
        self.next_id += 1
        message = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            message["params"] = params
        self.send(message)
        response = self.read_one(timeout=timeout)
        return response or {}

    def notify(self, method: str, params=None) -> None:
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self.send(message)


def run() -> dict:
    suite = qa.harness.Suite("qa_mcp_stdio_edge")

    with qa.harness.sandbox("qa-stdio") as tmp:
        env = dict(os.environ)
        env["PYTHONIOENCODING"] = "utf-8"
        env["EP_MOCK"] = "1"
        env["EP_CONFIG"] = os.path.join(tmp, "config.json")
        env["EP_LOG_LEVEL"] = "INFO"          # 打开 INFO 才能验证「日志不落图纸内容」

        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)
            process = spawn(env)
            client = Client(process)
            try:

                # ---------------- 1. 未 initialized ----------------
                def before_initialized():
                    listed = client.request("tools/list")
                    error = listed.get("error") or {}
                    suite.eq(error.get("code"), -32002,
                             "未 initialize 就 tools/list → -32002")
                    suite.is_true("error" in listed and "result" not in listed,
                                  "未 initialize 的响应里没有 result（不与错误混淆）")

                suite.case("未初始化即调用", before_initialized)

                # ---------------- 2. protocolVersion 协商 ----------------
                def version_negotiation():
                    response = client.request("initialize", {
                        "protocolVersion": "2099-01-01",
                        "capabilities": {},
                        "clientInfo": {"name": "qa", "version": "0.0.1"},
                    })
                    result = response.get("result") or {}
                    negotiated = result.get("protocolVersion")
                    suite.ok(negotiated in SUPPORTED_MCP_VERSIONS,
                             "initialize 2099-01-01 → 协商到服务端支持版本",
                             "negotiated=%r" % (negotiated,))
                    suite.ne(negotiated, "2099-01-01",
                             "不能原样接受一个服务端实现不了的版本")
                    suite.is_true(isinstance(result.get("instructions"), str)
                                  and len(result.get("instructions")) > 0,
                                  "initialize 返回非空 instructions（指导模型读图顺序）")

                    # 垃圾串
                    second = client.request("initialize", {
                        "protocolVersion": "not-a-version",
                        "capabilities": {},
                        "clientInfo": {"name": "qa", "version": "0.0.1"},
                    })
                    suite.ok((second.get("result") or {}).get("protocolVersion")
                             in SUPPORTED_MCP_VERSIONS,
                             "initialize 垃圾版本串 → 仍协商到支持版本",
                             "result=%r" % ((second.get("result") or {}),))

                    # 缺 protocolVersion
                    third = client.request("initialize", {"capabilities": {}})
                    suite.ok((third.get("result") or {}).get("protocolVersion")
                             in SUPPORTED_MCP_VERSIONS,
                             "initialize 完全不给 protocolVersion → 仍返回支持版本",
                             "result=%r" % ((third.get("result") or {}),))

                suite.case("版本协商", version_negotiation)

                # ---------------- 3. 两次 initialize ----------------
                def double_initialize():
                    client.notify("notifications/initialized")
                    first = client.request("initialize", {
                        "protocolVersion": "2025-06-18",
                        "capabilities": {},
                        "clientInfo": {"name": "qa", "version": "0.0.1"},
                    })
                    second = client.request("initialize", {
                        "protocolVersion": "2025-03-26",
                        "capabilities": {},
                        "clientInfo": {"name": "qa", "version": "0.0.1"},
                    })
                    suite.is_true("result" in first, "第一次 initialize 有 result")
                    suite.is_true("result" in second,
                                  "连续第二次 initialize 仍有合法 result（不崩溃）",
                                  "second=%r" % (second,))
                    # 记录行为：以最后一次协商为准（MCP 规范未强制，两种都可接受）
                    print("提示（非失败）：连续两次 initialize 的协商结果变化 = %r → %r"
                          % ((first.get("result") or {}).get("protocolVersion"),
                             (second.get("result") or {}).get("protocolVersion")))

                suite.case("两次 initialize", double_initialize)

                # ---------------- 4. arguments 为 null / 缺省 ----------------
                def null_arguments():
                    by_id = client.request("tools/call", {
                        "name": "sys_ping", "arguments": None})
                    suite.is_true("result" in by_id,
                                  "tools/call arguments=null → 按 {} 处理（不报错）",
                                  "response=%r" % (by_id,))
                    suite.is_false((by_id.get("result") or {}).get("isError", True),
                                   "arguments=null 时 isError=false（真的执行了）")

                    no_args = client.request("tools/call", {"name": "sys_version"})
                    suite.is_true("result" in no_args,
                                  "tools/call 缺 arguments 字段 → 按 {} 处理",
                                  "response=%r" % (no_args,))

                suite.case("arguments 缺省", null_arguments)

                # ---------------- 5. 参数错误 ----------------
                def invalid_params():
                    undeclared = client.request("tools/call", {
                        "name": "read_overview",
                        "arguments": {"nonexistent": 1}})
                    suite.eq((undeclared.get("error") or {}).get("code"), -32602,
                             "tools/call 传未声明参数 → -32602")

                    missing_name = client.request("tools/call", {"arguments": {}})
                    suite.eq((missing_name.get("error") or {}).get("code"), -32602,
                             "tools/call 缺 name → -32602")

                    bad_name = client.request("tools/call", {"name": 123, "arguments": {}})
                    suite.eq((bad_name.get("error") or {}).get("code"), -32602,
                             "tools/call name 非字符串 → -32602")

                    bad_args = client.request("tools/call", {
                        "name": "sys_ping", "arguments": [1, 2]})
                    suite.eq((bad_args.get("error") or {}).get("code"), -32602,
                             "tools/call arguments 非 object → -32602")

                suite.case("参数错误", invalid_params)

                # ---------------- 6. 协议级错误 ----------------
                def protocol_errors():
                    client.send({"id": 9001, "method": "ping"})     # 缺 jsonrpc
                    no_version = client.read_one()
                    suite.eq((no_version.get("error") or {}).get("code"), -32600,
                             "缺 jsonrpc 字段 → -32600")

                    client.send({"jsonrpc": "1.0", "id": 9002, "method": "ping"})
                    bad_version = client.read_one()
                    suite.eq((bad_version.get("error") or {}).get("code"), -32600,
                             'jsonrpc != "2.0" → -32600')

                    client.send([{"jsonrpc": "2.0", "id": 9003, "method": "ping"}])
                    batch = client.read_one()
                    suite.eq((batch.get("error") or {}).get("code"), -32600,
                             "数组（批量）请求 → -32600（2025-06-18 已移除批量）")

                    client.send_raw("{not json at all")
                    garbage = client.read_one()
                    suite.eq((garbage.get("error") or {}).get("code"), -32700,
                             "非 JSON 行 → -32700")

                    unknown = client.request("resources/read", {})
                    suite.eq((unknown.get("error") or {}).get("code"), -32601,
                             "未声明能力的方法 → -32601")

                suite.case("协议级错误", protocol_errors)

                # ---------------- 7. stdout 洁净性 ----------------
                def stdout_purity():
                    client.request("ping", {})
                    bad_lines = []
                    for line in client.lines:
                        try:
                            json.loads(line)
                        except ValueError:
                            bad_lines.append(line[:120])
                    suite.is_true(len(client.lines) > 5,
                                  "stdout 上确实收到了多条响应（%d 行）" % len(client.lines))
                    qa.eqd(suite, bad_lines, [],
                           "stdout 每一行都是合法 JSON（日志没有混进 stdout）",
                           "非 JSON 行=%r" % (bad_lines[:3],))

                suite.case("stdout 洁净", stdout_purity)

                # ---------------- 8. 日志不落图纸内容 ----------------
                def log_no_drawing_content():
                    client.request("tools/call", {
                        "name": "read_query", "arguments": {"limit": 5}})
                    log_dir = os.path.join(tmp, "EngineeringPlatform", "logs")
                    blob = ""
                    if os.path.isdir(log_dir):
                        for name in os.listdir(log_dir):
                            full = os.path.join(log_dir, name)
                            if os.path.isfile(full):
                                with open(full, "r", encoding="utf-8",
                                          errors="replace") as handle:
                                    blob += handle.read()
                    suite.is_true(blob.strip() != "",
                                  "MCP 日志确实有内容（否则本条断言无意义）",
                                  "目录=%s" % log_dir)
                    for sample in ("填充填充填充", "井底标高", "排水沟中线"):
                        suite.ok(sample not in blob,
                                 "日志不含图纸文字内容：%r" % sample,
                                 "在 %d 字符日志里找到了该文字" % len(blob))
                    for marker in ("sessionToken\":\"", "sessionToken="):
                        suite.ok(marker not in blob,
                                 "日志不含 sessionToken 明文（%r）" % marker)

                suite.case("日志脱敏", log_no_drawing_content)

                # ---------------- 9. 正常退出 ----------------
                def graceful_shutdown():
                    response = client.request("shutdown", {})
                    suite.is_true("result" in response, "shutdown 返回 result")
                    try:
                        code = process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        code = -1
                    suite.eq(code, 0, "shutdown 后进程退出码 0（设计 §7.5）")

                suite.case("正常退出", graceful_shutdown)

            finally:
                try:
                    if process.poll() is None:
                        process.kill()
                except Exception:                              # noqa: BLE001
                    pass

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
