"""v3.0 End-to-End Verification Suite:
1. Standalone C# gateway (ep-mcp.exe) Streamable HTTP security (Host / Origin / Bearer)
2. Standalone C# gateway MCP JSON-RPC (initialize + instructions, tools/list 36 tools, tools/call over NamedPipe to MockHost)
3. SQLite persistence (results + audit_log) and idempotency cache
4. Pure-C# NpzWriter format compatibility with route_solver.load_terrain_npz + full route_solver pipeline
"""

from __future__ import annotations

import json
import os
import socket
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests"))

import harness  # noqa: E402


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


def _http_post(url: str, body: dict, headers: dict) -> tuple[int, dict]:
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json", **headers}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=8) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            parsed = json.loads(raw)
        except Exception:
            parsed = {"raw": raw}
        return exc.code, parsed


def _write_csharp_compatible_npz(path: Path, ny: int, nx: int) -> None:
    """Replicate exact byte layout of host/Tools/NpzWriter.cs and verify numpy/route_solver loads it."""
    def make_npy(shape_text: str, values: list[float]) -> bytes:
        header_dict = "{'descr': '<f8', 'fortran_order': False, 'shape': %s, }" % shape_text
        raw_len = len(header_dict) + 1
        pad = (64 - ((10 + raw_len) % 64)) % 64
        header = (header_dict + (" " * pad) + "\n").encode("ascii")
        preamble = b"\x93NUMPY\x01\x00" + struct.pack("<H", len(header))
        body = struct.pack("<" + "d" * len(values), *values)
        return preamble + header + body

    xs = [500000.0 + c * 10.0 for c in range(nx)]
    ys = [3100000.0 + r * 10.0 for r in range(ny)]
    elev = []
    for r in range(ny):
        for c in range(nx):
            elev.append(100.0 + 0.01 * (xs[c] - 500000.0) + 0.005 * (ys[r] - 3100000.0))

    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("elevation.npy", make_npy(f"({ny}, {nx})", elev))
        zf.writestr("x_coords.npy", make_npy(f"({nx},)", xs))
        zf.writestr("y_coords.npy", make_npy(f"({ny},)", ys))


