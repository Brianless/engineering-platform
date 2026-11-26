"""HintCAD (纬地道路交通辅助设计系统 V8.x) 纯文本工程文件读写与错误诊断模块。

核心能力：
1. export_hintcad_project: 将 route_solver 的平纵选线方案一键导出为纬地 V8 标准纯文本工程包
   （.Prj / _JD_import.txt / .ZDM / .DMX / .sta / .ctr / .wid），自动完成 AutoCAD (X=东E, Y=北N)
   到纬地大地坐标 (X=北N, Y=东E) 的精确互换；
2. parse_hintcad_project: 读取既有纬地 .Prj 及其关联的 .ZDM / .DMX / .sta / .wid / .TF 文件；
3. diagnose_hintcad_errors: 解析纬地 Lst/ERR*.txt（GBK 编码）历史/实时报错日志，按错误类型归类并给出
   精确桩号清单与自动化修复建议（HDTM_EXHDM / LJSJ / CTREDIT / 挡墙桥梁）。
"""

from __future__ import annotations

import argparse
import json
import math
import re
import uuid
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple


_HIGHWAY_CLASS_BY_SPEED = {
    20: ("四级公路", 2, 6.5, "最大超高6%"),
    30: ("三级公路", 2, 7.5, "最大超高6%"),
    40: ("三级公路", 2, 8.5, "最大超高6%"),
    60: ("二级公路", 2, 10.0, "最大超高6%"),
    80: ("一级公路", 4, 21.5, "最大超高8%"),
    100: ("高速公路", 4, 26.0, "最大超高8%"),
}


def autocad_xy_to_hintcad_ne(x_east: float, y_north: float) -> Tuple[float, float]:
    """AutoCAD (X=东E, Y=北N) -> 纬地大地坐标 (X=北N, Y=东E)。"""
    return float(y_north), float(x_east)


def hintcad_ne_to_autocad_xy(x_north: float, y_east: float) -> Tuple[float, float]:
    """纬地大地坐标 (X=北N, Y=东E) -> AutoCAD (X=东E, Y=北N)。"""
    return float(y_east), float(x_north)


def _write_gbk(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.replace("\r\n", "\n").replace("\n", "\r\n").encode("gbk", errors="replace"))


