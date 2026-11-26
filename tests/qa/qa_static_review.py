"""tests/qa/qa_static_review.py —— 静态审查（QA 自写，不依赖工程师的 test_static_audit.py）。

工程师那份 static_audit 是「他自己定的判据」。QA 独立再写一遍，重点放在
**能被骗过的地方**：注释里写了不引用 CAD、实际代码却引用了；或者用一个包装函数
把写操作藏起来。判据尽量用「能定位到行号」的机械规则，而不是模糊的包含判断。

审查项：
  1. host/Pipe/** 与 host/Session/** 是否真的一处 CAD 引用都没有（设计 T02 判据 5）；
  2. host/Protocol/** 的 CAD 引用清单 —— 设计允许 ToolRouter 在主线程访问 CAD，
     但不允许别的协议文件偷偷碰（逐文件列出并核对白名单）；
  3. 写操作面：SendStringToExecute / Erase / AddEntity / Command( /
     OpenMode.ForWrite / AddNewlyCreatedDBObject / StartTransaction( 等；
  4. 硬编码绝对路径（C:\\ 或 D:\\ 开头的字面量）；
  5. Python 侧 import 白名单（必须全标准库 + 本项目模块）；
  6. .ps1 必须 UTF-8 with BOM（PS 5.1 无 BOM 会把中文注释当 ANSI → 语法错误）；
  7. .cs/.py 必须 UTF-8 无 BOM（设计 §7.1）。
"""

from __future__ import annotations

import os
import re
import sys

import qa_common as qa

#: 允许在 Protocol/ 下访问 CAD 的文件（设计 §3.3：分派发生在 MainThreadDispatcher 的主线程上）
PROTOCOL_CAD_ALLOWED = {"host/Protocol/ToolRouter.cs"}

CAD_TOKENS = ("using Autodesk.", "Autodesk.AutoCAD.ApplicationServices.Application",
              "Database ", "Transaction", "Document ", "MdiActiveDocument")

#: 写操作面（M0 一处都不许有）
WRITE_SURFACE = (
    "SendStringToExecute",
    "OpenMode.ForWrite",
    "OpenMode.ForRead",              # 只读也应是 StartOpenCloseTransaction；留作提示项
    "AddNewlyCreatedDBObject",
    ".Erase(",
    ".AddEntity(",
    "AddVertexAt",
    "DocumentLock",
    ".Command(",
    "Editor.Command",
)

#: 允许的本地模块前缀（Python 侧 import 白名单）
#: N04：新增三个**本项目**模块（都是确切模块名，不是前缀/通配）：
#:   pipeguard      → bridge/pipeguard.py（端口/令牌守卫）
#: relay_launcher → bridge/relay_launcher.py（唯一允许创建进程的地方）
#: upstream_map   → mcp/upstream_map.py（上游工具名 ↔ 我方工具名映射）
LOCAL_MODULES = {"config", "jsonrpc", "pipeclient", "instances", "tools_catalog",
                 "store", "audit", "budget", "preview", "reconcile",
                 "engineering_mcp", "mock_host", "mock_drawing", "harness",
                 "pipeguard", "relay_launcher", "upstream_map", "bridge_channel",
                 "base_bridge"}


def _lines(rel_path: str):
    for number, line in enumerate(qa.read_source(rel_path).splitlines(), 1):
        yield number, line


