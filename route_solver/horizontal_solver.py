"""Horizontal corridor search (Weighted A*) and alignment curve fitting."""

from __future__ import annotations

import heapq
import math
from dataclasses import dataclass, field, asdict
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
from shapely.geometry import LineString

from .cost_field import CostField


@dataclass
class DesignSpeedSpec:
    """Geometric design standards keyed by design speed (km/h, JTG D20-2017)."""

    design_speed_kmh: int = 60
    min_radius_m: float = 125.0
    recommended_radius_m: float = 200.0
    min_spiral_m: float = 50.0
    min_jd_spacing_m: float = 120.0
    min_deflection_deg: float = 3.0
    max_grade_pct: float = 6.0
    min_grade_pct: float = 0.3
    min_grade_len_m: float = 150.0
    min_convex_rv_m: float = 1400.0
    min_concave_rv_m: float = 1000.0
    # JTG D20-2017 表8.2.5 纵坡坡长限制与缓和坡段标准
    relief_grade_max_pct: float = 3.0
    relief_grade_min_len_m: float = 80.0
    max_continuous_climb_avg_pct: float = 5.5
    tunnel_max_grade_pct: float = 3.0

    def max_grade_length_for(self, abs_grade_pct: float) -> Optional[float]:
        """Return JTG D20-2017 表8.2.5 maximum allowable slope length (m) for given grade (%)."""
        v = self.design_speed_kmh
        # JTG D20-2017 表8.2.5 (speed_kmh -> [(grade_threshold_pct, max_len_m), ...])
        if v <= 30:
            table = [(5.0, 900.0), (6.0, 700.0), (7.0, 500.0), (8.0, 300.0), (9.0, 200.0)]
        elif v <= 40:
            table = [(5.0, 900.0), (6.0, 700.0), (7.0, 500.0), (8.0, 300.0)]
        elif v <= 60:
            table = [(5.0, 800.0), (6.0, 600.0), (7.0, 400.0)]
        elif v <= 80:
            table = [(4.0, 1000.0), (5.0, 700.0), (6.0, 500.0)]
        else:
            table = [(3.0, 1100.0), (4.0, 800.0), (5.0, 600.0)]

        if abs_grade_pct < table[0][0]:
            return None
        for idx in range(len(table) - 1):
            g0, l0 = table[idx]
            g1, l1 = table[idx + 1]
            if g0 <= abs_grade_pct <= g1:
                t = (abs_grade_pct - g0) / max(1e-6, g1 - g0)
                return l0 + t * (l1 - l0)
        return table[-1][1]

    @classmethod
    def from_speed(cls, speed_kmh: int) -> "DesignSpeedSpec":
        presets = {
            20: cls(20, 15.0, 30.0, 20.0, 45.0, 3.0, 9.0, 0.3, 60.0, 100.0, 100.0, 3.0, 60.0, 5.5, 3.0),
            30: cls(30, 30.0, 65.0, 25.0, 65.0, 3.0, 8.0, 0.3, 100.0, 250.0, 250.0, 3.0, 60.0, 5.5, 3.0),
            40: cls(40, 60.0, 100.0, 35.0, 80.0, 3.0, 7.0, 0.3, 120.0, 600.0, 600.0, 3.0, 80.0, 5.5, 3.0),
            60: cls(60, 125.0, 200.0, 50.0, 120.0, 3.0, 6.0, 0.3, 150.0, 1400.0, 1000.0, 3.0, 100.0, 5.5, 3.0),
            80: cls(80, 250.0, 400.0, 70.0, 180.0, 2.5, 5.0, 0.3, 200.0, 3000.0, 2000.0, 3.0, 120.0, 5.0, 3.0),
            100: cls(100, 400.0, 700.0, 85.0, 250.0, 2.0, 4.0, 0.3, 250.0, 6500.0, 3000.0, 2.5, 150.0, 4.5, 3.0),
        }
        if speed_kmh in presets:
            return presets[speed_kmh]
        nearest = min(presets.keys(), key=lambda k: abs(k - speed_kmh))
        return presets[nearest]


