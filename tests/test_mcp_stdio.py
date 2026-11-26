"""stdio 脚本化客户端：WorkBuddy ↔ MCP 全链路验收（判据 3）。

黑盒方式：把 `mcp/engineering_mcp.py` 当独立进程启动，用行分隔 JSON-RPC 与它对话，
同时在本进程里跑 mockhost 作为「AutoCAD 宿主」。这条链路一旦通过，就等价于：

    stdio JSON-RPC → MCP 工具路由 → 参数校验 → ctypes 命名管道 → 宿主 → 语义结果
    → 预算截断 → 摘要/resultRef → 预览图内联 → 项目库落库 → 审计

覆盖：
    1. initialize（协议版本协商 + serverInfo + instructions）
    2. notifications/initialized → tools/list（16 个工具、inputSchema 完整）
    3. tools/call read_overview：content[0] 摘要 + content[1] resultRef + structuredContent
    4. tools/call read_layers / read_query / read_describe
    5. tools/call read_preview：content 含 image(base64 png)
    6. tools/call cad_create_line：isError=true + E2003（协议层仍是成功响应）
    7. tools/call 拼错工具名：E2001
    8. tools/call 参数非法：JSON-RPC -32602
    9. 调用前未 initialize：-32002
   10. 落库与审计：tasks 表有记录、日志无 token 明文
   11. shutdown：正常退出码 0
"""

from __future__ import annotations

import json
import os
import queue
import subprocess
import sys
import threading
import time

import harness

MCP_ENTRY = os.path.join(harness.MCP_DIR, "engineering_mcp.py")