def run() -> dict:
    suite = qa.harness.Suite("qa_static_review")

    # ==================================================================
    # 1. Pipe/ 与 Session/ 零 CAD 引用
    # ==================================================================
    def pipe_and_session_clean():
        for sub_dir in ("host/Pipe", "host/Session"):
            files = list(qa.iter_files(sub_dir, ".cs"))
            suite.is_true(len(files) >= 1, "%s 下有源码文件（%d 个）" % (sub_dir, len(files)),
                          "files=%r" % ([os.path.basename(f) for f in files],))
            hits = []
            for rel_path in files:
                for number, line in _lines(rel_path):
                    stripped = line.strip()
                    if stripped.startswith("//") or stripped.startswith("///"):
                        continue            # 注释里提 CAD 是允许的（说明性文字）
                    for token in CAD_TOKENS:
                        if token in line:
                            hits.append("%s:%d %s" % (rel_path, number, stripped[:100]))
            qa.eqd(suite, hits, [],
                   "%s 下一处 CAD API 引用都没有（注释除外）" % sub_dir,
                   "命中=%r" % (hits[:5],))

    suite.case("Pipe/Session 无 CAD", pipe_and_session_clean)

    # ==================================================================
    # 2. Protocol/ 的 CAD 引用白名单
    # ==================================================================
    def protocol_whitelist():
        offenders = []
        referenced = {}
        for rel_path in qa.iter_files("host/Protocol", ".cs"):
            files_hit = []
            for number, line in _lines(rel_path):
                stripped = line.strip()
                if stripped.startswith("//") or stripped.startswith("///"):
                    continue
                if "Autodesk." in line or "Autodesk." in stripped:
                    files_hit.append(number)
            if files_hit:
                referenced[rel_path] = files_hit
                if rel_path.replace("\\", "/") not in PROTOCOL_CAD_ALLOWED:
                    offenders.append("%s:%r" % (rel_path, files_hit[:5]))
        print("Protocol/ 中引用 Autodesk 的文件：%r" % (
            {key.replace(os.sep, "/"): value[:3] for key, value in referenced.items()},))
        qa.eqd(suite, offenders, [],
               "Protocol/ 下只有 ToolRouter.cs 可引用 CAD（其余文件必须纯协议）",
               "越界=%r" % (offenders,))
        suite.contains([key.replace(os.sep, "/") for key in referenced],
                       "host/Protocol/ToolRouter.cs",
                       "ToolRouter.cs 确实在主线程侧引用 CAD（符合设计 §3.3）")

        # 反向证据：Pipe 层即使间接也不该通过 ToolRouter 之外的路径碰 CAD
        pipe_sources = "\n".join(qa.read_source(path)
                                 for path in qa.iter_files("host/Pipe", ".cs"))
        suite.is_false("ToolRouter" in pipe_sources,
                       "Pipe/ 层不直接调用 ToolRouter（分派只在主线程经过 Dispatcher）")

    suite.case("Protocol 白名单", protocol_whitelist)

    # ==================================================================
    # 3. 写操作面
    # ==================================================================
    def write_surface():
        hits = []
        for rel_path in list(qa.iter_files("host", ".cs")):
            for number, line in _lines(rel_path):
                stripped = line.strip()
                if stripped.startswith("//") or stripped.startswith("///"):
                    continue
                for token in WRITE_SURFACE:
                    if token in line:
                        hits.append((token, "%s:%d" % (rel_path, number), stripped[:90]))
        by_token = {}
        for token, where, text in hits:
            by_token.setdefault(token, []).append((where, text))
        print("写操作面扫描结果：%r" % ({key: len(value) for key, value in by_token.items()},))

        # M1：写 API **只允许**出现在 host/Tools/WriteToolHandlers.cs（审批写通道三步）。
        # 其它任何文件出现写 API 都是绕过审批的旁路 —— 这才是现在要守的不变量。
        WRITE_HANDLER = "host/Tools/WriteToolHandlers.cs"
        critical = ("SendStringToExecute", "OpenMode.ForWrite", ".Erase(",
                    ".AddEntity(", "AddNewlyCreatedDBObject", "DocumentLock",
                    ".Command(", "Editor.Command")
        for token in critical:
            where_list = by_token.get(token, [])
            outside = [item for item in where_list if not item[0].startswith(WRITE_HANDLER)]
            suite.ok(outside == [],
                     "写操作 API 只出现在审批写通道（%s）：%s" % (WRITE_HANDLER, token),
                     "越界命中=%r" % (outside[:3],))
        suite.is_true(bool(by_token),
                      "写通道确实在写（WriteToolHandlers 里有写 API，不是空壳）",
                      "命中=%r" % ({k: len(v) for k, v in by_token.items()},))

        # OpenMode.ForRead 用「只读事务」替代更佳，但出现也不算写风险：只做提示，不判失败
        if "OpenMode.ForRead" in by_token:
            print("提示（非失败）：出现 OpenMode.ForRead %d 处，建议统一改 "
                  "StartOpenCloseTransaction：%r"
                  % (len(by_token["OpenMode.ForRead"]),
                     by_token["OpenMode.ForRead"][:3]))

    suite.case("写操作面", write_surface)

    # ==================================================================
    # 4. 硬编码绝对路径
    # ==================================================================
    def hardcoded_paths():
        pattern = re.compile(r'["\']([A-Za-z]:[\\/](?:Program Files|Users|AutoCAD|Windows)[^"\']*)["\']')
        hits = []
        for sub_dir in ("host", "mcp", "mockhost"):
            for rel_path in list(qa.iter_files(sub_dir, ".cs")) + \
                    list(qa.iter_files(sub_dir, ".py")):
                for number, line in _lines(rel_path):
                    stripped = line.strip()
                    if stripped.startswith("//") or stripped.startswith("#"):
                        continue
                    for match in pattern.finditer(line):
                        hits.append("%s:%d %s" % (rel_path, number, match.group(1)[:80]))
        qa.eqd(suite, hits, [],
               "host/mcp/mockhost 中没有任何硬编码系统绝对路径字面量",
               "命中=%r" % (hits[:5],))

        # AutoCADDir 必须来自构建属性且可被命令行覆盖（不是写死在代码里）
        props = qa.read_source("host/Directory.Build.props")
        suite.contains(props, "AutoCADDir",
                       "构建属性里有 AutoCADDir 定义")
        suite.contains(props, "<AutoCADDir Condition=",
                       "AutoCADDir 带 Condition（可被 -p:AutoCADDir=... 覆盖）")

    suite.case("硬编码路径", hardcoded_paths)

    # ==================================================================
    # 5. Python import 白名单
    # ==================================================================
    def python_imports():
        stdlib = set(sys.stdlib_module_names)
        offenders = []
        scanned = 0
        for base in ("mcp", "mockhost"):
            for rel_path in qa.iter_files(base, ".py"):
                scanned += 1
                for name in qa.py_imports(rel_path):
                    if name in stdlib or name in LOCAL_MODULES:
                        continue
                    offenders.append("%s: %s" % (rel_path, name))
        suite.is_true(scanned >= 12, "扫描到 ≥12 个 Python 文件（实际 %d）" % scanned)
        qa.eqd(suite, offenders, [],
               "mcp/mockhost 的 import 全部是标准库或本项目模块（零第三方依赖）",
               "越界=%r" % (offenders[:8],))

        # 反向证据：requirements/pyproject 不存在（没有第三方依赖声明）
        for name in ("requirements.txt", "pyproject.toml", "setup.py"):
            suite.is_false(os.path.exists(os.path.join(qa.ROOT, name)),
                           "仓库根没有 %s（零依赖，无第三方包声明）" % name)

    suite.case("Python import", python_imports)

    # ==================================================================
    # 6/7. 编码与 BOM
    # ==================================================================
    def encodings():
        ps1 = list(qa.iter_files("tools", ".ps1"))
        suite.is_true(len(ps1) >= 3, "tools/ 下有 ≥3 个 ps1 脚本（实际 %d）" % len(ps1))
        for rel_path in ps1:
            with open(os.path.join(qa.ROOT, rel_path), "rb") as handle:
                head = handle.read(3)
            suite.eq(head, b"\xef\xbb\xbf",
                     "%s 是 UTF-8 with BOM（PS 5.1 读中文注释的前提）" % rel_path)

        bad = []
        for base in ("host", "mcp", "mockhost", "tests"):
            for suffix in (".cs", ".py"):
                for rel_path in qa.iter_files(base, suffix):
                    if "/obj/" in rel_path or "/bin/" in rel_path:
                        continue
                    with open(os.path.join(qa.ROOT, rel_path), "rb") as handle:
                        head = handle.read(3)
                    if head == b"\xef\xbb\xbf":
                        bad.append(rel_path)
        qa.eqd(suite, bad, [],
               "所有 .cs/.py 都是 UTF-8 无 BOM（设计 §7.1）",
               "带 BOM=%r" % (bad[:5],))

    suite.case("编码与 BOM", encodings)

    # ==================================================================
    # 8. C# 7.3 语法合规（设计 §3.9 硬约束）
    # ==================================================================
    def csharp73():
        props = qa.read_source("host/Directory.Build.props")
        suite.contains(props, "<LangVersion>7.3</LangVersion>",
                       "构建属性把 LangVersion 锁在 7.3")

        # 只靠「能编过」不足以证明，直接把 C# 8+ 专有语法逐条找出来
        patterns = {
            "record 类型": re.compile(r"^\s*(public|internal|private|sealed)?\s*record\s+\w+"),
            "init 访问器": re.compile(r"\{\s*get;\s*init;"),
            "??= 赋值": re.compile(r"\?\?="),
            "using 声明（using var）": re.compile(r"^\s*using\s+var\s+"),
            "文件作用域 namespace": re.compile(r"^\s*namespace\s+[\w.]+\s*;"),
            "switch 表达式": re.compile(r"\bswitch\s*\{"),
            "#nullable 指令": re.compile(r"^\s*#nullable\s+(enable|disable)"),
            "目标类型 new()": re.compile(r"=\s*new\(\)"),
            "可空引用类型标注": re.compile(r"\bstring\?\s+\w+\s*[;=)]"),
        }
        hits = []
        for rel_path in qa.iter_files("host", ".cs"):
            if "/obj/" in rel_path or "/bin/" in rel_path:
                continue
            for number, line in enumerate(qa.read_source(rel_path).splitlines(), 1):
                stripped = line.strip()
                if stripped.startswith("//") or stripped.startswith("///"):
                    continue
                for label, pattern in patterns.items():
                    if pattern.search(line):
                        hits.append("%s:%d [%s] %s"
                                    % (rel_path, number, label, stripped[:70]))
        qa.eqd(suite, hits, [],
               "宿主源码不含任何 C# 8+ 专有语法（无需依赖编译器兜底）",
               "命中=%r" % (hits[:6],))

    suite.case("C# 7.3 合规", csharp73)

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
