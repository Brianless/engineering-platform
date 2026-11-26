#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""bridge/relay_launcher.py —— **全仓唯一的进程启动出口**（N04-A）。

## 为什么单独一个文件

本项目有一条硬约束：**不许启动 CAD 宿主**。早期实现用手法把「禁止一切子进程」当成这条约束的代理指标，
粒度错了 —— N04 需要启动 relay（Node 脚本），两者会直接对撞。

现在的规则精确成一句：**禁止的是「启动 CAD 宿主」，允许的是「启动 relay，且只能走这一个白名单出口」**。
静态审查据此断言：`subprocess` 系列只允许出现在本文件；本文件内不许出现宿主相关字样、不许开启 shell 解释、
不许读取整份环境变量。任何人在别处「顺手起个进程」都会被 `tests/test_static_audit.py` 打红。

## 它做什么

1. **白名单校验可执行文件**：basename 必须匹配 `^node(\\.exe)?$`，路径只来自 `config.bridge.nodePath`
   或 `shutil.which("node")`，**不接受调用方传入任意路径**；
2. **固定参数模板**：只有 `[exe, <repo>/third_party/new-acad/server/panel-relay.js]`，脚本路径由 `__file__` 推导，
   不拼接、不接受外部参数；
3. **环境变量白名单**：只注入桥层的 5 个变量 + `PATH`（指向 node 自身目录），**绝不透传整份环境**；
4. **输出重定向到文件**（见下，这是本文件最关键的约束）；
5. **失败即抛**（fail-closed）：不回退到「不起 relay 直接连插件端口」。

## 为什么输出必须重定向（两个坑，别再踩）

relay 用 `console.log` 打日志（`panel-relay.js` 的 `log()`），**每个请求一行**。于是：

* ❌ **继承 stdout** → 直接污染 MCP 的 JSON-RPC 通道，客户端解析即崩；
* ❌ **`stdout=PIPE` 但不读** → Node 写 fd 是**阻塞**的，管道缓冲（约 64 KB）填满后整个 relay 卡死，
  表现是「调用莫名超时」，而且极难归因到日志。

所以：`stdout` / `stderr` 指向**同一个二进制追加文件** `logs/relay-<relayPort>.log`，
`stdin=DEVNULL`，`close_fds=True`，Windows 上再加 `CREATE_NO_WINDOW`（不弹黑框 —— 与「别打扰用户桌面」同一方向）。

## 端口占用（TOCTOU）的显式处理

