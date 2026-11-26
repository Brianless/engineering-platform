"""mockhost/mock_host.py —— 无 CAD 环境下的宿主替身（M0 唯一可自动验证路径）。

它模拟的是 **AutoCAD 宿主进程**，不是 MCP 客户端。因此：

* 只依赖 Python 标准库（`ctypes` 调 Win32 命名管道，`zlib` 手写 PNG）；
* 不 import `mcp/` 下的任何模块（宿主与 MCP 是两侧独立进程，耦合会掩盖真实协议问题）；
* 协议行为严格对齐 `protocol/` 下 4 份 schema 与 `docs/00-M0-实施设计.md` §2：
  分帧、握手、封套、错误码、写通道关闭、预算与摘要约定。

与真实宿主的差异（刻意保留，便于定位问题）：

| 能力 | 真实宿主（host/） | mockhost |
|---|---|---|
| 几何来源 | AutoCAD 数据库遍历 | `mock_drawing.build_model()` 内置脱敏样例 |
| 主线程语义 | `Application.Idle` 派发 + 看门狗 | 进程内互斥锁串行（无双线程竞态可测） |
| 预览图 | `System.Drawing` 按真实几何绘制 | 极简光栅器按包围盒/线段绘制结构示意 |
| Civil 3D | 反射探测 `AeccDbMgd` | 恒不可用（与「Civil 3D 未安装」环境事实一致） |

用法：
    python mockhost/mock_host.py --demo            # 起服务 + 内置自检客户端跑通全链路
    python mockhost/mock_host.py --serve-seconds 60
    python mockhost/mock_host.py --silent          # 握手后不响应，用于测心跳判死

模块内也可直接使用（测试用，进程内起服务更稳）：

    server = PipeServer(host=MockHost())
    server.start()
    ...  # 用 mcp/pipeclient.py 连 server.pipe_name
    server.stop()
"""

from __future__ import annotations

import argparse
import base64
import ctypes
import ctypes.wintypes as wintypes
import hashlib
import json
import os
import re
import sys
import threading
import time
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mock_drawing import build_model  # noqa: E402  （同目录模块，必须后置导入）

# ============================================================================
# 一、常量（协议 1.0 唯一真相源）
# ============================================================================

PROTOCOL_VERSION = "1.0"
PROTOCOL_MAJOR = 1
PROTOCOL_MINOR = 0

HOST_VERSION = "0.1.0+mock"
HOST_APP = "mock"
# 第二轮：契约新增宿主身份字段。mockhost 模拟「AutoCAD 2022 + 无 Civil 3D」这一档，
# 用来验证降级路径（gaps[] + W1001 + 代理对象 confidence=0）；真实 Civil 3D 宿主由 e2e 覆盖。
HOST_PRODUCT = "AutoCAD"
HOST_RELEASE = "R24.1"
MESSAGE_LIMIT_BYTES = 1024 * 1024
MAX_INSTANCES = 4
TASK_RING_SIZE = 200

HEARTBEAT_INTERVAL_MS = 5000
HEARTBEAT_MISS_LIMIT = 3
HOST_IDLE_CLOSE_MS = 15000

PREVIEW_MAX_WIDTH = 2400
PREVIEW_MAX_HEIGHT = 1600
PREVIEW_MAX_BYTES = 1024 * 1024
PREVIEW_NOTE = "结构示意图，非出图成果"

#: 写域前缀：M0 一律 E2003（设计 §2.2 / 硬约束 5）
#: 判定时先 `.upper()` 归一，保证 CAD_CREATE_LINE / Cad_Create_Line 都命中写域
#: （否则会退化成 E2001「工具不存在」，把「写通道关闭」误报成「名字写错」）。
WRITE_DOMAIN_PREFIXES = ("cad_", "civil_", "dyn_", "file_", "rev_", "p3d_", "calc_", "skill_")
WRITE_DOMAIN_PREFIXES_UPPER = tuple(prefix.upper() for prefix in WRITE_DOMAIN_PREFIXES)

#: hello.requestedFeatures 的合法能力名（与 handshake.schema.json 的 enum 同源）
SUPPORTED_FEATURES = ("preview", "cancel", "heartbeat", "taskReconcile",
                      "layers", "query", "describe", "anomalies")

#: clientNonce：16–64 位十六进制（handshake.schema.json 的 pattern）
CLIENT_NONCE_RE = re.compile(r"^[0-9a-fA-F]{16,64}$")

#: 只读域名（capabilities.domains）
READ_DOMAINS = ("sys", "read", "task")

#: 8 色调色板（与 host/Preview/PreviewRenderer.cs 保持一致）
PALETTE = ["#1f77b4", "#ff7f0e", "#2ca02c", "#d62728",
           "#9467bd", "#8c564b", "#e377c2", "#17becf"]

HANDLE_RE = re.compile(r"^[0-9A-Fa-f]{1,16}$")
OP_ID_RE = re.compile(r"^op-[0-9T:A-Za-z-]{8,64}$")


# ============================================================================
# 二、时间工具（只用 ISO 8601 字符串，禁止序列化 DateTime）
# ============================================================================


def utc_iso(epoch_seconds: float | None = None) -> str:
    """`2026-11-21T14:03:11.902Z` —— 固定 3 位毫秒、Z 结尾（envelope.schema.json#UtcDateTime）。"""
    stamp = time.time() if epoch_seconds is None else epoch_seconds
    seconds = int(stamp)
    millis = int(round((stamp - seconds) * 1000.0))
    if millis >= 1000:  # 四舍五入进位
        seconds += 1
        millis -= 1000
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(seconds)) + ".%03dZ" % millis


def clip(text: str, limit: int) -> str:
    """按字符数截断并加省略号（用于 summary ≤ 400、message ≤ 200 等 schema 约束）。"""
    value = text or ""
    if len(value) <= limit:
        return value
    return value[: max(0, limit - 1)] + "…"


# ============================================================================
# 三、Win32 命名管道服务端（ctypes）
# ============================================================================

ERROR_PIPE_CONNECTED = 535
ERROR_PIPE_BUSY = 231
ERROR_BROKEN_PIPE = 109
ERROR_NO_DATA = 232
ERROR_PIPE_NOT_CONNECTED = 233
ERROR_IO_PENDING = 997
ERROR_OPERATION_ABORTED = 995

PIPE_ACCESS_DUPLEX = 0x00000003
#: 必须带 OVERLAPPED：同步句柄会让「读线程阻塞」连带把同一句柄的写也堵死，
#: 而且 `CloseHandle` / `DisconnectNamedPipe` 会一直等挂起 I/O，进程直接死锁（实测）。
#: 真实宿主侧对应 `PipeOptions.Asynchronous`（同一条约束，见偏差清单）。
FILE_FLAG_OVERLAPPED = 0x40000000
PIPE_TYPE_BYTE = 0x00000000
PIPE_READMODE_BYTE = 0x00000000
PIPE_WAIT = 0x00000000
PIPE_UNLIMITED_INSTANCES = 255
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
INFINITE = 0xFFFFFFFF
WAIT_OBJECT_0 = 0

_IS_WINDOWS = os.name == "nt"

if _IS_WINDOWS:
    _kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)

    _kernel32.CreateNamedPipeW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
        wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
    ]
    _kernel32.CreateNamedPipeW.restype = wintypes.HANDLE

    _kernel32.ConnectNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
    _kernel32.ConnectNamedPipe.restype = wintypes.BOOL

    _kernel32.DisconnectNamedPipe.argtypes = [wintypes.HANDLE]
    _kernel32.DisconnectNamedPipe.restype = wintypes.BOOL

    _kernel32.ReadFile.argtypes = [
        wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
    ]
    _kernel32.ReadFile.restype = wintypes.BOOL

    _kernel32.WriteFile.argtypes = [
        wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
    ]
    _kernel32.WriteFile.restype = wintypes.BOOL

    _kernel32.CreateEventW.argtypes = [
        ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
    _kernel32.CreateEventW.restype = wintypes.HANDLE

    _kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    _kernel32.WaitForSingleObject.restype = wintypes.DWORD

    _kernel32.GetOverlappedResult.argtypes = [
        wintypes.HANDLE, ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
    _kernel32.GetOverlappedResult.restype = wintypes.BOOL

    _kernel32.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
    _kernel32.CancelIoEx.restype = wintypes.BOOL

    _kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    _kernel32.CloseHandle.restype = wintypes.BOOL

    _kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
        wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE,
    ]
    _kernel32.CreateFileW.restype = wintypes.HANDLE

    _kernel32.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
    _kernel32.WaitNamedPipeW.restype = wintypes.BOOL

    _kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    _kernel32.OpenProcess.restype = wintypes.HANDLE

    _kernel32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    _kernel32.GetExitCodeProcess.restype = wintypes.BOOL

    _kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    _kernel32.LocalFree.restype = ctypes.c_void_p

    _advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p,
    ]
    _advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wintypes.BOOL
else:  # pragma: no cover - 本项目只在 Windows 上运行
    _kernel32 = None
    _advapi32 = None


class OVERLAPPED(ctypes.Structure):
    """Win32 OVERLAPPED（只需 hEvent，其余字段必须存在并归零）。"""

    _fields_ = [
        ("Internal", ctypes.c_void_p),
        ("InternalHigh", ctypes.c_void_p),
        ("Offset", wintypes.DWORD),
        ("OffsetHigh", wintypes.DWORD),
        ("hEvent", wintypes.HANDLE),
    ]


class SECURITY_ATTRIBUTES(ctypes.Structure):
    """CreateNamedPipeW 的安全属性（用于把 DACL 限制为「仅当前用户」）。"""

    _fields_ = [
        ("nLength", wintypes.DWORD),
        ("lpSecurityDescriptor", ctypes.c_void_p),
        ("bInheritHandle", wintypes.BOOL),
    ]


def _handle_invalid(handle) -> bool:
    if handle is None:
        return True
    if isinstance(handle, int):
        return handle == 0 or handle == INVALID_HANDLE_VALUE
    return False


def close_handle(handle) -> None:
    """静默关闭句柄（重复关闭/空句柄都不抛）。"""
    if _handle_invalid(handle) or not _IS_WINDOWS:
        return
    try:
        _kernel32.CloseHandle(handle)
    except Exception:
        pass


def pid_alive(pid) -> bool:
    """进程是否存活（OpenProcess + GetExitCodeProcess，STILL_ACTIVE=259）。"""
    if not pid or not _IS_WINDOWS:
        return False
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    STILL_ACTIVE = 259
    handle = _kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
    if _handle_invalid(handle):
        return False
    try:
        code = wintypes.DWORD(0)
        if not _kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return False
        return code.value == STILL_ACTIVE
    finally:
        close_handle(handle)


# ============================================================================
# 四、用户标识 / 管道名 / 发现文件（必须与 host/Pipe/PipeName.cs 逐字节一致）
# ============================================================================


def user_key() -> str:
    """`USERDOMAIN\\USERNAME` 大写；USERDOMAIN 为空时退化为 USERNAME。"""
    domain = os.environ.get("USERDOMAIN") or ""
    name = os.environ.get("USERNAME") or os.environ.get("USER") or ""
    key = (domain + "\\" + name) if domain else name
    return key.upper()


def user_hash8(key: str | None = None) -> str:
    """`sha256(utf8(key))[:8]`。"""
    value = key if key is not None else user_key()
    return hashlib.sha256(value.upper().encode("utf-8")).hexdigest()[:8]


def local_appdata() -> str:
    value = os.environ.get("LOCALAPPDATA")
    if value:
        return value
    return os.path.join(os.path.expanduser("~"), ".local", "share")


def platform_root() -> str:
    return os.path.join(local_appdata(), "EngineeringPlatform")


def decode_instance_dir() -> str:
    env = os.environ.get("EP_INSTANCE_DIR")
    if env:
        return os.path.expandvars(env)
    return os.path.join(platform_root(), "instances")


def build_pipe_name(app: str, instance_id: str, hash8: str | None = None) -> str:
    """`\\\\.\\pipe\\engineering-platform.v1.<userHash8>.<app>.<instanceId>`（设计 §7.3）。"""
    return r"\\.\pipe\engineering-platform.v1.%s.%s.%s" % (hash8 or user_hash8(), app, instance_id)


def discovery_file_path(app: str, instance_id: str, hash8: str | None = None,
                        directory: str | None = None) -> str:
    base = directory or decode_instance_dir()
    return os.path.join(base, "%s.%s.%s.json" % (hash8 or user_hash8(), app, instance_id))


def project_root() -> str:
    """仓库根目录（mockhost/ 的上一级）。"""
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def tool_catalog_hash() -> str:
    """`sha256:` + sha256(protocol/tools.schema.json 原始字节)[:16]。

    与 `host/Protocol/ToolCatalog.cs` 的 CatalogHash 算法完全一致：
    两侧算的是**同一份文件字节**，所以 M0 不出现 W1007。
    """
    path = os.path.join(project_root(), "protocol", "tools.schema.json")
    try:
        with open(path, "rb") as handle:
            digest = hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return "sha256:0000000000000000"
    return "sha256:" + digest[:16]


# ============================================================================
# 五、分帧（4 字节小端长度前缀 + UTF-8 JSON）
# ============================================================================

FRAME_OK = "ok"
FRAME_TOO_LARGE = "too_large"
FRAME_EMPTY = "empty"
FRAME_EOF = "eof"
FRAME_VIOLATION = "violation"


def encode_frame(payload) -> bytes:
    """dict/list → 长度前缀 + 紧凑 UTF-8 JSON。"""
    if isinstance(payload, (dict, list)):
        data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    elif isinstance(payload, str):
        data = payload.encode("utf-8")
    elif isinstance(payload, (bytes, bytearray)):
        data = bytes(payload)
    else:
        raise TypeError("unsupported payload type: %r" % type(payload))
    if not data:
        raise ValueError("empty_payload")
    return len(data).to_bytes(4, "little") + data


def _overlapped_io(handle, func, buffer, count: int):
    """执行一次 overlapped 读写；返回 (状态, 字节数, 错误码)。

    状态：
        "ok"      正常完成
        "eof"     对端断开（BROKEN_PIPE / NO_DATA / NOT_CONNECTED）
        "aborted" 被 CancelIoEx 取消（停止服务时正常出现）
        "error"   其它错误
    """
    overlapped = OVERLAPPED()
    event = _kernel32.CreateEventW(None, True, False, None)
    if not event:
        return "error", 0, ctypes.get_last_error()
    overlapped.hEvent = event
    transferred = wintypes.DWORD(0)
    try:
        ok = func(handle, buffer, count, ctypes.byref(transferred), ctypes.byref(overlapped))
        if ok:
            return "ok", transferred.value, 0
        error = ctypes.get_last_error()
        if error == ERROR_IO_PENDING:
            if _kernel32.WaitForSingleObject(event, INFINITE) != WAIT_OBJECT_0:
                return "error", 0, ctypes.get_last_error()
            if _kernel32.GetOverlappedResult(handle, ctypes.byref(overlapped),
                                             ctypes.byref(transferred), False):
                return "ok", transferred.value, 0
            error = ctypes.get_last_error()
        if error in (ERROR_BROKEN_PIPE, ERROR_PIPE_NOT_CONNECTED, ERROR_NO_DATA):
            return "eof", 0, error
        if error == ERROR_OPERATION_ABORTED:
            return "aborted", 0, error
        return "error", 0, error
    finally:
        close_handle(event)


