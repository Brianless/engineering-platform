"""tests/qa/qa_common.py —— QA 独立复核套件的公共底座。

设计意图（与 tests/harness.py 的分工）：
  * 复用 harness 的 Suite 断言器、sandbox、mock_server、connect、RawClient
    —— 不重复造轮子，也不修改 tests/ 下任何既有文件；
  * 额外提供 QA 专属工具：
      - rss_kb()            取当前进程工作集（用于证明「超长长度前缀不 OOM」）
      - frame_bytes()       按协议编码任意字节载荷（用于造非法 UTF-8 / 半包）
      - all_write_tools()   按写域前缀枚举「每一个」写域工具名（不是抽一个）
      - read_source()       读仓库源码（静态审查用）
      - py_imports()        抽取 Python 文件里的 import（标准库白名单审查用）

用法（每个模块自带 run()，可被 qa_run_all.py 以子进程方式聚合）：
    python tests/qa/qa_framing_edge.py      # 打印 JSON 报告，退出码即结论
"""

from __future__ import annotations

import ast
import contextlib
import ctypes
import json
import os
import struct
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TESTS_DIR = os.path.join(ROOT, "tests")
for _path in (TESTS_DIR, os.path.join(ROOT, "mcp"), os.path.join(ROOT, "mockhost")):
    if _path not in sys.path:
        sys.path.insert(0, _path)

import harness  # noqa: E402  —— 复用工程师的底座，但不改它
import pipeclient  # noqa: E402  —— mcp/pipeclient.py（管道客户端与帧编解码）

MCP_DIR = os.path.join(ROOT, "mcp")
MOCK_DIR = os.path.join(ROOT, "mockhost")
HOST_DIR = os.path.join(ROOT, "host")
PROTOCOL_DIR = os.path.join(ROOT, "protocol")

#: 设计 §2.1 硬上限
MAX_PAYLOAD = 1024 * 1024

#: 写域前缀（设计 §2.2 / §T04 判据 2；与 ToolCatalog.cs / tools_catalog.py 同集合）
WRITE_PREFIXES = ("cad_", "civil_", "dyn_", "file_", "rev_", "p3d_", "calc_", "skill_")

#: 每个前缀下的代表性工具名（连名字都像真的，避免「名字本身非法」掩盖 E2003）
WRITE_TOOL_SAMPLES = {
    "cad_": ("cad_create_line", "cad_erase_entity", "cad_set_layer"),
    "civil_": ("civil_create_profile", "civil_build_corridor", "civil_add_surface"),
    "dyn_": ("dyn_run_lisp", "dyn_compile_csharp", "dyn_exec_command"),
    "file_": ("file_write_text", "file_delete", "file_copy_dwg"),
    "rev_": ("rev_create_wall", "rev_set_parameter"),
    "p3d_": ("p3d_create_pipe", "p3d_set_spec"),
    "calc_": ("calc_solve_alignment", "calc_earthwork"),
    "skill_": ("skill_install", "skill_run"),
}


# ============================================================================
# 进程内存（证明不 OOM）
# ============================================================================