`pipeguard._port_available()` 是「bind 后立刻 close」的启发式，探测与 relay 真正 `listen` 之间有窗口。
本文件把这个窗口变成**显式行为**：`start_relay()` 启动后等端口可连；若子进程先退出（典型 `EADDRINUSE`），
**换一个 relay 端口重试一次**，仍失败才上抛 `C1003`。**不静默复用、不无限重试**。
"""

from __future__ import annotations

import errno
import os
import re
import shutil
import socket
import subprocess
import time

#: 允许的可执行文件名（白名单）。除 node 之外一律拒绝。
NODE_BASENAME_RE = re.compile(r"^node(\.exe)?$", re.IGNORECASE)

#: relay 脚本位置：由本文件位置推导，不接收外部路径。
RELAY_SCRIPT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "third_party", "new-acad", "server", "panel-relay.js")

#: 允许注入子进程的桥层变量（`pipeguard.BridgeGuard.environments()` 的产出）。
BRIDGE_ENV_KEYS = (
    "EP_RELAY_PORT",
    "EP_PLUGIN_PORT",
    "CIVIL3D_BRIDGE_PORT",
    "CIVIL3D_RELAY_PORT",
    "EP_BRIDGE_TOKEN",
)

#: 除桥层变量外，Node 在 Windows 上**唯一**必需的宿主机环境变量（见 `build_child_env()` 的说明）。
REQUIRED_HOST_ENV_KEYS = ("SYSTEMROOT",)

#: 日志轮转：超过此大小就把旧文件改名为 `.1`（只保留一份历史）。
LOG_MAX_BYTES = 5 * 1024 * 1024
#: 启动后等待 relay 端口可连的秒数。
START_TIMEOUT_SECONDS = 8.0
#: 允许的重试次数（含首次），即「失败后换端口重试一次」。
DEFAULT_ATTEMPTS = 2


class RelayLauncherError(Exception):
    """启动 relay 失败（fail-closed，不降级）。

    `code` 对齐 `protocol/errors.json`：`C1003 宿主不可达` —— 桥层起不来等价于「到宿主的链路不可达」。
    不新增错误码，避免动 `qa_error_codes` 的死码清单；细分靠 `detail`。
    """

    code = "C1003"

    def __init__(self, message: str, detail: str = "relay_launcher_unavailable"):
        super().__init__(message)
        self.message = message
        self.detail = detail

    def to_error(self) -> dict:
        return {
            "code": self.code,
            "message": self.message,
            "detail": self.detail,
            "retriable": False,
        }


def _null_audit(event: str, **_fields) -> None:
    """默认审计出口：什么都不做（调用方可注入自己的 audit）。"""


def relay_log_path(relay_port: int) -> str:
    """relay 原始输出文件路径（与审计日志分开：审计是结构化事件，这里是子进程的原始 stdout/stderr）。

    关于环境变量：本文件**不读整份环境**（静态审查禁止整份透传，也不该把父进程环境塞给子进程）。
    这里只读单个键 `LOCALAPPDATA` 用来定位日志目录，**不会进入子进程环境** ——
    子进程环境由 `build_child_env()` 逐键构造，见该函数。
    """
    base = os.getenv("LOCALAPPDATA") or os.path.expanduser("~")
    return os.path.join(base, "EngineeringPlatform", "logs", "relay-%d.log" % relay_port)


def _open_relay_log(relay_port: int) -> "object":
    """打开（并按需轮转）relay 日志，返回**二进制追加**文件对象。

    用二进制是为了不让 Python 再套一层编码：Node 写 fd 时已经是 UTF-8 字节，
    文本模式会二次编码/换行转换，日志里就会出现 `\\r\\r\\n` 之类的东西。
    """
    path = relay_log_path(relay_port)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    try:
        if os.path.getsize(path) > LOG_MAX_BYTES:
            rotated = path + ".1"
            if os.path.exists(rotated):
                os.unlink(rotated)
            os.replace(path, rotated)
    except OSError:
        # 轮转失败不阻断启动：宁可日志变大，也不能因为日志问题起不来 relay
        pass
    return open(path, "ab", buffering=0)


def resolve_node_path(config: dict | None = None) -> str:
    """解析 node 可执行文件的绝对路径，并做白名单校验。

    优先级：`config["bridge"]["nodePath"]`（若配置）→ `shutil.which("node")`。
    **不接受调用方传入的任意路径** —— 这是「唯一出口」的核心，参数化会立刻把白名单变成装饰。
    """
    configured = ""
    if isinstance(config, dict):
        configured = str((config.get("bridge") or {}).get("nodePath") or "").strip()
    candidate = configured or (shutil.which("node") or "")
    if not candidate:
        raise RelayLauncherError("找不到 node 可执行文件（未配置 bridge.nodePath，PATH 中也没有 node）")
    if not os.path.isabs(candidate):
        resolved = shutil.which(candidate) or ""
        if not resolved:
            raise RelayLauncherError("bridge.nodePath 不是绝对路径且无法在 PATH 中解析：%r" % candidate)
        candidate = resolved
    if not NODE_BASENAME_RE.match(os.path.basename(candidate)):
        raise RelayLauncherError(
            "可执行文件不在白名单内（只允许 node[.exe]）：%r" % os.path.basename(candidate))
    if not os.path.isfile(candidate):
        raise RelayLauncherError("node 路径不存在：%r" % candidate)
    return candidate


def build_child_env(bridge_env: dict, node_exe: str) -> dict:
    """构造子进程环境变量：**只有** 5 个桥层变量 + `PATH` + `SYSTEMROOT`。

    刻意不读整份父进程环境（逐键注入、不做整份透传）：令牌、以及用户其它变量都不该顺流入子进程。

    **为什么必须带 `SYSTEMROOT`（实测结论，不是猜的）**：只给 5 个变量 + `PATH` 时，Node 在本机直接 abort：

    ```
    Assertion failed: ncrypto::CSPRNG(nullptr, 0)   →  rc=134
    ```

    逐个变量二分验证：在基础集合上**只**加 `SYSTEMROOT` 就 `rc=0`；
    加 `WINDIR` / `TEMP` / `PATHEXT` / `COMSPEC` / `USERPROFILE` 等都**仍然崩**。
    即 Node 初始化 CSPRNG 需要 `%SystemRoot%` 来定位系统加密库，缺了就起不来（Windows 平台特性）。
    这是本文件唯一读取的宿主机变量，用 `os.getenv` 单独取一个键，**不是**透传。
    """
    missing = [key for key in BRIDGE_ENV_KEYS if key not in bridge_env]
    if missing:
        raise RelayLauncherError("桥层环境变量缺失：%s" % ", ".join(missing), detail="bridge_env_incomplete")
    if not str(bridge_env.get("EP_BRIDGE_TOKEN") or ""):
        raise RelayLauncherError("EP_BRIDGE_TOKEN 为空，拒绝启动 relay（fail-closed）",
                                 detail="bridge_token_missing")
    env = {key: str(bridge_env[key]) for key in BRIDGE_ENV_KEYS}
    env["PATH"] = os.path.dirname(node_exe) or os.defpath
    for key in REQUIRED_HOST_ENV_KEYS:
        value = os.getenv(key)
        if not value:
            raise RelayLauncherError(
                "缺少宿主机环境变量 %s，Node 无法初始化加密随机源（实测会 abort）" % key,
                detail="relay_launcher_unavailable")
        env[key] = value
    return env


def spawn_relay(bridge_env: dict, *, config: dict | None = None, audit=None,
                _popen=None) -> "subprocess.Popen":
    """启动一个 relay 子进程并返回句柄。**这是全仓唯一允许创建进程的地方。**

    参数：
        bridge_env: `BridgeGuard.environments()` 的产出（5 个键）。
        config:     可选配置（只用 `bridge.nodePath`）。
        audit:      可选审计回调 `audit(event, **fields)`；默认不做任何事。
        _popen:     仅测试注入用（默认 `subprocess.Popen`）。

    异常：
        `RelayLauncherError`（code `C1003`）：node 缺失/白名单不符/环境不完整/spawn 失败。
    """
    sink = audit or _null_audit
    if _popen is None and not os.path.isfile(RELAY_SCRIPT):
        raise RelayLauncherError(
            "relay 脚本不存在（未安装 relay 组件，跳过拉起且不写日志）：%r" % RELAY_SCRIPT,
            detail="relay_script_missing",
        )
    node_exe = resolve_node_path(config)
    child_env = build_child_env(bridge_env, node_exe)
    relay_port = int(child_env["EP_RELAY_PORT"])

    log_file = _open_relay_log(relay_port)
    kwargs = {
        "stdin": subprocess.DEVNULL,
        "stdout": log_file,          # 绝不继承、绝不 PIPE：见模块文档
        "stderr": log_file,
        "close_fds": True,
        "shell": False,              # 静态审查也禁止开启 shell 解释
        "env": child_env,
        "cwd": os.path.dirname(RELAY_SCRIPT),
    }
    if os.name == "nt":
        # 不弹控制台窗口（与「别打扰用户桌面」同一条约束）
        kwargs["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0x08000000)

    popen = _popen or subprocess.Popen
    try:
        proc = popen([node_exe, RELAY_SCRIPT], **kwargs)
    except OSError as exc:
        try:
            log_file.close()
        except OSError:
            pass
        sink("relay_spawn_failed", errno=getattr(exc, "errno", None), reason=str(exc)[:160],
             relayPort=relay_port, pluginPort=int(child_env["EP_PLUGIN_PORT"]))
        raise RelayLauncherError("启动 relay 失败：%s" % exc, detail="relay_spawn_failed") from exc

    sink("relay_spawned", pid=getattr(proc, "pid", None), relayPort=relay_port,
         pluginPort=int(child_env["EP_PLUGIN_PORT"]), logPath=relay_log_path(relay_port))
    return proc


def _port_accepting(port: int, timeout: float = 0.2) -> bool:
    """relay 是否已经在监听（只做一次 connect 探测，不重试）。"""
    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        probe.settimeout(timeout)
        probe.connect(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        probe.close()


def wait_until_listening(port: int, proc, timeout: float = START_TIMEOUT_SECONDS) -> bool:
    """等 relay 端口可连。子进程先退出则立刻返回 False（典型原因：端口被占）。"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            return False
        if _port_accepting(port):
            return True
        time.sleep(0.1)
    return False