def _connect_overlapped(handle) -> str:
    """overlapped ConnectNamedPipe：返回 connected / aborted / error。"""
    overlapped = OVERLAPPED()
    event = _kernel32.CreateEventW(None, True, False, None)
    if not event:
        return "error"
    overlapped.hEvent = event
    try:
        if _kernel32.ConnectNamedPipe(handle, ctypes.byref(overlapped)):
            return "connected"
        error = ctypes.get_last_error()
        if error == ERROR_PIPE_CONNECTED:
            return "connected"          # 客户端抢在 ConnectNamedPipe 之前连上了
        if error == ERROR_IO_PENDING:
            _kernel32.WaitForSingleObject(event, INFINITE)
            transferred = wintypes.DWORD(0)
            if _kernel32.GetOverlappedResult(handle, ctypes.byref(overlapped),
                                             ctypes.byref(transferred), False):
                return "connected"
            error = ctypes.get_last_error()
            if error == ERROR_PIPE_CONNECTED:
                return "connected"
            if error == ERROR_OPERATION_ABORTED:
                return "aborted"
            return "error"
        if error == ERROR_OPERATION_ABORTED:
            return "aborted"
        return "error"
    finally:
        close_handle(event)


def _read_exact(handle, count: int):
    """从管道句柄读满 count 字节；EOF/取消/错误返回 None。"""
    chunks = []
    remaining = count
    while remaining > 0:
        buffer = ctypes.create_string_buffer(remaining)
        status, read, _error = _overlapped_io(handle, _kernel32.ReadFile, buffer, remaining)
        if status != "ok" or read <= 0:
            return None
        chunks.append(buffer.raw[:read])
        remaining -= read
    return b"".join(chunks)


def _discard(handle, count: int) -> bool:
    """丢弃 count 字节。超限帧必须读完，否则流错位（设计 §2.1）。"""
    remaining = count
    while remaining > 0:
        size = min(remaining, 8192)
        buffer = ctypes.create_string_buffer(size)
        status, read, _error = _overlapped_io(handle, _kernel32.ReadFile, buffer, size)
        if status != "ok" or read <= 0:
            return False
        remaining -= read
    return True


def read_frame(handle):
    """读一帧。返回 (status, body_bytes)。

    * FRAME_OK —— body 为载荷；
    * FRAME_TOO_LARGE —— 已读完丢弃，调用方回 E1001 且连接保持可用；
    * FRAME_EMPTY / FRAME_EOF / FRAME_VIOLATION —— 连接必须关闭。
    """
    header = _read_exact(handle, 4)
    if header is None:
        return FRAME_EOF, b""
    length = int.from_bytes(header, "little")
    if length == 0:
        return FRAME_EMPTY, b""
    if length > MESSAGE_LIMIT_BYTES:
        if not _discard(handle, length):
            return FRAME_VIOLATION, b""
        return FRAME_TOO_LARGE, str(length).encode("ascii")
    body = _read_exact(handle, length)
    if body is None:
        return FRAME_VIOLATION, b""
    return FRAME_OK, body


def write_all(handle, data: bytes) -> bool:
    """写满 data；失败返回 False（连接已断）。"""
    total = len(data)
    written_total = 0
    while written_total < total:
        chunk = data[written_total:]
        buffer = ctypes.create_string_buffer(chunk, len(chunk))
        status, written, _error = _overlapped_io(handle, _kernel32.WriteFile,
                                                 buffer, len(chunk))
        if status != "ok" or written <= 0:
            return False
        written_total += written
    return True


def write_json_frame(handle, payload) -> bool:
    """序列化 + 分帧 + 写；frame 超 1 MiB 时返回 False（调用方降级）。"""
    try:
        data = encode_frame(payload)
    except (TypeError, ValueError):
        return False
    if len(data) - 4 > MESSAGE_LIMIT_BYTES:
        return False
    return write_all(handle, data)


# ============================================================================
# 六、极简 PNG 光栅器（不依赖 PIL / System.Drawing）
# ============================================================================


class Canvas:
    """RGB 画布：只实现线、矩形、点，足够画结构示意图。"""

    def __init__(self, width: int, height: int, background=(255, 255, 255)):
        self.width = int(width)
        self.height = int(height)
        self.pixels = bytearray(bytes(background) * (self.width * self.height))

    def _clip(self, value, upper):
        if value < 0:
            return 0
        if value >= upper:
            return upper - 1
        return value

    def set_pixel(self, x, y, color):
        if 0 <= x < self.width and 0 <= y < self.height:
            offset = (y * self.width + x) * 3
            self.pixels[offset:offset + 3] = bytes(color)

    def line(self, x0, y0, x1, y1, color, width: int = 1):
        """Bresenham 直线；width > 1 时按方形笔刷加粗。"""
        x0, y0, x1, y1 = int(x0), int(y0), int(x1), int(y1)
        dx = abs(x1 - x0)
        dy = -abs(y1 - y0)
        sx = 1 if x0 < x1 else -1
        sy = 1 if y0 < y1 else -1
        err = dx + dy
        guard = 0
        limit = dx - dy + 8  # 防止异常输入导致死循环
        while True:
            self._brush(x0, y0, color, width)
            if x0 == x1 and y0 == y1:
                break
            guard += 1
            if guard > limit:
                break
            double = 2 * err
            if double >= dy:
                err += dy
                x0 += sx
            if double <= dx:
                err += dx
                y0 += sy

    def _brush(self, x, y, color, width: int):
        if width <= 1:
            self.set_pixel(x, y, color)
            return
        half = width // 2
        for dy in range(-half, half + 1):
            for dx in range(-half, half + 1):
                self.set_pixel(x + dx, y + dy, color)

    def rect(self, x0, y0, x1, y1, color, width: int = 1, fill: bool = False):
        x0, y0, x1, y1 = int(x0), int(y0), int(x1), int(y1)
        if x1 < x0:
            x0, x1 = x1, x0
        if y1 < y0:
            y0, y1 = y1, y0
        if fill:
            for y in range(self._clip(y0, self.height), self._clip(y1, self.height) + 1):
                for x in range(self._clip(x0, self.width), self._clip(x1, self.width) + 1):
                    self.set_pixel(x, y, color)
            return
        self.line(x0, y0, x1, y0, color, width)
        self.line(x1, y0, x1, y1, color, width)
        self.line(x1, y1, x0, y1, color, width)
        self.line(x0, y1, x0, y0, color, width)

    def cross(self, x, y, size: int, color):
        self.line(x - size, y, x + size, y, color)
        self.line(x, y - size, x, y + size, color)

    def to_png(self) -> bytes:
        """RGB8 真彩色 PNG（IHDR + 单 IDAT + IEND）。

        过滤类型统一为 0（None）：结构示意图大面积是同色背景，zlib 已能压到很小。
        """
        raw = bytearray()
        stride = self.width * 3
        for row in range(self.height):
            raw.append(0)
            raw += self.pixels[row * stride:(row + 1) * stride]

        def chunk(tag: bytes, payload: bytes) -> bytes:
            return (len(payload).to_bytes(4, "big") + tag + payload
                    + (zlib.crc32(tag + payload) & 0xFFFFFFFF).to_bytes(4, "big"))

        ihdr = (self.width.to_bytes(4, "big") + self.height.to_bytes(4, "big")
                + bytes([8, 2, 0, 0, 0]))
        return (b"\x89PNG\r\n\x1a\n"
                + chunk(b"IHDR", ihdr)
                + chunk(b"IDAT", zlib.compress(bytes(raw), 6))
                + chunk(b"IEND", b""))


def hex_to_rgb(value: str):
    text = (value or "#000000").lstrip("#")
    return (int(text[0:2], 16), int(text[2:4], 16), int(text[4:6], 16))


# ============================================================================
# 七、游标编解码（不透明：base64url("h:<handle>")，与 host/Semantic 一致）
# ============================================================================


def encode_cursor(handle: str) -> str:
    raw = ("h:" + handle).encode("utf-8")
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def decode_cursor(cursor) -> str | None:
    """返回句柄（大写）；空游标返回 None；非法游标抛 ValueError。"""
    if not cursor:
        return None
    text = str(cursor)
    padding = "=" * (-len(text) % 4)
    try:
        raw = base64.urlsafe_b64decode((text + padding).encode("ascii")).decode("utf-8")
    except (ValueError, UnicodeDecodeError):
        raise ValueError("cursor_malformed")
    if not raw.startswith("h:"):
        raise ValueError("cursor_malformed")
    return raw[2:].upper()


# ============================================================================
# 八、错误 / 告警构造（对齐 protocol/errors.json 与 envelope.schema.json#Error）
# ============================================================================

ERROR_META = {
    "E1001": ("参数无效", True, "检查参数范围与类型后重试"),
    "E1002": ("目标图纸或对象与审批不符", False, "重新读取图纸并确认目标"),
    "E1003": ("宿主忙碌", True, "稍后重试；若有命令在跑请等它结束"),
    "E1004": ("存在模态对话框", True, "关闭 AutoCAD 中的模态对话框后重试"),
    "E1005": ("文档被锁或只读", True, "打开一个可读的图纸后重试"),
    "E1006": ("审批令牌无效或过期", False, "M0 写通道关闭，不要传 approvalToken"),
    "E2001": ("API 索引中不存在该能力", True, "调用 sys_capabilities 查看可用工具名"),
    "E2002": ("动态代码编译失败", True, "改用已注册的只读工具"),
    "E2003": ("命中禁止能力", False, "M0 只允许只读工具；写操作需后续里程碑"),
    "E3001": ("后置验证失败", False, "重读对象确认实际状态"),
    "E3002": ("结果不明", False, "用 task_reconcile 核对，重试需新 opId"),
    "E4001": ("协议或宿主版本不匹配", False, "升级 MCP 或宿主使主版本一致后重连"),
    "E5001": ("磁盘空间或配额不足", True, "清理磁盘或降低预览图尺寸"),
    "E6001": ("角色权限不足", False, "联系管理员开通权限"),
}

WARNING_MESSAGES = {
    "W1001": "Civil 3D 原生 API 不可用，相关对象仅以代理对象呈现",
    "W1002": "客户端协议次版本高于宿主，已按宿主次版本降级",
    "W1003": "心跳超时",
    "W1004": "图纸已变化，请重新读取",
    "W1005": "采集耗时偏高",
    "W1006": "字体回退",
    "W1007": "工具目录不一致",
    "W1008": "结果被截断",
    "W1009": "对象数量超采集上限",
}


def make_error(code: str, detail: str | None = None, field: str | None = None,
               message: str | None = None) -> dict:
    """构造 schema 合法的 Error 对象（必填 code/message/retriable）。"""
    default_message, retriable, advice = ERROR_META.get(code, ("未知错误", False, None))
    return {
        "code": code,
        "message": clip(message or default_message, 500),
        "field": field,
        "detail": clip(detail, 2000) if detail else None,
        "retriable": bool(retriable),
        "userAdvice": advice,
    }


def make_warning(code: str, detail: str | None = None) -> dict:
    return {
        "code": code,
        "message": clip(WARNING_MESSAGES.get(code, code), 500),
        "detail": clip(detail, 2000) if detail else None,
    }


#: D5：`truncated=true` 且没有 nextCursor 可翻页时的兜底下钻建议。
#: 以前这种组合会返回空数组，调用方看到「被截断 + 无建议」只能重复调同一个工具。
DRILLDOWN_FALLBACK = (
    {"tool": "read_query", "params": {"layer": "*", "limit": 20},
     "why": "按图层/范围收窄查询（结果被预算截断，不是数据缺失）"},
    {"tool": "read_query", "params": {"limit": 100},
     "why": "增大 limit 到上限 100（默认 20）单次取更多明细"},
)


def ensure_drilldown(drilldown, truncated: bool, has_cursor: bool):
    """D5 契约：truncated 且无游标时 drilldown 不得为空。"""
    if not truncated or drilldown or has_cursor:
        return drilldown
    return [dict(item) for item in DRILLDOWN_FALLBACK]


class ToolFailure(Exception):
    """工具内部抛出的业务失败（会被转成 ok:false 封套）。"""

    def __init__(self, code: str, detail: str | None = None, field: str | None = None,
                 message: str | None = None):
        super().__init__("%s %s" % (code, detail or ""))
        self.code = code
        self.detail = detail
        self.field = field
        self.message = message

    def to_error(self) -> dict:
        return make_error(self.code, self.detail, self.field, self.message)


# ============================================================================
# 九、任务环（内存 200 条；task_* 工具的数据源）
# ============================================================================


class TaskRing:
    """固定容量环形缓冲：最新在前。"""

    def __init__(self, capacity: int = TASK_RING_SIZE):
        self.capacity = capacity
        self._items = []
        self._lock = threading.Lock()

    def add(self, record: dict) -> None:
        with self._lock:
            self._items.insert(0, record)
            if len(self._items) > self.capacity:
                del self._items[self.capacity:]

    def find(self, op_id: str):
        with self._lock:
            for item in self._items:
                if item.get("opId") == op_id:
                    return dict(item)
        return None

    def update(self, op_id: str, **fields) -> bool:
        with self._lock:
            for item in self._items:
                if item.get("opId") == op_id:
                    item.update(fields)
                    return True
        return False

    def recent(self, limit: int, since_utc: str | None = None) -> list:
        with self._lock:
            items = [dict(item) for item in self._items]
        if since_utc:
            items = [item for item in items if (item.get("startedAtUtc") or "") >= since_utc]
        return items[:limit]


# ============================================================================
# 十、MockHost —— 16 个只读工具的实现
# ============================================================================

#: 参数校验规格：tool → {param: spec}
#: spec: ("int", min, max) / ("bool",) / ("str", maxLen) / ("str|null", maxLen)
#:       ("enum", [..]) / ("numarray4",) / ("strarray", maxItems) / ("strmap",)
#:       ("extent",)  —— "all" 或 [x0,y0,x1,y1]

#: `read_civil_list.type` 的允许集（与 `protocol/tools.schema.json` 的 enum、
#: `host/Tools/ReadToolHandlers.cs::CivilRecordsOf` 的 case 逐一对应）。
#:
#: 刻意**不含** `corridor` / `sampleLineGroup`：`Civil3dSnapshot` 目前只采这五类，
#: 列一个代码永远返回空集的类型等于给模型假承诺。它们随 N05 方法补充再开放。
CIVIL_TYPES = ("any", "alignment", "profile", "surface", "pipeNetwork", "pointGroup")

