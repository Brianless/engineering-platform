"""tests/harness.py —— 零依赖测试底座（不用 pytest / unittest 之外的任何东西）。

提供：
    Suite        极简断言器（计数 + 失败明细 + 子用例异常捕获）
    sandbox()    把 LOCALAPPDATA 等环境变量重定向到临时目录（不污染真实用户数据）
    mock_server() 进程内起 mockhost（比 subprocess 稳，端口/时机都可控）
    connect()    连上 mockhost 并完成握手（心跳默认关闭，避免干扰断言）
    RawClient    裸管道客户端（只读写帧），用于分帧/协议边界用例
"""

from __future__ import annotations

import contextlib
import json
import os
import shutil
import struct
import sys
import tempfile
import threading
import time
import traceback

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MCP_DIR = os.path.join(ROOT, "mcp")
MOCK_DIR = os.path.join(ROOT, "mockhost")
TESTS_DIR = os.path.join(ROOT, "tests")

for _path in (MCP_DIR, MOCK_DIR, TESTS_DIR):
    if _path not in sys.path:
        sys.path.insert(0, _path)

_ENV_KEYS = ("LOCALAPPDATA", "EP_CONFIG", "EP_MOCK", "EP_INSTANCE_DIR",
             "EP_PROJECT_DB", "EP_LOG_DIR", "EP_LOG_LEVEL")


# ============================================================================
# 断言器
# ============================================================================


class Suite:
    """一个测试文件一个 Suite；run_all.py 汇总所有 Suite 的结果。"""

    def __init__(self, name: str):
        self.name = name
        self.passed = 0
        self.failed = 0
        self.failures = []
        self.started = time.time()

    # ---- 基本断言 ----

    def ok(self, condition, label: str, detail: str = "") -> bool:
        if condition:
            self.passed += 1
            return True
        self.failed += 1
        self.failures.append("[%s] %s%s" % (self.name, label,
                                           ("  → " + detail) if detail else ""))
        return False

    def eq(self, actual, expected, label: str) -> bool:
        return self.ok(actual == expected, label,
                       "expected=%r actual=%r" % (expected, actual))

    def ne(self, actual, unexpected, label: str) -> bool:
        return self.ok(actual != unexpected, label,
                       "不应等于 %r，实际 %r" % (unexpected, actual))

    def is_true(self, value, label: str, detail: str = "") -> bool:
        return self.ok(bool(value), label, detail or "actual=%r" % (value,))

    def is_false(self, value, label: str, detail: str = "") -> bool:
        return self.ok(not bool(value), label, detail or "actual=%r" % (value,))

    def contains(self, haystack, needle, label: str) -> bool:
        try:
            found = needle in haystack
        except TypeError:
            found = False
        preview = haystack
        try:
            if isinstance(haystack, str) and len(haystack) > 160:
                preview = haystack[:160] + "…（共 %d 字符）" % len(haystack)
            elif isinstance(haystack, (list, tuple, set)) and len(haystack) > 20:
                preview = "%r…（共 %d 项）" % (list(haystack)[:20], len(haystack))
        except TypeError:
            pass
        return self.ok(found, label, "在 %r 中找不到 %r" % (preview, needle))

    def matches(self, value, pattern, label: str) -> bool:
        import re
        return self.ok(bool(isinstance(value, str) and re.match(pattern, value)), label,
                       "value=%r pattern=%r" % (value, pattern))

    def fail(self, label: str, detail: str = "") -> bool:
        return self.ok(False, label, detail)

    def case(self, label: str, func) -> None:
        """执行一个子用例：抛异常记为一条失败，不中断整轮。"""
        try:
            func()
        except Exception as exc:  # noqa: BLE001 - 测试框架要兜住一切
            self.failed += 1
            self.failures.append("[%s] %s 抛异常 %r\n%s"
                                 % (self.name, label, exc, traceback.format_exc(limit=6)))

    # ---- 结果 ----

    @property
    def elapsed_ms(self) -> int:
        return int((time.time() - self.started) * 1000)

    def report(self) -> dict:
        return {"name": self.name, "passed": self.passed, "failed": self.failed,
                "failures": self.failures, "elapsedMs": self.elapsed_ms}