def _read_gbk(path: Path) -> str:
    raw = path.read_bytes()
    for enc in ("gbk", "gb18030", "utf-8"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("gbk", errors="replace")


def export_hintcad_project(
    plan_payload: Dict[str, Any],
    out_dir: str | Path,
    project_name: str = "EP_Route",
    design_speed_kmh: Optional[int] = None,
    road_width_m: Optional[float] = None,
    start_station_m: float = 0.0,
    station_interval_m: float = 20.0,
    cut_slope: float = 0.75,
    fill_slope: float = 1.5,
) -> Dict[str, Any]:
    """将 route_solver 方案 JSON 导出为标准纬地 V8 纯文本工程目录。"""
    target = Path(out_dir)
    target.mkdir(parents=True, exist_ok=True)
    safe_name = re.sub(r'[\\/:*?"<>|\s]+', "_", (project_name or "EP_Route").strip()) or "EP_Route"

    align = plan_payload.get("alignment") or {}
    prof = plan_payload.get("profile") or {}
    sampled = plan_payload.get("sampled_centerline") or []
    metrics = plan_payload.get("metrics") or {}

    speed = int(design_speed_kmh or align.get("design_speed_kmh") or 60)
    hw_class, lanes, default_width, max_sup_label = _HIGHWAY_CLASS_BY_SPEED.get(
        speed, ("二级公路", 2, 10.0, "最大超高6%")
    )
    width = float(road_width_m if road_width_m is not None else default_width)

    # 1. 桩号范围与采样点
    total_len = float(
        metrics.get("total_length_m")
        or align.get("total_length_m")
        or (sampled[-1]["station_m"] if sampled else 0.0)
    )
    end_sta = start_station_m + total_len

    # 2. 写出 <name>_JD_import.txt（纬地 JDZB_IN 标准交点坐标导入文件：X=北N, Y=东E）
    raw_jds = align.get("jds") or []
    curves = align.get("curves") or []
    curve_by_idx: Dict[int, Dict[str, Any]] = {}
    for c in curves:
        if isinstance(c, dict) and "jd_index" in c:
            curve_by_idx[int(c["jd_index"])] = c

    jd_lines: List[str] = [
        "# 纬地 HintCAD V8 交点坐标导入文件 (JDZB_IN)",
        "# 列序：交点编号  X(北坐标N)  Y(东坐标E)  圆曲线半径R  前缓和曲线长Ls1  后缓和曲线长Ls2  虚交(0=正常)",
        f"# 起点桩号：{start_station_m:.3f}",
    ]
    jd_count = 0
    for idx, jd in enumerate(raw_jds):
        if isinstance(jd, dict):
            ex = float(jd.get("x", jd.get("e", 0.0)))
            ny = float(jd.get("y", jd.get("n", 0.0)))
        elif isinstance(jd, (list, tuple)) and len(jd) >= 2:
            ex, ny = float(jd[0]), float(jd[1])
        else:
            continue
        hn_x, he_y = autocad_xy_to_hintcad_ne(ex, ny)
        cinfo = curve_by_idx.get(idx) or {}
        radius = float(cinfo.get("radius_m", cinfo.get("radius", 0.0)) or 0.0)
        ls = float(cinfo.get("spiral_length_m", cinfo.get("ls", 0.0)) or 0.0)
        if idx == 0:
            label = "QD"
            radius, ls = 0.0, 0.0
        elif idx == len(raw_jds) - 1:
            label = "ZD"
            radius, ls = 0.0, 0.0
        else:
            label = f"JD{idx}"
        jd_lines.append(
            f"{label}\t{hn_x:.4f}\t{he_y:.4f}\t{radius:.3f}\t{ls:.3f}\t{ls:.3f}\t0"
        )
        jd_count += 1
    jd_import_path = target / f"{safe_name}_JD_import.txt"
    _write_gbk(jd_import_path, "\n".join(jd_lines) + "\n")

    # 3. 写出 <name>.ZDM（HINTCAD5.83_ZDM_SHUJU 纵断面变坡点文件）
    pvis = prof.get("pvis") or []
    zdm_lines: List[str] = ["HINTCAD5.83_ZDM_SHUJU", f"{len(pvis):10d}"]
    for i, pvi in enumerate(pvis):
        sta = start_station_m + float(pvi.get("station_m", pvi.get("station", 0.0)))
        elev = float(pvi.get("elevation_m", pvi.get("elevation", 0.0)))
        r_vert = 0.0 if (i == 0 or i == len(pvis) - 1) else float(pvi.get("vertical_radius_m", pvi.get("radius_m", 0.0)) or 0.0)
        if r_vert <= 0.0 and 0 < i < len(pvis) - 1:
            clen = float(pvi.get("curve_length_m", 0.0) or 0.0)
            g_in = float(pvi.get("grade_in_pct", 0.0) or 0.0)
            g_out = float(pvi.get("grade_out_pct", 0.0) or 0.0)
            omega = abs(g_out - g_in) / 100.0
            if clen > 0.0 and omega > 1e-5:
                r_vert = round(clen / omega, 2)
        zdm_lines.append(f"{sta:10.3f} {elev:12.8f} {r_vert:14.8f} {0.0:10.3f} {0.0:10.8f}")
    zdm_path = target / f"{safe_name}.ZDM"
    _write_gbk(zdm_path, "\n".join(zdm_lines) + "\n")

    # 4. 写出 <name>.DMX（HINTCAD5.83_DMX_SHUJU 纵断面地面线文件）
    dmx_lines: List[str] = ["HINTCAD5.83_DMX_SHUJU"]
    for row in sampled:
        sta = start_station_m + float(row.get("station_m", 0.0))
        gz = float(row.get("ground_z", 0.0))
        dmx_lines.append(f"{sta:10.3f}\t{gz:10.6f}")
    dmx_path = target / f"{safe_name}.DMX"
    _write_gbk(dmx_path, "\n".join(dmx_lines) + "\n")

    # 5. 写出 <name>.sta（HINTCAD5.84_STA_SHUJU 桩号序列文件）
    sta_lines: List[str] = ["HINTCAD5.84_STA_SHUJU"]
    stations_seen: List[float] = []
    if sampled:
        for row in sampled:
            stations_seen.append(round(start_station_m + float(row.get("station_m", 0.0)), 3))
    else:
        s = start_station_m
        while s < end_sta - 1e-3:
            stations_seen.append(round(s, 3))
            s += station_interval_m
        stations_seen.append(round(end_sta, 3))
    for seq, st in enumerate(sorted(set(stations_seen)), start=1):
        sta_lines.append(f"{st:10.3f}{seq:11d}")
    sta_path = target / f"{safe_name}.sta"
    _write_gbk(sta_path, "\n".join(sta_lines) + "\n")

    # 6. 写出 <name>.wid（HINTCAD6.00_WID_SHUJU 路幅宽度文件）
    half_w = width * 0.5
    earth_shoulder = 0.75 if width >= 8.5 else 0.50
    hard_shoulder = 0.75 if width >= 10.0 else 0.00
    lane_w = max(3.0, round(half_w - earth_shoulder - hard_shoulder, 2))
    wid_row_start = f"{start_station_m:10.3f} {0.0:10.2f} {lane_w:10.2f} {0.0:10.2f} {hard_shoulder:10.2f} {earth_shoulder:10.2f} {0:10d}"
    wid_row_end = f"{end_sta:10.3f} {0.0:10.2f} {lane_w:10.2f} {0.0:10.2f} {hard_shoulder:10.2f} {earth_shoulder:10.2f} {0:10d}"
    wid_lines = [
        "HINTCAD6.00_WID_SHUJU",
        "[LEFT]",
        wid_row_start,
        wid_row_end,
        "",
        "[RIGHT]",
        wid_row_start,
        wid_row_end,
    ]
    wid_path = target / f"{safe_name}.wid"
    _write_gbk(wid_path, "\n".join(wid_lines) + "\n")

    # 7. 写出 <name>.ctr（HINTCAD5.83_CTR_SHUJU 控制参数总文件）
    ctr_lines = [
        "HINTCAD5.83_CTR_SHUJU",
        " ZTFBP.DAT",
        f"{end_sta:10.3f}          1 {(-abs(fill_slope)):10.3f}      0.000      8.000          0",
        " YTFBP.DAT",
        f"{end_sta:10.3f}          1 {(-abs(fill_slope)):10.3f}      0.000      8.000          0",
        " ZWFBP.DAT",
        f"{end_sta:10.3f}          2      0.000      0.000      1.500          0 {abs(cut_slope):10.3f}      0.000      8.000          0",
        " YWFBP.DAT",
        f"{end_sta:10.3f}          2      0.000      0.000      1.500          0 {abs(cut_slope):10.3f}      0.000      8.000          0",
        " ZBGXS.DAT",
        f"{end_sta:10.3f}          3  -9999.000      0.600          0      0.000      0.600          0   9999.000      0.600          0",
        " YBGXS.DAT",
        f"{end_sta:10.3f}          3  -9999.000      0.600          0      0.000      0.600          0   9999.000      0.600          0",
        "ZPSGXS.DAT",
        f"{end_sta:10.3f}          3     -1.000      0.600          0      0.000      0.600          0      1.000      0.600          0",
        "YPSGXS.DAT",
        f"{end_sta:10.3f}          3     -1.000      0.600          0      0.000      0.600          0      1.000      0.600          0",
        "  TFFD.DAT",
        f"{end_sta:10.3f}      0.000     20.000     40.000      0.000     40.000      0.000",
        "  QHSJ.DAT",
        "  HDSJ.DAT",
        " ZLCSD.DAT",
        f"{end_sta:10.3f}      0.000      0.600      0.300      0.000",
        " YLCSD.DAT",
        f"{end_sta:10.3f}      0.000      0.600      0.300      0.000",
        " ZFJBK.DAT",
        " YFJBK.DAT",
    ]
    ctr_path = target / f"{safe_name}.ctr"
    _write_gbk(ctr_path, "\n".join(ctr_lines) + "\n")

    # 8. 写出 <name>.Prj（HINTCAD6.00_PRJ_SHUJU 纬地项目总控文件）
    prj_lines = [
        "HINTCAD6.00_PRJ_SHUJU",
        "[项目设置]2",
        f"201项目名 = {safe_name}",
        "202项目类型 = 101|公路主线",
        "204桩号小数精度 = 3",
        f"205桩号间隔 = {int(round(station_interval_m))}",
        "206图标字母标识 = 1|中文",
        "207图表英文字体 = ",
        "208图表中文字体 = ",
        "209土方计算方式 = 0|平均断面法（一般推荐采用）",
        "210横断面起始编号 = 1",
        "211纵坡表示形式 = 0|%",
        f"214项目ID = {uuid.uuid4()}",
        "251设计单位 = EngineeringPlatform",
        "252委托单位 = ",
        "253设计阶段 = 施工图/初设自动选线",
        "254设计人 = AI_RouteSolver",
        "255复核人 = ",
        "[项目分段1]4",
        f"301起点桩号 = {start_station_m:.3f}",
        f"302终点桩号 = {end_sta:.3f}",
        "303公路地区 = ",
        f"304计算车速 = {speed}",
        f"305公路等级 = {hw_class}",
        f"306横断面形式 = {lanes}车道",
        f"307路幅宽度 = {width:.3f}",
        "308行车道横坡 = 2.0%",
        "309土路肩横坡 = 3.0%",
        "310中间带 = 0.00",
        f"311最大超高或地区类型 = {max_sup_label}",
        "312超高旋转方式 = 1|绕行车道中心旋转",
        "313超高渐变方式 = 244|线性",
        "314加宽类型 = 三类加宽",
        "315加宽方式 = 曲线内侧加宽",
        "316加宽渐变方式 = 1|线性加宽(W = kb)",
        "[文件名]1",
        f"101平面线形文件(*.PM) = .\\{safe_name}.pm",
        f"102平面交点文件(*.JD) = .\\{safe_name}.JD",
        f"103纵断面地面线文件(*.DMX) = .\\{safe_name}.DMX",
        f"104纵断面设计文件(*.ZDM) = .\\{safe_name}.ZDM",
        f"105横断面地面线文件(*.HDM) = .\\{safe_name}.HDM",
        f"106路幅宽度文件(*.WID) = .\\{safe_name}.wid",
        f"107超高设置文件(*.SUP) = .\\{safe_name}.sup",
        f"108设计参数控制文件(*.CTR) = .\\{safe_name}.ctr",
        f"109桩号序列文件(*.STA) = .\\{safe_name}.sta",
        f"110路基中间数据文件(*.LJ) = .\\{safe_name}.lj",
        f"111土方数据文件(*.TF) = .\\{safe_name}.TF",
    ]
    prj_path = target / f"{safe_name}.Prj"
    _write_gbk(prj_path, "\n".join(prj_lines) + "\n")

    guide = (
        f"纬地 HintCAD V8 一键导入步骤（项目：{safe_name}）\n"
        f"1. 在纬地中运行 OPEN_PROJECT 打开 {prj_path.name}（或用 HPM 打开）；\n"
        f"2. 运行 JDZB_IN 导入 {jd_import_path.name}（已自动完成 AutoCAD E/N -> 纬地 X=北N/Y=东E 互换），生成 .JD/.pm；\n"
        f"3. 纵断面地面线 ({dmx_path.name})、设计拉坡 ({zdm_path.name})、控制参数 ({ctr_path.name})、路幅宽度 ({wid_path.name})、桩号序列 ({sta_path.name}) 已就绪；\n"
        f"4. 加载地形数模 (DTM/NEWDTM -> CHECKNET -> TRI) 后运行 GETHDM 采集横断面地面线；\n"
        f"5. 依次运行 CHECK_HDM_DMX -> LJSJ（必跑生成 .lj） -> HDM_NEW -> ZDMT / LJSJB / CALZPB 输出全套施工图与设计表。\n"
    )
    guide_path = target / "README_HINTCAD_IMPORT.txt"
    guide_path.write_text(guide, encoding="utf-8")

    return {
        "project_dir": str(target),
        "prj": str(prj_path),
        "jd_import": str(jd_import_path),
        "zdm": str(zdm_path),
        "dmx": str(dmx_path),
        "sta": str(sta_path),
        "ctr": str(ctr_path),
        "wid": str(wid_path),
        "guide": str(guide_path),
        "jd_count": jd_count,
        "pvi_count": len(pvis),
        "dmx_count": len(sampled),
        "design_speed_kmh": speed,
        "road_width_m": width,
        "start_station_m": start_station_m,
        "end_station_m": round(end_sta, 3),
    }


def parse_hintcad_project(prj_path: str | Path) -> Dict[str, Any]:
    """解析纬地 .Prj 项目及关联的 .ZDM / .DMX / .sta / .TF 文件。"""
    prj = Path(prj_path)
    if not prj.is_file():
        raise FileNotFoundError(f"HintCAD .Prj not found: {prj}")

    text = _read_gbk(prj)
    section = ""
    settings: Dict[str, str] = {}
    segment: Dict[str, str] = {}
    files_map: Dict[str, str] = {}

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        if line.startswith("[") and "]" in line:
            section = line[1 : line.index("]")]
            continue
        if "=" in line:
            k, v = [p.strip() for p in line.split("=", 1)]
            if section == "项目设置":
                settings[k] = v
            elif section.startswith("项目分段"):
                segment[k] = v
            elif section == "文件名" and v:
                files_map[k] = v

    base_dir = prj.parent
    def _resolve(key_substr: str) -> Optional[Path]:
        for k, v in files_map.items():
            if key_substr in k.upper():
                cand = (base_dir / v.replace("\\", "/")).resolve()
                if cand.is_file():
                    return cand
        return None

    # 解析 .ZDM
    pvis: List[Dict[str, float]] = []
    zdm_file = _resolve(".ZDM")
    if zdm_file:
        z_lines = [l.strip() for l in _read_gbk(zdm_file).splitlines() if l.strip()]
        for ln in z_lines[2:]:
            parts = ln.split()
            if len(parts) >= 3:
                try:
                    pvis.append({
                        "station_m": float(parts[0]),
                        "elevation_m": float(parts[1]),
                        "vertical_radius_m": float(parts[2]),
                    })
                except ValueError:
                    pass

    # 解析 .DMX
    dmx_points: List[Tuple[float, float]] = []
    dmx_file = _resolve(".DMX")
    if dmx_file:
        for ln in _read_gbk(dmx_file).splitlines()[1:]:
            parts = ln.split()
            if len(parts) >= 2:
                try:
                    dmx_points.append((float(parts[0]), float(parts[1])))
                except ValueError:
                    pass

    # 解析 .sta
    sta_count = 0
    sta_file = _resolve(".STA")
    if sta_file:
        for ln in _read_gbk(sta_file).splitlines()[1:]:
            if ln.strip():
                sta_count += 1

    # 解析 .TF（土方断面汇总）
    tf_sections = 0
    tf_file = _resolve(".TF")
    if tf_file:
        for ln in _read_gbk(tf_file).splitlines()[1:]:
            if ln.strip():
                tf_sections += 1

    return {
        "prj_path": str(prj),
        "project_name": settings.get("201项目名", prj.stem),
        "project_type": settings.get("202项目类型", ""),
        "design_speed_kmh": int(float(segment["304计算车速"])) if segment.get("304计算车速") else None,
        "highway_class": segment.get("305公路等级", ""),
        "road_width_m": float(segment["307路幅宽度"]) if segment.get("307路幅宽度") else None,
        "start_station_m": float(segment["301起点桩号"]) if segment.get("301起点桩号") else None,
        "end_station_m": float(segment["302终点桩号"]) if segment.get("302终点桩号") else None,
        "pvi_count": len(pvis),
        "pvis": pvis,
        "dmx_count": len(dmx_points),
        "dmx_elevation_range": (
            [round(min(z for _, z in dmx_points), 3), round(max(z for _, z in dmx_points), 3)]
            if dmx_points else None
        ),
        "station_count": sta_count,
        "tf_section_count": tf_sections,
        "linked_files": files_map,
    }


def diagnose_hintcad_errors(path: str | Path) -> Dict[str, Any]:
    """解析纬地 Lst/ERR*.txt 日志，按工程故障类别归类并生成修复建议。"""
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(f"HintCAD error path not found: {p}")

    files = sorted(p.glob("ERR*.txt")) if p.is_dir() else [p]
    categories: Dict[str, Dict[str, Any]] = {
        "cut_slope_no_intersect": {
            "label": "挖方边坡无法与地面线相交",
            "count": 0,
            "stations_sample": [],
            "fix": "使用 HDTM_EXHDM 补测加宽 .HDM 横断面，或在 CTREDIT (.ctr) 中调整挖方边坡/增设挡土墙或隧道起终点。",
        },
        "fill_slope_no_intersect": {
            "label": "填方边坡无法与地面线相交",
            "count": 0,
            "stations_sample": [],
            "fix": "使用 HDTM_EXHDM 补测加宽 .HDM 横断面，或对陡坡高填段在 .ctr (QHSJ/挡墙) 中改设桥梁或重力式挡土墙。",
        },
        "ditch_no_intersect": {
            "label": "排水沟/边沟边线无法与地面线相交",
            "count": 0,
            "stations_sample": [],
            "fix": "在 CTREDIT (.ctr) 的 ZPSGXS/YPSGXS 调整排水沟起设最小填高阈值，或对浅填零填过渡段改用边沟。",
        },
        "missing_lj_data": {
            "label": "断面缺少路基设计中间数据 (.lj)",
            "count": 0,
            "stations_sample": [],
            "fix": "先运行 CHECK_HDM_DMX 核对桩号一致性，再重跑 LJSJ 重新生成 .lj 中间数据，最后重跑 HDM_NEW。",
        },
        "other": {
            "label": "其他纬地计算提示",
            "count": 0,
            "stations_sample": [],
            "fix": "请查看原 ERR*.txt 详情并结合 CHECK_HDM_DMX / CHECKNET 检查原始数据。",
        },
    }

    total_lines = 0
    for f in files:
        for raw in _read_gbk(f).splitlines():
            line = raw.strip()
            if not line:
                continue
            total_lines += 1
            sta_match = re.match(r"^([A-Za-z]?\d+(?:\.\d+)?)\s*[:：]\s*(.+)$", line)
            sta = sta_match.group(1) if sta_match else ""
            msg = sta_match.group(2) if sta_match else line

            if "挖方边坡失败" in msg:
                key = "cut_slope_no_intersect"
            elif "填方边坡失败" in msg:
                key = "fill_slope_no_intersect"
            elif "排水沟失败" in msg or "边沟边线" in msg:
                key = "ditch_no_intersect"
            elif "缺少路基设计中间数据" in msg:
                key = "missing_lj_data"
            else:
                key = "other"

            bucket = categories[key]
            bucket["count"] += 1
            if sta and len(bucket["stations_sample"]) < 20 and sta not in bucket["stations_sample"]:
                bucket["stations_sample"].append(sta)

    active = {k: v for k, v in categories.items() if v["count"] > 0}
    return {
        "scanned_files": len(files),
        "total_error_lines": total_lines,
        "categories": active,
    }


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="HintCAD V8 project exporter, parser, and ERR diagnostics")
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_exp = sub.add_parser("export", help="Export route_solver plan_N.json to HintCAD V8 project")
    p_exp.add_argument("--plan", required=True, help="Path to plan_N.json")
    p_exp.add_argument("--out-dir", required=True, help="Output project folder")
    p_exp.add_argument("--name", default="EP_Route", help="HintCAD project name")
    p_exp.add_argument("--speed", type=int, default=None, help="Design speed (km/h)")
    p_exp.add_argument("--road-width", type=float, default=None, help="Roadbed width (m)")

    p_par = sub.add_parser("parse", help="Parse existing HintCAD .Prj project")
    p_par.add_argument("--prj", required=True, help="Path to .Prj file")

    p_diag = sub.add_parser("diagnose", help="Diagnose HintCAD Lst/ERR*.txt logs")
    p_diag.add_argument("--path", required=True, help="Path to ERR*.txt file or Lst directory")

    args = parser.parse_args(argv)
    if args.cmd == "export":
        payload = json.loads(Path(args.plan).read_text(encoding="utf-8"))
        res = export_hintcad_project(
            payload, args.out_dir, project_name=args.name,
            design_speed_kmh=args.speed, road_width_m=args.road_width,
        )
    elif args.cmd == "parse":
        res = parse_hintcad_project(args.prj)
    else:
        res = diagnose_hintcad_errors(args.path)

    print(json.dumps(res, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
