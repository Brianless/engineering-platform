"""Engineering metrics evaluator, economic analyzer, and multi-plan scorer."""

from __future__ import annotations

import math
from dataclasses import asdict, dataclass, field
from typing import Any, Dict, List, Optional

import numpy as np
from shapely.geometry import LineString

from .cost_field import ConstraintFeature
from .economics import EconomicBreakdown, HighwayUnitPrices, evaluate_economics
from .horizontal_solver import DesignSpeedSpec, HorizontalPlan
from .vertical_solver import VerticalPlan


@dataclass
class EncroachmentDetail:
    """Sensitive zone or obstacle intersected by a candidate alignment."""

    name: str
    category: str
    overlap_length_m: float

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass
class PlanMetrics:
    """Structured engineering & economic KPIs for a candidate alignment plan."""

    plan_id: int
    total_length_m: float
    min_radius_m: Optional[float]
    radius_compliant: bool
    max_grade_pct: float
    grade_compliant: bool
    total_cut_m3: float
    total_fill_m3: float
    total_earthwork_m3: float
    cut_fill_balance_ratio: float
    bridge_length_m: float
    tunnel_length_m: float
    bridge_tunnel_ratio_pct: float
    encroachments: List[EncroachmentDetail]
    score: float
    rank: int = 0
    # Design-institute extensions (JTG D20 + economics + mass haul)
    jtg_compliant: bool = True
    total_investment_wan: float = 0.0
    cost_per_km_wan: float = 0.0
    earthwork_utilization_pct: float = 0.0
    land_area_mu: float = 0.0
    economics: Dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> Dict[str, Any]:
        d = asdict(self)
        d["encroachments"] = [e.to_dict() for e in self.encroachments]
        return d


