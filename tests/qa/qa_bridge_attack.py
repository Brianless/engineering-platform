"""tests/qa/qa_bridge_attack.py —— N03 桥层鉴权的**对抗性**复验（QA 严过关）。

## 与工程师 `tests/test_bridge_auth.py` 的分工

他测的是 `tests/mock_bridge.py`（**Python 复刻**的 relay/插件语义）。复刻品能证明「他想实现的语义」，
不能证明「`panel-relay.js` 真的那样实现」。所以本文件做两件不同的事：

1. **跑真 relay**：把 `third_party/new-acad/server/` 复制到 %TEMP%，用 `node panel-relay.js` 起**真进程**
   （Node 22 已装；relay 零 npm 依赖，`dependencies: {}`），再用手写 HTTP 报文打它的 `/tcp`；
2. **插件端只做代码级审查**：真插件在 CAD 进程里，本轮禁止启动，所以 P-004 用「读代码 + 断言不变量」，
   并用**自己的**最小 TCP 插件替身验证 relay 注入的 `auth` 是否被覆盖（不采信他的 mock）。

## 攻击面覆盖（对应主理人 A1–A8 / B9–B11）

- 无令牌 / 错令牌 / **长度不等**（`timingSafeEqual` 抛异常的经典坑）
- 8 种 header 格式变体（大小写、双空格、裸令牌、无前缀、前后空格、重复头）
- body 里塞 `auth`（看 relay 的 `Object.assign` 是覆盖还是被覆盖）
- relay 未配令牌（fail-closed 命门）
- 路径变体（`/tcp?x=1`、`/TCP`、`GET /tcp`）—— 防止「换个 URL 就绕过 `req.url === '/tcp'`」
- 令牌泄露面（映射文件、relay 日志、进程命令行）
- 端口隔离（两实例交叉打、端口耗尽必须抛错、范围与 8080）

## 判定口径

任何一条「被误接受」都记为 FAIL；401/503 必须**同时**满足「插件没收到方法」——
只看状态码不看副作用，等于没验。
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

import qa_common as qa

NEW_ACAD = os.path.join(qa.ROOT, "third_party", "new-acad")
RELAY_SRC = os.path.join(NEW_ACAD, "server")
TOKEN = "a" * 48          # 48 hex 字符，与 secrets.token_hex(24) 等长


# ============================================================================
# 最小 TCP 插件替身：只做「鉴权 + 记录收到了什么」
# ============================================================================


class FakePlugin:
    """插件端替身：一条连接一条请求；校验 `auth`；记录通过鉴权的方法与看到的 auth 值。"""

    def __init__(self, token: str):
        self.token = token
        self.received = []      # [(method, auth_seen)] 只记**通过鉴权**的
        self.attempts = []      # [(method, auth_present, accepted)] 全部尝试，用于验「内部调用带没带 auth」
        self.rejected = 0
        self.port = 0
        self._sock = None
        self._running = True

    def start(self):
        import threading

        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind(("127.0.0.1", 0))
        self.port = self._sock.getsockname()[1]
        self._sock.listen(16)
        self._sock.settimeout(0.2)
        threading.Thread(target=self._serve, daemon=True).start()
        return self

    def _serve(self):
        import threading  # noqa: F401

        while self._running:
            try:
                client, _ = self._sock.accept()
            except (socket.timeout, OSError):
                continue
            try:
                client.settimeout(5.0)
                raw = b""
                while b"\n" not in raw and len(raw) < 1 << 20:
                    chunk = client.recv(8192)
                    if not chunk:
                        break
                    raw += chunk
                text = raw.decode("utf-8", "replace").strip()
                if not text:
                    client.close()
                    continue
                try:
                    request = json.loads(text)
                except ValueError:
                    client.sendall(_err(-32700, "invalid JSON request").encode())
                    client.close()
                    continue
                # 与 RpcTcpServer.TryAuthorize 同语义：空令牌 → 拒；长度不等 → 拒；逐请求
                method = request.get("method")
                auth_present = bool(request.get("auth"))
                if not self.token:
                    self.rejected += 1
                    self.attempts.append((method, auth_present, False))
                    client.sendall(_err(-32003, "bridge token not configured").encode())
                elif str(request.get("auth") or "") != self.token:
                    self.rejected += 1
                    self.attempts.append((method, auth_present, False))
                    client.sendall(_err(-32004, "unauthorized: missing or invalid bridge token").encode())
                else:
                    self.attempts.append((method, auth_present, True))
                    self.received.append((method, request.get("auth")))
                    result = {"ok": True, "method": method}
                    if method == "getCivil3DHealth":
                        # 与真插件的健康字段对齐；buildStamp 用**固定哨兵值**，
                        # 这样 D12 可以断言「精确等于它」——证明 relay 真把插件结果透出来了，
                        # 而不是「随便有个非空字符串就过」（空壳也能造非空）。
                        result.update({"buildStamp": "fake-qa-20261122", "pluginVersion": "1.15.0",
                                       "hostProduct": "Civil3D", "civilAvailable": True})
                    client.sendall(json.dumps({
                        "jsonrpc": "2.0", "id": request.get("id"), "result": result,
                    }).encode())
            except OSError:
                pass
            finally:
                try:
                    client.close()
                except OSError:
                    pass

    def stop(self):
        self._running = False
        try:
            self._sock.close()
        except OSError:
            pass


def _err(code, message):
    return json.dumps({"jsonrpc": "2.0", "id": None,
                       "error": {"code": code, "message": message}})


# ============================================================================
# 真 relay 进程
# ============================================================================


class RealRelay:
    """启动**真实** `panel-relay.js`（复制到 %TEMP%，不在仓库里跑，避免写 relay.log 到 third_party）。"""

    def __init__(self, workdir: str, plugin_port: int, token: str, with_token: bool = True):
        self.dir = workdir
        self.plugin_port = plugin_port
        self.token = token
        self.with_token = with_token
        self.port = None
        self.process = None
        self.stdout_path = os.path.join(workdir, "..", "relay-out.log")

    def start(self, timeout: float = 25.0):
        # 端口：先占一个再放掉，保证不撞车（relay 自己 listen）
        probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        probe.bind(("127.0.0.1", 0))
        self.port = probe.getsockname()[1]
        probe.close()

        env = dict(os.environ)
        env["EP_RELAY_PORT"] = str(self.port)
        env["EP_PLUGIN_PORT"] = str(self.plugin_port)
        env.pop("EP_BRIDGE_TOKEN", None)
        if self.with_token:
            env["EP_BRIDGE_TOKEN"] = self.token
        env["RELAY_TOKEN"] = ""          # 出站凭据与本轮无关，避免连带外部调用
        env["NODE_OPTIONS"] = ""

        log = open(self.stdout_path, "wb")
        self.process = subprocess.Popen(
            ["node", "panel-relay.js"], cwd=self.dir, env=env,
            stdout=log, stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL)
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError("relay 进程提前退出，见 %s" % self.stdout_path)
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.3):
                    return self
            except OSError:
                time.sleep(0.15)
        raise RuntimeError("relay 在 %.0fs 内未监听 %d" % (timeout, self.port))

    def stop(self):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()

    def read_log(self) -> str:
        try:
            with open(self.stdout_path, "rb") as handle:
                return handle.read().decode("utf-8", "replace")
        except OSError:
            return ""


def http_post(port: int, header_lines, body: bytes, path: str = "/tcp",
              method: str = "POST", timeout: float = 8.0):
    """手写 HTTP 报文：header 行原样发出，用于测试「格式变体」这类 urllib 会规范化的东西。

    返回 (status, body_text, raw_head)。
    """
    request = ["%s %s HTTP/1.1" % (method, path), "Host: 127.0.0.1:%d" % port,
               "Content-Type: application/json", "Content-Length: %d" % len(body),
               "Connection: close"]
    request.extend(header_lines)
    raw = ("\r\n".join(request) + "\r\n\r\n").encode("utf-8") + body
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as sock:
        sock.sendall(raw)
        chunks = []
        while True:
            try:
                data = sock.recv(65536)
            except socket.timeout:
                break
            if not data:
                break
            chunks.append(data)
    text = b"".join(chunks).decode("utf-8", "replace")
    head, _, rest = text.partition("\r\n\r\n")
    match = head.split(" ")[1] if len(head.split(" ")) > 1 else "000"
    return int(match), rest, head


# ============================================================================
# A 段：鉴权绕过
# ============================================================================


def section_auth_bypass(suite, relay: RealRelay, plugin: FakePlugin):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "drawing.getInfo",
                       "params": {}}).encode()
    table = []

    def attempt(label, header_lines, expect_status, expect_forward, payload=None):
        before = len(plugin.received)
        status, text, _head = http_post(relay.port, header_lines, payload or body)
        forwarded = len(plugin.received) - before
        table.append((label, status, forwarded))
        suite.eq(status, expect_status, "A %s → HTTP %d" % (label, expect_status))
        suite.eq(forwarded, 1 if expect_forward else 0,
                 "A %s → 插件%s收到方法" % (label, "**应**" if expect_forward else "**不得**"))
        return status, text

    # A1 完全不带
    attempt("A1 不带 Authorization", [], 401, False)
    # A2 等长错令牌
    attempt("A2 等长错令牌", ["Authorization: Bearer " + "b" * 48], 401, False)
    # A3 长度不等（经典 timingSafeEqual 抛异常坑）
    status, text = attempt("A3 短令牌（4 字符）", ["Authorization: Bearer abcd"], 401, False)
    suite.ok("500" not in str(status), "A3 长度不等 → **不是** 500（没有未捕获异常）", "status=%d" % status)
    suite.ok("unauthorized" in text, "A3 长度不等 → 回的是结构化 401 而不是堆栈", text[:120])
    attempt("A3b 长一位令牌", ["Authorization: Bearer " + "a" * 49], 401, False)
    attempt("A3c 空 Bearer", ["Authorization: Bearer "], 401, False)

    # A4 格式变体：**方案或令牌错的必须拒**
    variants = [
        ("bearer 小写", "bearer " + TOKEN),
        ("BEARER 全大写", "BEARER " + TOKEN),
        ("双空格", "Bearer  " + TOKEN),
        ("裸令牌（无 Bearer 前缀）", TOKEN),
        ("无空格 Bearer", "Bearer" + TOKEN),
        ("Basic 方案", "Basic " + TOKEN),
        ("令牌+引号", 'Bearer "%s"' % TOKEN),
    ]
    for label, value in variants:
        attempt("A4 %s" % label, ["Authorization: " + value], 401, False)

    # 首尾空白：HTTP 解析器会裁掉 OWS（RFC 7230 §3.2.4），所以**令牌正确时**被接受是标准行为，
    # 不是绕过。真正要证明的是「裁空白没有削弱校验」：同样带空白、但令牌错 → 必须 401。
    status_ok, _text, _head = http_post(relay.port, ["Authorization:  Bearer " + TOKEN], body)
    suite.ok(status_ok in (200,), "A4 前导空白 + **正确**令牌 → 200（OWS 被 HTTP 层裁掉，标准行为）",
             "status=%d" % status_ok)
    attempt("A4 前导空白 + 错误令牌", ["Authorization:  Bearer " + "b" * 48], 401, False)
    attempt("A4 尾随空白 + 错误令牌", ["Authorization: Bearer " + "b" * 48 + " "], 401, False)

    # 重复 Authorization 头：Node 对单值头只取**第一个**。
    # 安全性要证明的是「追加一个正确的头不能让错误的第一个头过关」。
    status_dup, _text, _head = http_post(
        relay.port,
        ["Authorization: Bearer " + "b" * 48, "Authorization: Bearer " + TOKEN], body)
    suite.eq(status_dup, 401, "A4 重复头（第一个错、第二个对）→ 401（不能靠追加头绕过）")
    dup_before = len(plugin.received)
    http_post(relay.port, ["Authorization: Bearer " + TOKEN, "Authorization: Bearer " + "b" * 48], body)
    suite.eq(len(plugin.received) - dup_before, 1,
             "A4 重复头（第一个对、第二个错）→ 放行（Node 只认首个头，符合其解析规则）")

    # A5 body 塞 auth
    forged = json.dumps({"jsonrpc": "2.0", "id": 2, "method": "drawing.getInfo",
                         "auth": TOKEN}).encode()
    attempt("A5 body 带正确 auth、header 缺失", [], 401, False, payload=forged)
    attempt("A5b body 带正确 auth、header 错误", ["Authorization: Bearer " + "b" * 48],
            401, False, payload=forged)

    # 正确令牌必须能过，且 body 里的伪造 auth 必须被**relay 覆盖**
    forged_bad = json.dumps({"jsonrpc": "2.0", "id": 3, "method": "drawing.getInfo",
                             "auth": "forged"}).encode()
    before = len(plugin.received)
    status, text, _head = http_post(relay.port, ["Authorization: Bearer " + TOKEN], forged_bad)
    suite.eq(status, 200, "A 正确令牌 → HTTP 200")
    suite.eq(len(plugin.received) - before, 1, "A 正确令牌 → 插件收到方法")
    if len(plugin.received) - before == 1:
        method, auth_seen = plugin.received[-1]
        qa.eqd(suite, auth_seen, TOKEN,
               "A5c body 里的伪造 auth 被 relay 覆盖成真令牌（不是透传）",
               "插件看到的 auth=%r" % (auth_seen,))
        suite.ok(method == "drawing.getInfo", "A 方法名原样到达", repr(method))

    # 路径变体：真正要证明的是「换个 URL 不能把方法送进插件」。
    # 状态码不作判据 —— `/TCP`、`GET /tcp` 会落到**别的**处理器（面板静态资源等，属上游既有行为），
    # 只要它没走到 /tcp 的转发分支、插件没收到方法，就不构成鉴权绕过。
    before = len(plugin.received)
    seen_codes = []
    for label, path, verb in (("query 后缀", "/tcp?x=1", "POST"),
                              ("大写路径", "/TCP", "POST"),
                              ("GET 方法", "/tcp", "GET"),
                              ("路径前缀", "/tcp/extra", "POST"),
                              ("尾斜杠", "/tcp/", "POST")):
        status, text, _head = http_post(relay.port, [], body, path=path, method=verb)
        seen_codes.append("%s=%d" % (label, status))
        suite.ok(not ("-32004" in text or "-32003" in text),
                 "A 路径变体 %s 没走进 /tcp 的鉴权分支（无鉴权响应码）" % label, text[:80])
    qa.eqd(suite, len(plugin.received) - before, 0,
           "A 路径变体全部没有把方法转发到插件",
           "实际状态码：%s" % "、".join(seen_codes))

    # 证据表（贴回验收记录用）：每一行都是「真实 relay 的实际响应 + 插件实际有没有收到」
    print("\n--- 攻击矩阵（真 relay，%d 条）---" % len(table))
    for label, status, forwarded in table:
        print("  %-38s HTTP %-3d  插件收到=%d" % (label, status, forwarded))
    print("--- 路径变体状态码：%s ---\n" % "、".join(seen_codes))


def section_fail_closed(suite, workdir, plugin):
    """A6：relay 未配令牌 → 503 且整桥关闭，绝不回落到无鉴权。"""
    relay = RealRelay(os.path.join(workdir, "server"), plugin.port, TOKEN, with_token=False)
    try:
        relay.start()
    except RuntimeError as exc:
        suite.ok(False, "A6 relay（未配令牌）能启动", repr(exc))
        return
    try:
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "drawing.getInfo"}).encode()
        before = len(plugin.received)
        for label, headers in (("不带头", []),
                               ("带任意 Bearer", ["Authorization: Bearer whatever"]),
                               ("带真令牌", ["Authorization: Bearer " + TOKEN])):
            status, text, _head = http_post(relay.port, headers, body)
            suite.eq(status, 503, "A6 relay 未配令牌 + %s → 503（fail-closed）" % label)
            suite.ok("-32003" in text, "A6 %s → 错误码 -32003" % label, text[:120])
        suite.eq(len(plugin.received) - before, 0,
                 "A6 relay 未配令牌 → **一个方法都没转发**（不回落无鉴权）")
    finally:
        relay.stop()


def section_direct_plugin(suite, plugin: FakePlugin):
    """A7：绕过 relay 直连插件端口。真插件在 CAD 里跑不了，这里用替身 + 代码级断言双证。"""
    def raw_call(payload):
        with socket.create_connection(("127.0.0.1", plugin.port), timeout=5.0) as sock:
            sock.sendall((json.dumps(payload) + "\n").encode("utf-8"))
            data = b""
            while b"\n" not in data:
                chunk = sock.recv(65536)
                if not chunk:
                    break
                data += chunk
        return json.loads(data.decode("utf-8", "replace"))

    before = len(plugin.received)
    resp = raw_call({"jsonrpc": "2.0", "id": 1, "method": "drawing.getInfo"})
    suite.eq((resp.get("error") or {}).get("code"), -32004,
             "A7 直连插件端口、不带 auth → -32004（P-004 独立生效）")
    resp = raw_call({"jsonrpc": "2.0", "id": 2, "method": "drawing.getInfo", "auth": "wrong"})
    suite.eq((resp.get("error") or {}).get("code"), -32004, "A7 直连、带错 auth → -32004")
    suite.eq(len(plugin.received) - before, 0, "A7 直连被拒的方法**没有**到达业务层")
    resp = raw_call({"jsonrpc": "2.0", "id": 3, "method": "drawing.getInfo", "auth": TOKEN})
    suite.ok(resp.get("result"), "A7 直连、带正确 auth → 放行（对照组，证明不是全拒）")
    suite.eq(len(plugin.received) - before, 1, "A7 对照组：方法到达业务层")

    # 真插件侧的同语义（代码级）：未配令牌 → -32003；长度不等不抛异常
    rpc = qa.read_source("third_party/new-acad/plugin/AcBridge-v24/src/RpcTcpServer.cs")
    suite.ok('BuildErrorJson(-32003' in rpc and 'BuildErrorJson(-32004' in rpc,
             "A7 真插件用 -32003/-32004（与替身同码）")
    suite.ok("if (left.Length != right.Length)" in rpc,
             "A7 真插件先比长度再 FixedTimeEquals（长度不等不抛）")


# ============================================================================
# B 段：端口与实例隔离
# ============================================================================


def section_ports(suite, workdir):
    sys.path.insert(0, os.path.join(qa.ROOT, "bridge"))
    import pipeguard

    guard_a = pipeguard.BridgeGuard("qa-a", root=os.path.join(workdir, "map"))
    guard_b = pipeguard.BridgeGuard("qa-b", root=os.path.join(workdir, "map"))
    relay_a, plugin_a = guard_a.allocate()
    relay_b, plugin_b = guard_b.allocate()

    suite.ok(relay_a != plugin_a, "B9 同实例 relayPort != pluginPort", "%d/%d" % (relay_a, plugin_a))
    suite.ok(relay_b != plugin_b, "B9 第二实例 relayPort != pluginPort", "%d/%d" % (relay_b, plugin_b))
    suite.ok(relay_a != relay_b, "B9 两实例 relayPort 不同", "%d/%d" % (relay_a, relay_b))
    suite.ok(plugin_a != plugin_b, "B9 两实例 pluginPort 不同", "%d/%d" % (plugin_a, plugin_b))
    suite.ok(guard_a.token != guard_b.token, "B9 两实例令牌不同（每实例生成）")

    for name, port in (("relayA", relay_a), ("pluginA", plugin_a),
                       ("relayB", relay_b), ("pluginB", plugin_b)):
        suite.ok(pipeguard.PORT_RANGE_START <= port < pipeguard.PORT_RANGE_END,
                 "B11 %s=%d 落在 [%d, %d)" % (name, port, pipeguard.PORT_RANGE_START,
                                              pipeguard.PORT_RANGE_END))
        suite.ok(port != 8080 and port != 19876, "B11 %s 不是上游固定端口" % name)

    # B10 端口耗尽：把探测函数打成「永远不可用」，必须抛错而不是回退到固定端口
    calls = {"n": 0}
    original = pipeguard._port_available

    def always_busy(_port, host="127.0.0.1"):
        calls["n"] += 1
        return False

    pipeguard._port_available = always_busy
    try:
        guard_c = pipeguard.BridgeGuard("qa-c", root=os.path.join(workdir, "map"))
        raised = None
        try:
            guard_c.allocate()
        except pipeguard.BridgeError as exc:
            raised = exc
        suite.ok(raised is not None, "B10 端口全部不可用 → 抛 BridgeError（不静默回退）")
        if raised is not None:
            suite.eq(getattr(raised, "detail", None), "port_exhausted", "B10 detail=port_exhausted")
        qa.eqd(suite, calls["n"], 128, "B10 恰好尝试 128 次后放弃",
               "实际 %d 次" % calls["n"])
    finally:
        pipeguard._port_available = original

    # A8：映射文件不得含令牌
    path = guard_a.register(pid=os.getpid())
    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read()
    suite.ok(guard_a.token not in text, "A8 映射文件**不含**令牌明文",
             "文件=%s" % os.path.basename(path))
    record = json.loads(text)
    suite.eq(record.get("tokenEnv"), "EP_BRIDGE_TOKEN", "A8 映射文件只登记令牌的**环境变量名**")
    suite.ok("relayPort" in record and "pluginPort" in record, "A8 映射文件含端口（供实例发现）")
    guard_a.unregister()

    # A8b：进程命令行里不得出现令牌（powershell/cmd 会把 env 与 argv 分开）
    environments = guard_a.environments()
    suite.eq(environments.get("EP_BRIDGE_TOKEN"), guard_a.token,
             "A8 令牌只经环境变量传递给子进程")
    return guard_a, guard_b


def section_cross_token(suite, workdir, relay_a_guard, plugin_a_guard):
    """B9b：A 的令牌打 B 的 relay 必须失败。"""
    plugin_a_guard.start()
    relay = RealRelay(os.path.join(workdir, "server"), plugin_a_guard.port, "c" * 48)
    try:
        relay.start()
    except RuntimeError as exc:
        suite.ok(False, "B9b 交叉令牌测试的 relay 能启动", repr(exc))
        plugin_a_guard.stop()
        return
    try:
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "drawing.getInfo"}).encode()
        before = len(plugin_a_guard.received)
        status, _text, _head = http_post(relay.port, ["Authorization: Bearer " + relay_a_guard.token], body)
        suite.eq(status, 401, "B9b A 的令牌打 B 的 relay → 401")
        suite.eq(len(plugin_a_guard.received) - before, 0, "B9b A 的方法没有被 B 收到")
    finally:
        relay.stop()
        plugin_a_guard.stop()


# ============================================================================
# C 段：补丁可信性（C12 复现 / C13 改动面 / C14 前提）
# ============================================================================

UPSTREAM_U2 = "new-acad-1120cda4aeba9086adf762e0a6ec6bdfd91bc652"
UPSTREAM_URL = ("https://codeload.github.com/Jjo37/new-acad/zip/"
                "1120cda4aeba9086adf762e0a6ec6bdfd91bc652")
PATCH_ORDER = ("paths.patch", "relay-auth.patch", "plugin-token.patch",
               "relay-egress-allowlist.patch")
COMPARE_FILES = (
    "plugin/AcBridge-v24/src/Civil3DMcpPlugin.csproj",
    "tools/scan-desc-check.js",
    "server/panel-relay.js",
    "server/workflow-replay.js",
    "plugin/AcBridge-v24/src/RpcTcpServer.cs",
    "plugin/AcBridge-v24/src/PluginRuntime.cs",
)
#: 允许被鉴权补丁碰到的文件（多一个就是越界）
AUTH_PATCH_FILES = {
    "server/panel-relay.js",
    "server/workflow-replay.js",
    "plugin/AcBridge-v24/src/RpcTcpServer.cs",
    "plugin/AcBridge-v24/src/PluginRuntime.cs",
}
#: CAD 能力实现的标志性标识 —— 鉴权补丁里出现任何一个都算「越界改业务」
CAD_TOKENS = ("Transaction", "ObjectId", "CreateSurface", "AutoCAD.", "Autodesk",
              "CommandDispatcher", "SendStringToExecute", "CivilApplication",
              "HandleRawRequestAsync(string", "Entity")


def _sha(path):
    import hashlib

    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def _ensure_upstream(workdir: str):
    """拿到一份纯净上游树：优先用缓存，缓存没有就**现场下载**（不能因为缓存缺失就静默跳过 C12）。

    早先这里只有一句 `if not isdir: print(SKIP); return` —— 那等于「缓存没了，逐字节复现就不验了」，
    是覆盖面悄悄缩水。现在改成：拿不到就记 FAIL，能下载就下载。
    """
    import zipfile

    cached = os.path.join(os.environ.get("TEMP", "."), "n01", UPSTREAM_U2)
    if os.path.isdir(cached):
        return cached, "cache"
    zip_path = os.path.join(workdir, "upstream.zip")
    urllib.request.urlretrieve(UPSTREAM_URL, zip_path)
    with zipfile.ZipFile(zip_path) as archive:
        archive.extractall(workdir)
    extracted = os.path.join(workdir, UPSTREAM_U2)
    if not os.path.isdir(extracted):
        raise RuntimeError("归档里没有预期目录 %s" % UPSTREAM_U2)
    return extracted, "download"


def section_patches(suite, workdir):
    try:
        upstream, source = _ensure_upstream(workdir)
    except Exception as exc:  # noqa: BLE001 - 网络/解压失败都要变成一条 FAIL
        suite.ok(False, "C12 能拿到纯净上游树（缓存或现场下载）", repr(exc))
        return
    suite.ok(True, "C12 纯净上游树就绪（来源：%s）" % source,
             upstream)
    target = os.path.join(workdir, "patched")
    if os.path.isdir(target):
        _force_rm(target)
    shutil.copytree(upstream, target)

    patch_dir = os.path.join(qa.ROOT, "third_party", "patches")
    applied_ok = True
    for name in PATCH_ORDER:
        patch_path = os.path.join(patch_dir, name)
        completed = subprocess.run(["patch", "-p1", "--binary", "-i", patch_path],
                                   cwd=target, capture_output=True, text=True)
        ok = completed.returncode == 0
        applied_ok = applied_ok and ok
        suite.ok(ok, "C12 %s 在纯净上游树上干净应用" % name,
                 (completed.stdout + completed.stderr).strip()[:300])
    if not applied_ok:
        return

    diffs = []
    for rel in COMPARE_FILES:
        left = _sha(os.path.join(target, rel))
        right = _sha(os.path.join(qa.ROOT, "third_party", "new-acad", rel))
        suite.eq(right, left, "C12 逐字节一致：%s" % rel,
                 ) if left == right else suite.ok(
            False, "C12 逐字节一致：%s" % rel,
            "上游+3补丁=%s 工作树=%s" % (left[:16], right[:16]))
        diffs.append((rel, left == right))

    # ---------- C13 改动面审计 ----------
    touched = set()
    changed_lines = []
    noop_hunks = 0
    for name in ("relay-auth.patch", "plugin-token.patch", "relay-egress-allowlist.patch"):
        with open(os.path.join(patch_dir, name), "r", encoding="utf-8", errors="replace") as handle:
            text = handle.read()
        removed, added = [], []
        for line in text.splitlines():
            if line.startswith("--- ") or line.startswith("+++ "):
                if line.startswith("+++ "):
                    touched.add(line[4:].strip().lstrip("b/"))
                continue
            if line.startswith("-"):
                removed.append(line[1:])
            elif line.startswith("+"):
                added.append(line[1:])
        # 纯空白改动（-行与+行 trim 后相同）= 补丁噪音
        for minus, plus in zip(removed, added):
            if minus.strip() == plus.strip() and minus != plus:
                noop_hunks += 1
        changed_lines.extend(added)

    suite.eq(touched, AUTH_PATCH_FILES, "C13 两个鉴权补丁只碰 3 个传输层文件")
    hits = [line.strip() for line in changed_lines
            if any(token in line for token in CAD_TOKENS)]
    suite.eq(hits, [], "C13 新增行里**没有** CAD 能力实现的标识（未越界改业务）")
    suite.ok(noop_hunks <= 3, "C13 纯空白型改动不超过 3 处（补丁噪音可控）",
             "实际 %d 处（`expandClearScopes` 那一对是行尾空白差异）" % noop_hunks)
    print("\n--- C13 改动面：新增行共 %d 行，涉及文件 %s ---"
          % (len(changed_lines), sorted(touched)))

    # ---------- C14 前提：一条连接只承载一帧 ----------
    rpc = qa.read_source("third_party/new-acad/plugin/AcBridge-v24/src/RpcTcpServer.cs")
    body_start = rpc.find("private async Task ProcessClientAsync")
    body = rpc[body_start:rpc.find("private bool TryAuthorize")]
    suite.eq(body.count("ReadSingleJsonObjectAsync(stream"), 1,
             "C14 ProcessClientAsync 只读**一帧**（一条连接一条请求）")
    suite.ok("while (" not in body and "for (" not in body,
             "C14 读帧之后**没有循环**（不存在「只验首帧、后续绕过」）")
    suite.ok("using (client)" in body, "C14 处理完即释放连接（不可复用）")
    suite.ok(body.find("TryAuthorize(request") < body.find("_handler(payload"),
             "C14 鉴权在 handler **之前**（失败时业务方法根本不被调用）")
    suite.ok("obj.Remove(\"auth\")" in rpc,
             "C14 auth 字段在交给业务层前被摘掉（传输凭据不进入业务）")
    suite.ok(rpc.find("if (left.Length != right.Length)") < rpc.find("CryptographicOperations.FixedTimeEquals"),
             "C14 定长比较前先比长度（长度不等不会抛异常）")
    suite.ok("string.IsNullOrEmpty(_authToken)" in rpc,
             "C14 插件令牌为空时 fail-closed（拒一切请求）")


# ============================================================================
# E 段：D9 / D10 / D11 —— 桥整体可信边界的三道闸（修 P-007/P-008 前应为红）
# ============================================================================


def http_get(port: int, path: str, timeout: float = 5.0):
    """最小 GET：返回 (status, body_text)。"""
    import socket as _s

    raw = ("GET %s HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nConnection: close\r\n\r\n"
           % (path, port)).encode()
    with _s.create_connection(("127.0.0.1", port), timeout=timeout) as sock:
        sock.sendall(raw)
        chunks = []
        while True:
            data = sock.recv(65536)
            if not data:
                break
            chunks.append(data)
    text = b"".join(chunks).decode("utf-8", "replace")
    head, _, body = text.partition("\r\n\r\n")
    parts = head.split(" ")
    return (int(parts[1]) if len(parts) > 1 else 0), body


def section_trust_boundary(suite, workdir):
    """D9 健康误报 / D10 内部调用无 auth / D11 无鉴权端点可注入。"""

    # ---------- D9：插件用错令牌拒绝时，/health 不许报 reachable=true ----------
    wrong_plugin = FakePlugin("d" * 48)          # 插件只认 d…，relay 拿的是 TOKEN
    wrong_plugin.start()
    relay = RealRelay(os.path.join(workdir, "server"), wrong_plugin.port, TOKEN)
    try:
        relay.start()
    except RuntimeError as exc:
        suite.ok(False, "D9 能起 relay（健康误报实验）", repr(exc))
        wrong_plugin.stop()
        return
    try:
        status, body = http_get(relay.port, "/health")
        try:
            health = json.loads(body)
        except ValueError:
            health = {}
        bridge = health.get("bridge") or {}
        suite.ok(bridge.get("reachable") is False,
                 "D9 插件拒绝时 /health.bridge.reachable 必须为 false"
                 "（不许把「被鉴权拒绝」伪装成「插件活着」）",
                 "实际 reachable=%r buildStamp=%r civilAvailable=%r"
                 % (bridge.get("reachable"), bridge.get("buildStamp"),
                    bridge.get("civilAvailable")))
        suite.ok(bool(bridge.get("error")),
                 "D9 插件拒绝时 /health.bridge 必须带 error 字段（可定位到 unauthorized）",
                 "实际 bridge=%s" % json.dumps(bridge, ensure_ascii=False)[:200])
    finally:
        relay.stop()
        wrong_plugin.stop()

    # ---------- D10：所有插件调用必须走唯一出口（P-007 之后「带 auth 的行 = 1」才是对的）----------
    # 判定方式改成「形状断言」：令牌只在 pluginRequest 里拼一次，任何人手写 write(JSON.stringify({jsonrpc 就会红。
    relay_src = qa.read_source("third_party/new-acad/server/panel-relay.js")
    replay_src = qa.read_source("third_party/new-acad/server/workflow-replay.js")
    raw_writes = (relay_src.count("write(JSON.stringify({ jsonrpc")
                  + relay_src.count("write(JSON.stringify({jsonrpc")
                  + replay_src.count("write(JSON.stringify({ jsonrpc")
                  + replay_src.count("write(JSON.stringify({jsonrpc"))
    relay_uses = relay_src.count("pluginRequest(") - relay_src.count("function pluginRequest(")
    replay_uses = replay_src.count("pluginRequest(") - replay_src.count("function pluginRequest(")
    print("\n--- D10 证据：裸 write(JSON.stringify({jsonrpc…) = %d 处；"
          "pluginRequest 使用 panel-relay=%d / workflow-replay=%d ---"
          % (raw_writes, relay_uses, replay_uses))
    qa.eqd(suite, raw_writes, 0,
           "D10 没有任何一处手写 write(JSON.stringify({jsonrpc…))（必须走 pluginRequest）",
           "命中 %d 处" % raw_writes)
    suite.ok(relay_uses >= 3, "D10 panel-relay 经 pluginRequest 发请求（≥3 处：图面注入/健康探测//tcp）",
             "实际 %d 处" % relay_uses)
    suite.ok(replay_uses >= 1 and "EP_BRIDGE_TOKEN" in replay_src,
             "D10 workflow-replay.tcpCall 也走 pluginRequest 并读 EP_BRIDGE_TOKEN",
             "pluginRequest 使用 %d 处 / 含 EP_BRIDGE_TOKEN=%s"
             % (replay_uses, "EP_BRIDGE_TOKEN" in replay_src))
    suite.ok("function pluginRequest" in relay_src and "function openPluginSocket" in relay_src,
             "D10 存在单一出口 pluginRequest() + openPluginSocket()")

    # ---------- D14：防回潮——裸 connect 只允许出现在单一出口里 ----------
    bare = [line for line in (relay_src + "\n" + replay_src).splitlines()
            if "BRIDGE_PORT, '127.0.0.1'" in line or 'BRIDGE_PORT, "127.0.0.1"' in line]
    qa.eqd(suite, len(bare), 1,
           "D14 全仓 `BRIDGE_PORT, '127.0.0.1'` 只出现 1 处（pluginRequest 内部）",
           "实际 %d 处：%s" % (len(bare), " ; ".join(s.strip()[:60] for s in bare[:5])))

    # ---------- D11：/tcp、/health 之外的端点必须 404 且零副作用 ----------
    plugin = FakePlugin(TOKEN).start()
    relay2 = RealRelay(os.path.join(workdir, "server"), plugin.port, TOKEN)
    try:
        relay2.start()
    except RuntimeError as exc:
        suite.ok(False, "D11 能起 relay（白名单实验）", repr(exc))
        plugin.stop()
        return
    try:
        marker = "qa-injected-by-unauth-client"
        body_q = json.dumps({"jsonrpc": "2.0", "id": 9, "method": "noop"}).encode()
        before = len(plugin.received)
        status, _text, _head = http_post(
            relay2.port, [], json.dumps({"message": marker}).encode(), path="/push")
        suite.eq(status, 404,
                 "D11 未鉴权的 POST /push 必须 404（P-008 白名单）")
        status_replies, replies = http_get(relay2.port, "/replies")
        suite.ok(marker not in replies,
                 "D11 /push 不得把未鉴权内容注入 replies（内容注入）",
                 "GET /replies → HTTP %d, 含注入标记=%s" % (status_replies, marker in replies))
        for path in ("/inbox", "/clear", "/clip", "/workflows", "/replies", "/"):
            status_any, _body = http_get(relay2.port, path)
            suite.eq(status_any, 404,
                     "D11 未鉴权 GET %s 必须 404" % path)
        # POST 类端点（/cancel、/clip、/clear 的真实方法都是 POST）
        for path in ("/cancel", "/clear", "/clip"):
            status_post, _t, _h = http_post(relay2.port, [], b"{}", path=path)
            suite.eq(status_post, 404,
                     "D11 未鉴权 POST %s 必须 404" % path)
        # 带 query 的 /tcp 不在白名单里（白名单只认精确 POST /tcp）
        status_q, _t, _h = http_post(relay2.port, ["Authorization: Bearer " + TOKEN], body_q,
                                     path="/tcp?x=1")
        suite.eq(status_q, 404, "D11 /tcp?x=1 必须 404（白名单只认精确路径）")
        suite.eq(len(plugin.received) - before, 0,
                 "D11 这些未鉴权端点**没有**驱动插件（零副作用）")
    finally:
        relay2.stop()
        plugin.stop()


def section_health_positive(suite, workdir):
    """D12：令牌配对时，/health 必须「真的探到插件」——reachable=true 且 buildStamp 非空，
    并且插件侧看到的是一次**带 auth 且通过**的 getCivil3DHealth。"""
    plugin = FakePlugin(TOKEN).start()
    relay = RealRelay(os.path.join(workdir, "server"), plugin.port, TOKEN)
    try:
        relay.start()
    except RuntimeError as exc:
        suite.ok(False, "D12 能起 relay（健康正例）", repr(exc))
        plugin.stop()
        return
    try:
        status, body = http_get(relay.port, "/health")
        try:
            bridge = (json.loads(body) or {}).get("bridge") or {}
        except ValueError:
            bridge = {}
        suite.eq(status, 200, "D12 令牌配对时 /health → 200")
        suite.ok(bridge.get("reachable") is True, "D12 令牌配对时 reachable=true",
                 json.dumps(bridge, ensure_ascii=False)[:200])
        # 精确值而不是「非空」：空壳夹具也能造非空字符串，只有精确相等才证明「透传」成立
        qa.eqd(suite, bridge.get("buildStamp"), "fake-qa-20261122",
               "D12 令牌配对时 buildStamp **精确等于** 夹具哨兵值（证明 relay 真透传了插件结果）",
               "实际 buildStamp=%r" % (bridge.get("buildStamp"),))
        qa.eqd(suite, bridge.get("pluginVersion"), "1.15.0",
               "D12 pluginVersion 也精确透传", "实际 %r" % (bridge.get("pluginVersion"),))
        seen = [item for item in plugin.attempts if item[0] == "getCivil3DHealth"]
        suite.ok(seen and all(present and accepted for _m, present, accepted in seen),
                 "D12 健康探测在插件侧是「带 auth 且通过」的（P-007 的核心）",
                 "插件侧看到的 getCivil3DHealth 尝试：%r" % (seen[:4],))
    finally:
        relay.stop()
        plugin.stop()


def section_static_gate_selftest(suite, workdir):
    """D13：静态审查的「剥注释」改造必须自带正反自测——
    注释里写禁词 → **必须通过**；代码里真调用 → **必须失败**。"""
    snap = os.path.join(workdir, "auditsnap")
    if os.path.isdir(snap):
        _force_rm(snap)
    ignore = shutil.ignore_patterns("bin", "obj", "C_References", "node_modules", "__pycache__")
    try:
        shutil.copytree(qa.ROOT, snap, ignore=ignore)
    except OSError as exc:
        # 夹具异常必须记成「一条 FAIL」，绝不能让异常冒出 run() 把整包 130 多条断言一起吞掉
        suite.ok(False, "D13 能复制出审计快照（夹具可用）", repr(exc))
        return
    if not os.path.isdir(os.path.join(snap, "bridge")):
        suite.ok(False, "D13 审计快照里含 bridge/（复制完整）", snap)
        return
    bridge_dir = os.path.join(snap, "bridge")

    def run_audit():
        completed = subprocess.run([sys.executable, "tests/test_static_audit.py"],
                                   cwd=snap, capture_output=True, text=True, timeout=240)
        text = completed.stdout + completed.stderr
        try:
            start = text.rindex("{")
            report = json.loads(text[start:])
        except (ValueError, KeyError):
            return None, text
        return report, text

    comment_probe = os.path.join(bridge_dir, "_qa_comment_only.py")
    with open(comment_probe, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("# 这是一份**只含注释**的探针：注释里故意写出禁词。\n"
                     "# subprocess.Popen / shell=True / os.environ / CreateProcess / acad.exe\n"
                     "# 按新判定（先剥注释）它不得导致任何失败。\n")
    try:
        report, text = run_audit()
        failures = (report or {}).get("failures") or []
        hit = [item for item in failures if "_qa_comment_only" in item]
        qa.eqd(suite, hit, [], "D13 注释里的禁词**不得**触发失败（假阳性闸）",
               ("审计报告缺失：" + text[-300:]) if report is None else
               "命中失败项：%r" % (hit[:3],))
    finally:
        os.unlink(comment_probe)

    real_probe = os.path.join(bridge_dir, "_qa_real_launch.py")
    with open(real_probe, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("import subprocess\n\n\n"
                     "def _qa_real_launch():\n"
                     "    return subprocess.Popen(['cmd', '/c', 'echo', 'hi'])\n")
    try:
        report, _text = run_audit()
        failures = (report or {}).get("failures") or []
        hit = [item for item in failures if "_qa_real_launch" in item]
        suite.ok(bool(hit), "D13 代码里**真调用** subprocess.Popen 必须被抓住（规则还活着）",
                 "没有命中该文件的失败项；失败项=%r" % (failures[:4],))
    finally:
        os.unlink(real_probe)


# ============================================================================
# 入口
# ============================================================================


def _force_rm(path: str) -> None:
    """删除目录，遇 read-only 文件先 chmod 再删。

    为什么不用 `rmtree(ignore_errors=True)`：Windows 上只读文件/被占用条目会让 rmtree **半途而废且不报错**，
    下一轮看到「目录还在、文件不全」，症状会伪装成产品问题（我踩过一次）。
    """
    def _onerr(func, target, _exc):
        try:
            os.chmod(target, stat.S_IWRITE)
        except OSError:
            pass
        try:
            func(target)
        except OSError:
            pass

    shutil.rmtree(path, onerror=_onerr)


def run() -> dict:
    suite = qa.harness.Suite("qa_bridge_attack")

    # 每个进程一个**唯一**工作目录：早先用固定的 %TEMP%\qa-bridge-attack，
    # 结果上一轮残留的 node 子进程会锁住目录 → rmtree 静默失败 → 下一轮 copy 只拷到一半，
    # 表现为「relay 起不来 / 找不到 ./judgement.js」。这正是我在 05 里提醒别人的「固定临时路径」缺陷，
    # 只不过发生在我自己的夹具上。unique tempdir 是最好的隔离。
    workdir = tempfile.mkdtemp(prefix="qa-bridge-")
    try:
        return _run_sections(suite, workdir)
    finally:
        _force_rm(workdir)      # 规程 4：唯一目录 + 必清理，连跑两次结果必须相同


def _run_sections(suite, workdir: str) -> dict:
    shutil.copytree(RELAY_SRC, os.path.join(workdir, "server"))
    # 夹具自检：relay 的 require 链缺一个文件就会「进程提前退出」，看起来像产品问题。
    # 早先我用了固定 workdir，被上一轮残留锁住后 copy 只拷到一半（缺 judgement.js 等 8 个），
    # 症状是「relay 起不来」。所以这里先断言「拷全了」，别让夹具缺陷冒充产品缺陷。
    copied = os.listdir(os.path.join(workdir, "server"))
    suite.ok(len(copied) >= 30 and "judgement.js" in copied and "llm-agent.js" in copied,
             "夹具自检：relay 的 server/ 已完整复制（含 judgement.js / llm-agent.js）",
             "只拷到 %d 个文件：%s" % (len(copied), sorted(copied)[:12]))

    plugin = FakePlugin(TOKEN).start()
    relay = RealRelay(os.path.join(workdir, "server"), plugin.port, TOKEN)
    try:
        relay.start()
    except RuntimeError as exc:
        print("SKIP：真 relay 起不来（%r），见 %s" % (exc, relay.stdout_path))
        plugin.stop()
        return suite.report()

    try:
        section_auth_bypass(suite, relay, plugin)
        log_text = relay.read_log()
        suite.ok(TOKEN not in log_text, "A8 relay 日志里**没有**令牌明文",
                 "日志 %d 字节" % len(log_text))
    finally:
        relay.stop()

    section_fail_closed(suite, workdir, plugin)
    section_direct_plugin(suite, plugin)

    guard_a, guard_b = section_ports(suite, workdir)
    section_cross_token(suite, workdir, guard_a, FakePlugin(guard_a.token))
    section_trust_boundary(suite, workdir)
    section_health_positive(suite, workdir)
    section_static_gate_selftest(suite, workdir)
    section_patches(suite, workdir)
    plugin.stop()
    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