# ============================================================================
# 沙箱
# ============================================================================


@contextlib.contextmanager
def sandbox(label: str = "case"):
    """把 %LOCALAPPDATA% 指到临时目录，测试结束后整个删掉。

    mockhost 与 mcp/config.py 都用 `platform_root()` 派生路径，
    因此只要改 LOCALAPPDATA，两侧的实例目录、项目库、日志、预览缓存全部落进临时目录。
    """
    previous = {key: os.environ.get(key) for key in _ENV_KEYS}
    tmp = tempfile.mkdtemp(prefix="ep-test-%s-" % label)
    os.environ["LOCALAPPDATA"] = tmp
    os.environ["EP_CONFIG"] = os.path.join(tmp, "config.json")   # 不存在 → 只用内置 + 样例
    os.environ["EP_MOCK"] = "1"                                  # host.app = mock
    os.environ["EP_LOG_LEVEL"] = "WARN"
    os.environ.pop("EP_INSTANCE_DIR", None)
    os.environ.pop("EP_PROJECT_DB", None)
    os.environ.pop("EP_LOG_DIR", None)
    try:
        yield tmp
    finally:
        for key, value in previous.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value
        shutil.rmtree(tmp, ignore_errors=True)


def load_config():
    """加载配置（沙箱内调用，故路径已在临时目录）。"""
    import config as config_module
    return config_module.load()


# ============================================================================
# mockhost 生命周期
# ============================================================================


_SPARE_SERVER_CLASS = None


def spare_pipe_server_class():
    """构造（并缓存）`mock_host.PipeServer` 的子类：**只改管道实例时序**，根治 `pipe_not_found`。

    ## 根因（实测，见 `docs/evidence/20261122-flake-pipe-not-found.md`）

    `PipeServer._accept_loop` 的原始时序是：

        建实例 → 等连接 → 移交连接线程 → 回到开头**才**重建实例

    「移交」与「重建」之间**没有任何实例存在**，这段空窗内客户端的 `CreateFile(\\\\.\\pipe\\...)`
    必然得到 `ERROR_FILE_NOT_FOUND` → 我们的 `HostUnreachable('pipe_not_found')`。空窗有两处：

    | 空窗 | 何时 | 实测失败率 |
    |---|---|---|
    | 启动 | `start()` 立刻返回、接受线程还没建出第一个实例 | 30 次立刻连接失败 **2** 次（≈7%） |
    | 移交 | 每次客户端连上之后、重建下一个实例之前 | 稳定后 200 次快速连接失败 **1** 次（≈0.5%） |

    第二处正是 `qa_framing_edge` 那种「一个用例里连两次」的用例反复红的原因；
    它也是为什么「只在调用点加重试」治不好——重试只是把空窗撞得更少，空窗还在。

    ## 修法

    把「建下一个实例」提到「移交当前实例」**之前**：任意时刻都至少有一个已创建的实例在监听，
    移交窗口消失。`PipeServer` 本就允许多实例（`maxInstances = 4`），同时存在两个实例是合法时序。
    剩下唯一的窗口是 `_create_instance()` 那次系统调用本身（微秒级，用下面的探针跑 2000 次复现不到）。

    ## 为什么写在测试底座而不是改 `mockhost/`

    `mockhost/` 是**被测替身**，已按交付纪律冻结。这个缺陷只影响**夹具时序**、
    不改变任何产品语义，所以在调用侧修最合适，也便于回滚。
    冻结解除后更干净的做法是把这段逻辑上移进 `PipeServer._accept_loop`（4 行）。

    ## 防漂移

    覆盖的私有成员在实例化后逐个 `hasattr` 检查；`mockhost` 一旦重命名这些成员，
    这里会抛 `RuntimeError` 而不是静默退化成有缺陷的时序。
    """
    global _SPARE_SERVER_CLASS
    if _SPARE_SERVER_CLASS is not None:
        return _SPARE_SERVER_CLASS

    import mock_host

    # 类级成员（方法）在这里查；实例级状态（_stop/_listen_lock/...）在 __init__ 之后由
    # `mock_server()` 里的 `_assert_server_state` 查 —— 两者都查才能挡住「mockhost 重构」。
    required_methods = ("_create_instance", "_connection_thread", "_accept_loop")
    required_helpers = ("_handle_invalid", "close_handle", "_connect_overlapped")
    missing = [name for name in required_methods if not hasattr(mock_host.PipeServer, name)]
    missing += [name for name in required_helpers if not hasattr(mock_host, name)]
    if missing:                             # pragma: no cover - 只在 mockhost 重构后触发
        raise RuntimeError(
            "SpareInstancePipeServer 依赖的成员缺失：%r；"
            "mockhost 可能被重构，请同步更新覆盖实现或退回直接用 PipeServer" % (missing,))

    class SpareInstancePipeServer(mock_host.PipeServer):
        """与 `PipeServer` 行为一致，但**先建好下一个实例再移交当前连接**。"""

        def _accept_loop(self) -> None:      # noqa: D102 - 覆盖父类
            stop = self._stop
            handle = None
            while not stop.is_set():
                if handle is None:
                    handle = self._create_instance()
                    if mock_host._handle_invalid(handle):
                        self.log("create_pipe_failed")
                        return
                with self._listen_lock:
                    self._listen_handle = handle
                state = mock_host._connect_overlapped(handle)
                with self._listen_lock:
                    self._listen_handle = None
                if state != "connected":
                    mock_host.close_handle(handle)
                    handle = None
                    if stop.is_set():
                        return
                    continue
                if stop.is_set():
                    mock_host.close_handle(handle)
                    return
                # ★ 关键：**先建下一个实例**，再移交当前连接 —— 空窗消失。
                spare = self._create_instance()
                with self._conn_lock:
                    self._connections.append(handle)
                threading.Thread(target=self._connection_thread, args=(handle,),
                                 name="mockhost-conn", daemon=True).start()
                handle = None if mock_host._handle_invalid(spare) else spare

    _SPARE_SERVER_CLASS = SpareInstancePipeServer
    return _SPARE_SERVER_CLASS