PARAM_SPECS = {
    "sys_ping": {},
    "sys_status": {},
    "sys_version": {},
    "sys_health": {},
    "sys_capabilities": {},
    "sys_list_instances": {},
    "read_overview": {"refresh": ("bool",)},
    "read_layers": {
        "disciplines": ("strarray", 10),
        "namePattern": ("str|null", 120),
        "minEntities": ("int", 0, 1000000),
        "limit": ("int", 1, 200),
        "cursor": ("str|null", 64),
    },
    "read_query": {
        "layer": ("str|null", 120),
        "dxfName": ("str|null", 40),
        "extent": ("numarray4|null",),
        "textContains": ("str|null", 80),
        "attrEquals": ("strmap|null",),
        "meaning": ("str|null", 60),
        "minConfidence": ("number", 0.0, 1.0),
        "limit": ("int", 1, 100),
        "cursor": ("str|null", 64),
    },
    "read_describe": {
        "handle": ("handle",),
        "fingerprint": ("str|null", 40),
    },
    "read_anomalies": {
        "types": ("enumarray", 8),
        "layer": ("str|null", 120),
        "severity": ("severity|null",),
        "limit": ("int", 1, 100),
        "cursor": ("str|null", 64),
    },
    "read_preview": {
        "extent": ("extent",),
        "width": ("int", 200, PREVIEW_MAX_WIDTH),
        "height": ("int", 200, PREVIEW_MAX_HEIGHT),
        "highlightHandles": ("handlearray", 50),
        "format": ("const:png",),
    },
    "task_status": {"opId": ("opid",)},
    "task_list": {"limit": ("int", 1, 200), "sinceUtc": ("str|null", 40)},
    "task_cancel": {"opId": ("opid",)},
    "task_reconcile": {"opId": ("opid",)},
    # N04-B：两个 Civil 只读工具（契约见 protocol/tools.schema.json）
    "read_civil_list": {
        # type 走 enum 而非 str：越界必须**拒**，不能静默返回空集。
        # 「未开放的 type」与「图上没有这种对象」在行为上若无法区分，收窄 enum 就白收了。
        "type": ("enum", CIVIL_TYPES),
        "nameContains": ("str|null", 120),
        "limit": ("int", 1, 500),
        "cursor": ("str|null", 64),
    },
    "write_preview": {
        "kind": ("str|null", 32),
        "points": ("any",),
        "layer": ("str|null", 255),
        "block": ("str|null", 255),
    },
    "write_approve": {
        "kind": ("str|null", 32),
        "points": ("any",),
        "layer": ("str|null", 255),
        "block": ("str|null", 255),
        "paramsDigest": ("str|null", 80),
        "targetFingerprint": ("str|null", 40),
        "userConfirmation": ("bool",),
    },
    "write_insert_points": {
        "kind": ("str|null", 32),
        "points": ("any",),
        "layer": ("str|null", 255),
        "block": ("str|null", 255),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
    },
    "read_civil_surfaces": {"limit": ("int", 1, 500)},
    "read_civil_alignments": {"limit": ("int", 1, 500)},
    "write_surface_breakline": {
        "kind": ("str|null", 40),
        "surface": ("str|null", 255),
        "points": ("list",),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
    },
    "write_command": {
        "kind": ("str|null", 32),
        "command": ("str|null", 500),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
        "dryRun": ("bool",),
        "wrapUndoGroup": ("bool",),
    },
    "read_civil_object": {
        "handle": ("handle",),
        "properties": ("strarray", 40),
        "fingerprint": ("str|null", 40),
    },
    "export_surface_grid": {
        "surface": ("str|null", 255),
        "resolution": ("number", 0.1, 500.0),
        "maxGridDim": ("int", 10, 2000),
        "format": ("enum", ("npz", "json")),
        "outPath": ("str|null", 500),
        "extent": ("numarray4|null",),
    },
    "export_layer_geojson": {
        "layers": ("strarray", 100),
        "layerCategoryMap": ("strmap|null",),
        "limit": ("int", 1, 100000),
        "outPath": ("str|null", 500),
    },
    "read_active_selection": {
        "limit": ("int", 1, 2000),
    },
    "read_viewport_snapshot": {
        "width": ("int", 200, 2560),
        "height": ("int", 200, 1600),
        "outPath": ("str|null", 500),
        "includeBase64": ("bool",),
    },
    "read_object_xdata": {
        "handle": ("handle",),
    },
    "write_object_xdata": {
        "handle": ("handle",),
        "regApp": ("str|null", 120),
        "xrecordKey": ("str|null", 120),
        "entries": ("any",),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
    },
    "write_undo": {
        "steps": ("int", 1, 50),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
    },
    "write_alignment_plan": {
        "planPath": ("str|null", 500),
        "name": ("str|null", 200),
        "layer": ("str|null", 255),
        "surfaceName": ("str|null", 255),
        "eraseSourcePolyline": ("bool",),
        "points": ("any",),
        "sampledCenterline": ("any",),
        "alignment": ("any",),
        "profile": ("any",),
        "pvis": ("any",),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
    },
    "write_eval_csharp": {
        "code": ("str|null", 65536),
        "args": ("any",),
        "usings": ("strarray", 30),
        "commit": ("bool",),
        "shadowDryRun": ("bool",),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
    },
    "write_entities": {
        "entities": ("any",),
        "layer": ("str|null", 255),
        "strict": ("bool",),
        "zoomExtents": ("bool",),
        "paramsDigest": ("str|null", 80),
        "approvalToken": ("str|null", 900),
    },
    "preview_transient": {
        "action": ("enum", ("add", "replace", "clear", "list")),
        "group": ("str|null", 120),
        "primitives": ("any",),
    },
    "batch_side_database": {
        "files": ("strarray", 50),
        "action": ("enum", ("inspect", "export_layers", "find_replace_text")),
        "findText": ("str|null", 500),
        "replaceText": ("str|null", 500),
        "saveChanges": ("bool",),
    },
}

ANOMALY_TYPES = ("duplicate_point", "zero_length", "extreme_coord", "degenerate_extents",
                 "proxy_object", "missing_unit", "empty_attr", "extremely_long_text")

#: 必填参数（`PARAM_SPECS` 只表达「字段存在时怎么校验」，必填要在这一层补）。
#: 与 C# `ParamValidator` 里的 `AddIssue("params.x", "required")` 同码同 detail。
#: 目前**没有**工具的必填参数走这张表：`read_civil_list.type` 定为「不传 = any」，
#: 必填与否由 schema 的 `required` 决定，schema 没标必填就不该在这里偷偷加严。
#: 保留这张表是因为 `validate_params` 的通用 required 检查仍然有用（将来加必填工具直接用）。
REQUIRED_PARAMS = {}


def validate_params(tool: str, params: dict) -> list:
    """按 PARAM_SPECS 校验；返回 [(field, detail)]，空列表表示通过。

    宿主侧必须二次校验（设计 §2.2：不只信 MCP）。
    """
    issues = []
    spec = PARAM_SPECS.get(tool)
    if spec is None:
        return issues
    if not isinstance(params, dict):
        return [("params", "params_must_be_object")]
    for key in params:
        if key not in spec:
            issues.append(("params.%s" % key, "unknown_parameter"))
    for key in REQUIRED_PARAMS.get(tool, ()):
        if params.get(key) in (None, ""):
            issues.append(("params.%s" % key, "required"))
    for key, rule in spec.items():
        if key not in params:
            continue
        value = params[key]
        # NIT-3：显式 null 与「字段未提供」同义（与 C# ParamValidator.Has 对齐）。
        # 模型经常把可选参数发成 "limit": null，两端一个宽容一个严格会让同一请求
        # 在 mock 环境被拒、在真机通过——这种差异只能在 CI 里炸，必须消掉。
        if value is None:
            continue
        kind = rule[0]
        if kind == "any":
            # 写参数（points）在此只占位：逐点校验由宿主 WriteToolHandlers 给出 index 级错误，
            # 替身不重复实现一套，避免两端校验规则漂移。
            pass
        elif kind == "bool":
            if not isinstance(value, bool):
                issues.append(("params.%s" % key, "must_be_boolean"))
        elif kind == "int":
            if isinstance(value, bool) or not isinstance(value, int):
                issues.append(("params.%s" % key, "must_be_integer"))
            elif not (rule[1] <= value <= rule[2]):
                issues.append(("params.%s" % key,
                               "must_be_in_%d_%d_got_%d" % (rule[1], rule[2], value)))
        elif kind == "number":
            if isinstance(value, bool) or not isinstance(value, (int, float)):
                issues.append(("params.%s" % key, "must_be_number"))
            elif not (rule[1] <= float(value) <= rule[2]):
                issues.append(("params.%s" % key,
                               "must_be_in_%s_%s" % (rule[1], rule[2])))
        elif kind in ("str", "str|null"):
            if value is None and kind == "str|null":
                continue
            if not isinstance(value, str):
                issues.append(("params.%s" % key, "must_be_string"))
            elif len(value) > rule[1]:
                issues.append(("params.%s" % key, "max_length_%d" % rule[1]))
        elif kind == "handle":
            if not isinstance(value, str) or not HANDLE_RE.match(value):
                issues.append(("params.%s" % key, "must_be_hex_handle"))
        elif kind == "opid":
            if not isinstance(value, str):
                issues.append(("params.%s" % key, "must_be_string"))
            elif len(value) > 64:
                issues.append(("params.%s" % key, "max_length_64"))
        elif kind == "strarray":
            if not isinstance(value, list):
                issues.append(("params.%s" % key, "must_be_array"))
            elif len(value) > rule[1]:
                issues.append(("params.%s" % key, "max_items_%d" % rule[1]))
            elif any(not isinstance(item, str) for item in value):
                issues.append(("params.%s" % key, "items_must_be_string"))
        elif kind == "handlearray":
            if not isinstance(value, list):
                issues.append(("params.%s" % key, "must_be_array"))
            elif len(value) > rule[1]:
                issues.append(("params.%s" % key, "max_items_%d" % rule[1]))
            elif any(not isinstance(item, str) or not HANDLE_RE.match(item) for item in value):
                issues.append(("params.%s" % key, "items_must_be_hex_handle"))
        elif kind == "enumarray":
            if not isinstance(value, list):
                issues.append(("params.%s" % key, "must_be_array"))
            elif len(value) > rule[1]:
                issues.append(("params.%s" % key, "max_items_%d" % rule[1]))
            elif any(item not in ANOMALY_TYPES for item in value):
                issues.append(("params.%s" % key, "unknown_anomaly_type"))
        elif kind == "numarray4|null":
            if value is None:
                continue
            if not isinstance(value, list) or len(value) != 4:
                issues.append(("params.%s" % key, "must_be_4_numbers"))
            elif any(isinstance(item, bool) or not isinstance(item, (int, float))
                     for item in value):
                issues.append(("params.%s" % key, "must_be_4_numbers"))
        elif kind == "extent":
            if value is None:
                continue
            if value == "all":
                continue
            if not isinstance(value, list) or len(value) != 4:
                issues.append(("params.%s" % key, "must_be_all_or_4_numbers"))
            elif any(isinstance(item, bool) or not isinstance(item, (int, float))
                     for item in value):
                issues.append(("params.%s" % key, "must_be_all_or_4_numbers"))
            elif float(value[2]) <= float(value[0]) or float(value[3]) <= float(value[1]):
                issues.append(("params.%s" % key, "extent_must_have_positive_area"))
        elif kind == "strmap":
            if not isinstance(value, dict):
                issues.append(("params.%s" % key, "must_be_object"))
            elif any(not isinstance(v, str) for v in value.values()):
                issues.append(("params.%s" % key, "values_must_be_string"))
        elif kind == "strmap|null":
            if value is None:
                continue
            if not isinstance(value, dict):
                issues.append(("params.%s" % key, "must_be_object"))
            elif any(not isinstance(v, str) for v in value.values()):
                issues.append(("params.%s" % key, "values_must_be_string"))
        elif kind == "severity|null":
            if value is None:
                continue
            if value not in ("info", "warn", "error"):
                issues.append(("params.%s" % key, "must_be_info_warn_error"))
        elif kind == "enum":
            # 与 C# `ParamValidator.GetEnumString` 的 detail 措辞**逐字对齐**（`not in enum: <value>`），
            # 否则同一种非法输入在 mock 与真机给模型两句话，属实现漂移。
            allowed = rule[1]
            if value is None:
                # NIT-3：显式 null 与「未提供」同义（C# 的 GetString 对两者都返回 null，
                # 两端无法区分，所以不在这个字段上另立规矩）。
                continue
            if value not in allowed:
                # 空串走这里：`""` 是「传了但给空」，与「没传」不同，必须拒。
                issues.append(("params.%s" % key, "not in enum: %s" % value))
        elif kind == "const:png":
            if value != "png":
                issues.append(("params.%s" % key, "only_png_supported"))
        else:  # pragma: no cover - 规格表写错时立即暴露
            issues.append(("params.%s" % key, "unknown_rule_%s" % kind))
    return issues


