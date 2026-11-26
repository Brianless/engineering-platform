"""Unified CLI entry point for external route_solver engine."""

from __future__ import annotations

import argparse
import json
import os
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from .cost_field import CostField, CostWeights, load_constraints_geojson, load_terrain_npz
from .economics import HighwayUnitPrices, export_economic_comparison_csv
from .evaluator import RouteEvaluator, build_plan_payload
from .hintcad_io import export_hintcad_project
from .horizontal_solver import DesignSpeedSpec, HorizontalSolver
from .vertical_solver import VerticalSolver


def _parse_xy(raw: str) -> Tuple[float, float]:
    parts = [p.strip() for p in raw.split(",")]
    if len(parts) != 2:
        raise ValueError(f"Expected 'x,y' coordinate pair, got: {raw!r}")
    return float(parts[0]), float(parts[1])


def run_pipeline(
    terrain_path: str | Path,
    start_xy: Tuple[float, float],
    end_xy: Tuple[float, float],
    constraints_path: Optional[str | Path] = None,
    config_path: Optional[str | Path] = None,
    design_speed_kmh: int = 60,
    num_plans: int = 3,
    sample_step_m: float = 10.0,
    road_width_m: float = 12.0,
    out_dir: Optional[str | Path] = None,
) -> Dict[str, Any]:
    """Execute full horizontal + vertical + evaluation pipeline and write JSON outputs."""
    cfg: Dict[str, Any] = {}
    if config_path:
        with Path(config_path).open("r", encoding="utf-8") as f:
            cfg = json.load(f)

    weights = CostWeights.from_dict(cfg.get("weights"))
    spec = DesignSpeedSpec.from_speed(int(cfg.get("design_speed_kmh", design_speed_kmh)))

    terrain = load_terrain_npz(terrain_path)
    constraints = load_constraints_geojson(constraints_path)
    cf = CostField(terrain=terrain, constraints=constraints, weights=weights)

    h_solver = HorizontalSolver(cf, spec=spec)
    h_plans = h_solver.solve_k_plans(
        start_xy=start_xy,
        end_xy=end_xy,
        num_plans=int(cfg.get("num_plans", num_plans)),
        sample_step_m=float(cfg.get("sample_step_m", sample_step_m)),
    )

    if not h_plans:
        raise RuntimeError("No feasible horizontal corridor found between start and end points.")

    rw = float(cfg.get("road_width_m", road_width_m))
    max_sw = float(cfg.get("max_slope_width_m", 12.0))
    prices = HighwayUnitPrices.from_dict(cfg.get("unit_prices"))

    v_solver = VerticalSolver(terrain, spec=spec, road_width_m=rw, max_slope_width_m=max_sw)
    v_plans = [v_solver.solve(hp) for hp in h_plans]

    evaluator = RouteEvaluator(
        spec=spec,
        constraints=constraints,
        road_width_m=rw,
        max_slope_width_m=max_sw,
        unit_prices=prices,
    )
    metrics_list = evaluator.evaluate_all(h_plans, v_plans)

    target_dir = Path(out_dir) if out_dir else Path(tempfile.gettempdir()) / "ep_workspace"
    target_dir.mkdir(parents=True, exist_ok=True)

    plan_files: List[str] = []
    payloads_by_id: Dict[int, Dict[str, Any]] = {}
    for hp, vp, m in zip(h_plans, v_plans, metrics_list):
        payload = build_plan_payload(hp, vp, m)
        payloads_by_id[hp.plan_id] = payload
        out_file = target_dir / f"plan_{hp.plan_id}.json"
        with out_file.open("w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        plan_files.append(str(out_file))

    econ_csv = export_economic_comparison_csv(
        [m.to_dict() for m in metrics_list],
        target_dir / "economic_comparison.csv",
    )

    rec_id = min(metrics_list, key=lambda m: m.rank).plan_id
    hintcad_info = export_hintcad_project(
        payloads_by_id[rec_id],
        out_dir=target_dir / "hintcad_project",
        project_name=f"EP_Plan_{rec_id}",
        design_speed_kmh=spec.design_speed_kmh,
        road_width_m=rw,
    )

    summary = {
        "design_speed_kmh": spec.design_speed_kmh,
        "num_plans": len(metrics_list),
        "weights": weights.to_dict(),
        "plans": [m.to_dict() for m in metrics_list],
        "recommended_plan_id": rec_id,
        "plan_files": plan_files,
        "economic_comparison_csv": str(econ_csv),
        "hintcad_project": hintcad_info,
    }
    summary_file = target_dir / "summary.json"
    with summary_file.open("w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    return summary


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="External route-solver engine for Civil 3D")
    parser.add_argument("--terrain", required=True, help="Path to terrain.npz")
    parser.add_argument("--constraints", default=None, help="Path to constraints.geojson")
    parser.add_argument("--config", default=None, help="Optional JSON config (weights, design_speed_kmh, etc.)")
    parser.add_argument("--start", required=True, help="Start coordinate 'x,y'")
    parser.add_argument("--end", required=True, help="End coordinate 'x,y'")
    parser.add_argument("--design-speed", type=int, default=60, help="Design speed in km/h (40/60/80/100)")
    parser.add_argument("--num-plans", type=int, default=3, help="Number of candidate plans to generate")
    parser.add_argument("--sample-step", type=float, default=10.0, help="Centerline sampling interval (m)")
    parser.add_argument("--road-width", type=float, default=12.0, help="Roadbed width for earthwork estimate (m)")
    parser.add_argument("--out-dir", default=None, help="Output directory (default: %%TEMP%%/ep_workspace)")

    args = parser.parse_args(argv)
    summary = run_pipeline(
        terrain_path=args.terrain,
        start_xy=_parse_xy(args.start),
        end_xy=_parse_xy(args.end),
        constraints_path=args.constraints,
        config_path=args.config,
        design_speed_kmh=args.design_speed,
        num_plans=args.num_plans,
        sample_step_m=args.sample_step,
        road_width_m=args.road_width,
        out_dir=args.out_dir,
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
