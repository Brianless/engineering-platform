"""全局一致性静态审查（判据 4，机器化而非人工 grep）。

这些约束一旦破防，功能测试仍然是绿的，但架构已经烂了，所以必须自动化：

    1. `host/Pipe/**` 与 `host/Protocol/**` 不得出现任何 AutoCAD 类型
       （管道/协议层必须能在无 CAD 的机器上编译与测试）；
    2. 写域不可达 CAD API：不存在写工具 handler，写域一律在路由层被 E2003 拦下；
       且 `protocol/tools.schema.json` 与宿主 `writeEnabled` 恒为 false；
    3. 日志不得出现 sessionToken 明文（`_sessionToken` 只能经 MaskToken 落日志）；
    4. 不得硬编码绝对路径（`C:\\...` / `/home/...`）；
    5. 不得用本地时间（`DateTime.Now`），只能用 UtcNow；
    6. `JavaScriptSerializer` 只能出现在 `host/Protocol/Json.cs`；
    7. 源码里不得留 TODO / NotImplementedException / 空实现占位；
    8. Python 侧只允许标准库 + 本地模块（零第三方依赖）；
    9. 线程模型：管道接收线程不得调用 `ToolRouter` 之外的 CAD 语义提取
       （读线程只入队，不调 CAD —— 用调用点断言）。
"""

from __future__ import annotations

import json
import os
import re
import sys

import harness

ROOT = harness.ROOT
HOST_DIR = os.path.join(ROOT, "host")
MCP_DIR = os.path.join(ROOT, "mcp")
MOCK_DIR = os.path.join(ROOT, "mockhost")
TESTS_DIR = os.path.join(ROOT, "tests")
#: N03 新增的适配层（PipeGuard）。与 mcp/ 同等对待：只许标准库、不许任何启动痕迹。
BRIDGE_DIR = os.path.join(ROOT, "bridge")

AUTODESK_NAMES = ("Autodesk", "acmgd", "acdbmgd", "accoremgd", "AeccDbMgd",
                  "CivilApplication", "Database ", "Transaction")


def _walk(directory, extensions):
    """遍历目录下指定后缀的文件（跳过 bin/obj）。"""
    result = []
    for base, dirs, files in os.walk(directory):
        dirs[:] = [name for name in dirs if name not in ("bin", "obj", "__pycache__")]
        for name in files:
            if name.endswith(extensions):
                result.append(os.path.join(base, name))
    return sorted(result)


def strip_comments_and_strings(text):
    """剥掉 Python 的 **注释与字符串字面量**，只留真实代码文本（N04-A 修正假阳性）。

    为什么需要它：`no_process_launch` 早期用「子串出现」判定，结果把**为了说明规则而写在注释/文档串里的禁词**
    也判成违规（QA 的 D13 探针专门打这个假阳性）。规则越加越多，假阳性堆积会把真阳性淹没。

    做法：用 `tokenize` 精确识别 `COMMENT` / `STRING` token，把它们的**原始字符区间替换成空格** ——
    这样保留其余代码的原始相邻关系（`acad.exe` 这种「名字+点+名字」不会被插入分隔符破坏），
    只是在被剥掉的位置留空。tokenize 失败（例如刻意构造的坏文件）时退回字符级扫描。

    **取舍（明确记下来）**：字符串里的宿主名不再算违规（`Popen("acad.exe")` 中的 "acad.exe" 会被剥掉）。
    这是有意的：判定目标是「**真实代码**里有没有启动能力」，而 `Popen(` 这种调用点会保留下来并被另一条规则抓住
    （见 `no_process_launch` 的正控自测）。
    """
    try:
        import io as _io
        import tokenize as _tokenize

        lines = text.splitlines(keepends=True)
        offsets = [0]
        for line in lines:
            offsets.append(offsets[-1] + len(line))
        chars = list(text)
        for token in _tokenize.generate_tokens(_io.StringIO(text).readline):
            if token.type not in (_tokenize.COMMENT, _tokenize.STRING):
                continue
            start = offsets[token.start[0] - 1] + token.start[1]
            end = offsets[token.end[0] - 1] + token.end[1]
            for index in range(start, min(end, len(chars))):
                if chars[index] not in ("\n", "\r"):
                    chars[index] = " "
        return "".join(chars)
    except Exception:
        return _strip_comments_fallback(text)


def _strip_comments_fallback(text):
    """字符级兜底：去掉 `#` 之后的行内注释（保留字符串内容）。tokenize 不可用时使用。"""
    out = []
    index = 0
    quote = None
    length = len(text)
    while index < length:
        char = text[index]
        if quote:
            out.append(char)
            if char == "\\" and index + 1 < length:
                out.append(text[index + 1])
                index += 2
                continue
            if char == quote:
                quote = None
            index += 1
            continue
        if char in ("'", '"'):
            quote = char
            out.append(char)
            index += 1
            continue
        if char == "#":
            while index < length and text[index] != "\n":
                index += 1
            continue
        out.append(char)
        index += 1
    return "".join(out)


#: `bridge/relay_launcher.py` 里**唯一**允许出现的、其文本含 `acad` 字样的字符串字面量。
#:
#: 为什么需要这份白名单：vendored 上游目录名 `new-acad` 本身就含 `acad`，而 launcher 必须引用
#: `<repo>/third_party/new-acad/server/panel-relay.js`。裸 `acad` 子串判定会把这条合法引用判成
#: 「启动宿主」。**但豁免粒度必须精确到字符串字面量的完整取值**：
#:
#: * 若写成 `text.replace("new-acad", "")`（本文件曾经的写法）→
#:   `Popen("D:/x-new-acad/evil.exe")` 里的 `new-acad` 被一起抹掉，`acad` 随之消失 ⇒
#:   「启动一个路径里带 acad 的可执行文件」逃过断言（**假阴性，安全洞**）；
#: * 精确到取值 ⇒ 只有**恰好等于**下列路径分段之一的字符串被抹掉，
#:   `"D:/x-new-acad/evil.exe"` 的取值不在名单里，原样保留 ⇒ `acad` 仍在 ⇒ 断言失败（真阳性）。
#:
#: 名单里**只放路径分段、不放任何含分隔符的整串**，避免「长前缀豁免」再次放大口径。
WHITELISTED_LAUNCHER_LITERALS = frozenset({
    "third_party",
    "new-acad",
    "server",
    "panel-relay.js",
})


def _offset_map(text):
    """行号/列号 → 字符下标的映射（供 token/ast 的位置换算用）。"""
    offsets = [0]
    for line in text.splitlines(keepends=True):
        offsets.append(offsets[-1] + len(line))
    return offsets


def _blank(chars, start, end):
    """把 `[start, end)` 区间抹成空格，但保留换行（保持行号不变）。"""
    for index in range(max(0, start), min(end, len(chars))):
        if chars[index] not in ("\n", "\r"):
            chars[index] = " "


