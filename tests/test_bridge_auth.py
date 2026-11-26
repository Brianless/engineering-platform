#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""N03 桥层鉴权与端口隔离测试（无 CAD、无 Node、无网络出站）。

覆盖（对应 `docs/08` §4.2 的三条补丁与 `third_party/PATCHES.md` P-003~P-005）：

1. **P-003 入站鉴权**：relay 的 `POST /tcp` 在「无令牌 / 错令牌」时必须 401，且**方法不得被转发**
   （证据：插件替身的 `received` 保持为空）；
2. **fail-closed**：relay 自身没配令牌时整条桥返回 503，同样不转发；
3. **P-004 逐请求校验**：绕过 relay 直连插件端口、不带令牌 → `-32004`，且不进入业务层；
4. **正确令牌**：方法透传、响应原样回程；
5. **P-005 端口隔离**：两个实例端口互不相同，调用不串（各自的插件替身只收到自己那一份）；
6. **映射文件**：登记 `relayPort`/`pluginPort`/`runtime`，且**绝不包含令牌**；
7. **环境注入**：`environments()` 给出 5 个变量，供 relay 与插件子进程使用；
8. **不可达**：relay 未启动时 `call()` 抛 `BridgeUnreachable`（不是静默超时）。

替代物说明：`tests/mock_bridge.py` 复刻的是 relay/插件的**鉴权与转发语义**，不是全部能力；
它不动 `mockhost/`（566 条 QA 断言依赖那套）。
"""

from __future__ import annotations

import glob
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request

import harness

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
for extra in (HERE, os.path.join(ROOT, "bridge")):
    if extra not in sys.path:
        sys.path.insert(0, extra)

import pipeguard  # noqa: E402  (路径需先注入)
import relay_launcher  # noqa: E402
from mock_bridge import MockPlugin, MockRelay  # noqa: E402


def _post(port: int, body: dict, auth_header: str | None = None, timeout: float = 5.0) -> tuple[int, dict]:
    """裸发一次 `POST /tcp`，返回 (HTTP 状态码, 解析后的 JSON)。用于「不带令牌」这类反例。"""
    headers = {"Content-Type": "application/json"}
    if auth_header is not None:
        headers["Authorization"] = auth_header
    request = urllib.request.Request(
        "http://127.0.0.1:%d/tcp" % port,
        data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
        headers=headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace")
        try:
            return exc.code, json.loads(raw)
        except ValueError:
            return exc.code, {"raw": raw}


def run() -> dict:
    suite = harness.Suite("bridge_auth")
    sandbox = tempfile.mkdtemp(prefix="ep-bridge-")
    token = "a" * 48

    try:
        # ---------- 1. 入站鉴权：无令牌 / 错令牌 / 正确令牌 ----------
        def relay_inbound_auth():
            guard = pipeguard.BridgeGuard("acad-auth", token=token, root=sandbox)
            plugin = MockPlugin(0, token=token).start()
            relay = MockRelay(0, plugin.port, token=token).start()
            guard.relay_port, guard.plugin_port = relay.port, plugin.port
            try:
                body = {"jsonrpc": "2.0", "id": 1, "method": "read_overview", "params": {}}

                status, payload = _post(relay.port, body)
                suite.eq(status, 401, "P-003 无 Authorization → HTTP 401")
                suite.eq((payload.get("error") or {}).get("code"), -32004,
                         "P-003 无令牌返回 -32004（unauthorized）")
                suite.eq(plugin.received, [], "P-003 无令牌时**方法未转发**（插件 received 为空）")

                status, payload = _post(relay.port, body, "Bearer " + "b" * 48)
                suite.eq(status, 401, "P-003 错令牌 → HTTP 401")
                suite.eq(plugin.received, [], "P-003 错令牌时**方法未转发**")

                status, payload = _post(relay.port, body, "Bearer " + token)
                suite.eq(status, 200, "P-003 正确令牌 → HTTP 200")
                suite.eq(plugin.received, ["read_overview"], "P-003 正确令牌 → 方法被转发一次")
                suite.eq((payload.get("result") or {}).get("method"), "read_overview",
                         "P-003 响应原样回程（result.method 回显）")

                suite.eq(relay.forwarded, ["read_overview"], "relay 只成功转发过 1 次")
                suite.eq(relay.rejected, 2, "relay 拦下 2 次（无令牌 + 错令牌）")
            finally:
                relay.stop()
                plugin.stop()

        # ---------- 2. relay 未配置令牌 → 503（fail-closed） ----------
        def relay_without_token():
            plugin = MockPlugin(0, token=token).start()
            relay = MockRelay(0, plugin.port, token="").start()   # ← 服务端没有令牌
            try:
                body = {"jsonrpc": "2.0", "id": 2, "method": "read_layers", "params": {}}
                status, payload = _post(relay.port, body, "Bearer " + token)
                suite.eq(status, 503, "P-003 relay 未配令牌 → 503（不回落到无鉴权）")
                suite.eq((payload.get("error") or {}).get("code"), -32003,
                         "P-003 503 的 error.code = -32003（token_not_configured）")
                suite.eq(plugin.received, [], "P-003 relay 无令牌时**方法未转发**")
                suite.eq(relay.forwarded, [], "relay 一次都没有转发")
            finally:
                relay.stop()
                plugin.stop()

        # ---------- 3. 直连插件绕过 relay：令牌校验必须独立成立 ----------
        def plugin_requires_token():
            plugin = MockPlugin(0, token=token).start()
            try:
                import socket
                body = {"jsonrpc": "2.0", "id": 3, "method": "read_query", "params": {}}
                with socket.create_connection(("127.0.0.1", plugin.port), timeout=5.0) as conn:
                    conn.sendall(json.dumps(body).encode("utf-8"))
                    response = json.loads(conn.recv(8192).decode("utf-8"))
                suite.eq((response.get("error") or {}).get("code"), -32004,
                         "P-004 直连插件、不带 auth → -32004")
                suite.eq(plugin.received, [], "P-004 直连且无令牌时方法未进入业务层")

                # 带上 auth 才放行（证明校验的是字段本身，不是「凡是直连就拒」）
                body["auth"] = token
                with socket.create_connection(("127.0.0.1", plugin.port), timeout=5.0) as conn:
                    conn.sendall(json.dumps(body).encode("utf-8"))
                    response = json.loads(conn.recv(8192).decode("utf-8"))
                suite.eq((response.get("result") or {}).get("method"), "read_query",
                         "P-004 直连带正确 auth → 放行")
                suite.eq(plugin.received, ["read_query"], "P-004 放行后才记录到业务调用")

                # 插件未配置令牌：一律 -32003
                bare = MockPlugin(0, token="").start()
                try:
                    with socket.create_connection(("127.0.0.1", bare.port), timeout=5.0) as conn:
                        conn.sendall(json.dumps({"jsonrpc": "2.0", "id": 4, "method": "x",
                                                 "params": {}, "auth": token}).encode("utf-8"))
                        response = json.loads(conn.recv(8192).decode("utf-8"))
                    suite.eq((response.get("error") or {}).get("code"), -32003,
                             "P-004 插件未配令牌 → -32003（fail-closed）")
                    suite.eq(bare.received, [], "P-004 插件无令牌时方法未进入业务层")
                finally:
                    bare.stop()
            finally:
                plugin.stop()

        # ---------- 4. 端口隔离：两个实例不串 ----------
        def port_isolation():
            guard_a = pipeguard.BridgeGuard("acad-aaa", token="t" * 48, root=sandbox)
            guard_b = pipeguard.BridgeGuard("acad-bbb", token="u" * 48, root=sandbox)
            guard_a.allocate()
            guard_b.allocate()
            suite.ok(guard_a.relay_port != guard_b.relay_port, "P-005 两实例 relayPort 不同")
            suite.ok(guard_a.plugin_port != guard_b.plugin_port, "P-005 两实例 pluginPort 不同")
            suite.ok(guard_a.relay_port != guard_b.plugin_port, "P-005 交叉也不相等（无别名）")

            plugin_a = MockPlugin(guard_a.plugin_port, token=guard_a.token).start()
            plugin_b = MockPlugin(guard_b.plugin_port, token=guard_b.token).start()
            relay_a = MockRelay(guard_a.relay_port, plugin_a.port, token=guard_a.token).start()
            relay_b = MockRelay(guard_b.relay_port, plugin_b.port, token=guard_b.token).start()
            try:
                result_a = guard_a.call("read_overview", {"who": "a"})
                result_b = guard_b.call("read_layers", {"who": "b"})
                suite.eq(plugin_a.received, ["read_overview"], "实例 A 只收到自己的方法")
                suite.eq(plugin_b.received, ["read_layers"], "实例 B 只收到自己的方法")
                suite.eq(result_a["result"]["params"].get("who"), "a", "A 的响应参数不被串改")
                suite.eq(result_b["result"]["params"].get("who"), "b", "B 的响应参数不被串改")

                # 用 A 的令牌打 B 的 relay 必须被拒（令牌不通用）
                try:
                    guard_b.token, saved = guard_a.token, guard_b.token
                    status = None
                    try:
                        guard_b.call("read_query", {})
                        status = 200
                    except pipeguard.BridgeAuthError:
                        status = 401
                    suite.eq(status, 401, "跨实例令牌无效（A 的令牌打 B 的 relay → E4001）")
                finally:
                    guard_b.token = saved
            finally:
                relay_b.stop()
                relay_a.stop()
                plugin_b.stop()
                plugin_a.stop()

        # ---------- 5. 映射文件：有端口、无令牌 ----------
        def mapping_file():
            guard = pipeguard.BridgeGuard("acad-map", token=token, root=sandbox)
            guard.allocate()
            path = guard.register(pid=4321)
            try:
                record = json.load(open(path, "r", encoding="utf-8"))
                suite.eq(record.get("relayPort"), guard.relay_port, "映射文件记录 relayPort")
                suite.eq(record.get("pluginPort"), guard.plugin_port, "映射文件记录 pluginPort")
                suite.eq(record.get("runtime"), "net8", "映射文件记录 runtime=net8")
                suite.eq(record.get("pid"), 4321, "映射文件记录 pid")
                raw = open(path, "r", encoding="utf-8").read()
                suite.is_false(token in raw, "映射文件**不含令牌**（令牌只在内存/环境变量里）")

                listed = pipeguard.BridgeGuard.list_records(root=sandbox)
                ids = [item.get("hostInstanceId") for item in listed]
                suite.contains(ids, "acad-map", "list_records 能列出该实例")
            finally:
                guard.unregister()
            suite.is_false(os.path.exists(path), "unregister 后映射文件被删除")
            suite.eq(guard.read_record(), None, "read_record 读不到时返回 None（不抛）")

        # ---------- 6. 环境注入 ----------
        def environments():
            guard = pipeguard.BridgeGuard("acad-env", token=token, root=sandbox)
            guard.allocate()
            env = guard.environments()
            for key in ("EP_RELAY_PORT", "EP_PLUGIN_PORT", "CIVIL3D_BRIDGE_PORT",
                        "CIVIL3D_RELAY_PORT", "EP_BRIDGE_TOKEN"):
                suite.contains(env, key, "environments 注入 %s" % key)
            suite.eq(env["EP_RELAY_PORT"], str(guard.relay_port), "EP_RELAY_PORT = relayPort")
            suite.eq(env["EP_PLUGIN_PORT"], str(guard.plugin_port), "EP_PLUGIN_PORT = pluginPort")
            suite.eq(env["CIVIL3D_BRIDGE_PORT"], str(guard.plugin_port),
                     "CIVIL3D_BRIDGE_PORT 与插件侧变量名对齐")
            suite.eq(env["EP_BRIDGE_TOKEN"], token, "EP_BRIDGE_TOKEN = 该实例令牌")

        # ---------- 7. 不可达 ----------
        def unreachable():
            guard = pipeguard.BridgeGuard("acad-dead", token=token, root=sandbox)
            guard.relay_port = 1        # 保留端口，必然连不上
            guard.plugin_port = 1
            try:
                guard.call("read_overview", {})
                suite.fail("E4001 不可达时应抛 BridgeUnreachable")
            except pipeguard.BridgeUnreachable as exc:
                suite.eq(exc.code, "C1003",
                         "relay 不可达 → code=C1003（N04-A 修正：C1001 是「没找到实例」，不可混用）")
                suite.eq(exc.detail, "bridge_unreachable", "detail=bridge_unreachable")

        # ---------- 9. `/tcp` 精确匹配是安全不变量（N04-A / QA-B） ----------
        def path_variants_not_forwarded():
            """`req.url === '/tcp'` 的**严格相等**本身是安全属性，不是实现细节。

            若有人为「兼容 query」把它改成 `startsWith('/tcp')`，路径混淆面立刻打开
            （`/tcp?x=1`、`/TCP`、`/tcp/extra` 都会被当成同一个入口）。这里锁死：
            这些变体既不转发给插件，也不会返回 JSON-RPC 结果（只会命中兜底的 `relay` 文本）。
            """
            plugin = MockPlugin(0, token=token).start()
            relay = MockRelay(0, plugin.port, token=token).start()
            try:
                body = {"jsonrpc": "2.0", "id": 9, "method": "read_overview", "params": {}}
                base = "http://127.0.0.1:%d" % relay.port
                # 注意：`#frag` 是客户端片段标识符，根本不会出现在请求行里（会被客户端剥掉），
                # 所以不把它当变体 —— 否则测的是 HTTP 客户端而不是 relay。
                for suffix in ("/tcp?x=1", "/TCP", "/tcp/", "/tcp/extra"):
                    request = urllib.request.Request(
                        base + suffix,
                        data=json.dumps(body).encode("utf-8"),
                        headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"},
                        method="POST")
                    try:
                        with urllib.request.urlopen(request, timeout=5) as response:
                            raw = response.read().decode("utf-8", "replace")
                    except urllib.error.HTTPError as exc:
                        raw = exc.read().decode("utf-8", "replace")
                    suite.is_false('"result"' in raw,
                                   "路径变体 %r 不得返回 JSON-RPC 结果（防 path confusion）" % suffix)
                # `GET /tcp` 也不该被当成桥（只有 POST 才是）
                try:
                    with urllib.request.urlopen(base + "/tcp", timeout=5) as response:
                        get_body = response.read().decode("utf-8", "replace")
                except urllib.error.HTTPError as exc:
                    get_body = exc.read().decode("utf-8", "replace")
                suite.is_false('"result"' in get_body, "GET /tcp 不得返回 JSON-RPC 结果")
                suite.eq(plugin.received, [],
                         "5 个路径变体 + GET 之后，插件一个方法都没收到")
            finally:
                relay.stop()
                plugin.stop()

        # ---------- 10. 真 relay 冒烟（有 node 才跑，没有就 skip） ----------
        def real_relay_smoke():
            """用**真** `panel-relay.js` 走一遍：白名单出口启动 → 401/200 语义 → 关停。

            N03 的 55 条打的是 Python 复刻；复刻品证明「你想实现的语义」，证明不了「真 relay 真那样实现」。
            这条用例把两者接上：`relay_launcher.start_relay()` 起真进程，前面挂 mock 插件（Python）。
            环境里没有 node 时 **skip 而不是 fail** —— 缺工具不该变成红。

            **vendored 树零残留**：relay 会往自己的 `__dirname` 写 `relay.log` / `.relay-runtime.json`。
            清理放在 `finally`（用例中途断言失败也照样清），并在清完之后**断言零残留** ——
            把「污染 vendored 树」从「靠自觉」变成「会红的缺陷」。
            """
            try:
                node_exe = relay_launcher.resolve_node_path()
            except relay_launcher.RelayLauncherError:
                suite.ok(True, "环境无 node → 真 relay 冒烟 SKIP（不算失败）")
                return
            guard = pipeguard.BridgeGuard("acad-real", token=token, root=sandbox)
            guard.allocate()
            plugin = MockPlugin(guard.plugin_port, token=token).start()
            proc = None
            try:
                proc = relay_launcher.start_relay(guard)
                suite.ok(True, "relay_launcher 用白名单出口启动了真 relay（pid=%s）" % getattr(proc, "pid", "?"))
                status, payload = _post(guard.relay_port, {"jsonrpc": "2.0", "id": 1,
                                                           "method": "read_overview", "params": {}})
                suite.eq(status, 401, "真 relay：无令牌 → 401")
                suite.eq((payload.get("error") or {}).get("code"), -32004, "真 relay：无令牌 → -32004")
                status, payload = _post(guard.relay_port, {"jsonrpc": "2.0", "id": 2,
                                                           "method": "read_overview", "params": {}},
                                        "Bearer " + token)
                suite.eq(status, 200, "真 relay：正确令牌 → 200")
                suite.eq((payload.get("result") or {}).get("method"), "read_overview",
                         "真 relay：透传到 mock 插件并回程")
                suite.eq(plugin.received, ["read_overview"], "真 relay：只转发了一次（401 那次没转发）")
            finally:
                if proc is not None:
                    try:
                        proc.terminate()
                        proc.wait(timeout=5)
                    except Exception:
                        try:
                            proc.kill()
                        except Exception:
                            pass
                plugin.stop()
                # relay 自带两个「写在 __dirname 里」的运行时文件（`relay.log` 与 `.relay-runtime.json`），
                # 起一次真进程就会出现。它们不是我方代码产物，但留在 vendored 树里会干扰逐字节审计。
                #
                # ★ 清理**必须**在 finally 里：本用例任何一条断言先红，也不能把 vendored 树留脏。
                # ★ 清理完**必须**断言零残留：靠自觉迟早会漏，把「污染 vendored 树」变成会红的缺陷。
                server_dir = os.path.join(ROOT, "third_party", "new-acad", "server")
                for pattern in ("relay*.log", ".relay-runtime.json"):
                    for stray in glob.glob(os.path.join(server_dir, pattern)):
                        try:
                            os.unlink(stray)
                        except OSError:
                            pass          # 删不掉留给下面的零残留断言去红，不在这里吞掉
                # 零残留断言（等价于 team-lead 要求的
                #   find third_party/new-acad/server -maxdepth 1 \
                #       \( -name "relay*.log" -o -name ".relay-runtime.json" \)
                # 必须为空）
                residue = []
                for pattern in ("relay*.log", ".relay-runtime.json"):
                    residue.extend(os.path.basename(p)
                                   for p in glob.glob(os.path.join(server_dir, pattern)))
                suite.eq(sorted(residue), [],
                         "真 relay 用例结束后 vendored 树零残留（relay*.log / .relay-runtime.json）")

        # ---------- 11. 判活：fail-closed 三判据（N04-A / Q3） ----------
        def health_probe_semantics():
            guard = pipeguard.BridgeGuard("acad-health", token=token, root=sandbox)
            guard.relay_port = 1
            result = guard.probe_health(timeout=1.0)
            suite.eq(result["state"], "unreachable", "判活：端口连不上 → state=unreachable")
            suite.is_false(result["healthy"], "判活：不可达时 healthy=False")

            class _FakeHealth(MockRelay):
                """把 /health 的响应换成固定 JSON，用来验证「reachable 但版本全空」被判定为 degraded。"""

                def __init__(self, port, payload):
                    super().__init__(port, 0, token=token)
                    self._payload = payload

                def start(self):
                    import json as _json
                    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
                    payload = self._payload

                    class Handler(BaseHTTPRequestHandler):
                        protocol_version = "HTTP/1.1"

                        def log_message(self, *_a):
                            pass

                        def do_GET(self):  # noqa: N802
                            data = _json.dumps(payload).encode()
                            self.send_response(200)
                            self.send_header("Content-Type", "application/json")
                            self.send_header("Content-Length", str(len(data)))
                            self.end_headers()
                            self.wfile.write(data)

                    self._httpd = ThreadingHTTPServer(("127.0.0.1", self.port), Handler)
                    self.port = self._httpd.server_address[1]
                    import threading
                    self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)
                    self._thread.start()
                    return self

            degraded = _FakeHealth(0, {"bridge": {"reachable": True, "buildStamp": "",
                                                  "pluginVersion": "", "hostProduct": "",
                                                  "civilAvailable": False}}).start()
            try:
                probe = pipeguard.BridgeGuard("acad-h2", token=token, root=sandbox)
                probe.relay_port = degraded.port
                result = probe.probe_health()
                suite.eq(result["state"], "degraded_auth",
                         "判活：reachable 但版本全空 → degraded_auth（不谎报 healthy）")
                suite.eq(result["detail"], "relay_internal_auth_missing",
                         "判活：detail 直指 relay 内部调用缺令牌（P-007 的显性化）")
                suite.is_false(result["healthy"], "判活：degraded 时 healthy=False")
            finally:
                degraded.stop()

            healthy = _FakeHealth(0, {"bridge": {"reachable": True, "buildStamp": "nogit-1",
                                                 "pluginVersion": "1.15.0"}}).start()
            try:
                probe = pipeguard.BridgeGuard("acad-h3", token=token, root=sandbox)
                probe.relay_port = healthy.port
                result = probe.probe_health()
                suite.eq(result["state"], "ok", "判活：有身份 → state=ok")
                suite.is_true(result["healthy"], "判活：有身份时 healthy=True")
            finally:
                healthy.stop()

        # ---------- 13. 唯一出口：白名单与环境隔离（N04-A） ----------
        def launcher_whitelist_and_env():
            """白名单必须真的拦得住，且子进程环境只能是我们允许的那几个键。

            用 `_popen` 注入假实现：既能断言「没启动真进程」，也能拿到将要传给子进程的 kwargs。
            """
            bridge_env = pipeguard.BridgeGuard("acad-launch", token=token, root=sandbox)
            bridge_env.allocate()
            env = bridge_env.environments()

            calls = []

            class FakeProc:
                pid = 4242

                def poll(self):
                    return None

            def fake_popen(argv, **kwargs):
                calls.append((argv, kwargs))
                return FakeProc()

            # ① 非白名单可执行文件：python.exe / cmd.exe 必须被拒，且**不创建进程**
            for bad in (sys.executable, r"C:\Windows\System32\cmd.exe"):
                try:
                    relay_launcher.spawn_relay(env, config={"bridge": {"nodePath": bad}}, _popen=fake_popen)
                    suite.fail("非白名单可执行文件 %r 竟然被放行" % os.path.basename(bad))
                except relay_launcher.RelayLauncherError as exc:
                    suite.eq(exc.code, "C1003", "非白名单 → C1003（%s）" % os.path.basename(bad))
            suite.eq(calls, [], "非白名单与空路径都没有真正创建进程（calls 为空）")

            # ② 空 nodePath：**按设计**回退到 PATH 里的 node（否则默认配置就起不来），
            #    但仍要过同一套 basename 白名单；PATH 里也没有 node 时 → C1003。
            before = len(calls)
            try:
                node_from_path = relay_launcher.resolve_node_path({"bridge": {"nodePath": ""}})
                suite.is_true(bool(relay_launcher.NODE_BASENAME_RE.match(os.path.basename(node_from_path))),
                              "空 nodePath 回退到 PATH，且回退结果仍通过 node 白名单")
            except relay_launcher.RelayLauncherError as exc:
                suite.eq(exc.code, "C1003", "空 nodePath 且 PATH 无 node → C1003")
            # 模拟「PATH 里也没有 node」
            saved_which = relay_launcher.shutil.which
            relay_launcher.shutil.which = lambda _name: None
            try:
                try:
                    relay_launcher.spawn_relay(env, config={"bridge": {"nodePath": ""}}, _popen=fake_popen)
                    suite.fail("PATH 无 node 时竟然被放行")
                except relay_launcher.RelayLauncherError as exc:
                    suite.eq(exc.code, "C1003", "PATH 无 node → C1003")
                    suite.eq(exc.detail, "relay_launcher_unavailable",
                             "detail=relay_launcher_unavailable（可定位到启动器而非实例）")
            finally:
                relay_launcher.shutil.which = saved_which
            suite.eq(len(calls), before, "空路径失败路径同样没有创建进程")

            # ③ 合法 node（用真实 node 路径，但 popen 换成假的）→ 检查参数模板与环境
            try:
                node_exe = relay_launcher.resolve_node_path()
            except relay_launcher.RelayLauncherError:
                suite.ok(True, "环境无 node → 参数/环境断言 SKIP（不算失败）")
                return
            proc = relay_launcher.spawn_relay(env, config={"bridge": {"nodePath": node_exe}}, _popen=fake_popen)
            suite.eq(proc.pid, 4242, "spawn 返回注入的假进程句柄")
            argv, kwargs = calls[-1]
            suite.eq(len(argv), 2, "参数模板恰好 2 个元素 [node, panel-relay.js]")
            suite.eq(argv[0], node_exe, "argv[0] 就是白名单里的 node")
            suite.is_true(argv[1].endswith(os.path.join("server", "panel-relay.js")),
                          "argv[1] 是 relay 脚本（由 __file__ 推导，不接受外部参数）")
            child_env = kwargs["env"]
            for key in ("EP_RELAY_PORT", "EP_PLUGIN_PORT", "CIVIL3D_BRIDGE_PORT",
                        "CIVIL3D_RELAY_PORT", "EP_BRIDGE_TOKEN", "PATH"):
                suite.contains(child_env, key, "子进程环境含 %s" % key)
            # 环境隔离：父进程的其它变量一律不得顺流
            leaked = [k for k in ("USERPROFILE", "APPDATA", "TEMP", "NUMBER_OF_PROCESSORS")
                      if k in child_env]
            suite.eq(leaked, [], "子进程环境不含父进程的无关变量（不透传整份环境）")
            suite.is_true("SYSTEMROOT" in child_env,
                          "子进程环境含 SYSTEMROOT（实测 Node 缺它会 abort）")
            # 输出重定向：不是 None（继承）也不是 PIPE
            suite.is_true(kwargs["stdout"] is not None, "stdout 不是继承（None）")
            suite.is_true(kwargs["stdout"] is not subprocess.PIPE, "stdout 不是 PIPE（补满会卡死 relay）")
            suite.is_true(hasattr(kwargs["stdout"], "name")
                          and "relay-" in str(kwargs["stdout"].name),
                          "stdout 指向 logs/relay-<port>.log")
            suite.eq(kwargs["stdin"], subprocess.DEVNULL, "stdin=DEVNULL（relay 不该读 stdin）")
            suite.is_true(kwargs["close_fds"] is True, "close_fds=True")
            suite.is_true(kwargs["shell"] is False, "不开 shell 解释")
            suite.is_true(kwargs.get("creationflags", 0) != 0, "Windows 上带 CREATE_NO_WINDOW（不弹黑框）")
            try:
                log_path = getattr(kwargs["stdout"], "name", None)
                kwargs["stdout"].close()
                if log_path and os.path.isfile(log_path):
                    os.unlink(log_path)
            except Exception:
                pass
        def no_launcher_traces():
            """`bridge/` 里除唯一出口外，任何模块都不许有创建进程的能力（N04-A 扩容）。

            N03 时这条只扫 `pipeguard.py`；加入 `relay_launcher.py` 后必须扩成「扫全部，除了出口本身」，
            否则新文件天然绕过它。宿主相关字样则连出口也不许有。
            """
            forbidden = ("acad.exe", "Start-Process", "os.startfile", "ShellExecute")
            spawn = ("subprocess", "os.system", "Popen(", "CreateProcess", "os.spawn", "shell=True")
            launcher = os.path.join(ROOT, "bridge", "relay_launcher.py")
            scanned = 0
            for path in sorted(glob.glob(os.path.join(ROOT, "bridge", "*.py"))):
                text = open(path, "r", encoding="utf-8").read()
                name = os.path.basename(path)
                for marker in forbidden:
                    suite.is_false(marker in text, "bridge/%s 不含宿主启动痕迹 %r" % (name, marker))
                if os.path.abspath(path) == os.path.abspath(launcher):
                    continue
                scanned += 1
                for marker in spawn:
                    suite.is_false(marker in text,
                                   "bridge/%s 不是唯一出口，不得出现 %r" % (name, marker))
            suite.is_true(scanned >= 1, "白名单外至少扫到 1 个 bridge 模块（防扫描面为空假绿）")
            suite.is_true(os.path.isfile(launcher), "唯一进程出口 bridge/relay_launcher.py 存在")
            suite.is_true(bool(re.search(r"urllib\.request", open(os.path.join(ROOT, "bridge", "pipeguard.py"),
                                                                  encoding="utf-8").read())),
                          "bridge/pipeguard.py 用标准库 http 客户端（无第三方依赖）")

        for label, func in (
            ("入站鉴权 P-003", relay_inbound_auth),
            ("fail-closed 503", relay_without_token),
            ("逐请求校验 P-004", plugin_requires_token),
            ("端口隔离 P-005", port_isolation),
            ("映射文件", mapping_file),
            ("环境注入", environments),
            ("不可达", unreachable),
            ("路径变体不转发", path_variants_not_forwarded),
            ("真 relay 冒烟", real_relay_smoke),
            ("唯一出口白名单", launcher_whitelist_and_env),
            ("判活 fail-closed", health_probe_semantics),
            ("无启动痕迹", no_launcher_traces),
        ):
            suite.case(label, func)
    finally:
        shutil.rmtree(sandbox, ignore_errors=True)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
