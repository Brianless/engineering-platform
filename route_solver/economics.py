"""Highway engineering economics, mass-haul LP optimization, and HintCAD report export."""

from __future__ import annotations

import csv
import math
import os
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
from scipy.optimize import linprog

from .cost_field import TerrainGrid
from .horizontal_solver import HorizontalPlan
from .vertical_solver import VerticalPlan

_trapz = getattr(np, "trapezoid", None) or np.trapz


@dataclass
class HighwayUnitPrices:
    """Chinese mountain highway budget unit prices (公路工程概算综合单价, CNY)."""

    earth_cut_cny_m3: float = 18.0
    rock_cut_cny_m3: float = 52.0
    rock_ratio: float = 0.45
    usable_cut_factor: float = 0.90
    local_fill_cny_m3: float = 22.0
    haul_base_cny_m3: float = 8.0
    haul_per_km_cny_m3: float = 5.5
    max_economic_haul_m: float = 4000.0
    borrow_fill_cny_m3: float = 48.0
    waste_disposal_cny_m3: float = 26.0
    pavement_cny_m2: float = 285.0
    retaining_wall_cny_m3: float = 680.0
    slope_protection_cny_m2: float = 95.0
    bridge_cny_m2: float = 6200.0
    tunnel_cny_m: float = 68000.0
    guardrail_cny_m: float = 320.0
    land_cny_mu: float = 65000.0
    other_fee_rate: float = 0.14  # 勘察设计/监理/预备费等二三类费用比例

    @classmethod
    def from_dict(cls, data: Optional[Dict[str, Any]]) -> "HighwayUnitPrices":
        if not data:
            return cls()
        valid = {k: float(v) for k, v in data.items() if hasattr(cls, k)}
        return cls(**valid)

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass
class MassHaulBlock:
    """Per-segment earthwork balance & haul allocation (每公里/分段土石方调配)."""

    block_index: int
    start_station_m: float
    end_station_m: float
    cut_m3: float
    fill_m3: float
    local_reuse_m3: float
    haul_out_m3: float
    haul_in_m3: float
    waste_m3: float
    borrow_m3: float

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass
class MassHaulResult:
    """Global minimum-cost earthwork mass-haul solution."""

    total_cut_m3: float
    total_fill_m3: float
    local_reuse_m3: float
    longitudinal_haul_m3: float
    avg_haul_distance_m: float
    waste_m3: float
    borrow_m3: float
    utilization_pct: float
    earthwork_total_cost_cny: float
    blocks: List[MassHaulBlock] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        d = asdict(self)
        d["blocks"] = [b.to_dict() for b in self.blocks]
        return d


@dataclass
class EconomicBreakdown:
    """Full engineering quantity and budget cost breakdown (单位: 万元)."""

    earthwork_cost_wan: float
    pavement_cost_wan: float
    retaining_and_protection_cost_wan: float
    bridge_cost_wan: float
    tunnel_cost_wan: float
    traffic_safety_cost_wan: float
    land_acquisition_cost_wan: float
    construction_subtotal_wan: float
    other_fees_wan: float
    total_investment_wan: float
    cost_per_km_wan: float
    # Key engineering quantities
    roadbed_width_m: float
    avg_corridor_width_m: float
    land_area_m2: float
    land_area_mu: float
    pavement_area_m2: float
    retaining_wall_length_m: float
    retaining_wall_volume_m3: float
    slope_protection_area_m2: float
    guardrail_length_m: float
    bridge_length_m: float
    tunnel_length_m: float
    mass_haul: MassHaulResult

    def to_dict(self) -> Dict[str, Any]:
        d = asdict(self)
        d["mass_haul"] = self.mass_haul.to_dict()
        return d