def _docstring_spans(text):
    """返回所有 docstring（模块/类/函数的第一条语句）的 `(start, end)` 字符区间。

    docstring 是**文档**，不是可执行代码：launcher 的模块文档里写着
    `<repo>/third_party/new-acad/server/panel-relay.js` 这条路径，那是给人看的，不能算违规。
    用 `ast` 精确区分 docstring 与「真在代码里被使用的字符串」——后者一律不豁免。
    """
    import ast

    spans = []
    tree = ast.parse(text)
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            continue
        body = getattr(node, "body", None) or []
        if not body:
            continue
        first = body[0]
        value = getattr(first, "value", None)
        if not (isinstance(first, ast.Expr) and isinstance(value, ast.Constant)
                and isinstance(value.value, str)):
            continue
        end_line = getattr(value, "end_lineno", None)
        end_col = getattr(value, "end_col_offset", None)
        if end_line is None or end_col is None:      # Python < 3.8 无结束位置
            continue
        spans.append((value.lineno, value.col_offset, end_line, end_col))
    return spans


def scrub_launcher_text(text, allowed=WHITELISTED_LAUNCHER_LITERALS):
    """为「launcher 不许出现 `acad` / `.dwg`」这条断言做**白名单式精确剥词**。

    只抹掉三类文本，其余原样保留：

    1. `COMMENT` token（注释不可能启动进程）；
    2. docstring（文档里的路径是给人看的）；
    3. 取值**恰好命中 `allowed`** 的字符串字面量（vendored 路径分段）。

    返回 `(scrubbed, stats)`，`stats` 里 `literalHits` 是白名单命中次数 ——
    调用方必须断言它非 0，否则「白名单没生效」会让后面的 `acad` 断言假红（而不是假绿），
    这条断言只是让原因可读。

    **fail-closed**：`tokenize`/`ast` 失败时返回 `(text, hits=0)` 原样文本，
    即放弃全部豁免 → 后面若真有 `acad` 字样就会红。宁可误红，不可假绿。
    """
    stats = {"literalHits": 0, "comments": 0, "docstrings": 0}
    try:
        import ast
        import io as _io
        import tokenize as _tokenize

        offsets = _offset_map(text)
        chars = list(text)

        def to_offsets(line, col):
            return offsets[line - 1] + col

        # ② docstring
        ast.parse(text)                       # 语法不合法就抛，走 fail-closed
        for start_line, start_col, end_line, end_col in _docstring_spans(text):
            _blank(chars, to_offsets(start_line, start_col), to_offsets(end_line, end_col))
            stats["docstrings"] += 1

        # ① 注释 + ③ 白名单字面量
        for token in _tokenize.generate_tokens(_io.StringIO(text).readline):
            if token.type == _tokenize.COMMENT:
                _blank(chars, to_offsets(*token.start), to_offsets(*token.end))
                stats["comments"] += 1
                continue
            if token.type != _tokenize.STRING:
                continue
            try:
                value = ast.literal_eval(token.string)
            except (ValueError, SyntaxError):
                continue                      # 不认识的写法一律不豁免（fail-closed）
            if isinstance(value, str) and value in allowed:
                _blank(chars, to_offsets(*token.start), to_offsets(*token.end))
                stats["literalHits"] += 1
        return "".join(chars), stats
    except Exception:
        return text, stats


def _read(path):
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def _rel(path):
    return os.path.relpath(path, ROOT).replace(os.sep, "/")