class MockHost:
    """宿主替身：持有样例模型、任务环、预览缓存与统计。"""

    def __init__(self, silent: bool = False, model=None):
        self.silent = bool(silent)
        self.model = model or build_model()
        self.start_time = time.time()
        self.started_iso = utc_iso(self.start_time)
        self.instance_id = os.urandom(4).hex()
        self.host_instance_id = "%s-%s" % (HOST_APP, self.instance_id)
        self.catalog_hash = tool_catalog_hash()
        self.tasks = TaskRing()
        self.exec_lock = threading.Lock()
        self.session_token = None
        self.session_active = False
        self.session_busy_count = 0
        self.request_log = []          # [(epoch, is_error)]
        self.cache_hits = 0
        self.cache_lookups = 0
        self.preview_dir = os.path.join(platform_root(), "cache", "preview")
        self._pixels = None            # 预览缓存：{(w,h,extentKey): (bytes, path)}

    # ---------------- 能力描述 ----------------

    def gaps(self) -> list:
        """Civil 3D 未安装 → 必须诚实上报的缺口（设计 §2.3）。"""
        return [{
            "capability": "civil.roadway|corridor|surface|pipe_network",
            "status": "absent",
            "reason": "Civil 3D 本体未安装，仅存在 Object Enabler；相关对象以 ACAD_PROXY_ENTITY 出现",
            "nextAction": "安装 Civil 3D 2022 或改用 AutoCAD 原生图元 + 明确标注",
        }]

    def capabilities(self) -> dict:
        return {
            "domains": list(READ_DOMAINS),
            "writeEnabled": False,
            "civilApi": {
                "available": False,
                "product": HOST_PRODUCT,
                "release": HOST_RELEASE,
                "probe": "mock:civil_api_never_available",
                "reason": "mockhost 不加载 Civil 3D 程序集（本档模拟 AutoCAD 无 C3D 的降级路径）",
                "writeSupported": False,
            },
            "preview": {
                "formats": ["png"],
                "maxWidth": PREVIEW_MAX_WIDTH,
                "maxHeight": PREVIEW_MAX_HEIGHT,
                "renderMode": "schematic",
            },
            "messageLimitBytes": MESSAGE_LIMIT_BYTES,
            "heartbeat": {"intervalMs": HEARTBEAT_INTERVAL_MS, "missLimit": HEARTBEAT_MISS_LIMIT},
            "cancellation": True,
            "toolCatalogHash": self.catalog_hash,
        }

    def tool_names(self) -> list:
        return sorted(PARAM_SPECS.keys())

    # ---------------- 统计 ----------------

    def record_request(self, is_error: bool) -> None:
        now = time.time()
        self.request_log.append((now, bool(is_error)))
        cutoff = now - 60.0
        if len(self.request_log) > 4096:
            self.request_log = [item for item in self.request_log if item[0] >= cutoff]

    def recent_stats(self):
        cutoff = time.time() - 60.0
        recent = [item for item in self.request_log if item[0] >= cutoff]
        errors = sum(1 for item in recent if item[1])
        return len(recent), errors

    # ---------------- 工具分派 ----------------

    def call(self, tool: str, params: dict, op_id: str) -> tuple:
        """执行工具；返回 (result_dict, warnings_list)。失败抛 ToolFailure。"""
        handler = getattr(self, "_tool_%s" % tool, None)
        if handler is None:
            raise ToolFailure("E2001", "tool_not_in_catalog:%s" % tool, "tool")
        warnings = []
        result = handler(params, op_id, warnings)
        return result, warnings

    # ---- sys_* ----

    def _tool_sys_ping(self, params, op_id, warnings):
        return {
            "pong": True,
            "serverTimeUtc": utc_iso(),
            "hostInstanceId": self.host_instance_id,
        }

    def _tool_sys_status(self, params, op_id, warnings):
        requests_count, errors = self.recent_stats()
        rate = 0.0
        if self.cache_lookups > 0:
            rate = round(self.cache_hits / float(self.cache_lookups), 4)
        uptime = int((time.time() - self.start_time) * 1000)
        return {
            "summary": clip(
                "mock 宿主运行 %d 秒，近 60 秒 %d 次请求（%d 次失败），队列 0，主线程泵送正常。"
                % (uptime // 1000, requests_count, errors), 400),
            "uptimeMs": uptime,
            "requestsLast60s": requests_count,
            "errorsLast60s": errors,
            "queueLength": 0,
            "idlePumping": True,
            "cacheHitRate": rate,
            "sessions": 1 if self.session_active else 0,
        }

    def _tool_sys_version(self, params, op_id, warnings):
        return {
            "host": {
                "name": "EngineeringHost(mock)",
                "version": HOST_VERSION,
                "buildUtc": self.started_iso,
            },
            "autocad": {
                "product": "AutoCAD(mock)",
                "release": "2022",
                "version": "24.1s",
            },
            "protocol": {
                "version": PROTOCOL_VERSION,
                "messageLimitBytes": MESSAGE_LIMIT_BYTES,
                "toolCatalogHash": self.catalog_hash,
            },
            "dotnet": {
                "runtime": "python-%d.%d" % sys.version_info[:2],
                "note": "mockhost 不是 .NET 宿主，仅用于链路验证",
            },
        }

    def _tool_sys_health(self, params, op_id, warnings):
        os.makedirs(self.preview_dir, exist_ok=True)
        log_dir = os.path.join(platform_root(), "logs")
        os.makedirs(log_dir, exist_ok=True)
        instance_dir = decode_instance_dir()
        os.makedirs(instance_dir, exist_ok=True)
        checks = [
            {"id": "pipeAcl", "ok": True, "detail": "mock 管道以 D:P(A;;GA;;;OW) 限制为当前用户"},
            {"id": "discoveryFile", "ok": os.path.isdir(instance_dir),
             "detail": instance_dir},
            {"id": "logWritable", "ok": os.access(log_dir, os.W_OK), "detail": log_dir},
            {"id": "cacheWritable", "ok": os.access(self.preview_dir, os.W_OK),
             "detail": self.preview_dir},
            {"id": "civilProbe", "ok": False, "detail": "Civil 3D 不可用（预期缺口 W1001）"},
            {"id": "layerMapLoaded", "ok": True, "detail": "mock_drawing 内置映射 10 图层"},
            {"id": "projectDb", "ok": True, "detail": "MCP 侧负责落库；mock 不持有数据库"},
        ]
        ok_count = sum(1 for item in checks if item["ok"])
        return {
            "summary": "自检 %d/%d 项通过；Civil 3D 缺失为已知缺口（W1001）。"
                       % (ok_count, len(checks)),
            "checks": checks,
        }

    def _tool_sys_capabilities(self, params, op_id, warnings):
        caps = self.capabilities()
        return {
            "domains": caps["domains"],
            "writeEnabled": False,
            "disabledDomains": [prefix.rstrip("_") for prefix in WRITE_DOMAIN_PREFIXES],
            "gaps": self.gaps(),
            "tools": self.tool_names(),
        }

    def _tool_sys_list_instances(self, params, op_id, warnings):
        directory = decode_instance_dir()
        instances = []
        try:
            names = sorted(os.listdir(directory))
        except OSError:
            names = []
        for name in names:
            if not name.endswith(".json"):
                continue
            path = os.path.join(directory, name)
            try:
                with open(path, "r", encoding="utf-8") as handle:
                    data = json.load(handle)
            except (OSError, ValueError):
                continue
            if not isinstance(data, dict) or not data.get("pipeName"):
                continue
            pid = data.get("pid")
            alive = True if data.get("instanceId") == self.instance_id else pid_alive(pid)
            instances.append({
                "hostInstanceId": data.get("hostInstanceId")
                                   or "%s-%s" % (data.get("app"), data.get("instanceId")),
                "app": data.get("app") or HOST_APP,
                "pid": pid,
                "hostVersion": data.get("hostVersion") or HOST_VERSION,
                "startedAtUtc": data.get("startedAtUtc") or self.started_iso,
                "documentNameHash": data.get("documentNameHash"),
                "alive": bool(alive),
            })
        instances.sort(key=lambda item: item.get("startedAtUtc") or "", reverse=True)
        return {"instances": instances}

    # ---- read_* ----

    def _result_ref(self, tool: str, op_id: str, detail_tool: str | None = None) -> dict:
        """storedAt 由 MCP 侧落库后回填；宿主侧恒为 null（设计 §13.6）。"""
        return {
            "tool": tool,
            "opId": op_id,
            "fingerprint": self.model["fingerprint"],
            "storedAt": None,
            "detailTool": detail_tool,
        }

    def _units(self) -> dict:
        return {
            "insUnits": self.model["insUnits"],
            "code": self.model["unitCode"],
            "scaleToMeter": self.model["scaleToMeter"],
        }

    def _extents(self, source=None) -> dict:
        box = source or self.model
        return {"min": list(box["min"]), "max": list(box["max"])}

    def _tool_read_overview(self, params, op_id, warnings):
        model = self.model
        counts = model["counts"]
        if counts.get("proxyEntities", 0) > 0:
            warnings.append(make_warning(
                "W1001", "civil_api_absent; %d 个 ACAD_PROXY_ENTITY" % counts["proxyEntities"]))
        top = sorted(model["anomalies"],
                     key=lambda item: {"error": 0, "warn": 1, "info": 2}.get(item["severity"], 3))
        top_anomalies = [{"code": item["code"], "severity": item["severity"],
                          "message": clip(item["message"], 200)} for item in top[:5]]
        disciplines = sorted(model["disciplines"], key=lambda item: -item["entityCount"])
        empty_layers = counts.get("emptyLayers", 0)
        layer_text = "图层 %d 个%s" % (
            counts["layers"],
            "（另有 %d 个空图层）" % empty_layers if empty_layers else "")
        summary = clip(
            "%s（%s）%s；图元 %d 个 / %s / 块 %d 个；%s；专业分布以%s为主；%d 项异常待看。"
            % (model["softwareName"], model["softwareVersion"],
               "单位 mm" if model["unitCode"] == "mm" else "单位 %s" % model["unitCode"],
               counts["entities"], layer_text, counts["blocks"],
               "含 %d 个代理对象（Civil 3D 不可用）" % counts["proxyEntities"]
               if counts.get("proxyEntities") else "无代理对象",
               disciplines[0]["name"] if disciplines else "未识别",
               len(model["anomalies"])), 400)
        return {
            "summary": summary,
            "resultRef": self._result_ref("read_overview", op_id, "read_layers"),
            "truncated": False,
            "drilldown": [
                {"tool": "read_layers", "params": {"limit": 50},
                 "why": "按图层看专业分布与识别置信度"},
                {"tool": "read_query", "params": {"dxfName": "INSERT", "limit": 20},
                 "why": "查看块引用与属性（如检查井编号）"},
                {"tool": "read_anomalies", "params": {"severity": "warn"},
                 "why": "先处理警告级异常"},
                {"tool": "read_preview", "params": {"width": 1200, "height": 800},
                 "why": "看结构示意图确认图纸内容"},
            ],
            "software": {
                "name": model["softwareName"],
                "version": model["softwareVersion"],
                "civilApiAvailable": model["civilApiAvailable"],
            },
            "drawing": {
                "nameHash": model["nameHash"],
                "modifiedUtc": model["modifiedUtc"],
                "readOnly": model["readOnly"],
            },
            "units": self._units(),
            "coordinateSystem": None,
            "extents": self._extents(),
            "counts": counts,
            "civil3d": {
                "detected": False,
                "hostProduct": HOST_PRODUCT,
                "hostRelease": HOST_RELEASE,
                "counts": {"surfaces": 0, "alignments": 0, "profiles": 0,
                           "pipeNetworks": 0, "pointGroups": 0, "total": 0},
                "surfaces": [],
                "alignments": [],
                "profiles": [],
                "pipeNetworks": [],
                "pointGroups": [],
                "attributeProbe": {},
                "objectCap": 200,
                "truncated": False,
                "elapsedMs": 0,
                "note": "mockhost 不加载 Civil 3D 程序集，AECC* 按代理对象上报（降级路径）",
            },
            "civilApi": {
                "available": False,
                "product": HOST_PRODUCT,
                "release": HOST_RELEASE,
                "probe": "mock:civil_api_never_available",
                "reason": "mockhost 不加载 Civil 3D 程序集（mock 档模拟 AutoCAD 无 C3D）",
            },
            "disciplines": disciplines,
            "topAnomalies": top_anomalies,
            "fingerprint": model["fingerprint"],
        }

    @staticmethod
    def _wildcard(pattern: str | None):
        """通配符 `*`/`?` → 大小写不敏感正则；None 表示不过滤。"""
        if not pattern:
            return None
        escaped = ""
        for char in pattern:
            if char == "*":
                escaped += ".*"
            elif char == "?":
                escaped += "."
            else:
                escaped += re.escape(char)
        return re.compile("^" + escaped + "$", re.IGNORECASE)

    def _tool_read_layers(self, params, op_id, warnings):
        limit = int(params.get("limit", 50))
        min_entities = int(params.get("minEntities", 0))
        disciplines = params.get("disciplines") or []
        regex = self._wildcard(params.get("namePattern"))
        layer_discipline = {}
        for entity in self.model["entities"]:
            layer_discipline.setdefault(entity["layer"], set())
        # 图层 → 专业名（取该层实体的含义所在专业；无实体则归未识别）
        layer_to_discipline = {}
        for item in self.model["layers"]:
            name = item.get("category") or "unknown"
            layer_to_discipline[item["name"]] = self._category_discipline(name)

        candidates = []
        for item in self.model["layers"]:
            if item["entityCount"] < min_entities:
                continue
            if regex is not None and not regex.match(item["name"]):
                continue
            if disciplines:
                discipline = layer_to_discipline.get(item["name"])
                if discipline not in disciplines:
                    continue
            candidates.append(item)
        candidates.sort(key=lambda item: (-item["entityCount"], item["name"]))
        total = len(candidates)

        cursor_handle = decode_cursor(params.get("cursor"))
        if cursor_handle:
            names = [item["name"].upper() for item in candidates]
            if cursor_handle in names:
                candidates = candidates[names.index(cursor_handle) + 1:]
        page = candidates[:limit]
        next_cursor = encode_cursor(page[-1]["name"]) if len(candidates) > limit else None

        layers = []
        for item in page:
            layers.append({
                "name": item["name"],
                "entityCount": item["entityCount"],
                "types": item["types"],
                "recognizedAs": item.get("meaning"),
                "confidence": item["confidence"],
                "confidenceBand": item["band"],
                "basis": item.get("basis") or [],
            })
        summary = clip(
            "共 %d 个图层满足条件，本页返回 %d 个（按图元数降序）：%s。"
            % (total, len(layers),
               "、".join("%s(%d)" % (item["name"], item["entityCount"]) for item in layers[:6])
               or "无"), 400)
        return {
            "summary": summary,
            "resultRef": self._result_ref("read_layers", op_id, "read_query"),
            "truncated": next_cursor is not None,
            "drilldown": ensure_drilldown(
                [{"tool": "read_query", "params": {"layer": page[0]["name"], "limit": 20},
                  "why": "查看该图层内的具体对象"}] if page else [],
                next_cursor is not None, next_cursor is not None),
            "layers": layers,
            "total": total,
            "nextCursor": next_cursor,
        }

    def _category_discipline(self, category: str) -> str:
        mapping = {
            "road": "道路",
            "site": "场地",
            "terrain": "场地/地形",
            "drainage": "排水",
            "annotation": "标注",
            "frame": "图框",
            "structure": "结构",
            "unknown": "未识别",
        }
        return mapping.get(category, "未识别")

    def _match_entity(self, entity, params) -> bool:
        layer_regex = self._wildcard(params.get("layer"))
        if layer_regex is not None and not layer_regex.match(entity["layer"]):
            return False
        dxf = params.get("dxfName")
        if dxf and entity["dxfName"].upper() != str(dxf).upper():
            return False
        extent = params.get("extent")
        if extent:
            if not self._intersects(entity, extent):
                return False
        meaning = params.get("meaning")
        if meaning and (entity.get("meaning") or "") != meaning:
            return False
        min_conf = float(params.get("minConfidence", 0) or 0)
        if entity["confidence"] < min_conf:
            return False
        attr_equals = params.get("attrEquals")
        if attr_equals:
            attributes = entity.get("attributes") or {}
            for key, value in attr_equals.items():
                if str(attributes.get(key, "")) != str(value):
                    return False
        text_contains = params.get("textContains")
        if text_contains:
            needle = str(text_contains).lower()
            haystack = list(entity.get("texts") or [])
            haystack.extend(str(value) for value in (entity.get("attributes") or {}).values())
            if not any(needle in str(item).lower() for item in haystack):
                return False
        return True

    @staticmethod
    def _intersects(entity, extent) -> bool:
        if not entity.get("hasExtents"):
            return False
        x0, y0, x1, y1 = (float(extent[0]), float(extent[1]),
                          float(extent[2]), float(extent[3]))
        emin, emax = entity["min"], entity["max"]
        return not (emax[0] < x0 or emin[0] > x1 or emax[1] < y0 or emin[1] > y1)

    def _tool_read_query(self, params, op_id, warnings):
        limit = int(params.get("limit", 20))
        text_contains = params.get("textContains")
        matched = [entity for entity in self.model["entities"]
                   if self._match_entity(entity, params)]
        matched.sort(key=lambda entity: (entity["layer"], entity["handle"]))
        total = len(matched)

        cursor_handle = decode_cursor(params.get("cursor"))
        if cursor_handle:
            handles = [entity["handle"] for entity in matched]
            if cursor_handle in handles:
                matched = matched[handles.index(cursor_handle) + 1:]
        page = matched[:limit]
        next_cursor = encode_cursor(page[-1]["handle"]) if len(matched) > limit else None

        items = []
        for entity in page:
            item = {
                "handle": entity["handle"],
                "dxfName": entity["dxfName"],
                "type": entity["dotNetType"],
                "layer": entity["layer"],
                "extents": ({"min": list(entity["min"]), "max": list(entity["max"])}
                            if entity.get("hasExtents") else None),
                "meaning": entity.get("meaning"),
                "confidence": entity["confidence"],
            }
            if text_contains:
                item["textHit"] = True
            items.append(item)

        summary = clip(
            "命中 %d 个对象，本页 %d 个%s。%s"
            % (total, len(items),
               "（已截断，用 nextCursor 翻页）" if next_cursor else "",
               "首个为 %s/%s@%s。" % (items[0]["dxfName"], items[0]["meaning"] or "未识别",
                                     items[0]["layer"]) if items else "无命中。"), 400)
        drilldown = []
        if items:
            drilldown.append({"tool": "read_describe",
                              "params": {"handle": items[0]["handle"]},
                              "why": "查看首个命中对象的完整几何与识别依据"})
            drilldown.append({"tool": "read_preview",
                              "params": {"highlightHandles": [item["handle"] for item in items[:20]]},
                              "why": "在预览图上高亮这批对象确认位置"})
        return {
            "summary": summary,
            "resultRef": self._result_ref("read_query", op_id, "read_describe"),
            "truncated": next_cursor is not None,
            "drilldown": ensure_drilldown(drilldown, next_cursor is not None,
                                          next_cursor is not None),
            "items": items,
            "total": total,
            "nextCursor": next_cursor,
        }

    def _tool_read_describe(self, params, op_id, warnings):
        handle = str(params.get("handle", "")).upper()
        entity = None
        for candidate in self.model["entities"]:
            if candidate["handle"].upper() == handle:
                entity = candidate
                break
        if entity is None:
            raise ToolFailure("E1001", "handle_not_found:%s" % handle, "params.handle",
                              "参数无效：句柄在当前图纸中不存在")
        fingerprint = params.get("fingerprint")
        if fingerprint and fingerprint != self.model["fingerprint"]:
            warnings.append(make_warning(
                "W1004", "expected=%s actual=%s" % (fingerprint, self.model["fingerprint"])))
        if entity.get("isProxy"):
            warnings.append(make_warning("W1001", "proxy_object:%s" % handle))

        attributes = entity.get("attributes") or {}
        props = {}
        for key, value in attributes.items():
            # 与 C# 宿主同形：`ReadToolHandlers.Describe` 写的是 `props["attr." + tag]`。
            # 以前 mock 用裸 tag，两端形状不同 → 契约测试无法发现「宿主一条属性都没读到」
            # （D-01）。这里加前缀不是美化，是让断言能钉住真实形状。
            props["attr." + key] = value
        if entity.get("rawTextLength"):
            props["rawTextLength"] = entity["rawTextLength"]
        anomalies = [{"code": item["code"], "severity": item["severity"],
                      "message": clip(item["message"], 200)}
                     for item in self.model["anomalies"]
                     if (item.get("handle") or "").upper() == handle]
        summary = clip(
            "%s（%s）位于图层 %s，识别为%s（置信度 %.2f，%s）%s。"
            % (entity["dxfName"], entity["dotNetType"], entity["layer"],
               entity.get("meaning") or "未识别对象", entity["confidence"],
               entity["band"],
               "，含 %d 项属性" % len(attributes) if attributes else ""), 400)
        return {
            "summary": summary,
            "resultRef": self._result_ref("read_describe", op_id, "read_preview"),
            "object": {
                "handle": entity["handle"],
                "dxfName": entity["dxfName"],
                "type": entity["dotNetType"],
                "layer": entity["layer"],
                "linetype": entity.get("linetype"),
                "colorIndex": entity.get("colorIndex"),
                "visible": bool(entity.get("visible", True)),
                "extents": ({"min": list(entity["min"]), "max": list(entity["max"])}
                            if entity.get("hasExtents") else None),
                "geometry": entity.get("geometry") or {},
                "props": props,
                # 与 C# 同形：`ReadAttributes` 会把属性值追加进 `obj.Texts`（最多 5 条），
                # 所以带属性的块引用 `texts` 一定非空；无属性块保持空。
                "texts": ([clip(text, 80) for text in (entity.get("texts") or [])]
                          + [clip(str(value), 80)
                             for value in (entity.get("attributes") or {}).values()])[:5],
            },
            "meaning": entity.get("meaning"),
            "confidence": entity["confidence"],
            "confidenceBand": entity["band"],
            "basis": entity.get("basis") or [],
            "relations": [],
            "anomalies": anomalies,
        }

    # ---------------- read_civil_list / read_civil_object（N04-B 新增，docs/08 §4.3 #2/#3） ----------------

    def _civil_objects(self):
        """Civil 只读夹具（`mock_drawing.build_model()` 的 `civilObjects`）。"""
        return self.model.get("civilObjects") or []

    @staticmethod
    def _parse_cursor(cursor):
        """游标格式 `o:<offset>`（与宿主 C# 侧 `ParseCursor` 同源）；坏游标按第一页处理。"""
        if not isinstance(cursor, str) or not cursor.startswith("o:"):
            return 0
        try:
            value = int(cursor[2:])
        except ValueError:
            return 0
        return value if value > 0 else 0

    def _tool_read_civil_list(self, params, op_id, warnings):
        """按类型/名称枚举 Civil 对象并给句柄（只读，不持有 ObjectId）。"""
        wanted = params.get("type")
        if wanted is None:
            # 「不传」= any（schema 的 default 语义）；空串**不**走这里，它是「传了但给空」。
            wanted = "any"
        # 防御层：`PARAM_SPECS` 的 enum 已先拦一道（detail 与 C# 逐字一致）。
        # 这里再挡一次，保证「handler 单独被调用」时也不会把未知类型落到空集过滤。
        if not isinstance(wanted, str) or wanted not in CIVIL_TYPES:
            raise ToolFailure(
                "E1001",
                "not in enum: %s" % (wanted if isinstance(wanted, str) else "(none)"),
                "params.type",
                "type 必填，允许集：any/alignment/profile/surface/pipeNetwork/pointGroup")
        name_contains = (params.get("nameContains") or "").lower()
        limit = int(params.get("limit", 100))
        limit = max(1, min(500, limit))
        offset = self._parse_cursor(params.get("cursor"))

        records = [item for item in self._civil_objects()
                   if wanted == "any" or item.get("kind") == wanted]
        if name_contains:
            records = [item for item in records
                       if name_contains in (item.get("name") or "").lower()]

        page = records[offset:offset + limit]
        items = [
            {
                "handle": item["handle"].upper(),
                "name": clip(item.get("name") or "", 200),
                "type": item.get("kind"),
                "basis": dict(item.get("basis") or {}),
            }
            for item in page
        ]
        truncated = (offset + len(items)) < len(records)
        summary = clip(
            "【Civil 对象】类型 %s%s｜命中 %d，本页返回 %d%s。"
            % (wanted,
               "，名称含「%s」" % clip(name_contains, 40) if name_contains else "",
               len(records), len(items),
               "（还有下一页）" if truncated else ""), 400)
        return {
            "summary": summary,
            "resultRef": self._result_ref("read_civil_list", op_id, "read_civil_object"),
            "counts": {"total": len(records), "returned": len(items)},
            "items": items,
            "truncated": truncated,
            "nextCursor": ("o:%d" % (offset + len(items))) if truncated else None,
            "units": self._units(),
        }

    def _tool_read_civil_object(self, params, op_id, warnings):
        """按句柄返回单个 Civil 对象的属性表；强制带 confidence 与 basis。"""
        handle = str(params.get("handle", "")).upper()
        record = None
        for candidate in self._civil_objects():
            if candidate["handle"].upper() == handle:
                record = candidate
                break
        if record is None:
            raise ToolFailure("E1001", "civil_object_not_found:%s" % handle, "params.handle",
                              "参数无效：Civil 句柄在当前图纸中不存在")

        fingerprint = params.get("fingerprint")
        if fingerprint and fingerprint != self.model["fingerprint"]:
            warnings.append(make_warning(
                "W1004", "expected=%s actual=%s" % (fingerprint, self.model["fingerprint"])))

        wanted = params.get("properties") or []
        attributes = []
        for key, value in (record.get("props") or {}).items():
            if wanted and key not in wanted:
                continue
            attributes.append({
                "name": key,
                "value": value if isinstance(value, (str, int, float, bool)) or value is None
                         else str(value),
                "unit": None,
            })
            if len(attributes) >= 200:
                break

        summary = clip(
            "【Civil 对象属性】%s%s｜句柄 %s｜%s｜置信度 %.2f｜属性 %d 项。"
            % (record.get("kindDisplay") or record.get("kind"),
               "「%s」" % clip(record.get("name") or "", 60) if record.get("name") else "",
               record["handle"].upper(),
               record.get("typeName") or "类型未知",
               float(record.get("confidence") or 0.0),
               len(attributes)), 400)
        return {
            "summary": summary,
            "resultRef": self._result_ref("read_civil_object", op_id, "read_preview"),
            "object": {
                "handle": record["handle"].upper(),
                "type": record.get("kind"),
                "name": clip(record.get("name") or "", 200),
                "confidence": float(record.get("confidence") or 0.0),
                "basis": dict(record.get("basis") or {}),
                "attributes": attributes,
                "units": self._units(),
            },
        }

    def _tool_read_civil_surfaces(self, params, op_id, warnings):
        # 替身不连基座：如实返回空集，并在 summary 里标注来源，避免让 QA 误以为抓到真数据
        limit = int(params.get("limit", 100))
        surfaces = list(self.model.get("surfaces") or [])[:limit]
        return {
            "summary": clip("【Civil 曲面】共 %d 个（替身无基座，返回空集为预期）｜指纹 %s"
                            % (len(surfaces), self.model["fingerprint"]), 400),
            "resultRef": self._result_ref("read_civil_surfaces", op_id, None),
            "total": len(surfaces),
            "surfaces": surfaces,
            "truncated": False,
            "source": "mock",
            "units": self._units(),
        }

    def _tool_read_civil_alignments(self, params, op_id, warnings):
        limit = int(params.get("limit", 100))
        rows = list(self.model.get("alignments") or [])[:limit]
        return {
            "summary": clip("【Civil 路线】共 %d 个（替身无基座，返回空集为预期）｜指纹 %s"
                            % (len(rows), self.model["fingerprint"]), 400),
            "resultRef": self._result_ref("read_civil_alignments", op_id, None),
            "total": len(rows),
            "alignments": rows,
            "truncated": False,
            "source": "mock",
            "units": self._units(),
        }

    def _tool_read_anomalies(self, params, op_id, warnings):
        limit = int(params.get("limit", 30))
        types = params.get("types") or []
        layer_regex = self._wildcard(params.get("layer"))
        severity = params.get("severity")

        matched = []
        for item in self.model["anomalies"]:
            if types and item["code"] not in types:
                continue
            if layer_regex is not None and not layer_regex.match(item.get("layer") or ""):
                continue
            matched.append(item)
        counts = {}
        for item in matched:
            counts[item["code"]] = counts.get(item["code"], 0) + 1
        if severity:
            matched = [item for item in matched
                       if item["severity"] == severity]
        order = {"error": 0, "warn": 1, "info": 2}
        matched.sort(key=lambda item: (order.get(item["severity"], 3),
                                       item["code"], item.get("handle") or ""))

        cursor_handle = decode_cursor(params.get("cursor"))
        if cursor_handle:
            keys = [(item.get("handle") or "").upper() for item in matched]
            if cursor_handle in keys:
                matched = matched[keys.index(cursor_handle) + 1:]
        page = matched[:limit]
        next_cursor = encode_cursor(page[-1].get("handle") or "") if len(matched) > limit else None

        items = [{
            "code": item["code"],
            "severity": item["severity"],
            "handle": item.get("handle"),
            "layer": item.get("layer"),
            "message": clip(item["message"], 200),
            "evidence": item.get("evidence") or {},
        } for item in page]
        summary = clip(
            "共 %d 项异常（%s），本页 %d 项。%s"
            % (sum(counts.values()), "、".join("%s×%d" % (k, v) for k, v in sorted(counts.items()))
               or "无", len(items),
               "建议先看 proxy_object 与 extreme_coord。" if counts.get("proxy_object") else ""), 400)
        return {
            "summary": summary,
            "resultRef": self._result_ref("read_anomalies", op_id, "read_describe"),
            "truncated": next_cursor is not None,
            "drilldown": ensure_drilldown(
                [{"tool": "read_describe", "params": {"handle": items[0]["handle"]},
                  "why": "定位首个异常对象"}] if items and items[0].get("handle") else [],
                next_cursor is not None, next_cursor is not None),
            "items": items,
            "counts": counts,
            "nextCursor": next_cursor,
        }

    # ---- read_preview ----

    def _tool_read_preview(self, params, op_id, warnings):
        width = int(params.get("width", 1200))
        height = int(params.get("height", 800))
        extent = params.get("extent", "all")
        if extent == "all" or extent is None:
            box = (self.model["min"][0], self.model["min"][1],
                   self.model["max"][0], self.model["max"][1])
        else:
            box = (float(extent[0]), float(extent[1]), float(extent[2]), float(extent[3]))
        highlights = {handle.upper() for handle in (params.get("highlightHandles") or [])}

        data = self._render_preview(box, width, height, highlights)
        if len(data) > PREVIEW_MAX_BYTES:
            # 超 1 MB 自动降尺寸重绘一次（设计 §2.5）
            width = max(200, int(width * 0.8))
            height = max(200, int(height * 0.8))
            data = self._render_preview(box, width, height, highlights)
        oversize = len(data) > PREVIEW_MAX_BYTES

        path = self._preview_path(box, width, height)
        try:
            with open(path, "wb") as handle:
                handle.write(data)
        except OSError as exc:
            raise ToolFailure("E5001", "preview_write_failed:%s" % exc, "params")

        legend = self._build_legend(box)
        summary = clip(
            "已生成结构示意图 %d×%d（%.1f KB），范围 [%.0f,%.0f]-[%.0f,%.0f]；"
            "注意：这是结构示意图，非出图成果。"
            % (width, height, len(data) / 1024.0, box[0], box[1], box[2], box[3]), 400)

        # NIT-7：与 C# `ReadToolHandlers.Preview` 对齐 —— 预览也要给下一步建议。
        # 其余四个读工具都有 drilldown，唯独预览缺字段；模型拿到图却不知道下一步做什么。
        drilldown = []
        if highlights:
            drilldown.append({"tool": "read_describe", "params": {"handle": sorted(highlights)[0]},
                              "why": "看该高亮对象的完整属性（预览只表达几何位置，不含语义细节）"})
        else:
            drilldown.append({"tool": "read_query", "params": {"limit": 20},
                              "why": "先取候选对象，再把 handle 放进 highlightHandles 重画，可在图上突出显示"})
        repaint = {"width": width, "height": height}
        if extent != "all" and extent is not None:
            repaint["extent"] = [box[0], box[1], box[2], box[3]]
        drilldown.append({"tool": "read_preview", "params": repaint,
                          "why": "换范围/分辨率重画（图纸很大时先收窄 extent，细节才看得清）"})

        return {
            "summary": summary,
            "resultRef": self._result_ref("read_preview", op_id, None),
            "truncated": False,
            "drilldown": drilldown,
            "previewPath": path,
            "width": width,
            "height": height,
            "bytes": len(data),
            "extents": {"min": [box[0], box[1], 0.0], "max": [box[2], box[3], 0.0]},
            "legend": legend,
            "note": PREVIEW_NOTE,
            "oversize": oversize,
        }

    def _preview_path(self, box, width: int, height: int) -> str:
        os.makedirs(self.preview_dir, exist_ok=True)
        key = hashlib.sha256(
            ("%s|%s|%d|%d" % (self.model["fingerprint"], box, width, height)).encode("utf-8")
        ).hexdigest()[:12]
        short_fp = self.model["fingerprint"].replace("fp1:", "")[:8]
        return os.path.join(self.preview_dir, "%s-%s-%dx%d.png" % (short_fp, key, width, height))

    def _layer_color_index(self, layer: str) -> int:
        names = [item["name"] for item in
                 sorted(self.model["layers"], key=lambda item: -item["entityCount"])]
        try:
            index = names.index(layer)
        except ValueError:
            index = len(names)
        return index % len(PALETTE)

    def _render_preview(self, box, width: int, height: int, highlights: set) -> bytes:
        """把语义模型画成结构示意图（包围盒 + 线段 + 点），不做 PLOT。"""
        canvas = Canvas(width, height)
        world_w = max(box[2] - box[0], 1e-9)
        world_h = max(box[3] - box[1], 1e-9)
        margin = 24
        inner_w = max(width - 2 * margin, 1)
        inner_h = max(height - 2 * margin, 1)
        scale = min(inner_w / world_w, inner_h / world_h)

        def project(x, y):
            px = margin + (x - box[0]) * scale
            py = height - margin - (y - box[1]) * scale
            return int(px), int(py)

        # 边框
        canvas.rect(margin - 8, margin - 8, width - margin + 8, height - margin + 8, (200, 200, 200))
        draw_ops = 0
        max_ops = 20000
        for entity in self.model["entities"]:
            if draw_ops >= max_ops:
                break
            if not entity.get("hasExtents"):
                continue
            emin, emax = entity["min"], entity["max"]
            if emax[0] < box[0] or emin[0] > box[2] or emax[1] < box[1] or emin[1] > box[3]:
                continue
            color = hex_to_rgb(PALETTE[self._layer_color_index(entity["layer"])])
            dxf = entity["dxfName"]
            highlight = entity["handle"].upper() in highlights
            if highlight:
                color = (0, 0, 0)
            if dxf == "LINE" and entity.get("geometry", {}).get("start"):
                start = entity["geometry"]["start"]
                end = entity["geometry"]["end"]
                x0, y0 = project(start[0], start[1])
                x1, y1 = project(end[0], end[1])
                canvas.line(x0, y0, x1, y1, color, 3 if highlight else 2)
            elif dxf in ("POINT", "INSERT"):
                cx, cy = project((emin[0] + emax[0]) / 2.0, (emin[1] + emax[1]) / 2.0)
                canvas.cross(cx, cy, 6 if highlight else 3, color)
            elif dxf in ("TEXT", "MTEXT"):
                x0, y0 = project(emin[0], emin[1])
                canvas.rect(x0, y0 - 6, x0 + 6, y0, color, 1, fill=True)
            else:
                x0, y0 = project(emin[0], emax[1])
                x1, y1 = project(emax[0], emin[1])
                canvas.rect(x0, y0, x1, y1, color, 3 if highlight else 1)
            draw_ops += 1

        # 比例尺（右下角），用图纸单位标注（不做文字渲染，仅几何刻度）
        bar_len = inner_w // 4
        bx1 = width - margin
        bx0 = bx1 - bar_len
        by = height - 12
        canvas.rect(bx0, by, bx1, by + 4, (60, 60, 60))
        canvas.line(bx0, by - 3, bx0, by + 7, (60, 60, 60))
        canvas.line(bx1, by - 3, bx1, by + 7, (60, 60, 60))
        return canvas.to_png()

    def _build_legend(self, box) -> list:
        """图例：按图元数取前 8 个图层，颜色与绘制一致。"""
        counters = {}
        for entity in self.model["entities"]:
            if not entity.get("hasExtents"):
                continue
            emin, emax = entity["min"], entity["max"]
            if emax[0] < box[0] or emin[0] > box[2] or emax[1] < box[1] or emin[1] > box[3]:
                continue
            counters[entity["layer"]] = counters.get(entity["layer"], 0) + 1
        ordered = sorted(counters.items(), key=lambda item: (-item[1], item[0]))[:8]
        return [{"color": PALETTE[self._layer_color_index(name)],
                 "label": clip(name, 40), "count": count} for name, count in ordered]

    # ---- task_* ----

    def _tool_task_status(self, params, op_id, warnings):
        target = params.get("opId")
        record = self.tasks.find(target)
        if record is None:
            return {"opId": target, "status": "notFound", "tool": None,
                    "startedAtUtc": None, "elapsedMs": None, "errorCode": None}
        return {
            "opId": record.get("opId"),
            "status": record.get("status"),
            "tool": record.get("tool"),
            "startedAtUtc": record.get("startedAtUtc"),
            "elapsedMs": record.get("elapsedMs"),
            "errorCode": record.get("errorCode"),
        }

    def _tool_task_list(self, params, op_id, warnings):
        limit = int(params.get("limit", 20))
        since = params.get("sinceUtc")
        records = self.tasks.recent(limit, since)
        tasks = [{
            "opId": item.get("opId"),
            "tool": item.get("tool"),
            "status": item.get("status"),
            "startedAtUtc": item.get("startedAtUtc") or self.started_iso,
            "elapsedMs": int(item.get("elapsedMs") or 0),
            "errorCode": item.get("errorCode"),
        } for item in records]
        return {"tasks": tasks}

    def _tool_task_cancel(self, params, op_id, warnings):
        target = params.get("opId")
        record = self.tasks.find(target)
        if record is None:
            return {"accepted": False, "status": "notFound", "reason": "op_not_found"}
        if record.get("status") in ("verified", "failed", "cancelled", "unknown"):
            return {"accepted": False, "status": record.get("status"), "reason": "already_finished"}
        self.tasks.update(target, status="cancelled", errorCode="cancelled")
        return {"accepted": True, "status": "cancelled", "reason": None}

    def _tool_task_reconcile(self, params, op_id, warnings):
        target = params.get("opId")
        record = self.tasks.find(target)
        checked = utc_iso()
        if record is None:
            return {
                "opId": target,
                "decision": "unknown",
                "evidence": {"reason": "host_restarted"},
                "checkedAtUtc": checked,
                "advice": "宿主侧无记录：可能进程重启过。可安全重试，但必须用新 opId。",
            }
        status = record.get("status")
        if status == "verified":
            return {"opId": target, "decision": "verified",
                    "evidence": {"elapsedMs": record.get("elapsedMs") or 0},
                    "checkedAtUtc": checked, "advice": None}
        if status in ("failed", "cancelled"):
            return {"opId": target, "decision": "failed",
                    "evidence": {"errorCode": record.get("errorCode")},
                    "checkedAtUtc": checked,
                    "advice": "该请求已失败；修正参数后以新 opId 重试。"}
        return {"opId": target, "decision": "unknown",
                "evidence": {"status": status},
                "checkedAtUtc": checked,
                "advice": "仍在执行或结果不明；稍后再查一次，不要立即重放。"}

    # ---------------- write_*（M1 写通道替身，2026-11-22） ----------------
    #
    # 替身只保证**契约形状**与门禁语义，不做真密码学、不真改图：
    #   * 真实 token 的签发/校验在宿主（host/Write/ApprovalTokens.cs）；
    #   * 这里用固定前缀的假 token，够 QA 验证「无 token 拒 / 有 token 过 / 摘要不符拒」三条。

    WRITE_DIGEST_KEYS = ("kind", "points", "layer", "block")

    def _write_digest(self, params) -> str:
        """与宿主 WriteToolHandlers.ComputeDigest 同口径：只取 4 个键，缺省补默认值。"""
        payload = {}
        for key in self.WRITE_DIGEST_KEYS:
            if key in params:
                payload[key] = params[key]
        payload.setdefault("kind", "insert_points")
        payload.setdefault("layer", "EP-STAKEOUT")
        canonical = json.dumps(payload, ensure_ascii=False, sort_keys=True,
                               separators=(",", ":"))
        return "sha256:" + hashlib.sha256(canonical.encode("utf-8")).hexdigest()

    def _tool_write_preview(self, params, op_id, warnings):
        points = params.get("points") or []
        layer = params.get("layer") or "EP-STAKEOUT"
        existing = {item["name"] for item in self.model["layers"]}
        will_create = layer not in existing
        digest = self._write_digest(params)
        return {
            "kind": "insert_points",
            "count": len(points),
            "layer": layer,
            "willCreateLayer": will_create,
            "paramsDigest": digest,
            "targetFingerprint": self.model["fingerprint"],
            "wrote": False,
            "summary": clip("将新增 %d 个点位到图层 %s%s。未做任何修改。"
                            % (len(points), layer, "（该图层不存在，执行时会新建）" if will_create else ""),
                            400),
        }

    def _tool_write_approve(self, params, op_id, warnings):
        if not params.get("userConfirmation"):
            raise ToolFailure("E1001", "chat_confirmation_required", "userConfirmation")
        digest = params.get("paramsDigest") or ""
        if (params.get("kind") or "insert_points") == "command":
            canonical = {"kind": "command", "command": (params.get("command") or "").strip()}
            recomputed = "sha256:" + hashlib.sha256(
                json.dumps(canonical, ensure_ascii=False, sort_keys=True,
                           separators=(",", ":")).encode("utf-8")).hexdigest()
        else:
            recomputed = self._write_digest(params)
        if digest and digest != recomputed:
            raise ToolFailure("E1002", "approval_params_mismatch", "paramsDigest")
        token = "v1.mock.%s" % hashlib.sha256((recomputed + op_id).encode("utf-8")).hexdigest()[:32]
        self._mock_tokens[token] = recomputed
        return {
            "approvalToken": token,
            "expiresInMs": 120000,
            "confirmedBy": "chat+modal_yes",
            "paramsDigest": recomputed,
            "targetFingerprint": self.model["fingerprint"],
            "summary": clip("替身已签发令牌（真机需人在 CAD 点确认）：%d 点 → %s。"
                            % (len(params.get("points") or []), params.get("layer") or "EP-STAKEOUT"), 400),
        }

    def _tool_write_surface_breakline(self, params, op_id, warnings):
        surface = (params.get("surface") or "").strip()
        if not surface:
            raise ToolFailure("E1001", "surface_required", "surface")
        points = params.get("points") or []
        if len(points) < 2:
            raise ToolFailure("E1001", "breakline_needs_two_points", "points")
        token = params.get("approvalToken") or ""
        expected = self._mock_tokens.pop(token, None)
        if expected is None:
            raise ToolFailure("E1006", "approval_invalid", "approvalToken")
        return {"queued": True, "tool": "write_surface_breakline", "surface": surface,
                "vertices": len(points), "saved": False,
                "summary": clip("已提交基座执行：向曲面「%s」添加断裂线（%d 个顶点）"
                                % (surface, len(points)), 400)}

    def _tool_write_command(self, params, op_id, warnings):
        command = (params.get("command") or "").strip()
        if not command:
            raise ToolFailure("E1001", "command_required", "command")
        canonical = {"kind": "command", "command": command}
        digest = "sha256:" + hashlib.sha256(json.dumps(canonical, ensure_ascii=False, sort_keys=True,
                                                      separators=(",", ":")).encode("utf-8")).hexdigest()
        if params.get("dryRun"):
            return {"kind": "command", "command": command, "blocked": None,
                    "paramsDigest": digest, "targetFingerprint": self.model["fingerprint"],
                    "wrote": False, "summary": clip("将执行命令：%s（未执行）" % command, 400)}
        token = params.get("approvalToken") or ""
        expected = self._mock_tokens.pop(token, None)
        if expected is None:
            raise ToolFailure("E1006", "approval_invalid", "approvalToken")
        if digest != expected:
            raise ToolFailure("E1002", "approval_params_mismatch", "paramsDigest")
        return {"queued": True, "command": command, "saved": False,
                "summary": clip("命令已排队执行（不保存文件）：%s" % command, 400)}

    def _tool_write_insert_points(self, params, op_id, warnings):
        token = params.get("approvalToken") or ""
        expected = self._mock_tokens.get(token)
        if expected is None:
            raise ToolFailure("E1006", "approval_invalid", "approvalToken")
        digest = self._write_digest(params)
        if digest != expected:
            raise ToolFailure("E1002", "approval_params_mismatch", "paramsDigest")
        del self._mock_tokens[token]      # 单次消费
        points = params.get("points") or []
        layer = params.get("layer") or "EP-STAKEOUT"
        handles = ["%04X" % (0x3000 + i) for i in range(min(len(points), 200))]
        return {
            "created": len(points),
            "layer": layer,
            "handles": handles,
            "handlesTruncated": len(points) > 200,
            "saved": False,
            "undoHint": "本次插入未保存；CAD 里执行 U 可撤销",
            "summary": clip("已新增 %d 个点位到图层 %s（未保存，U 可撤销）。" % (len(points), layer), 400),
        }

    # ---------------- v3.0 Phase 1 & Phase 3 原子能力替身 ----------------

    def _tool_export_surface_grid(self, params, op_id, warnings):
        raise ToolFailure("E2001", "civil_api_unavailable_for_surface_export", "surface")

    def _tool_export_layer_geojson(self, params, op_id, warnings):
        return {
            "outPath": params.get("outPath") or "constraints.geojson",
            "featureCount": 0,
            "scannedEntities": len(self.model.get("entities") or []),
            "fileBytes": 0,
            "byCategory": {},
            "elapsedMs": 1,
            "summary": "【图层 GeoJSON 导出】mock 宿主完成导出。",
        }

    def _tool_read_active_selection(self, params, op_id, warnings):
        return {
            "count": 0,
            "returned": 0,
            "truncated": False,
            "lastChangedUtc": None,
            "items": [],
            "summary": "【实时选区】当前选中 0 个实体。",
        }

    def _tool_read_viewport_snapshot(self, params, op_id, warnings):
        width = int(params.get("width") or 1280)
        height = int(params.get("height") or 720)
        return {
            "outPath": params.get("outPath") or "viewport_mock.png",
            "width": width,
            "height": height,
            "bytes": 0,
            "mode": "mock_stub",
            "elapsedMs": 1,
            "summary": "【视口截图】mock 宿主生成占位截图。",
        }

    def _tool_read_object_xdata(self, params, op_id, warnings):
        handle = (params.get("handle") or "").upper()
        return {
            "handle": handle,
            "dxfName": "LINE",
            "xdata": {},
            "xrecords": {},
            "summary": "【对象元数据】句柄 %s 无 XData。" % handle,
        }

    def _tool_write_object_xdata(self, params, op_id, warnings):
        handle = (params.get("handle") or "").upper()
        entries = params.get("entries") or {}
        return {
            "handle": handle,
            "regApp": params.get("regApp") or "EP_META",
            "xrecordKey": params.get("xrecordKey"),
            "writtenEntries": len(entries) if isinstance(entries, dict) else 0,
            "summary": "已向句柄 %s 写入元数据（mock）。" % handle,
        }

    def _tool_write_undo(self, params, op_id, warnings):
        steps = int(params.get("steps") or 1)
        return {
            "queued": True,
            "undoneSteps": steps,
            "summary": "已提交事务回滚：撤销最近 %d 步操作（mock）。" % steps,
        }

    def _tool_write_alignment_plan(self, params, op_id, warnings):
        name = params.get("name") or "EP_Route_Plan_1"
        return {
            "name": name,
            "layer": params.get("layer") or "EP-ALIGNMENT",
            "pointCount": len(params.get("points") or params.get("sampledCenterline") or []),
            "length": 0.0,
            "polylineHandle": "4001",
            "alignmentHandle": None,
            "surfaceProfileHandle": None,
            "layoutProfileHandle": None,
            "nativeCivilCreated": False,
            "note": "mock_stub",
            "summary": "【路线落盘】mock 宿主完成路线 %s 创建。" % name,
        }

    def _tool_write_eval_csharp(self, params, op_id, warnings):
        return {
            "ok": True,
            "engine": "mock-stub",
            "committed": bool(params.get("commit", True)),
            "compileMs": 0,
            "executeMs": 0,
            "returnValue": None,
            "logs": [],
            "shadowDryRun": None,
            "summary": "【Roslyn 执行成功】mock 宿主占位返回。",
        }

    def _tool_write_entities(self, params, op_id, warnings):
        ents = params.get("entities") or []
        layer = params.get("layer") or "0"
        return {
            "requestedCount": len(ents),
            "createdCount": len(ents),
            "failedCount": 0,
            "failedItems": [],
            "layer": layer,
            "handles": ["500%d" % i for i in range(min(len(ents), 20))],
            "byType": {},
            "saved": False,
            "summary": "【批量建实体】mock 宿主已创建 %d/%d 个实体到图层 %s。" % (len(ents), len(ents), layer),
        }

    def _tool_preview_transient(self, params, op_id, warnings):
        action = params.get("action") or "add"
        group = params.get("group") or "default"
        return {
            "action": action,
            "group": group,
            "added": len(params.get("primitives") or []) if action in ("add", "replace") else 0,
            "activeGroups": {},
            "summary": "【瞬态预览】mock 宿主执行 %s（组 %s）。" % (action, group),
        }

    def _tool_batch_side_database(self, params, op_id, warnings):
        files = params.get("files") or []
        return {
            "action": params.get("action") or "inspect",
            "totalFiles": len(files),
            "succeeded": 0,
            "failed": len(files),
            "results": [],
            "elapsedMs": 1,
            "summary": "【侧数据库批处理】mock 宿主处理 %d 份文件。" % len(files),
        }


# ============================================================================
# 十一、服务端：ACL、接受连接、握手、请求循环
# ============================================================================


class PipeServer:
    """单会话命名管道服务端（多实例句柄，应用层单会话）。

    * 每个连接一个线程（对齐 `maxInstances = 4`）；
    * 工具执行走 `MockHost.exec_lock` 串行（对齐「每实例单调度器、只读也串行」）；
    * 第二个 hello 在已有活动会话时返回 `E1003 session_busy` 并断开。
    """

    def __init__(self, host: MockHost | None = None, instance_dir: str | None = None,
                 pipe_name: str | None = None, verbose: bool = True):
        self.host = host or MockHost()
        self.instance_dir = instance_dir or decode_instance_dir()
        self.pipe_name = pipe_name or build_pipe_name(HOST_APP, self.host.instance_id)
        self.verbose = verbose
        self.discovery_path = discovery_file_path(
            HOST_APP, self.host.instance_id, directory=self.instance_dir)
        self._stop = threading.Event()
        self._accept_thread = None
        self._sa = None
        self._sa_buffer = None
        self._sd = None
        self._connections = []
        self._conn_lock = threading.Lock()
        self._session_lock = threading.Lock()
        self._active_token = None      # 应用层单会话（设计 §2.7 / §4.1）
        self._listen_lock = threading.Lock()
        self._listen_handle = None
        self.acl_applied = False

    # ---------------- 生命周期 ----------------

    def log(self, event: str, **fields) -> None:
        if not self.verbose:
            return
        parts = " ".join("%s=%s" % (key, value) for key, value in fields.items())
        sys.stderr.write("[mockhost] %s %s\n" % (event, parts))
        sys.stderr.flush()

    def start(self) -> "PipeServer":
        if not _IS_WINDOWS:
            raise RuntimeError("mockhost 需要 Windows 命名管道支持")
        self._build_security()
        os.makedirs(self.instance_dir, exist_ok=True)
        self._publish_discovery()
        self._accept_thread = threading.Thread(target=self._accept_loop,
                                               name="mockhost-accept", daemon=True)
        self._accept_thread.start()
        self.log("started", pipe=self.pipe_name, instance=self.host.host_instance_id)
        return self

    def stop(self, timeout: float = 3.0) -> None:
        """优雅停止（overlapped 句柄下可以安全取消挂起 I/O）。

        踩过的两个坑，都在这里被规避：
            1. 同步句柄上 `CloseHandle` 一个正在阻塞 `ReadFile` 的句柄会永不返回；
            2. `DisconnectNamedPipe` 同样会等挂起 I/O 排空。
        因此句柄一律以 FILE_FLAG_OVERLAPPED 创建，停止时用 `CancelIoEx` 取消挂起 I/O，
        连接线程随即在自己的线程里关闭句柄。
        """
        self._stop.set()

        with self._listen_lock:
            listen = self._listen_handle
        if listen:
            _kernel32.CancelIoEx(listen, None)   # 唤醒阻塞在 ConnectNamedPipe 的接受线程

        with self._conn_lock:
            connections = list(self._connections)
        for handle in connections:
            _kernel32.CancelIoEx(handle, None)   # 唤醒阻塞在 ReadFile 的连接线程

        if self._accept_thread is not None:
            self._accept_thread.join(timeout)

        deadline = time.time() + timeout
        while time.time() < deadline:
            with self._conn_lock:
                if not self._connections:
                    break
            time.sleep(0.02)

        self._remove_discovery()
        if self._sd:
            try:
                _kernel32.LocalFree(self._sd)
            except Exception:
                pass
            self._sd = None
        self.log("stopped")

    # ---------------- ACL ----------------

    def _build_security(self) -> None:
        """`D:P(A;;GA;;;OW)` —— 保护型 DACL，只授予「对象所有者」通用全部权限。

        OW（S-1-3-4 / OWNER RIGHTS）在创建管道时解析为当前用户，
        等价于真实宿主 `PipeSecurity` 的「仅当前用户 + SYSTEM」策略中最关键的一条。
        失败时退回系统默认 DACL（仍只允许创建者与被授予者），并记录告警。
        """
        sddl = "D:P(A;;GA;;;OW)"
        descriptor = ctypes.c_void_p()
        try:
            ok = _advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, 1, ctypes.byref(descriptor), None)
            if not ok or not descriptor.value:
                self.log("acl_failed", error=ctypes.get_last_error())
                return
            self._sd = descriptor
            attrs = SECURITY_ATTRIBUTES()
            attrs.nLength = ctypes.sizeof(SECURITY_ATTRIBUTES)
            attrs.lpSecurityDescriptor = descriptor
            attrs.bInheritHandle = False
            self._sa_buffer = attrs
            self._sa = ctypes.byref(attrs)
            self.acl_applied = True
        except Exception as exc:  # pragma: no cover - 极端环境
            self.log("acl_exception", detail=repr(exc))

    # ---------------- 发现文件 ----------------

    def _publish_discovery(self) -> None:
        payload = {
            "pipeName": self.pipe_name,
            "app": HOST_APP,
            "instanceId": self.host.instance_id,
            "hostInstanceId": self.host.host_instance_id,
            "pid": os.getpid(),
            "hostVersion": HOST_VERSION,
            "protocolVersion": PROTOCOL_VERSION,
            "startedAtUtc": self.host.started_iso,
            "documentNameHash": self.host.model["nameHash"],
            "fingerprintHint": self.host.model["fingerprint"],
            "writeEnabled": False,
            "mode": "mock",
            "toolCatalogHash": self.host.catalog_hash,
        }
        temporary = self.discovery_path + ".tmp"
        try:
            with open(temporary, "w", encoding="utf-8") as handle:
                json.dump(payload, handle, ensure_ascii=False, indent=2)
            os.replace(temporary, self.discovery_path)
        except OSError as exc:
            self.log("discovery_write_failed", detail=str(exc))

    def _remove_discovery(self) -> None:
        for path in (self.discovery_path, self.discovery_path + ".tmp"):
            try:
                if os.path.isfile(path):
                    os.remove(path)
            except OSError:
                pass

    # ---------------- 接受连接 ----------------

    def _create_instance(self):
        # FILE_FLAG_OVERLAPPED 是必须的：同步句柄会让阻塞读连带堵死写，
        # 且 CloseHandle / DisconnectNamedPipe 会一直等挂起 I/O（实测死锁）。
        flags = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED
        mode = PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT
        return _kernel32.CreateNamedPipeW(
            self.pipe_name, flags, mode, MAX_INSTANCES,
            65536, 65536, 0, self._sa)

    def _accept_loop(self) -> None:
        while not self._stop.is_set():
            handle = self._create_instance()
            if _handle_invalid(handle):
                self.log("create_pipe_failed", error=ctypes.get_last_error())
                return
            with self._listen_lock:
                self._listen_handle = handle
            state = _connect_overlapped(handle)
            with self._listen_lock:
                self._listen_handle = None
            if state != "connected":
                close_handle(handle)
                if self._stop.is_set():
                    return
                continue
            if self._stop.is_set():
                close_handle(handle)
                return
            with self._conn_lock:
                self._connections.append(handle)
            thread = threading.Thread(target=self._connection_thread, args=(handle,),
                                      name="mockhost-conn", daemon=True)
            thread.start()

    def _connection_thread(self, handle) -> None:
        try:
            self._serve(handle)
        except Exception as exc:  # pragma: no cover - 防御性
            self.log("connection_error", detail=repr(exc))
        finally:
            # overlapped 句柄在无挂起 I/O 时可直接关闭；不调用 DisconnectNamedPipe
            # （它会等挂起 I/O 排空，同步句柄下会死锁）
            close_handle(handle)
            with self._conn_lock:
                if handle in self._connections:
                    self._connections.remove(handle)

    # ---------------- 会话 ----------------

    def _serve(self, handle) -> None:
        session = {"token": None, "active": False}
        try:
            self._serve_loop(handle, session)
        finally:
            # 连接结束 → 释放单会话令牌，允许下一次 hello 接入
            with self._session_lock:
                if self._active_token is not None and session.get("token") == self._active_token:
                    self._active_token = None
                    self.host.session_active = False

    def _serve_loop(self, handle, session: dict) -> None:
        while not self._stop.is_set():
            status, body = read_frame(handle)
            if status == FRAME_OK:
                pass
            elif status == FRAME_TOO_LARGE:
                # 已读完丢弃 → 回 E1001，连接保持可用（设计 §2.1）
                length = int(body.decode("ascii") or 0)
                write_json_frame(handle, self._envelope_fail(
                    session, make_error("E1001", "message_too_large:%d" % length),
                    op_id=None, tool="unknown"))
                self.log("frame_too_large", length=length)
                continue
            elif status == FRAME_EMPTY:
                self.log("frame_empty_close")
                return
            else:
                return

            try:
                message = json.loads(body.decode("utf-8"))
            except (UnicodeDecodeError, ValueError):
                write_json_frame(handle, self._envelope_fail(
                    session, make_error("E1001", "invalid_json"), op_id=None, tool="unknown"))
                continue
            if not isinstance(message, dict):
                write_json_frame(handle, self._envelope_fail(
                    session, make_error("E1001", "frame_must_be_object"),
                    op_id=None, tool="unknown"))
                continue

            kind = message.get("kind")
            if kind == "hello":
                if not self._handle_hello(handle, session, message):
                    return
                continue
            if kind == "bye":
                self.log("client_bye", reason=message.get("reason"))
                return
            if kind is not None:
                write_json_frame(handle, self._envelope_fail(
                    session, make_error("E1001", "unknown_frame_kind:%s" % kind),
                    op_id=None, tool="unknown"))
                continue
            if not self._handle_request(handle, session, message):
                return

    def _handle_hello(self, handle, session: dict, message: dict) -> bool:
        """握手：版本兼容 → 必填字段 → 单会话 → 签发令牌。返回 False 表示连接应关闭。

        校验顺序与 C# `Protocol/Handshake.cs:HelloRequest.Parse` 严格一致：
        ① protocolVersion 形状/主版本（E4001，直接关连接）；
        ② clientInfo / clientInfo.name / clientInfo.version / clientNonce（E1001，连接保留可重试）；
        ③ 单会话（E1003）。
        顺序不能反：先把畸形帧拒掉，才不会给非法客户端签发令牌。
        """
        version = str(message.get("protocolVersion") or "")
        compatible, reason = self._version_compatible(version)
        if not compatible:
            # NIT-6：detail 字面量与 C# 端对齐，不再自造 `protocol_version_incompatible:2.0`。
            # 契约来源 protocol/errors.json#E4001.detailExamples：
            #   invalid_protocol_version  形状不合法（不是 "主.次" 两个数字）
            #   major_mismatch            主版本不等
            # 版本号本身仍然带上，但放在 bye 帧的 `protocolVersion` 字段里，
            # 不塞进 detail —— detail 是要被客户端按字面量分支的，带变量就等于没契约。
            detail = "invalid_protocol_version" if reason == "malformed_version" else "major_mismatch"
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E4001", detail, "protocolVersion"),
                op_id=None, tool="handshake"))
            write_json_frame(handle, {"kind": "bye", "reason": "protocol_violation",
                                      "detail": detail, "protocolVersion": version})
            self.log("hello_rejected", reason=reason, version=version)
            return False

        # ---- D1：按 handshake.schema.json#/$defs/Hello 的 required 逐字段校验 ----
        field_error = self._validate_hello_fields(message)
        if field_error is not None:
            write_json_frame(handle, self._envelope_fail(
                session, field_error, op_id=None, tool="handshake"))
            self.log("hello_rejected", reason=field_error.get("detail"),
                     field=field_error.get("field"))
            return True

        if session["active"]:
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E1003", "session_busy", "sessionToken"),
                op_id=None, tool="handshake"))
            write_json_frame(handle, {"kind": "bye", "reason": "protocol_violation",
                                      "detail": "session_busy"})
            return False

        with self._session_lock:
            if self._active_token is not None:
                write_json_frame(handle, self._envelope_fail(
                    session, make_error("E1003", "session_busy", "sessionToken"),
                    op_id=None, tool="handshake"))
                write_json_frame(handle, {"kind": "bye", "reason": "protocol_violation",
                                          "detail": "session_busy"})
                self.log("hello_rejected", reason="session_busy")
                return False
            token = os.urandom(32).hex()
            self._active_token = token

        session["token"] = token
        session["active"] = True
        self.host.session_token = token
        self.host.session_active = True

        nonce = message.get("clientNonce")
        requested = message.get("requestedFeatures") or []
        # D4：missingFeatures 只放**真实缺失的能力标识**；已知能力与工具名都不进。
        # 工具名混进来会让 Agent 以为「能力缺失」，实际上它只是把工具名填错了字段。
        missing = [item for item in requested
                   if item not in SUPPORTED_FEATURES and item not in PARAM_SPECS]

        # D3：客户端次版本高于宿主 → W1002 必须真的上线（以前只算不用）
        warnings = []
        if reason == "client_ahead":
            warnings.append(make_warning("W1002", "client_ahead:%s" % version))

        ack = {
            "kind": "hello_ack",
            "protocolVersion": PROTOCOL_VERSION,
            "hostVersion": HOST_VERSION,
            "hostProduct": HOST_PRODUCT,
            "hostRelease": HOST_RELEASE,
            "hostInstanceId": self.host.host_instance_id,
            "sessionToken": token,
            "serverTimeUtc": utc_iso(),
            "clientNonceEcho": nonce if isinstance(nonce, str) else None,
            "writeEnabled": False,
            "capabilities": self.host.capabilities(),
            "gaps": self.host.gaps(),
            "missingFeatures": missing,
            "warnings": warnings,
        }
        if not write_json_frame(handle, ack):
            return False
        self.log("hello_ok", instance=self.host.host_instance_id,
                 token=token[:4] + "***", warnings=len(warnings))
        return True

    @staticmethod
    def _validate_hello_fields(message: dict):
        """hello 必填字段校验；返回 make_error(...) 或 None。

        粒度到「具体缺哪个字段」（field + detail 都能定位），与 C# 侧同口径：
          - clientInfo 不是对象            → E1001 clientInfo_missing
          - clientInfo.name 缺失/空/非串    → E1001 clientInfo.name_missing
          - clientInfo.version 缺失/空/非串 → E1001 clientInfo.version_missing
          - clientNonce 缺失/空             → E1001 clientNonce_missing
          - clientNonce 非 16–64 hex        → E1001 clientNonce_invalid
        """
        info = message.get("clientInfo")
        if not isinstance(info, dict):
            return make_error("E1001", "clientInfo_missing", "clientInfo",
                              "hello 帧缺少 clientInfo 对象")
        name = info.get("name")
        if not isinstance(name, str) or not name.strip():
            return make_error("E1001", "clientInfo.name_missing", "clientInfo.name",
                              "clientInfo.name 必须是非空字符串")
        if len(name) > 80:
            return make_error("E1001", "clientInfo.name_too_long", "clientInfo.name",
                              "clientInfo.name 不得超过 80 字符")
        cversion = info.get("version")
        if not isinstance(cversion, str) or not cversion.strip():
            return make_error("E1001", "clientInfo.version_missing", "clientInfo.version",
                              "clientInfo.version 必须是非空字符串")
        if len(cversion) > 40:
            return make_error("E1001", "clientInfo.version_too_long", "clientInfo.version",
                              "clientInfo.version 不得超过 40 字符")
        pid = info.get("pid")
        if pid is not None:
            if not isinstance(pid, int) or isinstance(pid, bool) or pid < 1:
                return make_error("E1001", "clientInfo.pid_invalid", "clientInfo.pid",
                                  "clientInfo.pid 必须为正整数或 null")

        nonce = message.get("clientNonce")
        if nonce is None or nonce == "":
            return make_error("E1001", "clientNonce_missing", "clientNonce",
                              "hello 帧缺少 clientNonce（16–64 hex 随机串）")
        if not isinstance(nonce, str) or not CLIENT_NONCE_RE.match(nonce):
            return make_error("E1001", "clientNonce_invalid", "clientNonce",
                              "clientNonce 必须是 16–64 位十六进制串")
        return None

    @staticmethod
    def _version_compatible(version: str):
        parts = str(version).split(".")
        if len(parts) != 2 or not all(part.isdigit() for part in parts):
            return False, "malformed_version"
        major, minor = int(parts[0]), int(parts[1])
        if major != PROTOCOL_MAJOR:
            return False, "major_mismatch"
        if minor > PROTOCOL_MINOR:
            return True, "client_ahead"
        return True, "ok"

    # ---------------- 请求处理 ----------------

    def _handle_request(self, handle, session: dict, message: dict) -> bool:
        op_id = message.get("opId")
        tool = message.get("tool") if isinstance(message.get("tool"), str) else ""
        started = time.time()

        # 1. 协议版本（NIT-6：detail 与 C#/errors.json 用同一组字面量）
        compatible, reason = self._version_compatible(str(message.get("protocolVersion") or ""))
        if not compatible:
            detail = "invalid_protocol_version" if reason == "malformed_version" else "major_mismatch"
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E4001", detail, "protocolVersion"),
                op_id=op_id, tool=tool))
            self.host.record_request(True)
            return True

        # 2. 会话：从未握手 → handshake_required；握手过但令牌不对/过期 → session_expired
        #    （与 C# `Pipe/PipeConnection.cs:HandleRequest` 的 `if (!_handshaken)` 分支对齐。
        #     以前 mock 把「没握手」也报成 session_expired，客户端按 detail 分支时双端行为不同。）
        token = message.get("sessionToken")
        if not session["active"]:
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E4001", "handshake_required"), op_id=op_id, tool=tool))
            self.host.record_request(True)
            return True
        if token != session["token"]:
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E4001", "session_expired", "sessionToken"),
                op_id=op_id, tool=tool))
            self.host.record_request(True)
            return True

        # 3. approvalToken 必须为 null（M0 写通道关闭）
        if message.get("approvalToken") is not None:
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E1006", "approval_token_not_accepted_m0", "approvalToken"),
                op_id=op_id, tool=tool))
            self.host.record_request(True)
            return True

        # 4. opId 形状
        if not isinstance(op_id, str) or not OP_ID_RE.match(op_id):
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E1001", "op_id_malformed", "opId"),
                op_id=op_id if isinstance(op_id, str) else None, tool=tool))
            self.host.record_request(True)
            return True

        # 5. 写域优先于未知工具（写工具永不可达 CAD API）
        #    D6：大小写归一后再比前缀。CAD_CREATE_LINE / Cad_Create_Line 必须同样是
        #    E2003，否则调用方看到 E2001 会以为「名字拼错」而反复改名重试。
        if tool.upper().startswith(WRITE_DOMAIN_PREFIXES_UPPER):
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E2003", "write_channel_disabled_m0", "tool",
                                    "命中禁止能力：M0 写通道关闭"),
                op_id=op_id, tool=tool))
            self._record_task(op_id, tool, "failed", started, "E2003")
            self.host.record_request(True)
            return True

        if tool not in PARAM_SPECS:
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E2001", "tool_not_in_catalog:%s" % tool, "tool"),
                op_id=op_id, tool=tool))
            self._record_task(op_id, tool, "failed", started, "E2001")
            self.host.record_request(True)
            return True

        params = message.get("params")
        if params is None:
            params = {}
        if isinstance(params, dict):
            # NIT-3：显式 null 视为「未提供」——后面 handler 的 params.get(k, 默认值)
            # 会拿到 None 而崩，所以在这里就剥掉（与 C# ParamValidator 的语义一致）。
            params = {key: value for key, value in params.items() if value is not None}
        issues = validate_params(tool, params)
        if issues:
            field, detail = issues[0]
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E1001", detail, field,
                                    "参数无效：%s" % detail.replace("_", " ")),
                op_id=op_id, tool=tool, params=params))
            self._record_task(op_id, tool, "failed", started, "E1001")
            self.host.record_request(True)
            return True

        # 6. silent 模式：正常收帧但不回包（用于验证 MCP 心跳判死）
        if self.host.silent:
            self.host.record_request(True)
            return True

        # 7. 执行（串行，对齐单调度器语义）
        self._record_task(op_id, tool, "running", started, None)
        with self.host.exec_lock:
            try:
                result, warnings = self.host.call(tool, params, op_id)
                ok = True
                error = None
                status = "verified"
            except ToolFailure as failure:
                result, warnings = None, []
                ok = False
                error = failure.to_error()
                status = "failed"
            except Exception as exc:  # pragma: no cover - 防御性
                result, warnings = None, []
                ok = False
                error = make_error("E1001", "handler_crashed:%r" % exc)
                status = "failed"

        envelope = self._envelope(
            session, op_id=op_id, tool=tool, params=params, result=result,
            ok=ok, error=error, status=status, warnings=warnings, started=started)
        if not write_json_frame(handle, envelope):
            # 响应超 1 MiB：降级为错误封套（连接保持可用）
            write_json_frame(handle, self._envelope_fail(
                session, make_error("E1001", "response_too_large", None),
                op_id=op_id, tool=tool, params=params))
            ok = False
            error = {"code": "E1001"}
        self._record_task(op_id, tool, status, started,
                          (error or {}).get("code") if error else None,
                          elapsed_ms=int(round((time.time() - started) * 1000)))
        self.host.record_request(not ok)
        return True

    def _record_task(self, op_id, tool, status, started, error_code, elapsed_ms=None) -> None:
        if not isinstance(op_id, str):
            return
        elapsed = elapsed_ms
        if elapsed is None:
            elapsed = int(round((time.time() - started) * 1000))
        existing = self.host.tasks.find(op_id)
        if existing is None:
            self.host.tasks.add({
                "opId": op_id,
                "tool": tool or "unknown",
                "status": status,
                "startedAtUtc": utc_iso(started),
                "elapsedMs": elapsed,
                "errorCode": error_code,
            })
        else:
            self.host.tasks.update(op_id, status=status, errorCode=error_code,
                                   elapsedMs=elapsed)

    # ---------------- 封套构造 ----------------

    def _envelope(self, session, op_id, tool, params=None, result=None, ok=True,
                  error=None, status="verified", warnings=None, started=None) -> dict:
        started = started if started is not None else time.time()
        digest = None
        if isinstance(params, dict):
            digest = "sha256:" + hashlib.sha256(
                json.dumps(params, sort_keys=True, separators=(",", ":"),
                           ensure_ascii=False).encode("utf-8")).hexdigest()
        deduped = []
        seen = set()
        for item in (warnings or []):
            code = item.get("code")
            if code in seen:
                continue
            seen.add(code)
            deduped.append(item)
            if len(deduped) >= 32:
                break
        return {
            "protocolVersion": PROTOCOL_VERSION,
            "sessionToken": session.get("token") or "",
            "hostInstanceId": self.host.host_instance_id,
            "opId": op_id,
            "tool": tool or "unknown",
            "status": status,
            "ok": bool(ok),
            "startedAt": utc_iso(started),
            "elapsedMs": int(round((time.time() - started) * 1000)),
            "paramsDigest": digest,
            "targetFingerprint": self.host.model["fingerprint"] if ok else None,
            "warnings": deduped,
            "result": result,
            "error": error,
        }

    def _envelope_fail(self, session, error, op_id=None, tool="unknown", params=None) -> dict:
        return self._envelope(session, op_id=op_id, tool=tool, params=params,
                              result=None, ok=False, error=error,
                              status="failed", warnings=[], started=time.time())