def solve_mass_haul(
    stations_m: np.ndarray,
    cut_areas_m2: np.ndarray,
    fill_areas_m2: np.ndarray,
    prices: HighwayUnitPrices,
    block_length_m: float = 1000.0,
) -> MassHaulResult:
    """Solve optimal earthwork allocation (本桩利用 / 远运调配 / 借方 / 弃方) via LP."""
    if len(stations_m) < 2:
        return MassHaulResult(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 100.0, 0.0, [])

    total_len = float(stations_m[-1] - stations_m[0])
    n_blocks = max(1, int(math.ceil(total_len / max(100.0, block_length_m))))
    edges = np.linspace(float(stations_m[0]), float(stations_m[-1]), n_blocks + 1)

    blk_cut = np.zeros(n_blocks, dtype=np.float64)
    blk_fill = np.zeros(n_blocks, dtype=np.float64)
    blk_mid = 0.5 * (edges[:-1] + edges[1:])

    for b in range(n_blocks):
        s0, s1 = edges[b], edges[b + 1]
        sub_s = np.unique(np.concatenate([[s0], stations_m[(stations_m > s0) & (stations_m < s1)], [s1]]))
        sub_c = np.interp(sub_s, stations_m, cut_areas_m2)
        sub_f = np.interp(sub_s, stations_m, fill_areas_m2)
        blk_cut[b] = max(0.0, float(_trapz(sub_c, sub_s)))
        blk_fill[b] = max(0.0, float(_trapz(sub_f, sub_s)))

    # Local reuse within each block first (本桩利用)
    usable_cut = blk_cut * prices.usable_cut_factor
    unusable_waste = blk_cut - usable_cut
    local_reuse = np.minimum(usable_cut, blk_fill)
    surplus = usable_cut - local_reuse
    deficit = blk_fill - local_reuse

    # Transportation LP for inter-block longitudinal haul vs waste/borrow
    # Decision variables:
    #   x_{i,j} for all surplus block i and deficit block j within max_economic_haul_m
    #   w_i >= 0 (waste from surplus block i)
    #   b_j >= 0 (borrow for deficit block j)
    surplus_idx = [i for i in range(n_blocks) if surplus[i] > 1e-3]
    deficit_idx = [j for j in range(n_blocks) if deficit[j] > 1e-3]

    haul_pairs: List[Tuple[int, int, float]] = []
    for i in surplus_idx:
        for j in deficit_idx:
            dist_m = abs(float(blk_mid[i] - blk_mid[j]))
            if dist_m <= prices.max_economic_haul_m:
                haul_pairs.append((i, j, dist_m))

    haul_out = np.zeros(n_blocks, dtype=np.float64)
    haul_in = np.zeros(n_blocks, dtype=np.float64)
    waste = unusable_waste.copy()
    borrow = np.zeros(n_blocks, dtype=np.float64)
    haul_dist_sum = 0.0
    haul_cost_cny = 0.0

    if haul_pairs:
        n_h = len(haul_pairs)
        n_s = len(surplus_idx)
        n_d = len(deficit_idx)
        n_vars = n_h + n_s + n_d
        c_vec = np.zeros(n_vars, dtype=np.float64)
        for k, (_, _, dist_m) in enumerate(haul_pairs):
            c_vec[k] = prices.local_fill_cny_m3 + prices.haul_base_cny_m3 + prices.haul_per_km_cny_m3 * (dist_m / 1000.0)
        for si in range(n_s):
            c_vec[n_h + si] = prices.waste_disposal_cny_m3
        for dj in range(n_d):
            c_vec[n_h + n_s + dj] = prices.borrow_fill_cny_m3

        A_eq = np.zeros((n_s + n_d, n_vars), dtype=np.float64)
        b_eq = np.zeros(n_s + n_d, dtype=np.float64)
        s_map = {idx: pos for pos, idx in enumerate(surplus_idx)}
        d_map = {idx: pos for pos, idx in enumerate(deficit_idx)}

        for k, (i, j, _) in enumerate(haul_pairs):
            A_eq[s_map[i], k] = 1.0
            A_eq[n_s + d_map[j], k] = 1.0
        for pos, i in enumerate(surplus_idx):
            A_eq[pos, n_h + pos] = 1.0
            b_eq[pos] = surplus[i]
        for pos, j in enumerate(deficit_idx):
            A_eq[n_s + pos, n_h + n_s + pos] = 1.0
            b_eq[n_s + pos] = deficit[j]

        res = linprog(c_vec, A_eq=A_eq, b_eq=b_eq, bounds=(0.0, None), method="highs")
        if res.success and res.x is not None:
            sol = res.x
            for k, (i, j, dist_m) in enumerate(haul_pairs):
                q = float(sol[k])
                if q > 1e-4:
                    haul_out[i] += q
                    haul_in[j] += q
                    haul_dist_sum += q * dist_m
                    haul_cost_cny += q * c_vec[k]
            for pos, i in enumerate(surplus_idx):
                waste[i] += float(sol[n_h + pos])
            for pos, j in enumerate(deficit_idx):
                borrow[j] += float(sol[n_h + n_s + pos])
        else:
            for i in surplus_idx:
                waste[i] += surplus[i]
            for j in deficit_idx:
                borrow[j] += deficit[j]
    else:
        for i in surplus_idx:
            waste[i] += surplus[i]
        for j in deficit_idx:
            borrow[j] += deficit[j]

    tot_cut = float(np.sum(blk_cut))
    tot_fill = float(np.sum(blk_fill))
    tot_local = float(np.sum(local_reuse))
    tot_haul = float(np.sum(haul_out))
    tot_waste = float(np.sum(waste))
    tot_borrow = float(np.sum(borrow))
    avg_dist = (haul_dist_sum / tot_haul) if tot_haul > 1e-3 else 0.0

    # Total earthwork cost = excavation + local compaction + haul + waste disposal + borrow
    blended_cut_price = prices.earth_cut_cny_m3 * (1.0 - prices.rock_ratio) + prices.rock_cut_cny_m3 * prices.rock_ratio
    total_ew_cost = (
        tot_cut * blended_cut_price
        + tot_local * prices.local_fill_cny_m3
        + haul_cost_cny
        + tot_waste * prices.waste_disposal_cny_m3
        + tot_borrow * prices.borrow_fill_cny_m3
    )

    denom = max(1.0, tot_cut + tot_borrow)
    util_pct = min(100.0, ((tot_local + tot_haul) / denom) * 100.0) if (tot_cut + tot_fill) > 1.0 else 100.0

    blocks: List[MassHaulBlock] = []
    for b in range(n_blocks):
        blocks.append(
            MassHaulBlock(
                block_index=b + 1,
                start_station_m=round(float(edges[b]), 1),
                end_station_m=round(float(edges[b + 1]), 1),
                cut_m3=round(float(blk_cut[b]), 1),
                fill_m3=round(float(blk_fill[b]), 1),
                local_reuse_m3=round(float(local_reuse[b]), 1),
                haul_out_m3=round(float(haul_out[b]), 1),
                haul_in_m3=round(float(haul_in[b]), 1),
                waste_m3=round(float(waste[b]), 1),
                borrow_m3=round(float(borrow[b]), 1),
            )
        )

    return MassHaulResult(
        total_cut_m3=round(tot_cut, 1),
        total_fill_m3=round(tot_fill, 1),
        local_reuse_m3=round(tot_local, 1),
        longitudinal_haul_m3=round(tot_haul, 1),
        avg_haul_distance_m=round(avg_dist, 1),
        waste_m3=round(tot_waste, 1),
        borrow_m3=round(tot_borrow, 1),
        utilization_pct=round(util_pct, 2),
        earthwork_total_cost_cny=round(total_ew_cost, 1),
        blocks=blocks,
    )