@contextlib.contextmanager
def mock_server(silent: bool = False, verbose: bool = False, ready_timeout: float = 5.0):
    """进程内起一个 mockhost 服务端，yield (server, host)。

    两件事一起做，缺一不可：

    1. 用 `spare_pipe_server_class()` —— 消除「移交窗口」（连过一次之后的重连不再撞空窗）；
    2. **返回前等到管道真的可连** —— 消除「启动窗口」。以前 `start()` 立刻返回、实例由接受线程
       异步创建，第一个连接的用例有约 7% 概率撞 `HostUnreachable('pipe_not_found')`。

    有了这两条，`raw_client()` / `connect()` 里的有界重试退化为**保险**而不是必需品；
    直接 `RawClient(server.pipe_name)` 的裸客户端用例（如 `qa_framing_edge`）也不再需要自己等。
    """
    import mock_host
    import pipeclient

    host = mock_host.MockHost(silent=silent)
    server = spare_pipe_server_class()(host=host, verbose=verbose)
    # 实例级状态检查：覆盖实现读写这些属性，mockhost 一改就得立刻知道（而不是静默坏掉）。
    missing_state = [name for name in ("_stop", "_listen_lock", "_listen_handle",
                                       "_conn_lock", "_connections", "_connection_thread",
                                       "_accept_thread")
                     if not hasattr(server, name)]
    if missing_state:                       # pragma: no cover - 只在 mockhost 重构后触发
        raise RuntimeError(
            "mock_server(): PipeServer 实例缺少成员 %r，无法应用「先建后移交」时序；"
            "请同步更新 tests/harness.py 的覆盖实现" % (missing_state,))
    server.start()
    try:
        # **就绪判据用服务端信号，不用「自己连一下」**。
        #
        # 为什么不探测连接：`open_pipe` 成功会**消耗掉**唯一那个监听实例，随后接受循环
        # 才去建下一个 —— 探测本身把「空窗」重新制造出来了（实测：探测后就绪窗口 2/30 仍失败）。
        # `_listen_handle` 非空 == 接受循环已建出实例、正阻塞在 `ConnectNamedPipe` 上 ==
        # 此刻必然可连。这个信号不消耗任何东西，因此 yield 之后调用方的第一次连接不会撞空窗。
        deadline = time.time() + max(0.1, ready_timeout)
        while True:
            if getattr(server, "_listen_handle", None):
                break
            if not server._accept_thread.is_alive():
                raise RuntimeError("mock_server(): 接受线程已退出（管道实例创建失败），pipe=%s"
                                   % (server.pipe_name,))
            if time.time() >= deadline:
                raise RuntimeError("mock_server(): 管道在 %.1fs 内未就绪（pipe=%s）"
                                   % (ready_timeout, server.pipe_name))
            time.sleep(0.005)
        yield server, host
    finally:
        server.stop()