class RouteEvaluator:
    """Evaluates geometric compliance, JTG D20 rules, mass-haul economics, and impacts."""

    def __init__(
        self,
        spec: Optional[DesignSpeedSpec] = None,
        constraints: Optional[List[ConstraintFeature]] = None,
        bridge_fill_threshold_m: float = 18.0,
        tunnel_cut_threshold_m: float = 22.0,
        road_width_m: float = 7.5,
        max_slope_width_m: float = 12.0,
        unit_prices: Optional[HighwayUnitPrices] = None,
    ) -> None:
        self.spec = spec or DesignSpeedSpec.from_speed(60)
        self.constraints = constraints or []
        self.bridge_fill_threshold_m = bridge_fill_threshold_m
        self.tunnel_cut_threshold_m = tunnel_cut_threshold_m
        self.road_width_m = road_width_m
        self.max_slope_width_m = max_slope_width_m
        self.unit_prices = unit_prices or HighwayUnitPrices()

    def evaluate_all(
        self,
        h_plans: List[HorizontalPlan],
        v_plans: List[VerticalPlan],
    ) -> List[PlanMetrics]:
        raw_metrics: List[PlanMetrics] = []
        for h_plan, v_plan in zip(h_plans, v_plans):
            raw_metrics.append(self._evaluate_single(h_plan, v_plan))

        if not raw_metrics:
            return []

        # Normalize & score (0..100, higher is better — incorporates real economic investment!)
        min_len = min(m.total_length_m for m in raw_metrics)
        min_cost = max(1.0, min(m.total_investment_wan for m in raw_metrics))
        min_ew = max(1.0, min(m.total_earthwork_m3 for m in raw_metrics))

        for m in raw_metrics:
            cost_score = 35.0 * (min_cost / max(1.0, m.total_investment_wan))
            len_score = 20.0 * (min_len / max(1.0, m.total_length_m))
            ew_score = 15.0 * (min_ew / max(1.0, m.total_earthwork_m3))
            comp_score = (
                (10.0 if m.radius_compliant else 0.0)
                + (10.0 if m.grade_compliant else 0.0)
                + (10.0 if m.jtg_compliant else 0.0)
            )
            enc_penalty = sum(e.overlap_length_m for e in m.encroachments) * 0.25
            bt_penalty = m.bridge_tunnel_ratio_pct * 0.25
            m.score = round(
                max(0.0, min(100.0, cost_score + len_score + ew_score + comp_score - enc_penalty - bt_penalty)),
                2,
            )

        ranked = sorted(raw_metrics, key=lambda x: x.score, reverse=True)
        for idx, m in enumerate(ranked, start=1):
            m.rank = idx

        return raw_metrics

    def _evaluate_single(self, h_plan: HorizontalPlan, v_plan: VerticalPlan) -> PlanMetrics:
        line = LineString(h_plan.centerline_xy)
        encroachments: List[EncroachmentDetail] = []

        for feat in self.constraints:
            if feat.category in {"control_point", "waypoint", "control", "existing_road", "road"}:
                continue
            inter = line.intersection(feat.geometry)
            if not inter.is_empty and inter.length > 0.1:
                encroachments.append(
                    EncroachmentDetail(
                        name=feat.name,
                        category=feat.category,
                        overlap_length_m=round(float(inter.length), 2),
                    )
                )

        econ: EconomicBreakdown = evaluate_economics(
            h_plan=h_plan,
            v_plan=v_plan,
            road_width_m=self.road_width_m,
            max_slope_width_m=self.max_slope_width_m,
            bridge_fill_threshold_m=self.bridge_fill_threshold_m,
            tunnel_cut_threshold_m=self.tunnel_cut_threshold_m,
            prices=self.unit_prices,
        )

        bridge_len = econ.bridge_length_m
        tunnel_len = econ.tunnel_length_m
        bt_ratio = ((bridge_len + tunnel_len) / max(1.0, h_plan.total_length_m)) * 100.0

        min_r = h_plan.min_radius_m if math.isfinite(h_plan.min_radius_m) else None
        radius_ok = True if min_r is None else bool(min_r >= self.spec.min_radius_m - 1e-3)
        grade_ok = bool(v_plan.max_grade_pct <= self.spec.max_grade_pct + 0.05)
        jtg_ok = bool(radius_ok and grade_ok and v_plan.jtg_audit.get("all_passed", True))

        tot_cut = econ.mass_haul.total_cut_m3
        tot_fill = econ.mass_haul.total_fill_m3
        total_ew = tot_cut + tot_fill
        if tot_fill > 1e-3:
            cf_ratio = tot_cut / tot_fill
        else:
            cf_ratio = 1.0 if tot_cut < 1e-3 else 99.9

        return PlanMetrics(
            plan_id=h_plan.plan_id,
            total_length_m=round(h_plan.total_length_m, 2),
            min_radius_m=round(min_r, 2) if min_r is not None else None,
            radius_compliant=radius_ok,
            max_grade_pct=round(v_plan.max_grade_pct, 3),
            grade_compliant=grade_ok,
            total_cut_m3=round(tot_cut, 1),
            total_fill_m3=round(tot_fill, 1),
            total_earthwork_m3=round(total_ew, 1),
            cut_fill_balance_ratio=round(cf_ratio, 3),
            bridge_length_m=round(bridge_len, 2),
            tunnel_length_m=round(tunnel_len, 2),
            bridge_tunnel_ratio_pct=round(bt_ratio, 2),
            encroachments=encroachments,
            score=0.0,
            jtg_compliant=jtg_ok,
            total_investment_wan=econ.total_investment_wan,
            cost_per_km_wan=econ.cost_per_km_wan,
            earthwork_utilization_pct=econ.mass_haul.utilization_pct,
            land_area_mu=econ.land_area_mu,
            economics=econ.to_dict(),
        )


def build_plan_payload(
    h_plan: HorizontalPlan,
    v_plan: VerticalPlan,
    metrics: PlanMetrics,
) -> Dict[str, Any]:
    """Assemble full Civil 3D-ready alignment + profile + economics JSON payload."""
    sampled = [
        {
            "station_m": round(float(s), 3),
            "x": round(float(xy[0]), 3),
            "y": round(float(xy[1]), 3),
            "ground_z": round(float(gz), 3),
            "design_z": round(float(dz), 3),
        }
        for s, xy, gz, dz in zip(
            v_plan.stations_m,
            h_plan.centerline_xy,
            v_plan.ground_elev_m,
            v_plan.design_elev_m,
        )
    ]
    return {
        "plan_id": h_plan.plan_id,
        "metrics": metrics.to_dict(),
        "alignment": h_plan.to_dict(),
        "profile": v_plan.to_dict(),
        "sampled_centerline": sampled,
    }