def evaluate_economics(
    h_plan: HorizontalPlan,
    v_plan: VerticalPlan,
    road_width_m: float = 7.5,
    cut_slope_h_to_v: float = 0.8,
    fill_slope_h_to_v: float = 1.5,
    max_slope_width_m: float = 12.0,
    bridge_fill_threshold_m: float = 18.0,
    tunnel_cut_threshold_m: float = 22.0,
    prices: Optional[HighwayUnitPrices] = None,
) -> EconomicBreakdown:
    """Compute realistic design-institute quantities, mass haul, and highway budget."""
    up = prices or HighwayUnitPrices()
    sta = v_plan.stations_m
    dh = v_plan.design_elev_m - v_plan.ground_elev_m
    total_len = max(1.0, float(h_plan.total_length_m))

    ds = np.diff(sta) if len(sta) > 1 else np.array([], dtype=np.float64)
    mid_dh = 0.5 * (dh[:-1] + dh[1:]) if len(dh) > 1 else np.array([], dtype=np.float64)

    is_bridge = mid_dh > bridge_fill_threshold_m
    is_tunnel = (-mid_dh) > tunnel_cut_threshold_m
    is_subgrade = ~(is_bridge | is_tunnel)

    bridge_len = float(np.sum(ds[is_bridge])) if ds.size else 0.0
    tunnel_len = float(np.sum(ds[is_tunnel])) if ds.size else 0.0
    subgrade_len = max(0.0, total_len - bridge_len - tunnel_len)

    # Subgrade cross-section areas (excluding bridges and tunnels, with max slope width cap!)
    node_is_bridge = dh > bridge_fill_threshold_m
    node_is_tunnel = (-dh) > tunnel_cut_threshold_m
    node_sub = ~(node_is_bridge | node_is_tunnel)

    cut_h = np.where(node_sub, np.maximum(0.0, -dh), 0.0)
    fill_h = np.where(node_sub, np.maximum(0.0, dh), 0.0)

    # Capped slope horizontal projection (matches Civil 3D DaylightMaxWidth!)
    cut_w_side = np.minimum(max_slope_width_m, cut_h * cut_slope_h_to_v)
    fill_w_side = np.minimum(max_slope_width_m, fill_h * fill_slope_h_to_v)

    cut_areas = road_width_m * cut_h + cut_w_side * cut_h
    fill_areas = road_width_m * fill_h + fill_w_side * fill_h

    # Retaining walls triggered wherever fill slope hits max_slope_width_m or fill_h > 6m
    rw_needed = node_sub & ((fill_h * fill_slope_h_to_v > max_slope_width_m * 0.85) | (fill_h > 6.5))
    rw_h = np.where(rw_needed, np.clip(fill_h - 1.5, 2.0, 9.0), 0.0)
    # Gravity retaining wall cross-section area approx: 0.42 * H^2 + 0.6 * H (m^2)
    rw_area = np.where(rw_needed, 0.42 * (rw_h ** 2) + 0.6 * rw_h, 0.0)
    rw_vol = float(_trapz(rw_area, sta)) if len(sta) >= 2 else 0.0
    rw_len = float(np.sum(ds[0.5 * (rw_needed[:-1].astype(float) + rw_needed[1:].astype(float)) >= 0.5])) if ds.size else 0.0

    # Corridor footprint width (both sides + roadbed + 2m drainage/acquisition buffer)
    corridor_w = np.where(
        node_sub,
        road_width_m + 2.0 * (cut_w_side + fill_w_side) + 2.0,
        np.where(node_is_bridge, road_width_m + 4.0, 0.0),
    )
    land_area_m2 = float(_trapz(corridor_w, sta)) if len(sta) >= 2 else 0.0
    land_area_mu = land_area_m2 / 666.6667
    avg_corridor_w = land_area_m2 / max(1.0, subgrade_len + bridge_len) if (subgrade_len + bridge_len) > 1.0 else road_width_m

    # Slope surface area for ecological protection & ditches
    slope_face_len = np.hypot(cut_w_side, cut_h) + np.hypot(fill_w_side, fill_h)
    slope_prot_area = float(_trapz(2.0 * slope_face_len, sta)) if len(sta) >= 2 else 0.0

    # Pavement area (subgrade + bridge deck + tunnel invert)
    pavement_area = total_len * max(road_width_m - 1.0, 6.0)

    # Guardrails: outer shoulder on fill > 2.0m (both sides if high fill > 4m)
    guardrail_sides = np.where(node_is_bridge, 0.0, np.where(fill_h > 4.0, 2.0, np.where(fill_h > 1.8, 1.0, 0.35)))
    guardrail_len = float(_trapz(guardrail_sides, sta)) if len(sta) >= 2 else 0.0

    # Mass haul optimization
    mh = solve_mass_haul(sta, cut_areas, fill_areas, up, block_length_m=1000.0)

    # Cost components (万元)
    ew_wan = mh.earthwork_total_cost_cny / 1e4
    pav_wan = (pavement_area * up.pavement_cny_m2) / 1e4
    rw_prot_wan = (rw_vol * up.retaining_wall_cny_m3 + slope_prot_area * up.slope_protection_cny_m2) / 1e4
    bridge_wan = (bridge_len * (road_width_m + 1.0) * up.bridge_cny_m2) / 1e4
    tunnel_wan = (tunnel_len * up.tunnel_cny_m) / 1e4
    safety_wan = (guardrail_len * up.guardrail_cny_m + total_len * 45.0) / 1e4
    land_wan = (land_area_mu * up.land_cny_mu) / 1e4

    subtotal = ew_wan + pav_wan + rw_prot_wan + bridge_wan + tunnel_wan + safety_wan + land_wan
    other_fees = subtotal * up.other_fee_rate
    total_inv = subtotal + other_fees
    cost_per_km = total_inv / max(0.05, total_len / 1000.0)

    return EconomicBreakdown(
        earthwork_cost_wan=round(ew_wan, 2),
        pavement_cost_wan=round(pav_wan, 2),
        retaining_and_protection_cost_wan=round(rw_prot_wan, 2),
        bridge_cost_wan=round(bridge_wan, 2),
        tunnel_cost_wan=round(tunnel_wan, 2),
        traffic_safety_cost_wan=round(safety_wan, 2),
        land_acquisition_cost_wan=round(land_wan, 2),
        construction_subtotal_wan=round(subtotal, 2),
        other_fees_wan=round(other_fees, 2),
        total_investment_wan=round(total_inv, 2),
        cost_per_km_wan=round(cost_per_km, 2),
        roadbed_width_m=round(road_width_m, 2),
        avg_corridor_width_m=round(avg_corridor_w, 2),
        land_area_m2=round(land_area_m2, 1),
        land_area_mu=round(land_area_mu, 2),
        pavement_area_m2=round(pavement_area, 1),
        retaining_wall_length_m=round(rw_len, 1),
        retaining_wall_volume_m3=round(rw_vol, 1),
        slope_protection_area_m2=round(slope_prot_area, 1),
        guardrail_length_m=round(guardrail_len, 1),
        bridge_length_m=round(bridge_len, 1),
        tunnel_length_m=round(tunnel_len, 1),
        mass_haul=mh,
    )