# ============================================================================
# 十二、极简客户端（仅 --demo 自检用；正式客户端在 mcp/pipeclient.py）
# ============================================================================


class DemoClient:
    """最小验证客户端：只做 hello → call，够证明链路通。"""

    GENERIC_READ = 0x80000000
    GENERIC_WRITE = 0x40000000
    OPEN_EXISTING = 3

    def __init__(self, pipe_name: str):
        self.pipe_name = pipe_name
        self.handle = None
        self.token = None
        self.instance_id = None
        self.op_seq = 0

    def connect(self, timeout_ms: int = 3000):
        deadline = time.monotonic() + timeout_ms / 1000.0
        while True:
            handle = _kernel32.CreateFileW(
                self.pipe_name, self.GENERIC_READ | self.GENERIC_WRITE, 0, None,
                self.OPEN_EXISTING, 0, None)
            if not _handle_invalid(handle):
                self.handle = handle
                return self
            error = ctypes.get_last_error()
            if time.monotonic() >= deadline:
                raise RuntimeError("connect_failed:%d" % error)
            if error == ERROR_PIPE_BUSY:
                _kernel32.WaitNamedPipeW(self.pipe_name, 100)
            else:
                time.sleep(0.05)

    def handshake(self):
        frame = {
            "kind": "hello",
            "protocolVersion": PROTOCOL_VERSION,
            "clientInfo": {"name": "mockhost-demo", "version": "0.1.0", "pid": os.getpid()},
            "clientNonce": os.urandom(8).hex(),
            "requestedFeatures": ["preview", "cancel", "heartbeat", "taskReconcile"],
        }
        write_json_frame(self.handle, frame)
        reply = self.read()
        if reply.get("kind") != "hello_ack":
            raise RuntimeError("handshake_failed:%s" % reply)
        self.token = reply["sessionToken"]
        self.instance_id = reply["hostInstanceId"]
        return reply

    def call(self, tool: str, params: dict | None = None):
        self.op_seq += 1
        op_id = "op-%s-%04x" % (time.strftime("%Y%m%dT%H%M%S", time.gmtime()), self.op_seq)
        payload = params or {}
        frame = {
            "protocolVersion": PROTOCOL_VERSION,
            "sessionToken": self.token,
            "hostInstanceId": self.instance_id,
            "opId": op_id,
            "idempotencyKey": None,
            "tool": tool,
            "params": payload,
            "paramsDigest": "sha256:" + hashlib.sha256(
                json.dumps(payload, sort_keys=True, separators=(",", ":"),
                           ensure_ascii=False).encode("utf-8")).hexdigest(),
            "targetFingerprint": None,
            "approvalToken": None,
            "timeoutMs": 15000,
            "cancelable": True,
        }
        write_json_frame(self.handle, frame)
        return self.read()

    def read(self) -> dict:
        status, body = read_frame(self.handle)
        if status != FRAME_OK:
            raise RuntimeError("read_failed:%s" % status)
        return json.loads(body.decode("utf-8"))

    def close(self):
        close_handle(self.handle)
        self.handle = None


