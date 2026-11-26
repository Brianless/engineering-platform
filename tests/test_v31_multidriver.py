"""v3.1 Multi-Driver Gateway Verification Suite:
1. Offline ACadSharp driver (--backend offline):
   - Self-contained synthetic DXF: read_overview, read_layers, export_layer_geojson, batch_side_database (inspect + find_replace_text + saveChanges)
   - Real Civil 3D / AutoCAD DWG (if present in temp dir): read_overview + hard proxy-overwrite safety guardrail
2. Generic Windows COM driver (--backend com) against live CAD (AutoCAD/Civil 3D/ZWCAD/GstarCAD if running):
   - sys_list_instances (PID resolution + vendor + version), sys_ping, sys_capabilities, read_overview, read_layers
   - Self-guiding fallback error (E2001) when calling C# eval on COM driver
3. Auto mode (--backend auto) PID deduplication & plugin-first priority
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests"))

import harness  # noqa: E402


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


def _http_post(url: str, body: dict, headers: dict, timeout: float = 20.0) -> tuple[int, dict]:
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json", **headers},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            parsed = json.loads(raw)
        except Exception:
            parsed = {"raw": raw}
        return exc.code, parsed


def _write_sample_dxf(path: Path) -> None:
    """Create a minimal valid AC1024 ASCII DXF with ROAD & LABEL layers and LINE/CIRCLE/TEXT entities."""
    dxf = "\n".join([
        "0", "SECTION",
        "2", "HEADER",
        "9", "$ACADVER",
        "1", "AC1024",
        "9", "$INSUNITS",
        "70", "6",
        "0", "ENDSEC",
        "0", "SECTION",
        "2", "TABLES",
        "0", "TABLE",
        "2", "LAYER",
        "70", "3",
        "0", "LAYER", "2", "0", "70", "0", "62", "7", "6", "CONTINUOUS",
        "0", "LAYER", "2", "ROAD", "70", "0", "62", "1", "6", "CONTINUOUS",
        "0", "LAYER", "2", "LABEL", "70", "0", "62", "3", "6", "CONTINUOUS",
        "0", "ENDTAB",
        "0", "ENDSEC",
        "0", "SECTION",
        "2", "ENTITIES",
        "0", "LINE", "8", "ROAD",
        "10", "100.0", "20", "200.0", "30", "0.0",
        "11", "250.0", "21", "350.0", "31", "0.0",
        "0", "CIRCLE", "8", "ROAD",
        "10", "175.0", "20", "275.0", "30", "0.0",
        "40", "12.5",
        "0", "TEXT", "8", "LABEL",
        "10", "110.0", "20", "210.0", "30", "0.0",
        "40", "2.5",
        "1", "OLD_STATION_K0",
        "0", "ENDSEC",
        "0", "EOF", ""
    ])
    path.write_text(dxf, encoding="ascii")


def _start_gateway(gw_exe: Path, port: int, token: str, backend: str, instance_dir: str, db_path: str):
    proc = subprocess.Popen(
        [
            str(gw_exe),
            "--port", str(port),
            "--token", token,
            "--backend", backend,
            "--instance-dir", instance_dir,
            "--db", db_path,
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    base_url = f"http://127.0.0.1:{port}"
    deadline = time.time() + 10.0
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{base_url}/health", timeout=1) as r:
                if r.status == 200:
                    return proc, base_url
        except Exception:
            time.sleep(0.15)
    proc.kill()
    raise RuntimeError(f"ep-mcp.exe failed to start in --backend {backend}")


def _stop_gateway(proc: subprocess.Popen) -> None:
    proc.terminate()
    try:
        proc.wait(timeout=3)
    except subprocess.TimeoutExpired:
        proc.kill()


def _mcp_call(base_url: str, token: str, tool_name: str, arguments: dict, req_id: int = 1) -> dict:
    code, res = _http_post(
        f"{base_url}/mcp",
        {
            "jsonrpc": "2.0",
            "id": req_id,
            "method": "tools/call",
            "params": {"name": tool_name, "arguments": arguments},
        },
        {"Authorization": f"Bearer {token}"},
    )
    assert code == 200, f"HTTP {code} calling {tool_name}: {res}"
    return res["result"]


def test_offline_driver(gw_exe: Path) -> None:
    port = _free_port()
    token = "ep_v31_offline_secret_token_0123456789abcdef0123456789abcdef"
    with tempfile.TemporaryDirectory(prefix="ep-v31-off-") as tmp:
        tmp_path = Path(tmp)
        sample_dxf = tmp_path / "sample_road.dxf"
        _write_sample_dxf(sample_dxf)
        empty_inst_dir = str(tmp_path / "instances")
        os.makedirs(empty_inst_dir, exist_ok=True)
        db_path = str(tmp_path / "offline.db")

        proc, base_url = _start_gateway(gw_exe, port, token, "offline", empty_inst_dir, db_path)
        try:
            # 1a. sys_list_instances
            r = _mcp_call(base_url, token, "sys_list_instances", {}, 1)
            assert r["isError"] is False, f"sys_list_instances failed: {r}"
            sc = r["structuredContent"]
            assert any(inst["driver"] == "offline_acadsharp" for inst in sc["instances"])

            # 1b. read_overview on synthetic DXF
            r = _mcp_call(base_url, token, "read_overview", {"dwgPath": str(sample_dxf)}, 2)
            assert r["isError"] is False, f"offline read_overview failed: {r}"
            ov = r["structuredContent"]
            assert ov["backend"] == "offline_acadsharp"
            assert ov["counts"]["entities"] == 3, f"Expected 3 entities, got {ov['counts']}"
            assert ov["counts"]["proxyEntities"] == 0

            # 1c. read_layers on synthetic DXF
            r = _mcp_call(base_url, token, "read_layers", {"dwgPath": str(sample_dxf)}, 3)
            assert r["isError"] is False, f"offline read_layers failed: {r}"
            layer_names = {x["name"] for x in r["structuredContent"]["layers"]}
            assert "ROAD" in layer_names and "LABEL" in layer_names

            # 1d. export_layer_geojson on synthetic DXF
            geojson_out = tmp_path / "road_export.geojson"
            r = _mcp_call(
                base_url, token, "export_layer_geojson",
                {"dwgPath": str(sample_dxf), "layers": ["ROAD"], "outPath": str(geojson_out)},
                4,
            )
            assert r["isError"] is False, f"offline export_layer_geojson failed: {r}"
            assert geojson_out.exists(), "GeoJSON output file missing"
            gj = json.loads(geojson_out.read_text(encoding="utf-8"))
            assert gj["type"] == "FeatureCollection"
            assert len(gj["features"]) == 2, f"Expected 2 ROAD features (LINE+CIRCLE), got {len(gj['features'])}"

            # 1e. batch_side_database find_replace_text + saveChanges on pure DXF
            r = _mcp_call(
                base_url, token, "batch_side_database",
                {
                    "files": [str(sample_dxf)],
                    "action": "find_replace_text",
                    "findText": "OLD_STATION_K0",
                    "replaceText": "NEW_STATION_K1",
                    "saveChanges": True,
                },
                5,
            )
            assert r["isError"] is False, f"offline batch_side_database failed: {r}"
            item = r["structuredContent"]["results"][0]
            assert item["ok"] is True and item["replacedCount"] == 1 and item["saved"] is True, f"Unexpected batch item: {item}"
            assert "NEW_STATION_K1" in sample_dxf.read_text(encoding="utf-8", errors="ignore")

            print("[PASS] Offline ACadSharp driver (overview, layers, GeoJSON, batch find_replace_text)")

            # 1e-2. Offline ACadSharp write_entities (all 9 entity types -> fresh .dxf + .dwg + readback verify)
            for ext in (".dxf", ".dwg"):
                gen_file = tmp_path / f"generated_entities{ext}"
                r_we = _mcp_call(
                    base_url, token, "write_entities",
                    {
                        "dwgPath": str(gen_file),
                        "layer": "EP-GEOM",
                        "entities": [
                            {"type": "line", "start": [0, 0, 0], "end": [100, 0, 0], "color": 1},
                            {"type": "polyline", "points": [[0, 10], [50, 30], [100, 10]], "closed": False, "color": 2},
                            {"type": "rectangle", "p1": [10, 40], "p2": [60, 80], "layer": "EP-RECT", "color": 3},
                            {"type": "circle", "center": [50, 50], "radius": 20, "color": 4},
                            {"type": "arc", "center": [50, 50], "radius": 30, "startAngle_deg": 0, "endAngle_deg": 180, "color": 5},
                            {"type": "ellipse", "center": [120, 50], "majorRadius": 25, "minorRadius": 12, "rotation_deg": 15, "color": 6},
                            {"type": "text", "text": "K0+100", "position": [10, 90], "height": 3.5},
                            {"type": "mtext", "text": "Bridge Pier\\PNo.1", "position": [60, 90], "height": 3.0, "width": 40},
                            {"type": "point", "position": [0, 0, 12.5]},
                            {"type": "circle", "center": [0, 0], "radius": -5},  # intentional invalid item
                        ],
                    },
                    51 if ext == ".dxf" else 52,
                )
                assert r_we["isError"] is False, f"offline write_entities({ext}) failed: {r_we}"
                sc_we = r_we["structuredContent"]
                assert sc_we["requestedCount"] == 10 and sc_we["createdCount"] == 9 and sc_we["failedCount"] == 1, f"Unexpected receipt: {sc_we}"
                assert sc_we["saved"] is True and gen_file.exists() and gen_file.stat().st_size > 0
                assert len(sc_we["handles"]) == 9 and len(sc_we["failedItems"]) == 1
                r_rb = _mcp_call(base_url, token, "read_overview", {"dwgPath": str(gen_file)}, 53 if ext == ".dxf" else 54)
                assert r_rb["isError"] is False, f"readback {ext} failed: {r_rb}"
                assert r_rb["structuredContent"]["counts"]["entities"] == 9, f"Expected 9 entities in {ext}, got {r_rb['structuredContent']['counts']}"
            print("[PASS] Offline ACadSharp write_entities (all 9 entity types -> DXF & DWG + readback verified)")

            # 1f. Real Civil 3D DWG test (if available in temp dir) + proxy-overwrite guardrail
            real_c3d = Path(tempfile.gettempdir()) / "ep-base-write-test.dwg"
            if real_c3d.exists():
                copy_dwg = tmp_path / "c3d_copy.dwg"
                shutil.copy2(real_c3d, copy_dwg)
                r = _mcp_call(base_url, token, "read_overview", {"dwgPath": str(copy_dwg)}, 6)
                assert r["isError"] is False, f"Real DWG read_overview failed: {r}"
                real_ov = r["structuredContent"]
                cnt = real_ov["counts"]
                print(
                    f"[PASS] Offline ACadSharp parsed real DWG ({copy_dwg.stat().st_size} B): "
                    f"ver={real_ov['software']['version']}, entities={cnt['entities']}, "
                    f"proxies={cnt['proxyEntities']}, customClasses={cnt.get('customClasses', 0)}, "
                    f"unsupported={cnt.get('unsupportedNotifications', 0)}, "
                    f"safeOverwrite={real_ov['drawing'].get('safeToOverwriteInPlace')}"
                )
                assert real_ov["drawing"].get("safeToOverwriteInPlace") is False, (
                    f"Real Civil 3D DWG must be flagged unsafe to overwrite in-place! Got: {real_ov}"
                )
                r_guard = _mcp_call(
                    base_url, token, "batch_side_database",
                    {
                        "files": [str(copy_dwg)],
                        "action": "find_replace_text",
                        "findText": "A",
                        "replaceText": "B",
                        "saveChanges": True,
                    },
                    7,
                )
                guard_item = r_guard["structuredContent"]["results"][0]
                assert guard_item["ok"] is False and "proxy_overwrite_blocked" in guard_item["error"], (
                    f"Proxy overwrite guardrail did not trigger! Got: {guard_item}"
                )
                print(f"[PASS] Hard proxy-overwrite guardrail blocked saving Civil 3D DWG: {guard_item['error']}")
        finally:
            _stop_gateway(proc)


def test_com_and_auto_driver(gw_exe: Path) -> None:
    port = _free_port()
    token = "ep_v31_com_secret_token_0123456789abcdef0123456789abcdef"
    with tempfile.TemporaryDirectory(prefix="ep-v31-com-") as tmp:
        empty_inst_dir = os.path.join(tmp, "instances")
        os.makedirs(empty_inst_dir, exist_ok=True)
        db_path = os.path.join(tmp, "com.db")

        proc, base_url = _start_gateway(gw_exe, port, token, "com", empty_inst_dir, db_path)
        try:
            r = _mcp_call(base_url, token, "sys_list_instances", {}, 10)
            assert r["isError"] is False, f"COM sys_list_instances failed: {r}"
            sc = r["structuredContent"]
            com_insts = [i for i in sc["instances"] if i["driver"] == "com_generic"]
            if not com_insts:
                # Verify UAC-mismatch / no-COM diagnostic when calling a COM tool
                r_ping = _mcp_call(base_url, token, "sys_ping", {}, 11)
                assert r_ping["isError"] is True
                diag = r_ping["structuredContent"]
                assert diag["code"] in ("C1001", "C1002"), f"Unexpected COM diagnostic: {diag}"
                print(f"[PASS] COM diagnostic verified ({diag['code']}): {diag['detail']}")
            else:
                inst = com_insts[0]
                assert inst["pid"] > 0, f"COM driver failed to resolve real PID: {inst}"
                print(
                    f"[PASS] Live COM CAD discovered: app={inst['app']}, version={inst['hostVersion']}, "
                    f"pid={inst['pid']}, doc={inst.get('activeDocument')}"
                )

                # sys_ping
                r_ping = _mcp_call(base_url, token, "sys_ping", {}, 11)
                assert r_ping["isError"] is False and r_ping["structuredContent"]["backend"] == "com_generic"

                # sys_capabilities
                r_cap = _mcp_call(base_url, token, "sys_capabilities", {}, 12)
                assert r_cap["isError"] is False and r_cap["structuredContent"]["backend"] == "com_generic"

                # read_overview
                r_ov = _mcp_call(base_url, token, "read_overview", {}, 13)
                assert r_ov["isError"] is False, f"COM read_overview failed: {r_ov}"
                ov = r_ov["structuredContent"]
                print(f"[PASS] Live COM read_overview: {ov['summary']}")

                # read_layers
                r_ly = _mcp_call(base_url, token, "read_layers", {}, 14)
                assert r_ly["isError"] is False, f"COM read_layers failed: {r_ly}"
                ly_cnt = len(r_ly["structuredContent"].get("layers", []))
                print(f"[PASS] Live COM read_layers: {ly_cnt} layers")

                # Self-guiding fallback error on C# eval over COM
                r_cs = _mcp_call(base_url, token, "write_eval_csharp", {"code": "return 1;"}, 15)
                assert r_cs["isError"] is True, "write_eval_csharp should return self-guiding error on COM backend"
                err_text = r_cs["content"][0]["text"]
                assert "write_run_command" in err_text or "AutoLISP" in err_text, f"Missing fallback hint: {err_text}"
                print("[PASS] COM self-guiding fallback error on write_eval_csharp verified")
        finally:
            _stop_gateway(proc)

    # Auto mode: PID deduplication + plugin priority over COM
    port2 = _free_port()
    with harness.sandbox("v31-auto") as tmp:
        db_path = os.path.join(tmp, "auto.db")
        with harness.mock_server() as (server, host):
            instance_dir = os.path.dirname(server.discovery_path)
            proc, base_url = _start_gateway(gw_exe, port2, token, "auto", instance_dir, db_path)
            try:
                r = _mcp_call(base_url, token, "sys_list_instances", {}, 20)
                assert r["isError"] is False
                insts = r["structuredContent"]["instances"]
                pids = [i["pid"] for i in insts if i.get("pid", 0) > 0]
                assert len(pids) == len(set(pids)), f"Duplicate PIDs found in auto mode: {insts}"
                # Default call should route to plugin (MockHost)
                r_ov = _mcp_call(base_url, token, "read_overview", {}, 21)
                assert r_ov["isError"] is False
                # Explicit targetPid matching MockHost plugin PID must succeed
                plugin_inst = next(i for i in insts if i["driver"] == "plugin")
                r_pid_ok = _mcp_call(base_url, token, "read_overview", {"targetPid": plugin_inst["pid"]}, 22)
                assert r_pid_ok["isError"] is False, f"targetPid routing to plugin failed: {r_pid_ok}"
                # Explicit hostInstanceId="offline-acadsharp" must route to offline driver
                r_off_id = _mcp_call(base_url, token, "sys_ping", {"hostInstanceId": "offline-acadsharp"}, 23)
                assert r_off_id["isError"] is False and r_off_id["structuredContent"]["backend"] == "offline_acadsharp"
                # Non-existent targetPid on plugin-only mode must cleanly fail with C1001
                r_bad_pid = _mcp_call(base_url, token, "read_overview", {"_backend": "plugin", "targetPid": 999999}, 24)
                assert r_bad_pid["isError"] is True and r_bad_pid["structuredContent"]["code"] == "C1001"
                # CASS command catalog lookup (built-in + optional CUIX fallback)
                r_cass = _mcp_call(base_url, token, "ui_cass_commands", {"query": "方格网"}, 25)
                assert r_cass["isError"] is False
                cmds = [c["command"] for c in r_cass["structuredContent"]["commands"]]
                assert "fgwtf" in cmds, f"Expected fgwtf in CASS catalog, got {cmds}"
                print(f"[PASS] Auto mode PID deduplication ({len(insts)} unique endpoints), targetPid, hostInstanceId & CASS catalog ({len(cmds)} matches) verified")
            finally:
                _stop_gateway(proc)


def run() -> int:
    gw_exe = ROOT / "gateway" / "publish" / "ep-mcp.exe"
    assert gw_exe.exists(), f"Missing gateway binary: {gw_exe}"
    test_offline_driver(gw_exe)
    test_com_and_auto_driver(gw_exe)
    print("ALL v3.1 MULTI-DRIVER CHECKS PASSED!")
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