@dataclass
class HorizontalCurveElement:
    """Fitted horizontal curve at an intersection point (JD)."""

    jd_index: int
    x: float
    y: float
    deflection_deg: float
    turn_direction: str  # "LEFT" or "RIGHT"
    radius_m: float
    spiral_in_m: float
    spiral_out_m: float
    tangent_len_m: float
    curve_len_m: float
    external_secant_m: float
    zh_station: float
    hz_station: float

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass
class HorizontalPlan:
    """Complete horizontal alignment solution."""

    plan_id: int
    design_speed_kmh: int
    total_length_m: float
    min_radius_m: float
    jds: List[Tuple[float, float]]
    curves: List[HorizontalCurveElement]
    centerline_xy: np.ndarray       # shape (M, 2)
    centerline_stations: np.ndarray # shape (M,)
    raw_corridor_xy: np.ndarray     # shape (K, 2)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "plan_id": self.plan_id,
            "design_speed_kmh": self.design_speed_kmh,
            "total_length_m": round(float(self.total_length_m), 3),
            "min_radius_m": round(float(self.min_radius_m), 3) if math.isfinite(self.min_radius_m) else None,
            "num_jds": len(self.jds),
            "jds": [{"index": i, "x": round(x, 3), "y": round(y, 3)} for i, (x, y) in enumerate(self.jds)],
            "curves": [c.to_dict() for c in self.curves],
        }


