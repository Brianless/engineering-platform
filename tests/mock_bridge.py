#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""tests/mock_bridge.py —— 无 CAD 的 relay / 插件替身（仅供桥层测试）。

## ⚠️ 定位：**只验证语义，不验证 HTTP/Node 解析器边界**

本文件是 Python 复刻，它能证明「我们想实现的鉴权/转发语义」，**证明不了「真 `panel-relay.js` 也那样实现」**。
两者在**解析器层**实测会漂移（QA 用真 relay 复现过）：

| 边界 | 真 relay（Node） | 本复刻 |
|---|---|---|
| 前导/尾随空白 + **正确**令牌 | 200（HTTP 层按 RFC 7230 §3.2.4 裁 OWS） | 401（按全等字符串比） |
| 重复 `Authorization` 头 | 只取**第一个**（Node 对单值头的解析规则） | 401（按全等比合并值） |

这两个差异**都不是漏洞**（同样空白配**错误**令牌仍 401），但它们说明：**解析器边界必须由真 relay 来测**。
相关证据与打法见 `tests/qa/qa_bridge_attack.py`（真 `node panel-relay.js`）、
以及 `tests/test_bridge_auth.py::real_relay_smoke`（经白名单出口起真进程的冒烟）。

## 为什么还要它

真 relay 需要 node，跑得慢；本复刻零依赖、毫秒级，负责**快速回归**语义（160+ 条断言跑 7 秒）。
两者并存：复刻跑语义、真 relay 跑边界。

## 与 `mockhost/` 的分工

`mockhost/` 是**整台宿主**的替身（命名管道 + 协议 + 工具），566 条 QA 断言依赖它；
本文件**不动它、也不复用它的状态**，只模拟「relay ↔ 插件」这一跳。