def connect(server, heartbeat: bool = False, heartbeat_interval_ms: int | None = None,
            heartbeat_miss_limit: int | None = None, connect_timeout: float = 2.0):
    """连上 mockhost 并完成握手，返回 PipeClient。

    `connect()` 阶段有界重试（与 `raw_client()` 同一口径）：管道实例由服务端线程创建，
    刚 `mock_server()` 完就连接可能撞到「还没就绪」→ `HostUnreachable('pipe_not_found')`。
    只重试建连这一步；握手失败（真正的协议问题）不重试，直接冒出去。
    """
    import pipeclient

    client = pipeclient.PipeClient(
        server.pipe_name, load_config(), logger=None, heartbeat_enabled=heartbeat,
        heartbeat_interval_ms=heartbeat_interval_ms,
        heartbeat_miss_limit=heartbeat_miss_limit)
    deadline = time.time() + max(0.0, connect_timeout)
    while True:
        try:
            client.connect()
            break
        except Exception:                       # noqa: BLE001 - 还没就绪
            if time.time() >= deadline:
                raise
            time.sleep(0.02)
    client.handshake({"name": "engineering-mcp", "version": "0.1.0"})
    return client


def call_ok(suite: Suite, client, tool: str, params: dict | None = None, label: str = ""):
    """调用工具并断言 ok=true；返回 result（失败时返回空 dict）。"""
    response = client.call(tool, params or {})
    tag = label or tool
    if not suite.ok(response.get("ok") is True, "%s 返回 ok=true" % tag,
                    json.dumps(response.get("error"), ensure_ascii=False)):
        return {}
    return response.get("result") or {}


def call_err(suite: Suite, client, tool: str, params: dict | None = None, label: str = ""):
    """调用工具并断言 ok=false；返回 error 字典。"""
    response = client.call(tool, params or {})
    tag = label or tool
    if not suite.ok(response.get("ok") is False, "%s 返回 ok=false" % tag,
                    json.dumps(response.get("result"), ensure_ascii=False)[:200]):
        return {}
    return response.get("error") or {}


# ============================================================================
# 裸管道客户端（分帧与协议边界用例）
# ============================================================================