class HorizontalSolver:
    """Weighted A* corridor search + K-diverse routes + horizontal curve fitting."""

    # 16-connected grid neighbors (8-way + 8 knight moves for smooth 22.5° contour hugging)
    NEIGHBORS = [
        (-1, 0), (1, 0), (0, -1), (0, 1),
        (-1, -1), (-1, 1), (1, -1), (1, 1),
        (-2, -1), (-2, 1), (2, -1), (2, 1),
        (-1, -2), (-1, 2), (1, -2), (1, 2),
    ]

    def __init__(self, cost_field: CostField, spec: Optional[DesignSpeedSpec] = None) -> None:
        self.cf = cost_field
        self.spec = spec or DesignSpeedSpec.from_speed(60)

    def solve_k_plans(
        self,
        start_xy: Tuple[float, float],
        end_xy: Tuple[float, float],
        num_plans: int = 3,
        waypoints: Optional[List[Tuple[float, float]]] = None,
        sample_step_m: float = 10.0,
        corridor_repulsion_m: float = 120.0,
        repulsion_factor: float = 4.5,
    ) -> List[HorizontalPlan]:
        """Generate up to `num_plans` distinct horizontal alignment plans."""
        if waypoints is None and self.cf.control_points:
            waypoints = [(cp[0], cp[1]) for cp in self.cf.control_points]

        working_cost = self.cf.cost_grid.copy()
        plans: List[HorizontalPlan] = []
        xx, yy = np.meshgrid(self.cf.terrain.x_coords, self.cf.terrain.y_coords)

        for idx in range(1, num_plans + 1):
            raw_xy = self._route_with_waypoints(start_xy, end_xy, waypoints or [], working_cost)
            if raw_xy is None or len(raw_xy) < 2:
                break

            plan = self._fit_alignment(plan_id=idx, raw_xy=raw_xy, sample_step_m=sample_step_m)
            plans.append(plan)

            # Apply spatial repulsion around this corridor (excluding start/end neighborhoods)
            if idx < num_plans:
                line = LineString(raw_xy)
                # Vectorized distance to simplified polyline segments
                dist_grid = self._distance_to_polyline(xx, yy, plan.centerline_xy[:: max(1, len(plan.centerline_xy) // 40)])
                start_dist = np.hypot(xx - start_xy[0], yy - start_xy[1])
                end_dist = np.hypot(xx - end_xy[0], yy - end_xy[1])
                shield = np.minimum(start_dist, end_dist) > max(corridor_repulsion_m, 40.0)
                repel_mask = (dist_grid < corridor_repulsion_m) & shield & np.isfinite(working_cost)
                working_cost[repel_mask] *= repulsion_factor

        return plans

    def _route_with_waypoints(
        self,
        start_xy: Tuple[float, float],
        end_xy: Tuple[float, float],
        waypoints: List[Tuple[float, float]],
        cost_grid: np.ndarray,
    ) -> Optional[np.ndarray]:
        points = [start_xy] + list(waypoints) + [end_xy]
        combined: List[Tuple[float, float]] = []
        for i in range(len(points) - 1):
            seg = self._astar(points[i], points[i + 1], cost_grid)
            if seg is None:
                return None
            if i > 0:
                seg = seg[1:]
            combined.extend(seg)
        return np.asarray(combined, dtype=np.float64)

    def _astar(
        self,
        start_xy: Tuple[float, float],
        end_xy: Tuple[float, float],
        cost_grid: np.ndarray,
        heuristic_weight: float = 1.15,
    ) -> Optional[List[Tuple[float, float]]]:
        t = self.cf.terrain
        sr, sc = t.world_to_grid(start_xy[0], start_xy[1])
        gr, gc = t.world_to_grid(end_xy[0], end_xy[1])

        if not np.isfinite(cost_grid[sr, sc]) or not np.isfinite(cost_grid[gr, gc]):
            return None

        ny, nx = cost_grid.shape
        dx = abs(t.dx)
        dy = abs(t.dy)
        step_dists = [math.hypot(dc * dx, dr * dy) for dr, dc in self.NEIGHBORS]

        finite_vals = cost_grid[np.isfinite(cost_grid)]
        min_unit_cost = float(np.min(finite_vals)) if finite_vals.size > 0 else 1.0

        def h_func(r: int, c: int) -> float:
            return math.hypot((c - gc) * dx, (r - gr) * dy) * min_unit_cost * heuristic_weight

        g_score = np.full((ny, nx), np.inf, dtype=np.float64)
        g_score[sr, sc] = 0.0
        came_from_r = np.full((ny, nx), -1, dtype=np.int32)
        came_from_c = np.full((ny, nx), -1, dtype=np.int32)
        closed = np.zeros((ny, nx), dtype=bool)

        pq: List[Tuple[float, int, int]] = [(h_func(sr, sc), sr, sc)]

        elev_grid = t.elevation
        max_g = max(self.spec.max_grade_pct, 1.0)

        while pq:
            _, r, c = heapq.heappop(pq)
            if closed[r, c]:
                continue
            if r == gr and c == gc:
                # Reconstruct path
                path: List[Tuple[float, float]] = []
                cr, cc = r, c
                while cr != -1 and cc != -1:
                    path.append(t.grid_to_world(cr, cc))
                    pr, pc = int(came_from_r[cr, cc]), int(came_from_c[cr, cc])
                    cr, cc = pr, pc
                path.reverse()
                # Snap exact endpoints
                path[0] = start_xy
                path[-1] = end_xy
                return path

            closed[r, c] = True
            cur_g = g_score[r, c]
            cur_cost = cost_grid[r, c]
            cur_z = elev_grid[r, c]

            for idx, (dr, dc) in enumerate(self.NEIGHBORS):
                nr, nc = r + dr, c + dc
                if 0 <= nr < ny and 0 <= nc < nx and not closed[nr, nc]:
                    ncost = cost_grid[nr, nc]
                    if not np.isfinite(ncost):
                        continue
                    dist = step_dists[idx]
                    edge_cost = 0.5 * (cur_cost + ncost) * dist
                    # Directional longitudinal grade penalty: encourages contour-hugging
                    # and mountain switchback climbing when |dZ|/ds exceeds max design grade
                    nz = elev_grid[nr, nc]
                    if math.isfinite(cur_z) and math.isfinite(nz):
                        step_grade_pct = abs(nz - cur_z) / dist * 100.0
                        if step_grade_pct > max_g:
                            excess = (step_grade_pct - max_g) / max_g
                            edge_cost *= 1.0 + 18.0 * excess * excess
                    tentative_g = cur_g + edge_cost
                    if tentative_g < g_score[nr, nc]:
                        g_score[nr, nc] = tentative_g
                        came_from_r[nr, nc] = r
                        came_from_c[nr, nc] = c
                        f_val = tentative_g + h_func(nr, nc)
                        heapq.heappush(pq, (f_val, nr, nc))

        return None

    def _fit_alignment(self, plan_id: int, raw_xy: np.ndarray, sample_step_m: float) -> HorizontalPlan:
        """Extract JD points via Douglas-Peucker and fit non-overlapping horizontal curves."""
        tol = max(self.cf.terrain.resolution * 2.5, 12.0)
        simplified = LineString(raw_xy).simplify(tol, preserve_topology=True)
        jds = list(simplified.coords)

        # Clean up short segments and near-collinear JDs
        jds = self._clean_jds(jds)

        # Fit curves at interior JDs
        curves = self._compute_curves(jds)

        # Sample dense centerline along tangents, spirals, and circular arcs
        cl_xy, cl_sta = self._sample_centerline(jds, curves, sample_step_m)
        total_len = float(cl_sta[-1]) if len(cl_sta) > 0 else 0.0
        min_r = min((c.radius_m for c in curves), default=math.inf)

        return HorizontalPlan(
            plan_id=plan_id,
            design_speed_kmh=self.spec.design_speed_kmh,
            total_length_m=total_len,
            min_radius_m=min_r,
            jds=jds,
            curves=curves,
            centerline_xy=cl_xy,
            centerline_stations=cl_sta,
            raw_corridor_xy=raw_xy,
        )

    def _clean_jds(self, jds: List[Tuple[float, float]]) -> List[Tuple[float, float]]:
        if len(jds) <= 2:
            return jds

        pts = list(jds)
        changed = True
        while changed and len(pts) > 2:
            changed = False
            # Remove near-collinear vertices
            for i in range(1, len(pts) - 1):
                ang = self._deflection_angle(pts[i - 1], pts[i], pts[i + 1])[0]
                if math.degrees(ang) < self.spec.min_deflection_deg:
                    pts.pop(i)
                    changed = True
                    break
            if changed:
                continue

            # Remove overly close JDs
            for i in range(1, len(pts) - 1):
                d_prev = math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1])
                d_next = math.hypot(pts[i + 1][0] - pts[i][0], pts[i + 1][1] - pts[i][1])
                if min(d_prev, d_next) < self.spec.min_jd_spacing_m and len(pts) > 2:
                    pts.pop(i)
                    changed = True
                    break
        return pts

    def _compute_curves(self, jds: List[Tuple[float, float]]) -> List[HorizontalCurveElement]:
        n = len(jds)
        if n <= 2:
            return []

        seg_lens = [
            math.hypot(jds[i + 1][0] - jds[i][0], jds[i + 1][1] - jds[i][1])
            for i in range(n - 1)
        ]

        # Initial desired R and Ls for each interior JD
        radii = [self.spec.recommended_radius_m] * (n - 2)
        spirals = [self.spec.min_spiral_m] * (n - 2)
        alphas: List[float] = []
        turns: List[str] = []

        for i in range(1, n - 1):
            alpha, turn = self._deflection_angle(jds[i - 1], jds[i], jds[i + 1])
            alphas.append(alpha)
            turns.append(turn)

        def calc_tangent(r: float, ls: float, alpha: float) -> Tuple[float, float, float, float]:
            if alpha <= 1e-5:
                return 0.0, 0.0, 0.0, ls
            # Ensure 2 * beta < alpha
            ls_eff = min(ls, r * alpha * 0.45)
            p = (ls_eff ** 2) / (24.0 * r)
            q = 0.5 * ls_eff - (ls_eff ** 3) / (240.0 * r ** 2)
            t_len = (r + p) * math.tan(0.5 * alpha) + q
            ext = (r + p) / math.cos(0.5 * alpha) - r
            arc = r * (alpha - ls_eff / r) + 2.0 * ls_eff
            return t_len, ext, arc, ls_eff

        # Enforce non-overlapping tangents across every segment
        for k in range(n - 2):
            avail_left = seg_lens[k] * (0.95 if k == 0 else 0.48)
            avail_right = seg_lens[k + 1] * (0.95 if k == n - 3 else 0.48)
            t_budget = min(avail_left, avail_right)

            r = radii[k]
            ls = spirals[k]
            t_len, _, _, ls_eff = calc_tangent(r, ls, alphas[k])
            if t_len > t_budget and t_len > 1e-6:
                scale = t_budget / t_len
                r = max(self.spec.min_radius_m * 0.5, r * scale)
                ls = max(0.0, ls_eff * scale)
                # Re-verify
                t_len2, _, _, ls_eff2 = calc_tangent(r, ls, alphas[k])
                if t_len2 > t_budget and t_len2 > 1e-6:
                    scale2 = t_budget / t_len2
                    r *= scale2
                    ls = 0.0 if scale2 < 0.7 else ls_eff2 * scale2
            radii[k] = r
            spirals[k] = ls

        # Compute cumulative stations along alignment
        curves: List[HorizontalCurveElement] = []
        cur_station = 0.0
        prev_t = 0.0
        for k in range(n - 2):
            jd_idx = k + 1
            alpha = alphas[k]
            r = radii[k]
            ls_req = spirals[k]
            t_len, ext, c_len, ls_eff = calc_tangent(r, ls_req, alpha)

            tangent_run = seg_lens[k] - prev_t - t_len
            cur_station += max(0.0, tangent_run)
            zh_sta = cur_station
            hz_sta = zh_sta + c_len
            cur_station = hz_sta
            prev_t = t_len

            curves.append(
                HorizontalCurveElement(
                    jd_index=jd_idx,
                    x=round(jds[jd_idx][0], 3),
                    y=round(jds[jd_idx][1], 3),
                    deflection_deg=round(math.degrees(alpha), 4),
                    turn_direction=turns[k],
                    radius_m=round(r, 3),
                    spiral_in_m=round(ls_eff, 3),
                    spiral_out_m=round(ls_eff, 3),
                    tangent_len_m=round(t_len, 3),
                    curve_len_m=round(c_len, 3),
                    external_secant_m=round(ext, 3),
                    zh_station=round(zh_sta, 3),
                    hz_station=round(hz_sta, 3),
                )
            )
        return curves

    def _sample_centerline(
        self,
        jds: List[Tuple[float, float]],
        curves: List[HorizontalCurveElement],
        step_m: float,
    ) -> Tuple[np.ndarray, np.ndarray]:
        """Sample dense (x, y) and stationing along fitted tangents and curves."""
        if len(jds) < 2:
            return np.asarray(jds, dtype=np.float64), np.zeros(len(jds), dtype=np.float64)

        pts_out: List[Tuple[float, float]] = []
        if not curves:
            p0, p1 = jds[0], jds[-1]
            dist = math.hypot(p1[0] - p0[0], p1[1] - p0[1])
            n_pts = max(2, int(math.ceil(dist / step_m)) + 1)
            xs = np.linspace(p0[0], p1[0], n_pts)
            ys = np.linspace(p0[1], p1[1], n_pts)
            sta = np.linspace(0.0, dist, n_pts)
            return np.column_stack([xs, ys]), sta

        # Build sequence of tangent + curve points
        prev_pt = jds[0]
        for k, curve in enumerate(curves):
            jd_pt = jds[k + 1]
            nxt_pt = jds[k + 2]

            u_in = self._unit_vec(jds[k], jd_pt)
            u_out = self._unit_vec(jd_pt, nxt_pt)

            zh_pt = (jd_pt[0] - u_in[0] * curve.tangent_len_m, jd_pt[1] - u_in[1] * curve.tangent_len_m)
            hz_pt = (jd_pt[0] + u_out[0] * curve.tangent_len_m, jd_pt[1] + u_out[1] * curve.tangent_len_m)

            # Tangent segment from prev_pt to zh_pt
            self._append_segment(pts_out, prev_pt, zh_pt, step_m)

            # Curve segment from zh_pt to hz_pt (quadratic Bezier / circular arc approximation)
            self._append_curve(pts_out, zh_pt, jd_pt, hz_pt, curve, u_in, u_out, step_m)
            prev_pt = hz_pt

        # Final tangent to end point
        self._append_segment(pts_out, prev_pt, jds[-1], step_m, include_end=True)

        arr = np.asarray(pts_out, dtype=np.float64)
        diffs = np.hypot(np.diff(arr[:, 0]), np.diff(arr[:, 1]))
        sta = np.concatenate([[0.0], np.cumsum(diffs)])
        return arr, sta

    @staticmethod
    def _append_segment(
        out: List[Tuple[float, float]],
        p0: Tuple[float, float],
        p1: Tuple[float, float],
        step_m: float,
        include_end: bool = False,
    ) -> None:
        dist = math.hypot(p1[0] - p0[0], p1[1] - p0[1])
        if dist < 1e-6:
            if include_end and (not out or math.hypot(out[-1][0] - p1[0], out[-1][1] - p1[1]) > 1e-6):
                out.append(p1)
            return
        n = max(1, int(math.ceil(dist / step_m)))
        for i in range(n):
            f = i / n
            out.append((p0[0] + f * (p1[0] - p0[0]), p0[1] + f * (p1[1] - p0[1])))
        if include_end:
            out.append(p1)

    @staticmethod
    def _append_curve(
        out: List[Tuple[float, float]],
        zh: Tuple[float, float],
        jd: Tuple[float, float],
        hz: Tuple[float, float],
        curve: HorizontalCurveElement,
        u_in: Tuple[float, float],
        u_out: Tuple[float, float],
        step_m: float,
    ) -> None:
        n = max(4, int(math.ceil(curve.curve_len_m / step_m)))
        # Smooth cubic Hermite / Bezier blend respecting entry/exit tangents
        handle = curve.tangent_len_m * 0.55
        c1 = (zh[0] + u_in[0] * handle, zh[1] + u_in[1] * handle)
        c2 = (hz[0] - u_out[0] * handle, hz[1] - u_out[1] * handle)
        for i in range(n):
            t = i / n
            mt = 1.0 - t
            x = (mt ** 3) * zh[0] + 3 * (mt ** 2) * t * c1[0] + 3 * mt * (t ** 2) * c2[0] + (t ** 3) * hz[0]
            y = (mt ** 3) * zh[1] + 3 * (mt ** 2) * t * c1[1] + 3 * mt * (t ** 2) * c2[1] + (t ** 3) * hz[1]
            out.append((x, y))

    @staticmethod
    def _unit_vec(p0: Tuple[float, float], p1: Tuple[float, float]) -> Tuple[float, float]:
        dx = p1[0] - p0[0]
        dy = p1[1] - p0[1]
        d = math.hypot(dx, dy)
        if d < 1e-9:
            return (1.0, 0.0)
        return (dx / d, dy / d)

    @staticmethod
    def _deflection_angle(
        p0: Tuple[float, float], p1: Tuple[float, float], p2: Tuple[float, float]
    ) -> Tuple[float, str]:
        v1 = (p1[0] - p0[0], p1[1] - p0[1])
        v2 = (p2[0] - p1[0], p2[1] - p1[1])
        n1 = math.hypot(*v1)
        n2 = math.hypot(*v2)
        if n1 < 1e-9 or n2 < 1e-9:
            return 0.0, "RIGHT"
        dot = max(-1.0, min(1.0, (v1[0] * v2[0] + v1[1] * v2[1]) / (n1 * n2)))
        angle = math.acos(dot)
        cross = v1[0] * v2[1] - v1[1] * v2[0]
        turn = "LEFT" if cross > 0 else "RIGHT"
        return angle, turn

    @staticmethod
    def _distance_to_polyline(xx: np.ndarray, yy: np.ndarray, poly_pts: np.ndarray) -> np.ndarray:
        """Approximate vectorized distance from grid points to polyline vertices/segments."""
        min_d2 = np.full(xx.shape, np.inf, dtype=np.float64)
        for i in range(len(poly_pts) - 1):
            ax, ay = poly_pts[i]
            bx, by = poly_pts[i + 1]
            abx, aby = bx - ax, by - ay
            ab2 = abx * abx + aby * aby
            if ab2 < 1e-9:
                d2 = (xx - ax) ** 2 + (yy - ay) ** 2
            else:
                t = np.clip(((xx - ax) * abx + (yy - ay) * aby) / ab2, 0.0, 1.0)
                px = ax + t * abx
                py = ay + t * aby
                d2 = (xx - px) ** 2 + (yy - py) ** 2
            min_d2 = np.minimum(min_d2, d2)
        return np.sqrt(min_d2)