# ============================================================================
# 十三、命令行入口
# ============================================================================


def run_demo(server: PipeServer, serve_seconds) -> int:
    """起服务 → 自检客户端跑通 hello/sys_ping/read_* → 打印结果。

    serve_seconds：
        None  → 自检完就退出（默认，便于 CI/脚本一次跑完）
        0     → 自检完常驻，Ctrl+C 退出
        > 0   → 自检完再服务 N 秒
    """
    client = DemoClient(server.pipe_name).connect()
    lines = []
    try:
        ack = client.handshake()
        lines.append("hello_ack: instance=%s protocol=%s writeEnabled=%s catalogHash=%s gaps=%d"
                     % (ack["hostInstanceId"], ack["protocolVersion"], ack["writeEnabled"],
                        ack["capabilities"]["toolCatalogHash"], len(ack["gaps"])))
        checks = [
            ("sys_ping", {}),
            ("read_overview", {}),
            ("read_layers", {"limit": 3}),
            ("read_query", {"dxfName": "INSERT", "limit": 2}),
            ("read_anomalies", {"severity": "warn", "limit": 3}),
            ("read_preview", {"width": 640, "height": 400}),
        ]
        for tool, params in checks:
            response = client.call(tool, params)
            if not response.get("ok"):
                lines.append("%-16s FAIL %s" % (tool, response.get("error", {}).get("code")))
                continue
            result = response["result"]
            extra = ""
            if tool == "read_preview":
                extra = "%dx%d %d bytes note=%s path=%s" % (
                    result["width"], result["height"], result["bytes"],
                    result["note"], os.path.basename(result["previewPath"]))
            elif tool == "read_layers":
                extra = "total=%d first=%s" % (
                    result["total"], result["layers"][0]["name"] if result["layers"] else "-")
            elif tool == "read_query":
                extra = "total=%d" % result["total"]
            elif tool == "read_anomalies":
                extra = "counts=%s" % result["counts"]
            lines.append("%-16s ok %4dms %s" % (tool, response["elapsedMs"], extra))

        # 写通道必须关闭
        blocked = client.call("cad_create_line", {"layer": "0"})
        lines.append("cad_create_line  ok=%s code=%s detail=%s (预期 ok=False/E2003)"
                     % (blocked.get("ok"), (blocked.get("error") or {}).get("code"),
                        (blocked.get("error") or {}).get("detail")))
        unknown = client.call("read_overveiw", {})
        lines.append("read_overveiw    ok=%s code=%s (预期 ok=False/E2001)"
                     % (unknown.get("ok"), (unknown.get("error") or {}).get("code")))
    finally:
        client.close()

    print("\n".join(lines))
    print("pipe: %s" % server.pipe_name)
    print("discovery: %s" % server.discovery_path)
    if serve_seconds is None:
        print("自检完成（未指定 --serve-seconds，服务随即退出）。")
    elif serve_seconds <= 0:
        print("服务常驻中，Ctrl+C 退出。")
        try:
            while True:
                time.sleep(0.5)
        except KeyboardInterrupt:
            print("收到 Ctrl+C，退出。")
    else:
        print("服务保持 %.1f 秒后退出。" % serve_seconds)
        deadline = time.time() + serve_seconds
        while time.time() < deadline:
            time.sleep(0.2)
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Engineering Platform M0 宿主替身（无 CAD 环境验证用）")
    parser.add_argument("--demo", action="store_true", help="起服务并运行内置自检客户端")
    parser.add_argument("--silent", action="store_true",
                        help="握手后不响应任何请求（用于验证 MCP 心跳判死）")
    parser.add_argument("--serve-seconds", type=float, default=None,
                        help="服务时长（秒）：0=常驻，N=保持 N 秒；"
                             "--demo 下省略则自检完即退出，非 demo 下省略则常驻")
    parser.add_argument("--instance-dir", default=None, help="实例发现目录（默认 %%LOCALAPPDATA%%）")
    parser.add_argument("--pipe-name", default=None, help="直接指定管道全名（测试用）")
    parser.add_argument("--quiet", action="store_true", help="不打印服务端日志")
    args = parser.parse_args(argv)

    host = MockHost(silent=args.silent)
    server = PipeServer(host=host, instance_dir=args.instance_dir,
                        pipe_name=args.pipe_name, verbose=not args.quiet)
    server.start()
    try:
        if args.demo:
            return run_demo(server, args.serve_seconds)
        print(json.dumps({
            "pipeName": server.pipe_name,
            "hostInstanceId": host.host_instance_id,
            "discoveryPath": server.discovery_path,
            "aclApplied": server.acl_applied,
            "silent": host.silent,
            "toolCatalogHash": host.catalog_hash,
            "tools": host.tool_names(),
        }, ensure_ascii=False, indent=2))
        # 非 demo 模式：省略 --serve-seconds 即常驻（持续对外提供管道服务）
        serve_seconds = args.serve_seconds if args.serve_seconds is not None else 0.0
        if serve_seconds <= 0:
            print("服务常驻中，Ctrl+C 退出。")
            while True:
                time.sleep(0.5)
        deadline = time.time() + serve_seconds
        while time.time() < deadline:
            time.sleep(0.2)
        return 0
    except KeyboardInterrupt:
        return 0
    finally:
        server.stop()


if __name__ == "__main__":
    sys.exit(main())