class RawClient:
    """直接用 StreamAdapter 读写帧，可发半个帧 / 超长帧 / 零长度帧。"""

    def __init__(self, pipe_name: str, timeout_ms: int = 3000):
        import pipeclient
        self._pipeclient = pipeclient
        self.handle = pipeclient.open_pipe(pipe_name, timeout_ms)
        self.stream = pipeclient.StreamAdapter(self.handle)
        self.token = None
        self.instance_id = None
        self.op_seq = 0

    # ---- 发送 ----

    def send_bytes(self, data: bytes) -> None:
        self.stream.write(data)

    def send_frame(self, payload) -> None:
        self.stream.write(self._pipeclient.encode_frame(payload))

    def send_oversize_frame(self, declared_length: int, filler: bytes = b"x") -> None:
        """发一个声明长度超 1 MiB 的帧（先写头，再分块写体）。"""
        self.stream.write(struct.pack("<I", declared_length))
        chunk = (filler * 4096)[:4096]
        remaining = declared_length
        while remaining > 0:
            size = min(remaining, len(chunk))
            self.stream.write(chunk[:size])
            remaining -= size

    # ---- 接收 ----

    def read_json(self, timeout: float = 4.0) -> dict:
        """读一帧并解析；超时抛 TimeoutError，EOF 抛 EOFError。"""
        box = {}

        def worker():
            try:
                box["value"] = json.loads(
                    self._pipeclient.read_frame(self.stream).decode("utf-8"))
            except BaseException as exc:  # noqa: BLE001
                box["error"] = exc

        thread = threading.Thread(target=worker, daemon=True)
        thread.start()
        thread.join(timeout)
        if thread.is_alive():
            raise TimeoutError("read_json_timeout")
        if "error" in box:
            raise box["error"]
        return box["value"]

    def expect_silence(self, timeout: float = 0.6) -> bool:
        """断言在 timeout 内无任何帧（用于「连接被关闭」类用例）。"""
        try:
            self.read_json(timeout=timeout)
        except (TimeoutError, EOFError, OSError, ValueError):
            return True
        return False

    # ---- 握手 / 请求 ----

    def hello(self, version: str = "1.0") -> dict:
        self.send_frame({
            "kind": "hello",
            "protocolVersion": version,
            "clientInfo": {"name": "raw-test-client", "version": "0.1.0", "pid": os.getpid()},
            "clientNonce": "0" * 16,
            "requestedFeatures": ["preview", "cancel", "heartbeat"],
        })
        return self.read_json()

    def request(self, tool: str, params: dict | None = None, op_id: str | None = None,
                token: str | None = None, protocol_version: str = "1.0",
                approval_token=None, extra: dict | None = None) -> dict:
        self.op_seq += 1
        op_id = op_id or "op-rawtest-%04d" % self.op_seq
        frame = {
            "protocolVersion": protocol_version,
            "sessionToken": token if token is not None else self.token,
            "hostInstanceId": self.instance_id,
            "opId": op_id,
            "idempotencyKey": None,
            "tool": tool,
            "params": params or {},
            "paramsDigest": None,
            "targetFingerprint": None,
            "approvalToken": approval_token,
            "timeoutMs": 15000,
            "cancelable": True,
        }
        if extra:
            frame.update(extra)
        self.send_frame(frame)
        return self.read_json()

    def close(self) -> None:
        self._pipeclient.close_handle(self.handle)
        self.handle = None


@contextlib.contextmanager
def raw_client(server, handshake: bool = True, connect_timeout: float = 2.0):
    """裸管道客户端（默认握手），**连接阶段有界重试**。

    为什么要有界重试：`mock_server()` 起服务端线程后立刻返回，管道实例由服务端线程创建/回收；
    在慢机器或高负载下（以及单会话用例的「第二条连接」撞到实例切换瞬间），第一条 `RawClient(...)`
    可能早于管道就绪 → `HostUnreachable('pipe_not_found')` → **首个用例红、后续全绿**。
    这是夹具启动竞态，不是产品缺陷，但它会让同一套件「跑两次结果不同」，违反「用例必须可重复执行」。

    纪律（与 QA 侧 `qa_common.raw_client` 同一口径）：
    * **只重试「建立连接」这一步**，不对用例体重试 —— 连上之后抛的异常照原样冒出来；
    * 超时后 `raise` 原始异常（`HostUnreachable` 等），**不换成别的类型**，既有断言的语义不变；
    * 总等待上限默认 2 秒（成功路径上通常是 0~20 ms）。
    """
    client = None
    deadline = time.time() + max(0.0, connect_timeout)
    while client is None:
        try:
            client = RawClient(server.pipe_name)
        except Exception:                       # noqa: BLE001 - 任何连接错误都算「还没就绪」
            if time.time() >= deadline:
                raise                           # 原异常原样上抛
            time.sleep(0.02)
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