def discover_hintcad_template_dir(explicit_dir: Optional[str | Path] = None) -> Optional[Path]:
    """Auto-discover HintCAD template directory across environment vars and all mounted Windows drives."""
    candidates: List[Path] = []
    if explicit_dir:
        candidates.append(Path(explicit_dir))
    env_dir = os.environ.get("HINTCAD_TEMPLATE_DIR")
    if env_dir:
        candidates.append(Path(env_dir))
    # Scan all mounted drives dynamically (zero hardcoded drive letters)
    drives = [f"{chr(c)}:" for c in range(ord("C"), ord("Z") + 1) if Path(f"{chr(c)}:/").exists()]
    for drv in drives:
        candidates.append(Path(f"{drv}/HintCAD/模板/表格"))
        candidates.append(Path(f"{drv}/Program Files/HintCAD/模板/表格"))
        candidates.append(Path(f"{drv}/Program Files (x86)/HintCAD/模板/表格"))
    for p in candidates:
        if p.is_dir() and (p / "直曲表.xls").exists():
            return p
    return None


def export_economic_comparison_csv(
    plans_summary: List[Dict[str, Any]],
    out_csv: str | Path,
) -> Path:
    """Export multi-scheme technical & economic comparison table (UTF-8-BOM CSV, Excel-ready)."""
    out_path = Path(out_csv)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    headers = [
        "方案编号", "综合排名", "综合评分", "路线全长(m)", "最小平曲线半径(m)",
        "最大纵坡(%)", "JTG规范核验", "总挖方(m3)", "总填方(m3)",
        "本桩利用(m3)", "远运调配(m3)", "平均运距(m)", "弃方(m3)", "借方(m3)", "土石方利用率(%)",
        "挡墙长度(m)", "挡墙圬工(m3)", "桥梁总长(m)", "隧道总长(m)", "桥隧比(%)",
        "平均占宽(m)", "征地面积(亩)",
        "土石方造价(万元)", "路面造价(万元)", "挡墙防护造价(万元)", "桥梁造价(万元)",
        "隧道造价(万元)", "交安造价(万元)", "征地费(万元)", "估算总投资(万元)", "每公里造价(万元/km)",
    ]
    with out_path.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(headers)
        for m in plans_summary:
            ec = m.get("economics") or {}
            mh = ec.get("mass_haul") or {}
            w.writerow([
                f"方案{m.get('plan_id')}",
                m.get("rank", ""),
                m.get("score", ""),
                m.get("total_length_m", ""),
                m.get("min_radius_m", ""),
                m.get("max_grade_pct", ""),
                "通过" if m.get("jtg_compliant", False) else "需复核",
                mh.get("total_cut_m3", m.get("total_cut_m3", "")),
                mh.get("total_fill_m3", m.get("total_fill_m3", "")),
                mh.get("local_reuse_m3", ""),
                mh.get("longitudinal_haul_m3", ""),
                mh.get("avg_haul_distance_m", ""),
                mh.get("waste_m3", ""),
                mh.get("borrow_m3", ""),
                mh.get("utilization_pct", ""),
                ec.get("retaining_wall_length_m", ""),
                ec.get("retaining_wall_volume_m3", ""),
                ec.get("bridge_length_m", m.get("bridge_length_m", "")),
                ec.get("tunnel_length_m", m.get("tunnel_length_m", "")),
                m.get("bridge_tunnel_ratio_pct", ""),
                ec.get("avg_corridor_width_m", ""),
                ec.get("land_area_mu", ""),
                ec.get("earthwork_cost_wan", ""),
                ec.get("pavement_cost_wan", ""),
                ec.get("retaining_and_protection_cost_wan", ""),
                ec.get("bridge_cost_wan", ""),
                ec.get("tunnel_cost_wan", ""),
                ec.get("traffic_safety_cost_wan", ""),
                ec.get("land_acquisition_cost_wan", ""),
                ec.get("total_investment_wan", ""),
                ec.get("cost_per_km_wan", ""),
            ])
    return out_path
