"""Design-institute vertical profile optimizer (JTG D20-2017 compliant LP + relief grades + parabolic curves)."""

from __future__ import annotations

import math
from dataclasses import asdict, dataclass, field
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
from scipy.interpolate import RegularGridInterpolator
from scipy.optimize import linprog

from .cost_field import TerrainGrid
from .horizontal_solver import DesignSpeedSpec, HorizontalPlan

_trapz = getattr(np, "trapezoid", None) or np.trapz


@dataclass
class PVIElement:
    """Point of Vertical Intersection (变坡点)."""

    index: int
    station_m: float
    elevation_m: float
    grade_in_pct: Optional[float]
    grade_out_pct: Optional[float]
    curve_radius_m: float
    curve_length_m: float

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass
class VerticalPlan:
    """Complete vertical profile solution for a horizontal alignment."""

    plan_id: int
    stations_m: np.ndarray
    ground_elev_m: np.ndarray
    design_elev_m: np.ndarray
    pvis: List[PVIElement]
    max_grade_pct: float
    min_grade_pct: float
    total_cut_m3: float
    total_fill_m3: float
    max_cut_m: float
    max_fill_m: float
    jtg_audit: Dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "plan_id": self.plan_id,
            "num_pvis": len(self.pvis),
            "pvis": [p.to_dict() for p in self.pvis],
            "max_grade_pct": round(float(self.max_grade_pct), 3),
            "min_grade_pct": round(float(self.min_grade_pct), 3),
            "total_cut_m3": round(float(self.total_cut_m3), 1),
            "total_fill_m3": round(float(self.total_fill_m3), 1),
            "max_cut_m": round(float(self.max_cut_m), 3),
            "max_fill_m": round(float(self.max_fill_m), 3),
            "jtg_audit": self.jtg_audit,
        }