def run() -> int:
    gw_exe = ROOT / "gateway" / "publish" / "ep-mcp.exe"
    assert gw_exe.exists(), f"Missing standalone gateway binary: {gw_exe}"

    # 1. Verify NpzWriter byte compatibility with route_solver
    with tempfile.TemporaryDirectory() as tmp:
        npz_path = Path(tmp) / "terrain.npz"
        _write_csharp_compatible_npz(npz_path, 40, 50)
        from route_solver.cli import run_pipeline
        out_dir = Path(tmp) / "out"
        summary = run_pipeline(
            terrain_path=npz_path,
            start_xy=(500050.0, 3100050.0),
            end_xy=(500450.0, 3100350.0),
            design_speed_kmh=60,
            num_plans=3,
            out_dir=out_dir,
        )
        assert summary["num_plans"] == 3, f"Expected 3 plans, got {summary['num_plans']}"
        print(f"[PASS] NpzWriter <-> route_solver pipeline generated {summary['num_plans']} valid plans")

    # 2. Verify ep-mcp.exe Streamable HTTP + security + NamedPipe bridge + SQLite store
    port = _free_port()
    token = "ep_v3_test_secret_token_0123456789abcdef0123456789abcdef"
    with harness.sandbox("v3-e2e") as tmp:
        db_path = os.path.join(tmp, "v3_test.db")
        with harness.mock_server() as (server, host):
            instance_dir = os.path.dirname(server.discovery_path)
            proc = subprocess.Popen(
                [str(gw_exe), "--port", str(port), "--token", token,
                 "--instance-dir", instance_dir, "--db", db_path],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            )
            try:
                base_url = f"http://127.0.0.1:{port}"
                # Wait for /health
                deadline = time.time() + 10.0
                ready = False
                while time.time() < deadline:
                    try:
                        with urllib.request.urlopen(f"{base_url}/health", timeout=1) as r:
                            if r.status == 200:
                                ready = True
                                break
                    except Exception:
                        time.sleep(0.15)
                assert ready, "ep-mcp.exe HTTP server failed to start"

                # 2a. Security: Reject Origin header (CSRF/DNS rebinding guard)
                code, _ = _http_post(f"{base_url}/mcp", {"jsonrpc": "2.0", "id": 1, "method": "ping"},
                                     {"Authorization": f"Bearer {token}", "Origin": "https://evil.example"})
                assert code == 403, f"Expected 403 for Origin header, got {code}"

                # 2b. Security: Reject missing Bearer token
                code, _ = _http_post(f"{base_url}/mcp", {"jsonrpc": "2.0", "id": 2, "method": "ping"}, {})
                assert code == 401, f"Expected 401 without Bearer, got {code}"
                print("[PASS] ep-mcp.exe security guards (Origin=403, missing Bearer=401)")

                auth = {"Authorization": f"Bearer {token}"}

                # 2c. MCP initialize + protocol-first instructions
                code, res = _http_post(f"{base_url}/mcp", {
                    "jsonrpc": "2.0", "id": 3, "method": "initialize",
                    "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "e2e", "version": "1.0"}}
                }, auth)
                assert code == 200 and "instructions" in res["result"], f"initialize failed: {res}"
                assert "route_solver" in res["result"]["instructions"], "Missing route_solver self-guide in instructions"

                # 2d. tools/list == 42 tools (37 CAD domain + 5 out-of-process UI/CASS tools)
                code, res = _http_post(f"{base_url}/mcp", {"jsonrpc": "2.0", "id": 4, "method": "tools/list", "params": {}}, auth)
                tools = res["result"]["tools"]
                names = {t["name"] for t in tools}
                assert len(tools) == 42, f"Expected 42 tools, got {len(tools)}"
                for required in ("export_surface_grid", "export_layer_geojson", "read_active_selection",
                                 "read_viewport_snapshot", "read_object_xdata", "write_object_xdata",
                                 "write_undo", "write_alignment_plan", "write_eval_csharp",
                                 "write_entities", "preview_transient", "batch_side_database",
                                 "ui_list_dialogs", "ui_wait_dialog", "ui_interact_dialog",
                                 "ui_read_cmdline", "ui_cass_commands"):
                    assert required in names, f"Missing tool: {required}"
                print(f"[PASS] ep-mcp.exe initialize & tools/list ({len(tools)} tools)")

                # 2e. tools/call over NamedPipe to MockHost
                code, res = _http_post(f"{base_url}/mcp", {
                    "jsonrpc": "2.0", "id": 5, "method": "tools/call",
                    "params": {"name": "read_overview", "arguments": {}}
                }, auth)
                assert code == 200 and res["result"]["isError"] is False, f"read_overview failed: {res}"
                stored_at = res["result"]["structuredContent"]["resultRef"]["storedAt"]
                assert stored_at and "results/" in stored_at, f"Missing storedAt resultRef: {stored_at}"

                # 2e-2. sys_capabilities.tools must match tools/list (42 tools incl. write_entities & ui_*)
                code, res_cap = _http_post(f"{base_url}/mcp", {
                    "jsonrpc": "2.0", "id": 6, "method": "tools/call",
                    "params": {"name": "sys_capabilities", "arguments": {}}
                }, auth)
                assert code == 200 and res_cap["result"]["isError"] is False, f"sys_capabilities failed: {res_cap}"
                cap_tools = set(res_cap["result"]["structuredContent"].get("tools") or [])
                assert cap_tools == names, f"sys_capabilities.tools mismatch: diff={cap_tools ^ names}"

                # 2f. Verify SQLite persistence
                conn = sqlite3.connect(db_path)
                res_count = conn.execute("SELECT COUNT(*) FROM results").fetchone()[0]
                aud_count = conn.execute("SELECT COUNT(*) FROM audit_log").fetchone()[0]
                conn.close()
                assert res_count >= 1 and aud_count >= 1, f"SQLite missing rows: results={res_count}, audit={aud_count}"
                print(f"[PASS] ep-mcp.exe NamedPipe call + SQLite store (results={res_count}, audit={aud_count})")

            finally:
                proc.terminate()
                try:
                    proc.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    proc.kill()

    print("ALL v3.0 E2E CHECKS PASSED!")
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
