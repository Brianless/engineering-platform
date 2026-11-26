"""End-to-end and unit tests for route_solver."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from route_solver.cli import run_pipeline
from route_solver.cost_field import CostField, CostWeights, TerrainGrid, load_terrain_npz


class TestRouteSolver(unittest.TestCase):
    def _create_synthetic_inputs(self, workdir: Path) -> tuple[Path, Path]:
        # 1200m x 1000m terrain at 10m resolution (101 x 121)
        xs = np.linspace(500000.0, 501200.0, 121)
        ys = np.linspace(3100000.0, 3101000.0, 101)
        xx, yy = np.meshgrid(xs, ys)

        # Rolling terrain + central hill obstacle
        hill = 38.0 * np.exp(-(((xx - 500600.0) ** 2) / (2 * 130.0 ** 2) + ((yy - 3100500.0) ** 2) / (2 * 130.0 ** 2)))
        slope_bg = 0.015 * (xx - 500000.0) + 0.008 * (yy - 3100000.0)
        elev = 100.0 + slope_bg + hill

        terrain_path = workdir / "terrain.npz"
        np.savez(terrain_path, elevation=elev, x_coords=xs, y_coords=ys)

        # GeoJSON constraints: building cluster + forbidden zone + farmland
        geojson = {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "properties": {"category": "forbidden", "name": "Protected_Wetland"},
                    "geometry": {
                        "type": "Polygon",
                        "coordinates": [[
                            [500350.0, 3100250.0],
                            [500480.0, 3100250.0],
                            [500480.0, 3100380.0],
                            [500350.0, 3100380.0],
                            [500350.0, 3100250.0],
                        ]],
                    },
                },
                {
                    "type": "Feature",
                    "properties": {"category": "building", "name": "Village_Block"},
                    "geometry": {
                        "type": "Polygon",
                        "coordinates": [[
                            [500700.0, 3100650.0],
                            [500850.0, 3100650.0],
                            [500850.0, 3100800.0],
                            [500700.0, 3100800.0],
                            [500700.0, 3100650.0],
                        ]],
                    },
                },
            ],
        }
        constraints_path = workdir / "constraints.geojson"
        constraints_path.write_text(json.dumps(geojson), encoding="utf-8")
        return terrain_path, constraints_path

    def test_full_pipeline_generates_three_valid_plans(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            terrain_path, constraints_path = self._create_synthetic_inputs(tmp_dir)
            out_dir = tmp_dir / "ep_workspace"

            summary = run_pipeline(
                terrain_path=terrain_path,
                constraints_path=constraints_path,
                start_xy=(500080.0, 3100100.0),
                end_xy=(501100.0, 3100900.0),
                design_speed_kmh=60,
                num_plans=3,
                out_dir=out_dir,
            )

            self.assertEqual(summary["num_plans"], 3)
            self.assertIn(summary["recommended_plan_id"], {1, 2, 3})
            self.assertTrue((out_dir / "summary.json").exists())

            lengths = []
            for pid in (1, 2, 3):
                pfile = out_dir / f"plan_{pid}.json"
                self.assertTrue(pfile.exists())
                payload = json.loads(pfile.read_text(encoding="utf-8"))
                self.assertIn("alignment", payload)
                self.assertIn("profile", payload)
                self.assertIn("sampled_centerline", payload)
                self.assertGreater(len(payload["sampled_centerline"]), 20)

                # Verify zero encroachment into forbidden zone
                enc_names = {e["name"] for e in payload["metrics"]["encroachments"]}
                self.assertNotIn("Protected_Wetland", enc_names)

                # Verify grade compliance
                self.assertLessEqual(payload["metrics"]["max_grade_pct"], 6.001)
                lengths.append(payload["metrics"]["total_length_m"])

            # Ensure 3 plans are not identical and economics + JTG audit are populated
            self.assertGreater(len(set(round(l, 1) for l in lengths)), 1)
            self.assertTrue((out_dir / "economic_comparison.csv").exists())
            for pm in summary["plans"]:
                self.assertGreater(pm["total_investment_wan"], 0.0)
                self.assertIn("mass_haul", pm["economics"])
                self.assertGreaterEqual(pm["earthwork_utilization_pct"], 0.0)

    def test_jtg_mountain_30kmh_relief_grade_and_mass_haul(self) -> None:
        """Verify 30 km/h mountain climb enforces JTG D20 grade-length limits & relief grades."""
        from route_solver.horizontal_solver import DesignSpeedSpec, HorizontalPlan
        from route_solver.vertical_solver import VerticalSolver
        from route_solver.economics import evaluate_economics

        # 2400m straight mountain climb gaining 155m (~6.46% average -> requires relief grade under JTG 30 km/h)
        xs = np.linspace(0.0, 2400.0, 241)
        ys = np.linspace(-200.0, 200.0, 41)
        xx, yy = np.meshgrid(xs, ys)
        elev = 1000.0 + 0.068 * xx + 0.35 * yy  # steep cross-slope too
        tg = TerrainGrid(x_coords=xs, y_coords=ys, elevation=elev)

        spec = DesignSpeedSpec.from_speed(30)
        self.assertEqual(spec.design_speed_kmh, 30)
        self.assertEqual(spec.min_radius_m, 30.0)
        self.assertEqual(spec.max_grade_pct, 8.0)

        cl_xy = np.column_stack([xs, np.zeros_like(xs)])
        hp = HorizontalPlan(
            plan_id=1, design_speed_kmh=30, total_length_m=2400.0,
            min_radius_m=60.0, jds=[(0.0, 0.0), (2400.0, 0.0)],
            curves=[], centerline_xy=cl_xy, centerline_stations=xs, raw_corridor_xy=cl_xy,
        )
        vs = VerticalSolver(tg, spec=spec, road_width_m=7.5, max_slope_width_m=12.0)
        vp = vs.solve(hp)
        self.assertLessEqual(vp.max_grade_pct, 8.05)
        self.assertTrue(vp.jtg_audit["max_grade_ok"])
        self.assertTrue(vp.jtg_audit["grade_length_ok"], f"JTG violations: {vp.jtg_audit}")
        self.assertGreaterEqual(vp.jtg_audit["relief_grade_count"], 1)

        econ = evaluate_economics(hp, vp, road_width_m=7.5, max_slope_width_m=12.0)
        self.assertGreater(econ.total_investment_wan, 100.0)
        self.assertGreater(econ.cost_per_km_wan, 50.0)
        self.assertGreaterEqual(len(econ.mass_haul.blocks), 3)

    def test_terrain_loader_and_cost_field(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "t.npz"
            z = np.full((10, 10), 50.0)
            np.savez(p, Z=z, origin_x=100.0, origin_y=200.0, resolution=5.0)
            tg = load_terrain_npz(p)
            self.assertEqual(tg.nx, 10)
            self.assertEqual(tg.ny, 10)
            cf = CostField(tg, weights=CostWeights())
            self.assertTrue(np.all(np.isfinite(cf.cost_grid)))


if __name__ == "__main__":
    unittest.main()