def start_relay(guard, *, config: dict | None = None, audit=None,
                attempts: int = DEFAULT_ATTEMPTS) -> "subprocess.Popen":
    """启动 relay 并等它就绪；端口被占时**换一个 relay 端口重试一次**。

    为什么重试放在这一层（而不是 `BridgeGuard.allocate()` 里）：TOCTOU 的窗口在「relay 真正 listen」那一刻，
    不在探测那一刻。所以只有「启动后失败」才知道端口真被抢了 —— 此时应换端口重来，而不是让调用方去猜。

    注意：只重选 **relayPort**；`pluginPort` 属于插件进程，不能被我们改。
    """
    sink = audit or _null_audit
    last_reason = "not_started"
    for attempt in range(1, max(1, attempts) + 1):
        bridge_env = guard.environments()          # 端口未分配时会抛 BridgeError
        proc = spawn_relay(bridge_env, config=config, audit=sink)
        if wait_until_listening(int(bridge_env["EP_RELAY_PORT"]), proc):
            return proc
        last_reason = "port_busy" if proc.poll() is not None else "listen_timeout"
        sink("relay_start_failed", attempt=attempt, relayPort=int(bridge_env["EP_RELAY_PORT"]),
             exitCode=proc.poll(), reason=last_reason)
        try:
            proc.terminate()
            proc.wait(timeout=3)
        except (OSError, subprocess.TimeoutExpired):
            try:
                proc.kill()
            except OSError:
                pass
        if attempt < attempts:
            # 换 relay 端口（pluginPort 保持不变）
            guard.reallocate_relay_port()
    raise RelayLauncherError(
        "relay 启动失败（已尝试 %d 次，最后一次原因：%s）" % (attempts, last_reason),
        detail="relay_spawn_failed")