def run() -> dict:
    suite = harness.Suite("static_audit")

    cs_files = _walk(HOST_DIR, (".cs",))
    py_files = (_walk(MCP_DIR, (".py",)) + _walk(MOCK_DIR, (".py",))
                + _walk(BRIDGE_DIR, (".py",)) + _walk(TESTS_DIR, (".py",)))
    suite.is_true(len(cs_files) >= 20, "扫描到 C# 源文件", "count=%d" % len(cs_files))
    suite.is_true(len(py_files) >= 12, "扫描到 Python 源文件", "count=%d" % len(py_files))

    # ---------- 1. 管道层不得依赖 AutoCAD；协议层只允许 ToolRouter 触达 CAD ----------
    def pipe_protocol_no_cad():
        pipe_files = _walk(os.path.join(HOST_DIR, "Pipe"), (".cs",))
        suite.is_true(len(pipe_files) >= 3, "扫描到 Pipe 源文件", "count=%d" % len(pipe_files))
        for path in pipe_files:
            text = _read(path)
            for token in AUTODESK_NAMES:
                if token in text:
                    suite.fail("%s 出现 AutoCAD 依赖 %r" % (_rel(path), token))
        suite.ok(True, "Pipe/** 无任何 AutoCAD / Civil 3D 类型引用（可在无 CAD 机器上编译）")

        # 协议层：分帧/封套/握手/摘要/目录都必须与 CAD 无关；
        # ToolRouter 是唯一的例外（它持有 Document/Database 上下文，必须有 CAD 引用）。
        allowed_cad_file = "host/Protocol/ToolRouter.cs"
        protocol_files = _walk(os.path.join(HOST_DIR, "Protocol"), (".cs",))
        for path in protocol_files:
            if _rel(path) == allowed_cad_file:
                continue
            text = _read(path)
            if "using Autodesk" in text:
                suite.fail("%s 出现 using Autodesk（协议层不得编译期依赖 CAD）" % _rel(path))
        suite.ok(True, "Protocol/** 除 ToolRouter 外无 using Autodesk")

        # Handshake.cs 允许出现 Autodesk 字样，但只能作为反射探测的字符串
        handshake = _read(os.path.join(HOST_DIR, "Protocol", "Handshake.cs"))
        if "Autodesk" in handshake:
            suite.contains(handshake, "Type.GetType", "Handshake 里的 Autodesk 只用于反射探测")
            suite.is_false("using Autodesk" in handshake,
                           "Handshake.cs 无 using Autodesk（只靠 Type.GetType 字符串）")

        # 调度层：只有「主线程派发器」与「主线程状态探针」允许触达 CAD
        # （两者都只在 Application.Idle 回调里被调用；读线程不得引用）
        main_thread_files = {"MainThreadDispatcher.cs", "HostStateProbe.cs"}
        for path in _walk(os.path.join(HOST_DIR, "Scheduling"), (".cs",)):
            text = _read(path)
            if os.path.basename(path) in main_thread_files:
                continue
            if "using Autodesk" in text:
                suite.fail("%s 出现 using Autodesk" % _rel(path))
        suite.ok(True, "Scheduling/ 只有主线程派发器与状态探针引用 CAD")
        probe = _read(os.path.join(HOST_DIR, "Scheduling", "HostStateProbe.cs"))
        suite.contains(probe, "CMDACTIVE", "状态探针检查 CMDACTIVE（有命令在跑 → E1003）")

    # ---------- 2. 写域不可达 CAD API ----------
    def write_channel_closed():
        with open(os.path.join(ROOT, "protocol", "tools.schema.json"), "r",
                  encoding="utf-8") as handle:
            schema = json.load(handle)
        offenders = [tool["name"] for tool in schema["tools"]
                     if tool["name"].startswith(("cad_", "civil_", "dyn_", "file_",
                                                 "rev_", "p3d_", "calc_", "skill_"))]
        suite.eq(offenders, [], "tools.schema.json 不含任何写工具")

        # 写 handler 文件：M1 起**允许且仅允许** WriteToolHandlers.cs（审批写通道三步）。
        # 其它任何 write/create/modify/delete 处理器都视为绕过审批的旁路。
        tools_dir = os.path.join(HOST_DIR, "Tools")
        allowed_write_handlers = {"WriteToolHandlers.cs", "WriteToolHandlers.cs"}
        for name in os.listdir(tools_dir):
            if name.lower().startswith(("write", "create", "modify", "delete")):
                suite.is_true(name in allowed_write_handlers,
                              "Tools/ 下的写 handler 只能是 M1 审批写通道（%s）" % name)
        suite.is_true(os.path.exists(os.path.join(tools_dir, "WriteToolHandlers.cs")),
                      "M1 写通道 handler 存在（WriteToolHandlers.cs）")

        router = _read(os.path.join(HOST_DIR, "Protocol", "ToolRouter.cs"))
        suite.contains(router, "E2003", "ToolRouter 对写域返回 E2003")
        suite.contains(router, "IsWriteDomain", "ToolRouter 先判写域再判未知工具")

        catalog = _read(os.path.join(HOST_DIR, "Protocol", "ToolCatalog.cs"))
        suite.contains(catalog, "WriteDomainPrefixes", "ToolCatalog 定义写域前缀表")
        for prefix in ("cad_", "civil_", "dyn_", "file_", "rev_", "p3d_", "calc_", "skill_"):
            suite.contains(catalog, '"%s"' % prefix, "写域前缀含 %s" % prefix)

        # MCP 侧同样有本地门禁
        mcp_source = _read(os.path.join(MCP_DIR, "engineering_mcp.py"))
        suite.contains(mcp_source, "is_write_domain", "MCP 侧先拦截写域（双层门禁）")

    # ---------- 3. 日志无 token 明文 ----------
    def token_never_logged():
        for path in cs_files:
            text = _read(path)
            for number, line in enumerate(text.splitlines(), start=1):
                if "HostLog." not in line:
                    continue
                if "_sessionToken" not in line:
                    continue
                # 落日志的令牌必须经过 HostLog.Mask（打码为前 4 位 + ***）
                if "HostLog.Mask(" not in line:
                    suite.fail("%s:%d 直接把 sessionToken 落日志" % (_rel(path), number))
        suite.ok(True, "C# 侧 sessionToken 落日志一律经 HostLog.Mask 打码")

        log_source = _read(os.path.join(MCP_DIR, "audit.py"))
        suite.contains(log_source, "_mask_token", "Python 审计层对 token 打码")
        suite.contains(log_source, '"***"', "打码结果为 *** 形式")
        suite.contains(log_source, "_DENY_KEYS", "Python 审计层有禁记字段名单")

    # ---------- 4. 无硬编码绝对路径 ----------
    def no_hardcoded_paths():
        patterns = [
            re.compile(r'[A-Za-z]:\\\\[A-Za-z]'),          # C:\\Users
            re.compile(r'[A-Za-z]:/[A-Za-z]'),             # C:/Users
            re.compile(r'"/home/[A-Za-z]'),
            re.compile(r'"/Users/[A-Za-z]'),
        ]
        self_path = _rel(os.path.abspath(__file__))
        for path in cs_files + py_files:
            if _rel(path) == self_path:
                continue                                    # 本文件自身含正则样例
            text = _read(path)
            for number, line in enumerate(text.splitlines(), start=1):
                stripped = line.strip()
                if stripped.startswith(("//", "#", "*", "/*")):
                    continue
                # 拒绝名单的兜底默认值（os.environ.get("WINDIR", "C:\\Windows")）是安全默认，
                # 不是硬编码依赖；它是「禁止访问」列表，写错也只会更严格。
                if "os.environ.get(" in line and "denied" not in line:
                    continue
                for pattern in patterns:
                    if pattern.search(line):
                        suite.fail("%s:%d 出现硬编码绝对路径：%s"
                                   % (_rel(path), number, stripped[:80]))
        suite.ok(True, "源码中无硬编码绝对路径（路径一律从配置/%LOCALAPPDATA% 派生）")

    # ---------- 5. 只用 UTC ----------
    def utc_only():
        for path in cs_files:
            text = _read(path)
            for number, line in enumerate(text.splitlines(), start=1):
                if "DateTime.Now" in line and not line.strip().startswith("//"):
                    suite.fail("%s:%d 使用本地时间 DateTime.Now" % (_rel(path), number))
        suite.ok(True, "C# 侧未出现 DateTime.Now（协议要求 ISO 8601 UTC）")

        self_path = _rel(os.path.abspath(__file__))
        # 只扫**产品代码**：这个规则的目的是「协议与数据库里的时间戳必须是 UTC」
        # （`*Utc` 字段、`startedAtUtc`、审计 ts……）。测试里把文件 mtime 格式化成人看的
        # 「谁什么时候改的」与本规则无关 —— 把它算进来是规则越界，会产生假阳性。
        #
        # 反过来说，**不允许**用「不是产品目录」当挡箭牌把产品文件漏掉：下面先断言扫描面非空，
        # 再逐个目录点名（而不是 `not startswith("tests")` 这种隐式排除）。
        product_roots = ("mcp/", "bridge/", "mockhost/", "tools/")
        product_py = [path for path in py_files
                      if any(_rel(path).startswith(root) for root in product_roots)]
        suite.is_true(len(product_py) >= 10,
                      "UTC 规则的扫描面非空（产品代码 .py 文件 %d 个）" % len(product_py))
        for path in product_py:
            text = _read(path)
            for number, line in enumerate(text.splitlines(), start=1):
                if "time.localtime" in line:
                    suite.fail("%s:%d 使用本地时间 time.localtime（产品代码只许 UTC）"
                               % (_rel(path), number))
        suite.ok(True, "产品代码（mcp/ bridge/ mockhost/ tools/）未出现 time.localtime")

    # ---------- 6. JavaScriptSerializer 只在 Protocol/Json.cs ----------
    def serializer_confined():
        for path in cs_files:
            text = _read(path)
            if "JavaScriptSerializer" not in text:
                continue
            if _rel(path) != "host/Protocol/Json.cs":
                suite.fail("JavaScriptSerializer 出现在 %s（应封在 Protocol/Json.cs）"
                           % _rel(path))
        suite.ok(True, "JavaScriptSerializer 只出现在 host/Protocol/Json.cs")

    # ---------- 7. 无占位实现 ----------
    def no_placeholders():
        markers = ("TODO", "FIXME", "NotImplementedException", "throw new NotImplemented",
                   "pass  #", "...  # 待实现")
        self_path = _rel(os.path.abspath(__file__))
        for path in cs_files + py_files:
            if _rel(path) == self_path:
                continue
            text = _read(path)
            for marker in markers:
                if marker in text:
                    suite.fail("%s 含占位标记 %r" % (_rel(path), marker))
        suite.ok(True, "源码无 TODO / NotImplemented / 空实现占位")

    # ---------- 8. Python 零第三方依赖 ----------
    def stdlib_only():
        # 判据：只许 import 标准库或本地模块。标准库以解释器自带清单为准
        # （Python 3.10+ 提供 sys.stdlib_module_names），再叠加显式白名单兜底，
        # 避免因解释器版本差异把 ast / csv 这类标准库误判为第三方。
        allowed = set(getattr(sys, "stdlib_module_names", ()))
        allowed |= {
            "__future__", "argparse", "base64", "collections", "contextlib", "ctypes",
            "datetime", "glob", "hashlib", "hmac", "io", "json", "logging", "math", "os",
            "queue", "random", "re", "secrets", "shutil", "socket", "sqlite3", "struct",
            "subprocess", "sys", "tempfile", "threading", "time", "traceback", "typing",
            "unicodedata", "urllib", "uuid", "zipfile", "zlib", "binascii", "stat",
            "textwrap", "string", "itertools", "functools", "decimal", "copy", "signal",
            "ast", "csv", "types", "inspect", "pathlib", "platform", "errno", "stat",
        }
        local = {"config", "jsonrpc", "budget", "tools_catalog", "pipeclient",
                 "instances", "audit", "store", "preview", "reconcile",
                 "engineering_mcp", "harness", "mock_drawing", "mock_host", "test_framing",
                 "test_handshake", "test_tools", "test_heartbeat", "test_budget",
                 "test_store", "test_parity", "test_mcp_stdio", "test_static_audit",
                 # 我们自己的一等模块（tools/mcp_live_probe.py）：真机链路探针，
                 # 只连已运行的宿主、不启动进程。QA 用例 import 它做反向验证，属正常用法。
                 "mcp_live_probe",
                 # N03：桥层适配器与它的无 CAD 替身（tests/mock_bridge.py），
                 # 同样只是本地模块，不引入任何第三方包。
                 "pipeguard", "mock_bridge",
                 # N04-A：唯一进程出口
                 "relay_launcher",
                 # N04-B：网关的 net8 通道与上游错误翻译层（同仓本地模块）
                 "bridge_channel", "upstream_map",
                 # v3.0 外置自动选线算力引擎（同仓本地模块）
                 "route_solver"}
        # 同目录兄弟模块也算本地模块（例如 tests/qa/qa_common.py 被同目录用例 import）
        siblings = {}
        for candidate in py_files:
            folder = os.path.dirname(candidate)
            siblings.setdefault(folder, set()).add(os.path.splitext(os.path.basename(candidate))[0])

        pattern = re.compile(r"^\s*(?:from\s+([A-Za-z_][\w.]*)|import\s+([A-Za-z_][\w.]*))")
        for path in py_files:
            scope = siblings.get(os.path.dirname(path), set())
            for number, line in enumerate(_read(path).splitlines(), start=1):
                match = pattern.match(line)
                if not match:
                    continue
                module = (match.group(1) or match.group(2)).split(".")[0]
                if module in allowed or module in local or module in scope:
                    continue
                suite.fail("%s:%d 引入非标准库模块 %r"
                           % (_rel(path), number, module))
        suite.ok(True, "Python 侧只依赖标准库与本地模块")

    # ---------- 9. 线程模型：读线程只入队 ----------
    def thread_model():
        connection = _read(os.path.join(HOST_DIR, "Pipe", "PipeConnection.cs"))
        suite.contains(connection, "Enqueue", "管道读线程通过 Enqueue 交给主线程")
        suite.is_false(bool(re.search(r"class\s+PipeConnection[\s\S]{0,4000}?"
                                      r"\bnew\s+SemanticExtractor\b", connection)),
                       "PipeConnection 不直接实例化语义提取器（读线程绝不调 CAD）")
        suite.is_false("HostStateProbe.Probe(" in connection,
                       "PipeConnection 不直接探测 CAD 状态（只入队）")

        dispatcher = _read(os.path.join(HOST_DIR, "Scheduling", "MainThreadDispatcher.cs"))
        suite.contains(dispatcher, "Application.Idle", "主线程经 Application.Idle 派发")
        suite.contains(dispatcher, "E1004", "主线程未泵送 → E1004（模态对话框判定）")
        suite.contains(dispatcher, "2000", "派发等待上限 2 秒")

        program = _read(os.path.join(HOST_DIR, "ExtensionApplication.cs"))
        suite.contains(program, "Application.Idle", "插件在 Initialize 里挂 Application.Idle")

        # 写工具与 CAD 写 API 只允许出现在**审批写通道**（M1 起）。
        # 判据从「一处都没有」改成「只能有一处，且那一处就是 WriteToolHandlers.cs」——
        # 这仍然能抓住「别处偷偷写图」，同时允许受审批的 write_* 实现。
        WRITE_HANDLER = "host/Tools/WriteToolHandlers.cs"
        for path in cs_files:
            text = _read(path)
            rel = _rel(path)
            for forbidden in (".AddEntity(", ".Erase()", "Database.Transaction",
                              "SendStringToExecute"):
                if forbidden in text and rel != WRITE_HANDLER:
                    suite.fail("%s 出现 CAD 写操作 %r（只允许在 %s 里）" % (rel, forbidden, WRITE_HANDLER))
        suite.ok(True, "CAD 写操作只出现在审批写通道（WriteToolHandlers.cs）")

    # ---------- 10. 协议版本与写开关常量 ----------
    def protocol_constants():
        handshake = _read(os.path.join(HOST_DIR, "Protocol", "Handshake.cs"))
        suite.contains(handshake, '"1.0"', "宿主协议版本常量 1.0")
        suite.contains(handshake, "writeEnabled", "capabilities 内含 writeEnabled")
        mock = _read(os.path.join(MOCK_DIR, "mock_host.py"))
        suite.contains(mock, 'PROTOCOL_VERSION = "1.0"', "mockhost 协议版本 1.0")
        suite.contains(mock, '"writeEnabled": False', "mockhost writeEnabled 恒 false")

        schema = _read(os.path.join(ROOT, "protocol", "handshake.schema.json"))
        suite.contains(schema, '"writeEnabled"', "schema 层声明 writeEnabled（M1 起为布尔，不再是 const false）")

    # ---------- 11. 工具覆盖：schema ↔ C# handler ↔ mockhost handler ----------
    def tool_coverage():
        with open(os.path.join(ROOT, "protocol", "tools.schema.json"), "r",
                  encoding="utf-8") as handle:
            names = [tool["name"] for tool in json.load(handle)["tools"]]
        suite.is_true(len(names) >= 18, "schema 工具数 ≥ 18（数量不写死，集合一致性由其它断言卡）",
                      "实际=%d" % len(names))

        csharp = {
            "sys": _read(os.path.join(HOST_DIR, "Tools", "SysToolHandlers.cs")),
            "read": _read(os.path.join(HOST_DIR, "Tools", "ReadToolHandlers.cs")),
            "task": _read(os.path.join(HOST_DIR, "Tools", "TaskToolHandlers.cs")),
            "write": _read(os.path.join(HOST_DIR, "Tools", "WriteToolHandlers.cs")),
            "export": _read(os.path.join(HOST_DIR, "Tools", "ExportToolHandlers.cs")),
            "preview": _read(os.path.join(HOST_DIR, "Tools", "PreviewToolHandlers.cs")),
            "batch": _read(os.path.join(HOST_DIR, "Tools", "BatchToolHandlers.cs")),
        }
        catalog = _read(os.path.join(HOST_DIR, "Protocol", "ToolCatalog.cs"))
        mock = _read(os.path.join(MOCK_DIR, "mock_host.py"))

        for name in names:
            domain = name.split("_")[0]
            quoted = '"%s"' % name
            suite.contains(csharp.get(domain, ""), quoted,
                           "C# %s handler 覆盖 %s" % (domain, name))
            suite.contains(catalog, quoted, "C# ToolCatalog 登记 %s" % name)
            suite.contains(mock, "_tool_%s" % name, "mockhost 实现 %s" % name)

        # 反向：mockhost 不得实现 schema 之外的工具
        implemented = set(re.findall(r"def _tool_([a-z0-9_]+)\(", mock))
        suite.eq(sorted(implemented - set(names)), [],
                 "mockhost 没有 schema 之外的工具实现")

        # ---------- N04-B：底层的 430 个方法一个都不许漏进对外集合 ----------
        #
        # 判据是「**恰好等于**」而不是「包含」：多一个就是给模型开了个绕过审批的后门，
        # 少一个就是契约与实现对不上。三处一起卡：
        #   ① schema 的 tools[]（对外契约）
        #   ② tools_catalog.CANONICAL_TOOLS（网关的对外集合）
        #   ③ tools_catalog.UPSTREAM_METHODS（映射表必须覆盖全部 canonical，且不多余）
        bridge_dir = os.path.dirname(BRIDGE_DIR)
        sys.path.insert(0, os.path.join(bridge_dir, "mcp"))
        try:
            import tools_catalog as _catalog
        except Exception as exc:                          # pragma: no cover - 部署缺陷
            suite.fail("无法导入 mcp/tools_catalog.py：%r" % (exc,))
            return
        suite.eq(list(_catalog.CANONICAL_TOOLS), names,
                 "tools_catalog.CANONICAL_TOOLS 与 schema 的 tools[] 逐一相同（顺序也一致）")
        suite.eq(sorted(_catalog.UPSTREAM_METHODS.keys()), sorted(names),
                 "每个 canonical 都有映射项（显式 None 也算「不映射」），且没有多余的映射")
        suite.eq(len(_catalog.NET8_ONLY_TOOLS), 2,
                 "net8 独有工具恰好 2 个（read_civil_list / read_civil_object）")
        suite.eq(sorted(set(names) - set(_catalog.NET48_TOOLS)),
                 sorted(_catalog.NET8_ONLY_TOOLS),
                 "net48 覆盖集合 = 18 - net8 独有集合（差异只能来自这两个已批准工具）")

    # ---------- 13. N04-A：进程启动收口到唯一白名单出口 ----------
    def no_process_launch():
        """禁止的是「启动 CAD 宿主」，不是「启动一切子进程」。

        N03 时这条写成了「`bridge/` 与 `mcp/` 一律不许出现 subprocess」——粒度错了：
        N04 需要由 `mcp/` 拉起 relay（Node 脚本），两者会直接对撞。N04-A 把它精确成两条：

        1. **宿主相关字样仍全仓禁止**（`acad.exe` / `Start-Process` / `os.startfile` / `ShellExecute`）；
        2. **创建进程的能力只允许出现在 `bridge/relay_launcher.py` 这一个文件里**，
           其它任何文件（含 `mcp/**`、`bridge/` 其余模块）出现即失败。
        """
        launcher = os.path.join(BRIDGE_DIR, "relay_launcher.py")
        host_markers = ("acad.exe", "Start-Process", "os.startfile", "ShellExecute")
        spawn_markers = (
            "subprocess", "os.system", "Popen(", "CreateProcess",
            "win32process", "os.spawn", "shell=True",
        )

        targets = (_walk(BRIDGE_DIR, (".py",)) + _walk(MCP_DIR, (".py",)))
        suite.is_true(len(targets) > 0, "扫描目标非空（bridge/ 与 mcp/ 都有 .py）")

        # ① 宿主相关字样：全仓禁止（launcher 也不例外）。扫「去注释」后的代码，避免注释误伤。
        for path in targets:
            text = strip_comments_and_strings(_read(path))
            for marker in host_markers:
                suite.is_false(marker in text,
                               "%s 不含宿主启动痕迹 %r" % (_rel(path), marker))

        # ② 创建进程的能力：只允许在 relay_launcher.py
        suite.is_true(os.path.isfile(launcher), "存在唯一进程出口 bridge/relay_launcher.py")
        others = 0
        for path in targets:
            if os.path.abspath(path) == os.path.abspath(launcher):
                continue
            others += 1
            text = strip_comments_and_strings(_read(path))
            for marker in spawn_markers:
                suite.is_false(marker in text,
                               "%s 不是唯一出口，不得出现 %r（只允许 bridge/relay_launcher.py）"
                               % (_rel(path), marker))
        suite.is_true(others > 0, "白名单外的被检查文件非空（防止扫描面意外为空而假绿）")

        # ③ launcher 自身：不得出现宿主/项目图纸相关字样，不得开 shell 解释，不得读整份环境
        if os.path.isfile(launcher):
            text = _read(launcher)
            # 先做**白名单式精确剥词**：只有「取值恰好是 vendored 路径分段」的字符串字面量、
            # 注释、docstring 会被抹掉，其它一律保留 —— 见 `scrub_launcher_text()` 文档里
            # 关于 `Popen("D:/x-new-acad/evil.exe")` 的假阴性案例（不能用裸子串替换）。
            scrubbed, scrub_stats = scrub_launcher_text(text)
            suite.is_true(scrub_stats["literalHits"] >= 4,
                          "launcher 的 vendored 路径字面量命中 %d 处（≥4：third_party/new-acad/server/panel-relay.js 各一次）"
                          % scrub_stats["literalHits"])
            lowered = scrubbed.lower()
            for forbidden in ("acad", ".dwg"):
                suite.is_false(forbidden in lowered,
                               "bridge/relay_launcher.py 不含 %r（白名单式剥词后，大小写不敏感）"
                               % forbidden)
            suite.is_false('"shell": True' in text or "shell=True" in text.replace('"shell": False', ''),
                           "bridge/relay_launcher.py 不开 shell 解释")
            suite.is_false("os.environ" in text,
                           "bridge/relay_launcher.py 不读整份环境（逐键注入，不透传）")
            suite.contains(text, "NODE_BASENAME_RE",
                           "bridge/relay_launcher.py 有可执行文件白名单校验")
            suite.contains(text, "CREATE_NO_WINDOW",
                           "bridge/relay_launcher.py 抑制控制台窗口（不打扰用户桌面）")

        # 反向：mcp/ 里必须仍然只有「连接既存宿主」的能力，没有任何「创建宿主」的字样
        mcp_text = "".join(_read(p) for p in _walk(MCP_DIR, (".py",)))
        for phrase in ("create_instance", "launch_host", "spawn_host"):
            suite.is_false(phrase in mcp_text, "mcp/ 不含 %r 这类宿主创建语义" % phrase)

    # ---------- 14. 剥词逻辑自身的正控 / 反控（防「规则被改松」） ----------
    def strip_logic_selftest():
        """证明 `strip_comments_and_strings()` 既去掉了假阳性、又没有把真阳性一起放走。

        这是团队要求的「剥词逻辑自测」：只改规则不加自测，等于把「规则还活着」这件事交给运气。
        """
        comment_only = (
            "\"\"\"文档串里提到 acad.exe 与 subprocess 只是说明规则\"\"\"\n"
            "# 注释里写 acad.exe / subprocess.Popen 也不该被判违规\n"
            "def harmless():\n"
            "    return 1\n"
        )
        stripped = strip_comments_and_strings(comment_only)
        suite.is_false("acad.exe" in stripped,
                       "反控①：只在注释/文档串里的 acad.exe 被剥掉（不再假阳性）")
        suite.is_false("subprocess" in stripped,
                       "反控②：只在注释里的 subprocess 被剥掉（不再假阳性）")

        real_call = (
            "import subprocess\n"
            "def spawn():\n"
            "    subprocess.Popen(['node', 'x.js'])\n"
        )
        stripped_real = strip_comments_and_strings(real_call)
        suite.is_true("subprocess" in stripped_real and "Popen(" in stripped_real,
                      "正控①：真实代码里的 subprocess.Popen 必须留下（规则不能被改松）")
        suite.is_false(any(m in stripped_real for m in ("acad.exe", "Start-Process")),
                       "正控②：真实调用里没有宿主字样时不误报")

        # 已声明的取舍：字符串里的宿主名会被剥掉（判定目标是「真实代码里的启动能力」）
        string_only = "value = 'acad.exe'\nrun('node')\n"
        stripped_string = strip_comments_and_strings(string_only)
        suite.is_false("acad.exe" in stripped_string,
                       "取舍：字符串字面量里的宿主名会被剥掉（已记录在函数文档里）")
        suite.is_true("run(" in stripped_string, "取舍：字符串旁的调用点仍然保留")

        # tokenize 失败也要有兜底（坏文件不能把整条审查弄崩）
        broken = "def f(:\n    # acad.exe\n"
        suite.is_true(isinstance(strip_comments_and_strings(broken), str),
                      "兜底：tokenize 失败时退回字符级扫描，不抛异常")

        # ---------- launcher 的「白名单式精确剥词」正反控 ----------
        # 正控：vendored 路径分段确实被剥掉（否则 launcher 会假红）
        legit = (
            "RELAY_SCRIPT = os.path.join(BASE, \"third_party\", \"new-acad\", "
            "\"server\", \"panel-relay.js\")\n"
        )
        scrubbed, stats = scrub_launcher_text(legit)
        suite.eq(stats["literalHits"], 4,
                 "launcher 剥词：4 个 vendored 路径分段正好命中 4 次")
        suite.is_false("acad" in scrubbed.lower(),
                       "launcher 剥词：合法 vendored 路径被剥掉，不产生 acad 假阳性")

        # 反控①：**白名单外**的字符串，即便含 new-acad，也不许被豁免 ——
        # 这正是 `text.replace("new-acad", "")` 会漏掉的逃逸。
        evasion = 'subprocess.Popen("D:/x-new-acad/evil.exe")\n'
        scrubbed, stats = scrub_launcher_text(evasion)
        suite.eq(stats["literalHits"], 0,
                 "launcher 剥词反控：白名单外字符串的命中次数为 0（不豁免）")
        suite.is_true("acad" in scrubbed.lower(),
                      "launcher 剥词反控①：Popen(\"D:/x-new-acad/evil.exe\") 的 acad 必须留下（否则逃逸）")

        # 反控②：以 vendored 名打头的相对路径同样不许豁免
        scrubbed, _ = scrub_launcher_text('run("new-acad/evil.exe")\n')
        suite.is_true("acad" in scrubbed.lower(),
                      "launcher 剥词反控②：run(\"new-acad/evil.exe\") 的 acad 必须留下")

        # 反控③：非 docstring 的普通字符串宿主名不许豁免
        scrubbed, _ = scrub_launcher_text('cmd = "acad.exe"\nstart(cmd)\n')
        suite.is_true("acad" in scrubbed.lower(),
                      "launcher 剥词反控③：代码里赋值的 \"acad.exe\" 必须留下")

        # 豁免面：注释与 docstring 里提到路径不算违规
        documented = (
            '"""模块文档提到 third_party/new-acad/server/panel-relay.js 这条路径"""\n'
            "# 注释里也提一下 new-acad\n"
            "def noop():\n"
            '    """函数文档同样提 third_party/new-acad/server/panel-relay.js"""\n'
            "    return 1\n"
        )
        scrubbed, stats = scrub_launcher_text(documented)
        suite.eq((stats["comments"], stats["docstrings"]), (1, 2),
                 "launcher 剥词：注释 1 处 + docstring 2 处（模块级与函数级）被豁免")
        suite.is_false("acad" in scrubbed.lower(),
                       "launcher 剥词：文档/注释里的 vendored 路径不产生假阳性")

        # fail-closed：剥不动就一个字符都不豁免（宁可误红，不可假绿）
        scrubbed_broken, stats_broken = scrub_launcher_text("def f(:\n    # acad\n")
        suite.eq((stats_broken["literalHits"], stats_broken["comments"]), (0, 0),
                 "launcher 剥词 fail-closed：语法坏掉时不豁免任何文本")
        suite.is_true("acad" in scrubbed_broken,
                      "launcher 剥词 fail-closed：原文原样返回（不会静默放行）")

    # ---------- N5：语义缓存键 = 内容指纹 + 文档修订号（2026-11-22 真机「未保存修改读到旧模型」） ----------
    def cache_key_invariant():
        """**这个函数守的是一条正确性不变量，不是性能优化。**

        背景：`DrawingFingerprint.Compute` = 路径哈希 + Handseed + 实体数 + 前 200 句柄 + **文件 mtime**。
        对「改了但没保存」的图（移动图元 / 改文字 / 改属性值 / 改图层颜色）这六项**全都不变**，
        指纹因此不变 —— 若缓存键只用指纹，30 分钟 TTL 下就会把旧模型返回半小时。
        所以键必须是 `指纹 + "#" + 修订号`：指纹兜「身份/结构变化」，修订号兜「结构没变但内容变了」。

        为什么这里是**源码断言**而不是行为测试：`tests/` 是 Python，无法执行 C#（宿主只有
        `dotnet build`，没有 C# 测试工程）。行为验证分两层：宿主启动时的 `cache_key_selftest`
        （可执行，落在真机日志里）+ 真机判据「移动一个图元后必须 miss」。这里只钉住结构，
        防止有人把附加项悄悄去掉。
        """
        host_src = _read(os.path.join(BRIDGE_DIR, os.pardir, "host", "HostRuntime.cs"))
        rev_path = os.path.join(BRIDGE_DIR, os.pardir, "host", "Semantic", "DocumentRevision.cs")
        suite.is_true(os.path.isfile(rev_path), "DocumentRevision.cs 存在（修订号机制在位）")
        rev_src = _read(rev_path)

        # ① 键的构成函数必须把指纹与修订号都拼进去
        suite.is_true('fingerprint + "#" + revision.ToString' in rev_src,
                      "ComposeKey 把指纹与修订号都拼进键（fp#rev）")
        # ② 缓存读写必须用同一个组合键（GET 与 PUT 不能各用一套）
        suite.is_true("Cache.Get(cacheKey)" in host_src,
                      "GetSemanticModel 用组合键查缓存")
        suite.is_true("Cache.Put(cacheKey, model)" in host_src,
                      "GetSemanticModel 用同一个组合键写缓存（GET/PUT 同键）")
        suite.is_true("TryComposeKey(" in host_src,
                      "取键走 TryComposeKey（带保守回退）")
        # ③ 身份判定必须是「引用快路径 + 逻辑回退」，且判定不了时**绕过缓存**（不静默降级）
        suite.is_true("ReferenceEquals(_db, db) && ReferenceEquals(_doc, doc)" in rev_src,
                      "TryComposeKey 先走引用相等快路径（mode=ref）")
        suite.is_true("private bool SameDocumentIdentity(" in rev_src,
                      "引用不等时走逻辑身份判定（mode=logical）")
        for needle, label in (
                ("SafeDocName", "逻辑身份比较 doc.Name"),
                ("SafeFileName", "逻辑身份比较 db.Filename"),
                ("StringComparison.OrdinalIgnoreCase", "路径比较忽略大小写")):
            suite.is_true(needle in rev_src, label)
        # 只在**代码**里禁 Handseed：类注释里会提到「为什么不用它」，那是合理的说明文字。
        rev_code = re.sub(r"/\*.*?\*/", "", rev_src, flags=re.S)
        rev_code = re.sub(r"//[^\n]*", "", rev_code)
        suite.is_false("Handseed" in rev_code,
                       "身份判定**代码里**不许用 Handseed（新增图元会让它变，把同一张图误判成两张）")
        suite.is_true('cache_bypassed_no_revision_key' in host_src,
                      "判定不了时记 cache_bypassed_no_revision_key（不静默降级）")
        suite.is_true("bool cacheAllowed = revisionKeyed;" in host_src,
                      "cacheAllowed 由 revisionKeyed 决定（miss 时整轮旁路）")
        suite.is_true("if (cacheAllowed && !string.IsNullOrEmpty(cacheKey))" in host_src,
                      "旁路时不写缓存（Get/Put 同一开关）")
        suite.is_false("cacheKey = fingerprint" in host_src,
                       "不许退回「仅指纹做键」（那等于悄悄关掉 D-02 的保证）")
        # ④ 修订号必须订阅「结构没变但内容变了」那几类事件
        for evt in ("ObjectModified", "ObjectAppended", "ObjectErased",
                    "SaveComplete", "CommandEnded"):
            suite.is_true(evt in rev_src, "修订号订阅 %s（漏一个就漏一类修改）" % evt)
        # ⑤ 事件处理器只许做自增（高频事件里做 IO/加锁会拖垮宿主）
        bump_body = rev_src.split("private void Bump(string source)", 1)
        suite.eq(len(bump_body), 2, "能定位到 Bump 实现")
        bump_src = bump_body[1].split("private void OnObjectChanged", 1)[0]
        suite.is_true("Interlocked.Increment" in bump_src,
                      "Bump 用 Interlocked.Increment（无锁自增）")
        for banned in ("File.", "lock (", "_sync)"):
            suite.is_false(banned in bump_src,
                           "Bump 内不出现 %r（高频事件里不许做 IO/加锁）" % banned)
        # ⑥ LRU 必须用单调计数器，不许用时间戳（Windows 上 DateTime 分辨率 15.6ms 会造成相等）
        cache_src = _read(os.path.join(BRIDGE_DIR, os.pardir, "host", "Semantic", "SemanticCache.cs"))
        suite.is_true("public long UseSeq;" in cache_src, "LRU 用单调序号 UseSeq（不是时间戳）")
        suite.is_true("Interlocked.Increment(ref _useSeq)" in cache_src,
                      "UseSeq 由 Interlocked.Increment 生成（严格有序、无相等）")
        suite.is_false("LastUsedUtc" in cache_src,
                       "淘汰排序里不再出现 LastUsedUtc（15.6ms 相等的问题已消除）")
        suite.is_true("StoredUtc" in cache_src,
                      "TTL / AgeMs 仍用 StoredUtc（不与 LRU 混用）")

        # ⑦ 宿主必须在启动时**真跑一遍缓存**（不是只测拼装函数）：四条判据齐全
        for needle, label in (
                ("cache_behavior_selftest", "启动时输出 cache_behavior_selftest"),
                ("missOnBump", "自检覆盖「同指纹+修订号+1 → miss」"),
                ("hitSame", "自检覆盖「同指纹+同修订号 → hit」"),
                ("missOtherFp", "自检覆盖「换指纹 → miss」"),
                ("lruEvicts", "自检覆盖「容量满淘汰最久未用」"),
                ("ReferenceEquals(probe.Get(keyRev1), model)", "hit 判定用引用相等（不是「非 null」）")):
            suite.is_true(needle in host_src, label)

        # ⑦ 这条不变量必须同时受 TTL 兜底（修订号漏挂时不至于永久旧）
        suite.is_true("DefaultTtlMs = 1800000" in _read(
            os.path.join(BRIDGE_DIR, os.pardir, "host", "Semantic", "SemanticCache.cs")),
            "缓存 TTL 仍是 30 分钟（修订号漏挂时由 TTL 封顶）")

    # ---------- D-01 防回潮：禁止脱离事务的 ObjectId.GetObject() ----------
    #
    # **白名单必须为空**。若确有必要豁免，请在此处逐个列出 `文件:行` + 理由，
    # 不许用「除了某目录/某文件类型」这类模糊豁免 —— 模糊豁免等于没有规则。
    DETACHED_GETOBJECT_WHITELIST = ()

    def no_detached_object_open():
        """`host/**/*.cs` 里禁止 `xxxId.GetObject(...)`（必须用 `tr.GetObject(...)`）。

        ## 为什么这条规则值得单独立案（D-01 的真实代价）

        `ObjectId.GetObject(OpenMode)` 是**脱离事务**的对象打开。在本项目的调用上下文里它会抛异常，
        而 `ReadAttributes` 的逐条 `catch { continue; }` 把异常**静默吞掉** —— 结果：

        * net48 宿主从 M0 起**一条块属性都没读到**；
        * 而 `run_all` / `qa_run_all` 一直全绿，因为**没有任何断言检查过 `read_describe.props` 的形状**；
        * 最后是靠真机日志的 `attrMs=1637` 与 `attrReads=0`（花 1.6 秒却读到 0 条）才暴露的。

        所以这里禁的不是「风格」，是「静默失败的高发写法」。正确写法：
        `tr.GetObject(id, OpenMode.ForRead, false)` —— 与图元打开共用同一个事务。

        ## 剥注释的必要性

        代码里的**注释**会提到旧写法（本仓 `SemanticExtractor` 就有一条注释写着 `attributeId.GetObject(...)`），
        不剥注释会误报。剥注释用朴素正则，前提是「host 里不存在 `://`」——下面第一条断言就在守这个前提，
        一旦有人写了 `"http://..."` 之类的字符串，前提被破坏会先红，而不是让剥注释悄悄吃掉一行代码。
        """
        host_root = os.path.join(BRIDGE_DIR, os.pardir, "host")
        cs_paths = []
        for base, _dirs, files in os.walk(host_root):
            for name in files:
                if name.endswith(".cs"):
                    cs_paths.append(os.path.join(base, name))
        suite.is_true(len(cs_paths) >= 20,
                      "扫描面非空（host 下 .cs 共 %d 个）" % len(cs_paths))

        joined = "\n".join(_read(path) for path in cs_paths)
        suite.is_false("://" in joined,
                       "前提检查：host 源码里不含 '://'（否则朴素剥注释会吃掉行内代码）")

        detached = re.compile(r"\b[A-Za-z_][A-Za-z0-9_]*[Ii]d\s*\.\s*GetObject\s*\(")
        violations = []
        for path in cs_paths:
            text = _read(path)
            text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)      # 块注释
            text = re.sub(r"//[^\n]*", "", text)                    # 行注释
            for number, line in enumerate(text.splitlines(), start=1):
                if detached.search(line):
                    violations.append("%s:%d %s" % (_rel(path), number, line.strip()[:80]))

        whitelisted = []
        real = []
        for item in violations:
            hit = next((w for w in DETACHED_GETOBJECT_WHITELIST if item.startswith(w)), None)
            (whitelisted if hit else real).append(item)

        if real:
            for item in real:
                suite.fail(
                    "%s —— ObjectId.GetObject() 脱离事务，失败时抛异常；本仓多处 "
                    "`catch { continue; }` 会把它吞成静默失败（D-01：M0 起块属性一条都没读到，"
                    "直到真机 attrMs=1637 / attrReads=0 才发现）。"
                    "请改用 `tr.GetObject(id, OpenMode.ForRead, false)`。" % item)
        suite.eq(len(whitelisted), len(DETACHED_GETOBJECT_WHITELIST),
                 "白名单条目都被真实命中（没有过期豁免）")
        suite.ok(True, "host 中无脱离事务的 ObjectId.GetObject()（D-01 防回潮）")

        # 反控：规则本身必须能抓到违规写法（否则这条规则是摆设）
        probe_hits = detached.findall(
            "attribute = attributeId.GetObject(OpenMode.ForRead) as AttributeReference;")
        suite.eq(len(probe_hits), 1, "反控：正则能命中 `xxxId.GetObject(` 形态")
        suite.eq(len(detached.findall(
            "attr = tr.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;")), 0,
            "反控：正则不命中合法的 `tr.GetObject(id, ...)`")
        stripped = re.sub(r"//[^\n]*", "", "// 旧写法 attributeId.GetObject(...)")
        suite.eq(len(detached.findall(stripped)), 0, "反控：注释里的旧写法被剥掉（不误报）")

    def civil_native_enumerator_safety():
        """曲面三角形**不得枚举**：空 TIN 曲面上 foreach 会原生 AV（2026-11-24 13:26 实测进程崩溃）。

        判据不是"别写 foreach"，而是"别把原生集合交给通用枚举计数器"——
        `CountOfMember(obj, {"Triangles", ...})` 这条写法会走到 foreach，必须用属性式计数。
        """
        text = _read(os.path.join(HOST_DIR, "Semantic", "Civil3dReader.cs"))
        suite.contains(text, "CountOfPropertyOnly",
                      "曲面三角形走属性式计数（ICollection.Count / Count 属性）")
        suite.contains(text, "pointCount.Value == 0",
                      "空曲面短路：0 顶点时直接判 0，不进入枚举")
        suite.is_true('CountOfMember(obj, new[] { "Triangles"' not in text,
                      "反例：Triangles 不再交给会 foreach 的 CountOfMember",
                      "仍存在 CountOfMember(obj, {\"Triangles\"...})")

    for label, func in (
        ("管道/协议层无 CAD 依赖", pipe_protocol_no_cad),
        ("原生集合不枚举（曲面三角形）", civil_native_enumerator_safety),
        ("写通道关闭", write_channel_closed),
        ("日志无 token", token_never_logged),
        ("无硬编码路径", no_hardcoded_paths),
        ("只用 UTC", utc_only),
        ("序列化器收口", serializer_confined),
        ("无占位实现", no_placeholders),
        ("零第三方依赖", stdlib_only),
        ("线程模型", thread_model),
        ("协议常量", protocol_constants),
        ("工具覆盖", tool_coverage),
        ("无进程启动痕迹", no_process_launch),
        ("剥词逻辑正反控", strip_logic_selftest),
        ("缓存键不变量（指纹+修订号）", cache_key_invariant),
        ("禁止脱离事务的对象打开", no_detached_object_open),
    ):
        suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