语义严格对齐补丁后的实现：
* relay 未配置令牌 → `503`（fail-closed），**不转发**；
* `Authorization` 缺失/错误 → `401`，**不转发**；
* 转发时把令牌注入到 JSON 的 `auth` 字段（与 `panel-relay.js` 的 `Object.assign({}, reqBody, { auth })` 一致）；
* 插件逐请求校验 `auth`，失败回 `-32004`，**不调用业务处理**；
* 插件未配置令牌 → 回 `-32003`（与 `RpcTcpServer.TryAuthorize` 一致）。
"""

from __future__ import annotations

import json
import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class MockPlugin:
    """插件端替身：TCP，一条连接一条 JSON-RPC 请求，逐请求校验 `auth`。

    记录：
        `received`  —— **通过鉴权**、真正到达业务层的方法名列表（用来证明「被拒的方法没被转发」）
        `rejected`  —— 被鉴权挡下的次数
    """

    def __init__(self, port: int, token: str = "", host: str = "127.0.0.1"):
        self.port = port
        self.token = token
        self.host = host
        self.received: list[str] = []
        self.rejected = 0
        self._sock: socket.socket | None = None
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()

    # ---- 生命周期 ----
    def start(self) -> "MockPlugin":
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind((self.host, self.port))
        # port=0 时由系统分配，回写真实端口，方便测试用随机端口而不撞车
        self.port = self._sock.getsockname()[1]
        self._sock.listen(8)
        self._sock.settimeout(0.2)
        self._thread = threading.Thread(target=self._serve, name="mock-plugin", daemon=True)
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
        if self._thread is not None:
            self._thread.join(timeout=2.0)

    def __enter__(self) -> "MockPlugin":
        return self.start()

    def __exit__(self, *_exc) -> None:
        self.stop()

    # ---- 服务 ----
    def _serve(self) -> None:
        while not self._stop.is_set():
            try:
                conn, _addr = self._sock.accept()
            except (socket.timeout, OSError):
                continue
            with conn:
                conn.settimeout(5.0)
                try:
                    payload = self._read_one(conn)
                except (socket.timeout, OSError):
                    continue
                if payload is None:
                    continue
                conn.sendall(json.dumps(self.handle(payload), ensure_ascii=False).encode("utf-8"))

    @staticmethod
    def _read_one(conn: socket.socket) -> dict | None:
        buf = b""
        while True:
            chunk = conn.recv(8192)
            if not chunk:
                return None
            buf += chunk
            text = buf.decode("utf-8", "replace").strip()
            if not text:
                continue
            try:
                parsed = json.loads(text)
            except ValueError:
                continue
            return parsed if isinstance(parsed, dict) else None

    def handle(self, payload: dict) -> dict:
        """鉴权 + 业务（业务只回一个可辨认的结果，不模拟任何 CAD 行为）。"""
        request_id = payload.get("id")
        if not self.token:
            self.rejected += 1
            return _error(request_id, -32003, "bridge token not configured (EP_BRIDGE_TOKEN)")
        if str(payload.get("auth") or "") != self.token:
            self.rejected += 1
            return _error(request_id, -32004, "unauthorized: missing or invalid bridge token")
        method = str(payload.get("method") or "")
        self.received.append(method)
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {"mock": True, "method": method, "params": payload.get("params") or {},
                       "tokenSeen": True},
        }


class MockRelay:
    """relay 端替身：HTTP `POST /tcp`，Bearer 校验后把请求转发给插件并注入 `auth`。"""

    def __init__(self, port: int, plugin_port: int, token: str = "", host: str = "127.0.0.1"):
        self.port = port
        self.plugin_port = plugin_port
        self.token = token
        self.host = host
        self.forwarded: list[str] = []
        self.rejected = 0
        self._httpd: ThreadingHTTPServer | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> "MockRelay":
        relay = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *_args):  # 安静：测试输出不要被 access log 淹
                pass

            def do_POST(self):  # noqa: N802 (http.server 约定的方法名)
                if self.path != "/tcp":
                    self._reply(404, {"jsonrpc": "2.0", "error": {"code": -32700, "message": "not found"}})
                    return
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b"{}"
                try:
                    body = json.loads(raw.decode("utf-8"))
                except ValueError:
                    self._reply(400, {"jsonrpc": "2.0", "error": {"code": -32700, "message": "parse error"}})
                    return
                relay._handle_tcp(self, body)

            def _reply(self, status: int, payload: dict):
                data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        self._httpd = ThreadingHTTPServer((self.host, self.port), Handler)
        self.port = self._httpd.server_address[1]
        self._thread = threading.Thread(target=self._httpd.serve_forever, name="mock-relay", daemon=True)
        self._thread.start()
        return self

    def stop(self) -> None:
        if self._httpd is not None:
            self._httpd.shutdown()
            self._httpd.server_close()
        if self._thread is not None:
            self._thread.join(timeout=2.0)

    def __enter__(self) -> "MockRelay":
        return self.start()

    def __exit__(self, *_exc) -> None:
        self.stop()

    def _handle_tcp(self, handler, body: dict) -> None:
        """与补丁后的 `panel-relay.js` 同一套判定顺序：先看服务端有没有令牌，再看请求头。"""
        request_id = body.get("id")
        if not self.token:
            self.rejected += 1
            handler._reply(503, _error(request_id, -32003, "bridge token not configured (EP_BRIDGE_TOKEN)"))
            return
        provided = str(handler.headers.get("Authorization") or "")
        if provided != "Bearer %s" % self.token:
            self.rejected += 1
            handler._reply(401, _error(request_id, -32004, "unauthorized: missing or invalid bearer token"))
            return  # 关键：不建立到插件的连接
        self.forwarded.append(str(body.get("method") or ""))
        response = self._call_plugin(body)
        handler._reply(200, response)

    def _call_plugin(self, body: dict) -> dict:
        """把请求转发给插件，并注入 `auth`（对齐 `Object.assign({}, reqBody, { auth: BRIDGE_TOKEN })`）。"""
        payload = dict(body)
        payload["auth"] = self.token
        try:
            with socket.create_connection((self.host, self.plugin_port), timeout=5.0) as conn:
                conn.sendall(json.dumps(payload, ensure_ascii=False).encode("utf-8") + b"\n")
                conn.settimeout(5.0)
                buf = b""
                while True:
                    chunk = conn.recv(8192)
                    if not chunk:
                        break
                    buf += chunk
                    try:
                        return json.loads(buf.decode("utf-8", "replace"))
                    except ValueError:
                        continue
        except OSError as exc:
            return _error(body.get("id"), -32001, "TCP error: %s" % exc)
        return _error(body.get("id"), -32002, "TCP closed without a complete response")


def _error(request_id, code: int, message: str) -> dict:
    return {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}}