class _PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("PageFaultCount", ctypes.c_ulong),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def rss_kb() -> int:
    """当前进程工作集（KB）；取不到时返回 -1（断言里会跳过）。"""
    try:
        counters = _PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(counters)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        handle = kernel32.GetCurrentProcess()
        if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return -1
        return int(counters.WorkingSetSize // 1024)
    except Exception:  # noqa: BLE001 - 非 Windows / 权限受限时降级
        return -1


# ============================================================================
# 分帧辅助
# ============================================================================


def frame_bytes(payload) -> bytes:
    """把 dict/str/bytes 编码成 4 字节小端长度前缀帧。"""
    if isinstance(payload, (dict, list)):
        data = json.dumps(payload, ensure_ascii=False,
                          separators=(",", ":")).encode("utf-8")
    elif isinstance(payload, str):
        data = payload.encode("utf-8")
    else:
        data = bytes(payload)
    return struct.pack("<I", len(data)) + data


def header_only(declared_length: int) -> bytes:
    """只发长度前缀（不发载荷），用于造「声明很大但中途断连」。"""
    return struct.pack("<I", declared_length)


# ============================================================================
# 写域枚举
# ============================================================================


def all_write_tools():
    """展开「每一个非只读前缀 × 每个前缀下 3 个工具名」的完整待测清单。"""
    items = []
    for prefix in WRITE_PREFIXES:
        for name in WRITE_TOOL_SAMPLES[prefix]:
            items.append((prefix, name))
    return items


# ============================================================================
# 静态审查辅助
# ============================================================================


def read_source(rel_path: str, encoding: str = "utf-8") -> str:
    with open(os.path.join(ROOT, rel_path), "r", encoding=encoding,
              errors="replace") as handle:
        return handle.read()


def iter_files(sub_dir: str, suffix: str):
    """递归列出子目录下指定后缀的文件（返回相对仓库根的路径）。"""
    base = os.path.join(ROOT, sub_dir)
    for current, _dirs, files in os.walk(base):
        for name in files:
            if name.endswith(suffix):
                full = os.path.join(current, name)
                yield os.path.relpath(full, ROOT).replace("\\", "/")


def py_imports(rel_path: str):
    """AST 解析出一个 Python 文件的顶层模块名集合（失败返回空集）。"""
    try:
        tree = ast.parse(read_source(rel_path))
    except SyntaxError:
        return set()
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                names.add(alias.name.split(".")[0])
        elif isinstance(node, ast.ImportFrom):
            if node.level == 0 and node.module:
                names.add(node.module.split(".")[0])
    return names


# ============================================================================
# 报告输出
# ============================================================================


def emit(result) -> int:
    """打印 JSON 报告并返回退出码（0 = 全通过）。result 可以是 Suite 或已生成的 dict。"""
    report = result if isinstance(result, dict) else result.report()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["failed"] == 0 else 1


def eqd(suite, actual, expected, label: str, detail: str = "") -> bool:
    """带 detail 的相等断言（harness.Suite.eq 只接受 3 参，QA 侧统一走这个）。"""
    return suite.ok(actual == expected, label,
                    detail or "expected=%r actual=%r" % (expected, actual))


def describe_error(response) -> str:
    """把响应里的 error 压成一行，便于失败明细里直接看出是哪一类错误。"""
    try:
        error = (response or {}).get("error") or {}
        return "%s/%s/%s" % (error.get("code"), error.get("field"), error.get("detail"))
    except Exception:  # noqa: BLE001
        return repr(response)[:200]



def wait_for_pipe(server, timeout: float = 5.0) -> bool:
    """等 mockhost 的命名管道真正可连（否则首个用例会偶发 HostUnreachable: pipe_not_found）。

    背景：`harness.mock_server()` 起服务端线程后立刻返回，管道实例由服务端线程创建；
    在慢机器/高负载下，第一条 `raw_client(...)` 可能早于管道就绪 → 首用例失败、后续全绿。
    这是**夹具启动竞态**，不是产品缺陷；在客户端侧加一个有界等待即可，且异常信息带上最后一次原因。
    """
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        handle = None
        try:
            handle = pipeclient.open_pipe(server.pipe_name, 200)
            return True
        except Exception as exc:  # noqa: BLE001 - 任何连接错误都属「还没就绪」
            last = exc
            time.sleep(0.02)
        finally:
            if handle is not None:
                try:
                    pipeclient.close_handle(handle)
                except Exception:  # noqa: BLE001
                    pass
    raise RuntimeError("mockhost 管道在 %.1fs 内未就绪：%r" % (timeout, last))


@contextlib.contextmanager
def raw_client(server, handshake: bool = True, tries: int = 10):
    """包一层 harness.raw_client：**只对「连不上管道」重试**，不对用例体重试。

    为什么要它：mockhost 的管道实例由服务端线程创建/回收，单会话用例里「第二连接」
    有时会撞到实例切换的瞬间 → `HostUnreachable('pipe_not_found')`。
    这是**夹具启动/切换竞态**，不是产品缺陷；但会让同一套件「跑两次结果不同」，违反规程 4。
    重试只覆盖「建立连接」这一步；一旦连上，用例体抛的异常照样原样冒出来。
    默认 `handshake=True` 与 `harness.raw_client` 保持一致（改默认值会静默改变所有用例的语义）。
    """
    last = None
    client = None
    for _ in range(tries):
        try:
            client = harness.RawClient(server.pipe_name)
            break
        except Exception as exc:  # noqa: BLE001 - pipe_not_found / 暂时不可用
            last = exc
            time.sleep(0.05)
    if client is None:
        raise RuntimeError("raw_client 连续 %d 次未能连上管道：%r" % (tries, last))
    try:
        if handshake:
            ack = client.hello()
            client.token = ack.get("sessionToken")
            client.instance_id = ack.get("hostInstanceId")
        yield client
    finally:
        try:
            client.close()
        except Exception:  # noqa: BLE001
            pass

__all__ = [
    "ROOT", "TESTS_DIR", "MCP_DIR", "MOCK_DIR", "HOST_DIR", "PROTOCOL_DIR",
    "MAX_PAYLOAD", "WRITE_PREFIXES", "WRITE_TOOL_SAMPLES",
    "harness", "pipeclient", "rss_kb", "frame_bytes", "header_only", "all_write_tools",
    "read_source", "iter_files", "py_imports", "emit", "eqd", "describe_error",
    "wait_for_pipe", "raw_client",
]