class VerticalSolver:
    """JTG D20-2017 design-institute vertical grade optimizer.

    Features:
    - Terrain inflection-point PVI placement (ridges, valleys, saddles)
    - Cross-slope half-cut/half-fill bench awareness
    - Dense-fidelity piecewise-linear convex LP (minimizes earthwork + deep cut/fill penalties + grade jumps)
    - JTG D20-2017 表8.2.5 maximum grade-length enforcement with automatic relief-grade insertion (<=3.0%)
    - Continuous 3 km climb average grade <= 5.5% constraint
    - Tunnel section (cut > threshold) grade cap <= 3.0%
    - Non-overlapping symmetric parabolic vertical curves & horizontal-vertical coordination (平包竖) audit
    """

    def __init__(
        self,
        terrain: TerrainGrid,
        spec: Optional[DesignSpeedSpec] = None,
        road_width_m: float = 12.0,
        side_slope: float = 1.5,
        cut_slope: float = 0.8,
        max_slope_width_m: float = 12.0,
        tunnel_cut_threshold_m: float = 22.0,
    ) -> None:
        self.terrain = terrain
        self.spec = spec or DesignSpeedSpec.from_speed(60)
        self.road_width_m = road_width_m
        self.side_slope = side_slope
        self.cut_slope = cut_slope
        self.max_slope_width_m = max_slope_width_m
        self.tunnel_cut_threshold_m = tunnel_cut_threshold_m

        elev = terrain.elevation.copy()
        nan_mask = np.isnan(elev)
        if np.any(nan_mask):
            valid = elev[~nan_mask]
            if valid.size > 0:
                try:
                    from scipy.ndimage import distance_transform_edt
                    _, nearest_idx = distance_transform_edt(nan_mask, return_indices=True)
                    elev[nan_mask] = elev[tuple(nearest_idx[:, nan_mask])]
                except Exception:
                    elev[nan_mask] = float(np.median(valid))
            else:
                elev[nan_mask] = 0.0

        self._interpolator = RegularGridInterpolator(
            (terrain.y_coords, terrain.x_coords),
            elev,
            method="linear",
            bounds_error=False,
            fill_value=None,
        )

    def sample_ground_profile(self, centerline_xy: np.ndarray) -> np.ndarray:
        """Sample original ground elevation along (x, y) centerline."""
        pts_yx = np.column_stack([centerline_xy[:, 1], centerline_xy[:, 0]])
        return np.asarray(self._interpolator(pts_yx), dtype=np.float64)

    def _compute_target_profile(
        self, centerline_xy: np.ndarray, ground_z: np.ndarray
    ) -> np.ndarray:
        """Compute cross-slope-aware target elevation (encourages safe half-cut/half-fill on steep hillsides)."""
        n = len(centerline_xy)
        if n < 3:
            return ground_z.copy()

        # Tangent vectors along centerline
        dx = np.gradient(centerline_xy[:, 0])
        dy = np.gradient(centerline_xy[:, 1])
        norm = np.hypot(dx, dy)
        norm = np.where(norm < 1e-6, 1.0, norm)
        # Left unit normal (-dy, dx) / norm
        nx_l = -dy / norm
        ny_l = dx / norm

        half_w = max(3.5, 0.5 * self.road_width_m)
        left_xy = np.column_stack([centerline_xy[:, 0] + nx_l * half_w, centerline_xy[:, 1] + ny_l * half_w])
        right_xy = np.column_stack([centerline_xy[:, 0] - nx_l * half_w, centerline_xy[:, 1] - ny_l * half_w])
        z_left = self.sample_ground_profile(left_xy)
        z_right = self.sample_ground_profile(right_xy)
        cross_slope = np.abs(z_left - z_right) / (2.0 * half_w)

        # On steep mountain cross-slopes (> 30%), shift target slightly down into the hill
        # so the road sits in a stable half-cut/half-fill bench instead of huge downhill fill
        bench_shift = np.where(cross_slope > 0.30, -np.minimum(1.8, 0.25 * (cross_slope - 0.30) * half_w), 0.0)
        return ground_z + bench_shift

    @staticmethod
    def _rdp_1d(stations: np.ndarray, vals: np.ndarray, tol: float) -> List[int]:
        """Recursive Douglas-Peucker on 1D elevation profile to find natural terrain inflection points."""
        n = len(stations)
        if n <= 2:
            return list(range(n))

        stack = [(0, n - 1)]
        keep = {0, n - 1}
        while stack:
            i0, i1 = stack.pop()
            if i1 - i0 <= 1:
                continue
            s0, s1 = stations[i0], stations[i1]
            v0, v1 = vals[i0], vals[i1]
            span = max(1e-6, s1 - s0)
            interp = v0 + (v1 - v0) * ((stations[i0 + 1 : i1] - s0) / span)
            err = np.abs(vals[i0 + 1 : i1] - interp)
            rel_max = int(np.argmax(err))
            if float(err[rel_max]) > tol:
                mid = i0 + 1 + rel_max
                keep.add(mid)
                stack.append((i0, mid))
                stack.append((mid, i1))
        return sorted(keep)

    def _select_pvi_stations(
        self,
        stations: np.ndarray,
        ground_z: np.ndarray,
        control_elevations: Optional[List[Tuple[float, float]]] = None,
    ) -> np.ndarray:
        """Select adaptive terrain-following PVI stations respecting min_grade_len_m."""
        total_len = float(stations[-1])
        min_len = max(self.spec.min_grade_len_m, 60.0)
        if total_len <= 1.8 * min_len:
            return np.array([0.0, total_len], dtype=np.float64)

        # Light moving-average smoothing to ignore micro-noise
        win = max(3, min(15, len(ground_z) // 25))
        if win % 2 == 0:
            win += 1
        kernel = np.ones(win, dtype=np.float64) / win
        pad = win // 2
        g_smooth = np.convolve(np.pad(ground_z, (pad, pad), mode="edge"), kernel, mode="valid")

        # Adaptive RDP vertical tolerance
        tol = 3.5 if total_len > 4000.0 else 1.8
        idx_keep = self._rdp_1d(stations, g_smooth, tol=tol)
        cand = [float(stations[i]) for i in idx_keep]

        # Add explicit control stations
        if control_elevations:
            for c_sta, _ in control_elevations:
                if 0.0 < c_sta < total_len:
                    cand.append(float(c_sta))

        # Ensure maximum segment length isn't too long (<= 450m for long mountain corridors)
        max_seg = min(450.0, max(min_len * 2.2, total_len / 14.0))
        cand = sorted(set(cand))
        refined: List[float] = [cand[0]]
        for s_next in cand[1:]:
            s_prev = refined[-1]
            gap = s_next - s_prev
            if gap > max_seg:
                n_sub = int(math.ceil(gap / max_seg))
                for m in range(1, n_sub):
                    refined.append(s_prev + gap * (m / n_sub))
            refined.append(s_next)

        # Enforce minimum spacing min_len (preserving endpoints & control points)
        ctrl_set = {float(c[0]) for c in (control_elevations or [])}
        merged: List[float] = [0.0]
        for s in refined[1:-1]:
            if s - merged[-1] >= min_len and (total_len - s) >= min_len:
                merged.append(round(s / 10.0) * 10.0 if s not in ctrl_set else s)
        merged.append(total_len)

        # Cap total PVIs to keep LP super fast & clean (<= 60 segments)
        while len(merged) > 62:
            # Drop interior point with smallest local elevation deviation
            best_drop = -1
            best_err = math.inf
            for k in range(1, len(merged) - 1):
                if merged[k] in ctrl_set:
                    continue
                s0, s1, s2 = merged[k - 1], merged[k], merged[k + 1]
                z0 = float(np.interp(s0, stations, g_smooth))
                z1 = float(np.interp(s1, stations, g_smooth))
                z2 = float(np.interp(s2, stations, g_smooth))
                z_lin = z0 + (z2 - z0) * ((s1 - s0) / max(1e-6, s2 - s0))
                err = abs(z1 - z_lin)
                if err < best_err:
                    best_err = err
                    best_drop = k
            if best_drop < 0:
                break
            merged.pop(best_drop)

        return np.asarray(sorted(set(merged)), dtype=np.float64)

    def _solve_vertical_lp(
        self,
        pvi_stations: np.ndarray,
        seg_max_grades_pct: List[float],
        sample_stations: np.ndarray,
        sample_targets: np.ndarray,
        locked: Dict[int, float],
        max_3km_avg_pct: Optional[float] = None,
    ) -> Optional[np.ndarray]:
        """Exact convex LP minimizing dense tiered cut/fill earthwork + grade-change smoothness."""
        n_p = len(pvi_stations)
        n_m = len(sample_stations)
        n_u = max(0, n_p - 2)  # grade change slacks |g_k - g_{k-1}|

        # Variable layout:
        #   [0 .. n_p-1]               : z_k (PVI elevations)
        #   [n_p .. n_p+n_m-1]         : c_m >= max(0, target_m - z(s_m))  (cut depth)
        #   [n_p+n_m .. n_p+2*n_m-1]   : f_m >= max(0, z(s_m) - target_m)  (fill height)
        #   [n_p+2*n_m .. n_p+3*n_m-1] : e1_m >= max(0, |z-target| - 6.0)  (medium cut/fill excess)
        #   [n_p+3*n_m .. n_p+4*n_m-1] : e2_m >= max(0, |z-target| - 12.0) (deep cut/fill excess)
        #   [n_p+4*n_m .. end]         : u_k >= |g_k - g_{k-1}|            (grade change smoothness)
        off_c = n_p
        off_f = n_p + n_m
        off_e1 = n_p + 2 * n_m
        off_e2 = n_p + 3 * n_m
        off_u = n_p + 4 * n_m
        n_vars = off_u + n_u

        # Station integration weights
        w_m = np.ones(n_m, dtype=np.float64)
        if n_m >= 2:
            ds_m = np.diff(sample_stations)
            w_m[0] = 0.5 * ds_m[0]
            w_m[-1] = 0.5 * ds_m[-1]
            if n_m > 2:
                w_m[1:-1] = 0.5 * (ds_m[:-1] + ds_m[1:])

        c_obj = np.zeros(n_vars, dtype=np.float64)
        # Fill slightly pricier than cut on steep mountain slopes (avoids huge fill walls)
        c_obj[off_c : off_c + n_m] = w_m * 1.00
        c_obj[off_f : off_f + n_m] = w_m * 1.12
        c_obj[off_e1 : off_e1 + n_m] = w_m * 3.20
        c_obj[off_e2 : off_e2 + n_m] = w_m * 9.00
        if n_u > 0:
            c_obj[off_u : off_u + n_u] = 75.0  # gentle grade smoothness without detaching from mountainside

        # Sparse COO triplets for A_ub
        from scipy.sparse import coo_matrix

        rows: List[int] = []
        cols: List[int] = []
        vals: List[float] = []
        rhs: List[float] = []
        row_idx = 0

        def add_row(entries: List[Tuple[int, float]], b_val: float) -> None:
            nonlocal row_idx
            for col_i, v in entries:
                rows.append(row_idx)
                cols.append(col_i)
                vals.append(v)
            rhs.append(b_val)
            row_idx += 1

        # 1) Sampled cut/fill & tiered excess constraints
        seg_indices = np.searchsorted(pvi_stations, sample_stations, side="right") - 1
        seg_indices = np.clip(seg_indices, 0, n_p - 2)
        for m in range(n_m):
            k = int(seg_indices[m])
            s0, s1 = pvi_stations[k], pvi_stations[k + 1]
            alpha = float((sample_stations[m] - s0) / max(1e-6, s1 - s0))
            w0 = 1.0 - alpha
            w1 = alpha
            tg = float(sample_targets[m])

            # c_m >= tg - (w0*z_k + w1*z_{k+1})  =>  -w0*z_k - w1*z_{k+1} - c_m <= -tg
            add_row([(k, -w0), (k + 1, -w1), (off_c + m, -1.0)], -tg)
            # f_m >= (w0*z_k + w1*z_{k+1}) - tg  =>   w0*z_k + w1*z_{k+1} - f_m <= tg
            add_row([(k, w0), (k + 1, w1), (off_f + m, -1.0)], tg)
            # e1_m >= c_m + f_m - 6.0  => c_m + f_m - e1_m <= 6.0
            add_row([(off_c + m, 1.0), (off_f + m, 1.0), (off_e1 + m, -1.0)], 6.0)
            # e2_m >= c_m + f_m - 12.0 => c_m + f_m - e2_m <= 12.0
            add_row([(off_c + m, 1.0), (off_f + m, 1.0), (off_e2 + m, -1.0)], 12.0)

        # 2) Strict per-segment grade bounds: |z_{k+1} - z_k| <= (g_max_k / 100) * ds_k
        for k in range(n_p - 1):
            ds_k = float(pvi_stations[k + 1] - pvi_stations[k])
            lim = (seg_max_grades_pct[k] / 100.0) * ds_k - 1e-4
            lim = max(0.0, lim)
            add_row([(k + 1, 1.0), (k, -1.0)], lim)
            add_row([(k, 1.0), (k + 1, -1.0)], lim)

        # 3) Grade-change smoothness u_k >= |(z_{k+1}-z_k)/ds_k - (z_k-z_{k-1})/ds_{k-1}| * 100
        for k in range(1, n_p - 1):
            inv_prev = 100.0 / max(1e-3, float(pvi_stations[k] - pvi_stations[k - 1]))
            inv_next = 100.0 / max(1e-3, float(pvi_stations[k + 1] - pvi_stations[k]))
            u_col = off_u + (k - 1)
            # g_next - g_prev - u_k <= 0
            add_row([(k + 1, inv_next), (k, -(inv_next + inv_prev)), (k - 1, inv_prev), (u_col, -1.0)], 0.0)
            # g_prev - g_next - u_k <= 0
            add_row([(k + 1, -inv_next), (k, inv_next + inv_prev), (k - 1, -inv_prev), (u_col, -1.0)], 0.0)

        # 4) Continuous 3 km climb average grade limit (JTG D20-2017: <= 5.5%)
        # Enforce across all PVI spans >= 2600m with a tight 0.12% buffer so every exact 3000m sliding window passes
        if max_3km_avg_pct is not None and float(pvi_stations[-1] - pvi_stations[0]) >= 3000.0:
            slope_3k = max(0.01, (max_3km_avg_pct - 0.12) / 100.0)
            for i in range(n_p - 1):
                for j in range(i + 2, n_p):
                    span = float(pvi_stations[j] - pvi_stations[i])
                    if span >= 2600.0:
                        lim_3k = slope_3k * span
                        add_row([(j, 1.0), (i, -1.0)], lim_3k)
                        add_row([(i, 1.0), (j, -1.0)], lim_3k)
                        if span >= 3200.0:
                            break

        A_ub = coo_matrix((vals, (rows, cols)), shape=(row_idx, n_vars)).tocsc()
        b_ub = np.asarray(rhs, dtype=np.float64)

        # Equality constraints for locked endpoints & control elevations
        eq_rows, eq_cols, eq_vals, eq_rhs = [], [], [], []
        for r_i, (idx_lock, val_lock) in enumerate(sorted(locked.items())):
            eq_rows.append(r_i)
            eq_cols.append(idx_lock)
            eq_vals.append(1.0)
            eq_rhs.append(float(val_lock))
        A_eq = coo_matrix((eq_vals, (eq_rows, eq_cols)), shape=(len(eq_rhs), n_vars)).tocsc() if eq_rhs else None
        b_eq = np.asarray(eq_rhs, dtype=np.float64) if eq_rhs else None

        bounds = [(None, None)] * n_p + [(0.0, None)] * (n_vars - n_p)
        res = linprog(c_obj, A_ub=A_ub, b_ub=b_ub, A_eq=A_eq, b_eq=b_eq, bounds=bounds, method="highs")
        if res.success and res.x is not None:
            return res.x[:n_p].copy()
        return None

    def _insert_relief_grades_if_needed(
        self,
        pvi_stations: np.ndarray,
        pvi_elevs: np.ndarray,
        seg_max_grades: List[float],
    ) -> Tuple[np.ndarray, List[float], bool]:
        """Enforce JTG D20-2017 表8.2.5 max grade-length by splitting overly long steep climbs with a relief grade (<=3%)."""
        n = len(pvi_stations)
        grades = [
            ((pvi_elevs[k + 1] - pvi_elevs[k]) / max(1e-3, pvi_stations[k + 1] - pvi_stations[k])) * 100.0
            for k in range(n - 1)
        ]

        # Find contiguous runs of same-sign steep grades (> 5.0%) whose length exceeds JTG limit
        relief_len = max(self.spec.relief_grade_min_len_m, 60.0)
        new_sta = list(pvi_stations)
        new_max = list(seg_max_grades)

        # Track cumulative length & grade-weighted run
        run_start = -1
        run_sign = 0
        run_len = 0.0
        run_max_abs_g = 0.0

        for k in range(n - 1):
            g = grades[k]
            abs_g = abs(g)
            seg_l = float(pvi_stations[k + 1] - pvi_stations[k])
            sign = 1 if g > 0 else (-1 if g < 0 else 0)

            # A relief grade (<= 3.05% and >= relief_len * 0.9) resets the steep run
            if abs_g <= self.spec.relief_grade_max_pct + 0.08 and seg_l >= relief_len * 0.85:
                run_start = -1
                run_sign = 0
                run_len = 0.0
                run_max_abs_g = 0.0
                continue

            if abs_g >= 5.05:
                if sign == run_sign and run_start >= 0:
                    run_len += seg_l
                    run_max_abs_g = max(run_max_abs_g, abs_g)
                else:
                    run_start = k
                    run_sign = sign
                    run_len = seg_l
                    run_max_abs_g = abs_g

                limit = self.spec.max_grade_length_for(run_max_abs_g)
                if limit is not None and run_len > limit + 5.0:
                    # Insert a relief grade segment near the end of this allowable span
                    target_s = float(pvi_stations[run_start]) + max(self.spec.min_grade_len_m, limit * 0.90)
                    s_r0 = target_s
                    s_r1 = target_s + relief_len
                    # Find which existing segment contains s_r0..s_r1
                    seg_idx = int(np.searchsorted(new_sta, s_r0, side="right")) - 1
                    seg_idx = max(0, min(len(new_sta) - 2, seg_idx))
                    seg_a, seg_b = new_sta[seg_idx], new_sta[seg_idx + 1]
                    # If the whole segment is already relief-sized, convert it directly; otherwise split cleanly
                    if (seg_b - seg_a) <= relief_len * 1.25 and new_max[seg_idx] > self.spec.relief_grade_max_pct:
                        new_max[seg_idx] = self.spec.relief_grade_max_pct
                        return np.asarray(new_sta, dtype=np.float64), new_max, True
                    # Otherwise split this segment cleanly into [seg_a, s_r0, s_r1, seg_b]
                    min_stub = 25.0
                    s_r0 = max(seg_a + min_stub, min(s_r0, seg_b - relief_len - min_stub))
                    s_r1 = s_r0 + relief_len
                    if s_r1 <= seg_b - min_stub:
                        orig_cap = new_max[seg_idx]
                        new_sta = new_sta[: seg_idx + 1] + [round(s_r0, 1), round(s_r1, 1)] + new_sta[seg_idx + 1 :]
                        new_max = new_max[:seg_idx] + [orig_cap, self.spec.relief_grade_max_pct, orig_cap] + new_max[seg_idx + 1 :]
                        return np.asarray(new_sta, dtype=np.float64), new_max, True
                    elif (seg_b - seg_a) >= relief_len * 0.9 and new_max[seg_idx] > self.spec.relief_grade_max_pct:
                        new_max[seg_idx] = self.spec.relief_grade_max_pct
                        return np.asarray(new_sta, dtype=np.float64), new_max, True
            else:
                # Mild grade (3%..5%): does not add to steep length, resets only if opposite sign
                if sign != run_sign:
                    run_start = -1
                    run_sign = 0
                    run_len = 0.0
                    run_max_abs_g = 0.0

        return pvi_stations, seg_max_grades, False

    def solve(
        self,
        h_plan: HorizontalPlan,
        start_elev_m: Optional[float] = None,
        end_elev_m: Optional[float] = None,
        control_elevations: Optional[List[Tuple[float, float]]] = None,
        max_offset_m: float = 14.0,
        elev_step_m: float = 0.5,
    ) -> VerticalPlan:
        """Compute JTG D20-2017 compliant PVI sequence and parabolic vertical curves."""
        stations = h_plan.centerline_stations
        ground_z = self.sample_ground_profile(h_plan.centerline_xy)
        target_z = self._compute_target_profile(h_plan.centerline_xy, ground_z)
        total_len = float(stations[-1])

        z_start = float(ground_z[0]) if start_elev_m is None else float(start_elev_m)
        z_end = float(ground_z[-1]) if end_elev_m is None else float(end_elev_m)

        # Downsample evaluation points for LP (<= 450 samples keeps LP solve < 40 ms)
        if len(stations) > 450:
            idx_sub = np.linspace(0, len(stations) - 1, 450).astype(int)
            sample_sta = stations[idx_sub]
            sample_tgt = target_z[idx_sub]
        else:
            sample_sta = stations
            sample_tgt = target_z

        pvi_stations = self._select_pvi_stations(stations, ground_z, control_elevations)
        seg_max_grades = [self.spec.max_grade_pct] * (len(pvi_stations) - 1)

        def build_locked(sta_arr: np.ndarray) -> Dict[int, float]:
            lk: Dict[int, float] = {0: z_start, len(sta_arr) - 1: z_end}
            if control_elevations:
                for c_sta, c_z in control_elevations:
                    idx = int(np.argmin(np.abs(sta_arr - c_sta)))
                    if idx not in lk:
                        lk[idx] = float(c_z)
            return lk

        # Pass 1: Global convex LP with JTG max grade & 3 km avg climb limit
        locked = build_locked(pvi_stations)
        pvi_elevs = self._solve_vertical_lp(
            pvi_stations,
            seg_max_grades,
            sample_sta,
            sample_tgt,
            locked,
            max_3km_avg_pct=self.spec.max_continuous_climb_avg_pct,
        )
        # If 3 km average climb constraint made it infeasible (steep short valley), relax only 3 km constraint
        if pvi_elevs is None:
            pvi_elevs = self._solve_vertical_lp(
                pvi_stations, seg_max_grades, sample_sta, sample_tgt, locked, max_3km_avg_pct=None
            )

        # Pass 2: Tunnel section grade cap (<= 3.0% where cut > tunnel_cut_threshold_m)
        if pvi_elevs is not None:
            cand_design = np.interp(stations, pvi_stations, pvi_elevs)
            changed_tunnel = False
            for k in range(len(pvi_stations) - 1):
                s0, s1 = pvi_stations[k], pvi_stations[k + 1]
                mask = (stations >= s0) & (stations <= s1)
                if np.any(mask):
                    cut_seg = ground_z[mask] - cand_design[mask]
                    # If > 40% of segment is in deep tunnel cut, cap grade to tunnel_max_grade_pct
                    if float(np.mean(cut_seg > self.tunnel_cut_threshold_m)) >= 0.40:
                        if seg_max_grades[k] > self.spec.tunnel_max_grade_pct:
                            seg_max_grades[k] = self.spec.tunnel_max_grade_pct
                            changed_tunnel = True
            if changed_tunnel:
                pvi_try = self._solve_vertical_lp(
                    pvi_stations, seg_max_grades, sample_sta, sample_tgt, locked,
                    max_3km_avg_pct=self.spec.max_continuous_climb_avg_pct,
                )
                if pvi_try is not None:
                    pvi_elevs = pvi_try

        # Pass 3: JTG D20-2017 表8.2.5 grade-length & relief-grade insertion (up to 16 passes)
        if pvi_elevs is not None:
            for _ in range(16):
                next_sta, next_max, modified = self._insert_relief_grades_if_needed(
                    pvi_stations, pvi_elevs, seg_max_grades
                )
                if not modified:
                    break
                next_locked = build_locked(next_sta)
                next_elevs = self._solve_vertical_lp(
                    next_sta, next_max, sample_sta, sample_tgt, next_locked,
                    max_3km_avg_pct=self.spec.max_continuous_climb_avg_pct,
                )
                if next_elevs is None:
                    next_elevs = self._solve_vertical_lp(
                        next_sta, next_max, sample_sta, sample_tgt, next_locked, max_3km_avg_pct=None
                    )
                if next_elevs is not None:
                    pvi_stations, seg_max_grades, pvi_elevs = next_sta, next_max, next_elevs
                else:
                    break

        if pvi_elevs is None:
            # Last-resort straight interpolation (only if |z_end - z_start|/L > max_grade physically!)
            pvi_elevs = np.interp(pvi_stations, stations, ground_z)
            pvi_elevs[0] = z_start
            pvi_elevs[-1] = z_end

        # Build PVI elements with non-overlapping symmetric parabolic vertical curves
        pvis = self._build_pvis(pvi_stations, pvi_elevs)
        design_z = self._evaluate_profile(stations, pvis)

        grades = [
            ((pvi_elevs[k + 1] - pvi_elevs[k]) / max(1e-3, pvi_stations[k + 1] - pvi_stations[k])) * 100.0
            for k in range(len(pvi_stations) - 1)
        ]
        max_g = max((abs(g) for g in grades), default=0.0)
        min_g = min((abs(g) for g in grades), default=0.0)

        # Capped cross-section earthwork (matches Civil 3D DaylightMaxWidth!)
        dh = design_z - ground_z
        cut_h = np.maximum(0.0, -dh)
        fill_h = np.maximum(0.0, dh)
        cut_w = np.minimum(self.max_slope_width_m, cut_h * self.cut_slope)
        fill_w = np.minimum(self.max_slope_width_m, fill_h * self.side_slope)
        cut_area = self.road_width_m * cut_h + cut_w * cut_h
        fill_area = self.road_width_m * fill_h + fill_w * fill_h

        total_cut = float(_trapz(cut_area, stations)) if len(stations) >= 2 else 0.0
        total_fill = float(_trapz(fill_area, stations)) if len(stations) >= 2 else 0.0

        jtg_audit = self._audit_jtg(h_plan, pvis, stations, ground_z, design_z)

        return VerticalPlan(
            plan_id=h_plan.plan_id,
            stations_m=stations,
            ground_elev_m=ground_z,
            design_elev_m=design_z,
            pvis=pvis,
            max_grade_pct=max_g,
            min_grade_pct=min_g,
            total_cut_m3=total_cut,
            total_fill_m3=total_fill,
            max_cut_m=float(np.max(cut_h)) if cut_h.size else 0.0,
            max_fill_m=float(np.max(fill_h)) if fill_h.size else 0.0,
            jtg_audit=jtg_audit,
        )

    def _audit_jtg(
        self,
        h_plan: HorizontalPlan,
        pvis: List[PVIElement],
        stations: np.ndarray,
        ground_z: np.ndarray,
        design_z: np.ndarray,
    ) -> Dict[str, Any]:
        """Audit full JTG D20-2017 vertical & horizontal-vertical coordination compliance."""
        n = len(pvis)
        grades = []
        lengths = []
        for k in range(n - 1):
            l_k = float(pvis[k + 1].station_m - pvis[k].station_m)
            g_k = ((pvis[k + 1].elevation_m - pvis[k].elevation_m) / max(1e-3, l_k)) * 100.0
            grades.append(g_k)
            lengths.append(l_k)

        max_g = max((abs(g) for g in grades), default=0.0)
        max_grade_ok = max_g <= self.spec.max_grade_pct + 0.05

        # Grade-length & relief-grade audit
        relief_count = 0
        violations: List[str] = []
        run_len = 0.0
        run_sign = 0
        run_max_g = 0.0
        for k, (g, l_k) in enumerate(zip(grades, lengths)):
            abs_g = abs(g)
            sign = 1 if g > 0 else (-1 if g < 0 else 0)
            if abs_g <= self.spec.relief_grade_max_pct + 0.1 and l_k >= self.spec.relief_grade_min_len_m * 0.85:
                if run_len > 0:
                    relief_count += 1
                run_len = 0.0
                run_sign = 0
                run_max_g = 0.0
                continue
            if abs_g >= 5.05:
                if sign == run_sign:
                    run_len += l_k
                    run_max_g = max(run_max_g, abs_g)
                else:
                    run_len = l_k
                    run_sign = sign
                    run_max_g = abs_g
                lim = self.spec.max_grade_length_for(run_max_g)
                if lim is not None and run_len > lim + 15.0:
                    violations.append(
                        f"K{pvis[k].station_m/1000:.3f}: 坡度 {run_max_g:.2f}% 连续坡长 {run_len:.0f}m > 限值 {lim:.0f}m"
                    )
            elif sign != run_sign:
                run_len = 0.0
                run_sign = 0
                run_max_g = 0.0

        # Continuous 3 km average grade check
        max_3km_avg = 0.0
        if len(stations) >= 2 and float(stations[-1] - stations[0]) >= 3000.0:
            for i in range(0, len(stations), max(1, len(stations) // 120)):
                s_end = stations[i] + 3000.0
                if s_end > stations[-1]:
                    break
                z_end = float(np.interp(s_end, stations, design_z))
                avg_g = abs(z_end - float(design_z[i])) / 3000.0 * 100.0
                max_3km_avg = max(max_3km_avg, avg_g)
        continuous_climb_ok = max_3km_avg <= self.spec.max_continuous_climb_avg_pct + 0.05

        # Tunnel section grade check
        tunnel_grade_ok = True
        for k in range(n - 1):
            s0, s1 = pvis[k].station_m, pvis[k + 1].station_m
            mask = (stations >= s0) & (stations <= s1)
            if np.any(mask) and float(np.mean((ground_z[mask] - design_z[mask]) > self.tunnel_cut_threshold_m)) >= 0.40:
                if abs(grades[k]) > self.spec.tunnel_max_grade_pct + 0.15:
                    tunnel_grade_ok = False

        # 平包竖 (Horizontal-Vertical coordination) audit:
        # Warn if a sharp vertical curve apex (|dg| > 3%) sits within 25m of a small-radius horizontal curve ZH/HZ
        hv_warnings: List[str] = []
        small_r_thresh = self.spec.min_radius_m * 1.5
        for p in pvis[1:-1]:
            if p.grade_in_pct is None or p.grade_out_pct is None:
                continue
            if abs(p.grade_out_pct - p.grade_in_pct) < 3.0:
                continue
            for c in h_plan.curves:
                if c.radius_m <= small_r_thresh:
                    if abs(p.station_m - c.zh_station) < 25.0 or abs(p.station_m - c.hz_station) < 25.0:
                        hv_warnings.append(
                            f"PVI K{p.station_m/1000:.3f} (Δi={p.grade_out_pct - p.grade_in_pct:+.1f}%) 邻近小半径平曲线 JD{c.jd_index}(R={c.radius_m:.1f}m) 起终点"
                        )

        all_passed = max_grade_ok and (len(violations) == 0) and continuous_climb_ok and tunnel_grade_ok
        return {
            "max_grade_ok": max_grade_ok,
            "grade_length_ok": len(violations) == 0,
            "grade_length_violations": violations[:8],
            "relief_grade_count": relief_count,
            "max_3km_avg_grade_pct": round(max_3km_avg, 3),
            "continuous_climb_ok": continuous_climb_ok,
            "tunnel_grade_ok": tunnel_grade_ok,
            "hv_coordination_warnings": hv_warnings[:8],
            "all_passed": all_passed,
        }

    def _build_pvis(self, pvi_stations: np.ndarray, pvi_elevs: np.ndarray) -> List[PVIElement]:
        n = len(pvi_stations)
        grades_dec = [
            (pvi_elevs[k + 1] - pvi_elevs[k]) / max(1e-3, pvi_stations[k + 1] - pvi_stations[k])
            for k in range(n - 1)
        ]

        # Compute non-overlapping curve lengths across every adjacent PVI pair
        l_vals = [0.0] * n
        r_vals = [0.0] * n
        for k in range(1, n - 1):
            dg = grades_dec[k] - grades_dec[k - 1]
            abs_dg = abs(dg)
            if abs_dg > 1e-4:
                base_r = self.spec.min_convex_rv_m if dg < 0 else self.spec.min_concave_rv_m
                r_v = base_r * 1.4
                max_lv = 0.88 * min(
                    float(pvi_stations[k] - pvi_stations[k - 1]),
                    float(pvi_stations[k + 1] - pvi_stations[k]),
                )
                l_v = r_v * abs_dg
                if l_v > max_lv and max_lv > 1e-3:
                    l_v = max_lv
                    r_v = l_v / abs_dg
                l_vals[k] = l_v
                r_vals[k] = r_v

        # Guarantee zero tangent overlap between consecutive PVIs: 0.5*L_k + 0.5*L_{k+1} <= 0.94 * seg_len
        for k in range(1, n - 2):
            seg_len = float(pvi_stations[k + 1] - pvi_stations[k])
            sum_half = 0.5 * (l_vals[k] + l_vals[k + 1])
            if sum_half > 0.94 * seg_len and sum_half > 1e-6:
                scale = (0.94 * seg_len) / sum_half
                l_vals[k] *= scale
                r_vals[k] *= scale
                l_vals[k + 1] *= scale
                r_vals[k + 1] *= scale

        pvis: List[PVIElement] = []
        for k in range(n):
            g_in = grades_dec[k - 1] * 100.0 if k > 0 else None
            g_out = grades_dec[k] * 100.0 if k < n - 1 else None
            pvis.append(
                PVIElement(
                    index=k,
                    station_m=round(float(pvi_stations[k]), 3),
                    elevation_m=round(float(pvi_elevs[k]), 3),
                    grade_in_pct=round(g_in, 4) if g_in is not None else None,
                    grade_out_pct=round(g_out, 4) if g_out is not None else None,
                    curve_radius_m=round(float(r_vals[k]), 2),
                    curve_length_m=round(float(l_vals[k]), 3),
                )
            )
        return pvis

    @staticmethod
    def _evaluate_profile(stations: np.ndarray, pvis: List[PVIElement]) -> np.ndarray:
        """Evaluate design elevation at all stations including parabolic vertical curves."""
        pvi_s = np.array([p.station_m for p in pvis], dtype=np.float64)
        pvi_z = np.array([p.elevation_m for p in pvis], dtype=np.float64)
        design = np.interp(stations, pvi_s, pvi_z)

        # Apply parabolic vertical curve correction within [s_k - L_v/2, s_k + L_v/2]
        for k in range(1, len(pvis) - 1):
            p = pvis[k]
            lv = p.curve_length_m
            if lv <= 1e-3 or p.grade_in_pct is None or p.grade_out_pct is None:
                continue
            g1 = p.grade_in_pct / 100.0
            g2 = p.grade_out_pct / 100.0
            dg = g2 - g1
            half_l = 0.5 * lv
            s_bvc = p.station_m - half_l
            s_evc = p.station_m + half_l

            mask = (stations >= s_bvc) & (stations <= s_evc)
            if np.any(mask):
                x = stations[mask] - s_bvc
                z_bvc = p.elevation_m - g1 * half_l
                design[mask] = z_bvc + g1 * x + (dg / (2.0 * lv)) * (x ** 2)

        return design