class StdioClient:
    """最小 MCP stdio 客户端：写一行请求、按 id 收割响应。"""

    def __init__(self, process):
        self.process = process
        self._lines = queue.Queue()
        self.stderr_lines = []
        self._reader = threading.Thread(target=self._read_stdout, daemon=True)
        self._reader.start()
        self._stderr = threading.Thread(target=self._read_stderr, daemon=True)
        self._stderr.start()
        self._next_id = 1

    def _read_stdout(self):
        for raw in self.process.stdout:
            text = raw.decode("utf-8", "replace").rstrip("\r\n")
            if text.strip():
                self._lines.put(text)

    def _read_stderr(self):
        for raw in self.process.stderr:
            self.stderr_lines.append(raw.decode("utf-8", "replace").rstrip("\r\n"))

    def send(self, message: dict) -> None:
        payload = json.dumps(message, ensure_ascii=False)
        self.process.stdin.write((payload + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def request(self, method: str, params: dict | None = None, timeout: float = 20.0) -> dict:
        request_id = self._next_id
        self._next_id += 1
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method,
                   "params": params if params is not None else {}})
        deadline = time.time() + timeout
        while time.time() < deadline:
            remaining = max(0.05, deadline - time.time())
            try:
                line = self._lines.get(timeout=remaining)
            except queue.Empty:
                break
            message = json.loads(line)
            if message.get("id") == request_id:
                return message
        raise TimeoutError("no response for %s within %.1fs（stderr: %s）"
                           % (method, timeout, " | ".join(self.stderr_lines[-3:])))

    def notify(self, method: str, params: dict | None = None) -> None:
        self.send({"jsonrpc": "2.0", "method": method,
                   "params": params if params is not None else {}})

    def force_line(self, raw_line: str) -> None:
        """发一条任意文本行（用于测非法 JSON）。"""
        self.process.stdin.write((raw_line + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def read_any(self, timeout: float = 5.0):
        try:
            return json.loads(self._lines.get(timeout=timeout))
        except queue.Empty:
            return None


def _spawn(env):
    return subprocess.Popen(
        [sys.executable, "-u", MCP_ENTRY, "--no-heartbeat"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        cwd=harness.ROOT, env=env)


def run() -> dict:
    suite = harness.Suite("mcp_stdio")

    with harness.sandbox("stdio") as tmp:
        env = dict(os.environ)
        env["PYTHONIOENCODING"] = "utf-8"
        env["EP_MOCK"] = "1"
        env["EP_CONFIG"] = os.path.join(tmp, "config.json")

        with harness.mock_server() as (server, host):
            process = _spawn(env)
            client = StdioClient(process)
            try:
                # ---------- 1. initialize ----------
                def initialize():
                    response = client.request("initialize", {
                        "protocolVersion": "2025-06-18",
                        "capabilities": {},
                        "clientInfo": {"name": "workbuddy-test", "version": "0.0.1"},
                    })
                    result = response.get("result") or {}
                    suite.eq(result.get("protocolVersion"), "2025-06-18",
                             "initialize 协商到客户端请求的协议版本")
                    suite.eq((result.get("serverInfo") or {}).get("name"),
                             "engineering-mcp", "serverInfo.name=engineering-mcp")
                    suite.ok(isinstance(result.get("instructions"), str)
                             and bool(result["instructions"]),
                             "initialize 返回 instructions")
                    suite.eq((result.get("capabilities") or {}).get("tools"),
                             {"listChanged": False}, "capabilities.tools.listChanged=false")

                    # 不支持的版本 → 退到最近可用版本，不报错
                    response2 = client.request("initialize", {
                        "protocolVersion": "1999-01-01", "capabilities": {},
                        "clientInfo": {"name": "old", "version": "0"}})
                    suite.ok((response2.get("result") or {}).get("protocolVersion") in
                             ("2025-06-18", "2025-03-26", "2024-11-05"),
                             "未知协议版本协商到受支持的版本")

                    client.notify("notifications/initialized")

                # ---------- 2. tools/list ----------
                def tools_list():
                    response = client.request("tools/list", {})
                    tools = (response.get("result") or {}).get("tools") or []
                    suite.is_true(len(tools) >= 18,
                                  "tools/list 返回 ≥18 个工具（数量不写死）",
                                  "actual=%d" % len(tools))
                    names = [tool.get("name") for tool in tools]
                    for expected in ("read_overview", "read_layers", "read_query",
                                     "read_describe", "read_preview", "read_anomalies",
                                     "sys_ping", "sys_health", "task_reconcile"):
                        suite.contains(names, expected, "tools/list 含 %s" % expected)
                    suite.is_false(any(name.startswith(("cad_", "civil_", "dyn_"))
                                       for name in names),
                                   "tools/list 不含任何写工具")
                    not_readonly = []
                    for tool in tools:
                        suite.ok(isinstance(tool.get("inputSchema"), dict),
                                 "%s 带 inputSchema" % tool.get("name"))
                        annotations = tool.get("annotations") or {}
                        suite.is_false(annotations.get("destructiveHint", False),
                                       "%s destructiveHint=false" % tool.get("name"))
                        suite.ok("idempotentHint" in annotations,
                                 "%s 带 idempotentHint" % tool.get("name"))
                        if annotations.get("readOnlyHint") is not True:
                            not_readonly.append(tool.get("name"))
                    # 只有 task_cancel 会改任务状态，因此允许它不是只读
                    # M1：task_cancel 改任务状态；write_approve / write_insert_points 是写通道
                    # （write_preview 不改图，仍应是只读）
                    suite.eq(sorted(not_readonly),
                             sorted(["batch_side_database", "task_cancel", "write_alignment_plan",
                                     "write_approve", "write_command", "write_entities", "write_eval_csharp",
                                     "write_insert_points", "write_object_xdata",
                                     "write_surface_breakline", "write_undo"]),
                             "非只读工具与 v3.0 schema 声明完全一致（write_preview/export/preview 仍只读）")

                # ---------- 3. read_overview ----------
                def call_overview():
                    response = client.request("tools/call", {
                        "name": "read_overview", "arguments": {}})
                    result = response.get("result") or {}
                    suite.is_false(result.get("isError"), "read_overview isError=false")
                    content = result.get("content") or []
                    suite.is_true(len(content) >= 2, "content 至少 2 块（摘要 + resultRef）")
                    suite.eq((content[0] or {}).get("type"), "text", "content[0] 是文本摘要")
                    suite.is_true(len(content[0].get("text") or "") > 10,
                                  "content[0] 摘要非空")
                    ref_block = json.loads(content[1]["text"])
                    ref = ref_block.get("resultRef") or {}
                    suite.eq(ref.get("tool"), "read_overview", "resultRef.tool 正确")
                    suite.matches(ref.get("fingerprint") or "", r"^fp1:[0-9a-f]{32}$",
                                  "resultRef.fingerprint 合法")
                    suite.matches(ref.get("storedAt") or "",
                                  r"^projects/default\.db#results/\d+$",
                                  "resultRef.storedAt 已落库（项目库引用）")

                    structured = result.get("structuredContent") or {}
                    suite.ok(structured.get("counts"), "structuredContent 含 counts")
                    suite.eq(structured.get("units", {}).get("code"), "mm",
                             "structuredContent.units.code=mm")

                # ---------- 4. 下钻工具 ----------
                def call_drilldown():
                    layers = client.request("tools/call", {
                        "name": "read_layers", "arguments": {"limit": 5}})
                    data = (layers.get("result") or {}).get("structuredContent") or {}
                    suite.eq(len(data.get("layers") or []), 5,
                             "read_layers 经 MCP 返回 5 个图层")

                    query = client.request("tools/call", {
                        "name": "read_query",
                        "arguments": {"attrEquals": {"编号": "J1"}, "limit": 1}})
                    qdata = (query.get("result") or {}).get("structuredContent") or {}
                    items = qdata.get("items") or []
                    suite.eq(len(items), 1, "read_query 命中 1 个检查井")
                    handle = items[0]["handle"]

                    describe = client.request("tools/call", {
                        "name": "read_describe", "arguments": {"handle": handle}})
                    ddata = (describe.get("result") or {}).get("structuredContent") or {}
                    suite.eq((ddata.get("object") or {}).get("handle"), handle,
                             "read_describe 返回同一对象")

                    anomalies = client.request("tools/call", {
                        "name": "read_anomalies", "arguments": {"limit": 10}})
                    adata = (anomalies.get("result") or {}).get("structuredContent") or {}
                    suite.ok(adata.get("counts"), "read_anomalies 返回分类计数")

                # ---------- 5. read_preview ----------
                def call_preview():
                    response = client.request("tools/call", {
                        "name": "read_preview",
                        "arguments": {"width": 640, "height": 400}}, timeout=30.0)
                    result = response.get("result") or {}
                    suite.is_false(result.get("isError"), "read_preview isError=false")
                    content = result.get("content") or []
                    images = [block for block in content
                              if isinstance(block, dict) and block.get("type") == "image"]
                    suite.eq(len(images), 1, "content 含 1 个 image 块")
                    if images:
                        image = images[0]
                        suite.eq(image.get("mimeType"), "image/png", "image.mimeType=image/png")
                        data = image.get("data") or ""
                        suite.is_true(len(data) > 100, "image.data 非空（base64）")
                        import base64
                        raw = base64.b64decode(data)
                        suite.eq(raw[:8], b"\x89PNG\r\n\x1a\n", "内联图片是合法 PNG")
                    structured = result.get("structuredContent") or {}
                    suite.eq(structured.get("note"), "结构示意图，非出图成果",
                             "预览图 note 恒为「结构示意图，非出图成果」")

                # ---------- 6-8. 门禁 ----------
                def gates():
                    write_call = client.request("tools/call", {
                        "name": "cad_create_line",
                        "arguments": {"layer": "0", "start": [0, 0, 0], "end": [1, 1, 0]}})
                    wresult = write_call.get("result") or {}
                    suite.is_true(wresult.get("isError"), "写工具 tools/call isError=true")
                    suite.ok("result" in write_call, "写工具仍是成功的 JSON-RPC 响应（非协议错误）")
                    text = json.dumps(wresult, ensure_ascii=False)
                    suite.contains(text, "E2003", "写工具错误码 E2003")

                    unknown = client.request("tools/call", {
                        "name": "read_overveiw", "arguments": {}})
                    utext = json.dumps(unknown.get("result") or {}, ensure_ascii=False)
                    suite.contains(utext, "E2001", "拼错工具名 → E2001")

                    invalid = client.request("tools/call", {
                        "name": "read_layers", "arguments": {"limit": 999}})
                    suite.eq((invalid.get("error") or {}).get("code"), -32602,
                             "参数越界 → JSON-RPC -32602")
                    suite.ok((invalid.get("error") or {}).get("data", {}).get("errors"),
                             "参数错误带 errors 明细")

                # ---------- 9. 非法 JSON 与未知方法 ----------
                def protocol_errors():
                    client.force_line("{not json")
                    message = client.read_any(timeout=5.0)
                    suite.eq((message or {}).get("error", {}).get("code"), -32700,
                             "非法 JSON → -32700 Parse error")

                    unknown_method = client.request("no/such/method", {})
                    suite.eq((unknown_method.get("error") or {}).get("code"), -32601,
                             "未知方法 → -32601")

                # ---------- 10. 落库与审计 ----------
                def storage_verification():
                    import sqlite3
                    db_path = os.path.join(tmp, "EngineeringPlatform", "projects", "default.db")
                    deadline = time.time() + 5.0
                    while time.time() < deadline and not os.path.isfile(db_path):
                        time.sleep(0.05)
                    suite.is_true(os.path.isfile(db_path), "MCP 项目库文件已创建", db_path)
                    connection = sqlite3.connect(db_path)
                    try:
                        tasks = connection.execute(
                            "SELECT COUNT(*) FROM tasks").fetchone()[0]
                        results = connection.execute(
                            "SELECT COUNT(*) FROM results").fetchone()[0]
                        audits = connection.execute(
                            "SELECT COUNT(*) FROM audit").fetchone()[0]
                        objects = connection.execute(
                            "SELECT COUNT(*) FROM objects").fetchone()[0]
                    finally:
                        connection.close()
                    suite.is_true(tasks >= 4, "tasks 表有记录（≥4 次工具调用）",
                                  "tasks=%d" % tasks)
                    suite.is_true(results >= 4, "results 表有记录", "results=%d" % results)
                    suite.is_true(audits >= 4, "audit 表有记录", "audit=%d" % audits)
                    suite.is_true(objects >= 1, "objects 表有懒写入记录", "objects=%d" % objects)

                    log_dir = os.path.join(tmp, "EngineeringPlatform", "logs")
                    content = ""
                    if os.path.isdir(log_dir):
                        for name in os.listdir(log_dir):
                            with open(os.path.join(log_dir, name), "r",
                                      encoding="utf-8") as handle:
                                content += handle.read()
                    suite.is_true(bool(content), "MCP 日志已写入")
                    suite.is_false(host.session_token in content,
                                   "日志中不含 sessionToken 明文")

                # ---------- 11. shutdown ----------
                def shutdown():
                    response = client.request("shutdown", {})
                    suite.is_true("result" in response, "shutdown 返回结果")
                    try:
                        code = process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        code = -1
                    suite.eq(code, 0, "MCP 进程以退出码 0 结束")

                for label, func in (
                    ("initialize", initialize),
                    ("tools/list", tools_list),
                    ("read_overview", call_overview),
                    ("下钻工具", call_drilldown),
                    ("read_preview", call_preview),
                    ("门禁", gates),
                    ("协议错误", protocol_errors),
                    ("落库与审计", storage_verification),
                    ("shutdown", shutdown),
                ):
                    suite.case(label, func)
            finally:
                if process.poll() is None:
                    process.kill()
                try:
                    process.stdin.close()
                except Exception:
                    pass

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
