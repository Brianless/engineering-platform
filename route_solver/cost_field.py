"""Multi-factor cost field builder for external route optimization."""

from __future__ import annotations

import json
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple

import numpy as np
from shapely.geometry import shape, Point, LineString, Polygon, MultiPolygon
from shapely.geometry.base import BaseGeometry
from shapely.prepared import prep


@dataclass
class TerrainGrid:
    """2D elevation grid with real-world georeferenced coordinates."""

    elevation: np.ndarray  # shape (ny, nx), float64
    x_coords: np.ndarray   # shape (nx,), ascending
    y_coords: np.ndarray   # shape (ny,), ascending

    @property
    def ny(self) -> int:
        return int(self.elevation.shape[0])

    @property
    def nx(self) -> int:
        return int(self.elevation.shape[1])

    @property
    def dx(self) -> float:
        return float(np.mean(np.diff(self.x_coords)))

    @property
    def dy(self) -> float:
        return float(np.mean(np.diff(self.y_coords)))

    @property
    def resolution(self) -> float:
        return 0.5 * (abs(self.dx) + abs(self.dy))

    def world_to_grid(self, x: float, y: float) -> Tuple[int, int]:
        """Convert world (x, y) to nearest (row, col) grid index."""
        col = int(np.clip(np.searchsorted(self.x_coords, x), 0, self.nx - 1))
        if col > 0 and abs(self.x_coords[col - 1] - x) < abs(self.x_coords[col] - x):
            col -= 1
        row = int(np.clip(np.searchsorted(self.y_coords, y), 0, self.ny - 1))
        if row > 0 and abs(self.y_coords[row - 1] - y) < abs(self.y_coords[row] - y):
            row -= 1
        return row, col

    def grid_to_world(self, row: int, col: int) -> Tuple[float, float]:
        """Convert (row, col) index to world (x, y)."""
        r = int(np.clip(row, 0, self.ny - 1))
        c = int(np.clip(col, 0, self.nx - 1))
        return float(self.x_coords[c]), float(self.y_coords[r])


@dataclass
class ConstraintFeature:
    """Parsed GeoJSON constraint feature."""

    geometry: BaseGeometry
    category: str
    name: str
    penalty: Optional[float] = None
    target_elevation: Optional[float] = None
    properties: Dict[str, Any] = field(default_factory=dict)


@dataclass
class CostWeights:
    """Configurable weights for multi-factor route cost field."""

    w_base: float = 1.0          # Base distance cost per meter
    w_slope: float = 4.0         # Slope penalty multiplier
    w_elev: float = 0.25         # Cut/fill elevation deviation multiplier
    w_avoid: float = 25.0        # Sensitive/avoidance zone penalty (water, farmland, etc.)
    w_land: float = 60.0         # Building/demolition zone penalty
    w_existing_bonus: float = 0.6  # Multiplier (<1 reduces cost) near existing roads
    max_slope_pct: float = 18.0  # Cross-slope above this becomes heavily penalized / forbidden
    hard_slope_cutoff_pct: float = 45.0  # Absolute impassable terrain slope (%)
    avoid_buffer_m: float = 5.0  # Buffer around polygons/lines

    @classmethod
    def from_dict(cls, data: Optional[Dict[str, Any]]) -> "CostWeights":
        if not data:
            return cls()
        valid_keys = {f.name for f in cls.__dataclass_fields__.values()}
        filtered = {k: v for k, v in data.items() if k in valid_keys}
        return cls(**filtered)

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


def load_terrain_npz(path: str | Path) -> TerrainGrid:
    """Load terrain elevation grid from .npz file."""
    data = np.load(str(path), allow_pickle=False)
    keys = set(data.files)

    if "elevation" in keys:
        elev = np.asarray(data["elevation"], dtype=np.float64)
    elif "Z" in keys:
        elev = np.asarray(data["Z"], dtype=np.float64)
    elif "z" in keys:
        elev = np.asarray(data["z"], dtype=np.float64)
    else:
        raise ValueError(f"terrain.npz missing 'elevation' or 'Z' array; found keys: {sorted(keys)}")

    if elev.ndim != 2:
        raise ValueError(f"Elevation grid must be 2D, got shape {elev.shape}")

    ny, nx = elev.shape

    if "x_coords" in keys and "y_coords" in keys:
        x_coords = np.asarray(data["x_coords"], dtype=np.float64)
        y_coords = np.asarray(data["y_coords"], dtype=np.float64)
    elif "x" in keys and "y" in keys:
        x_coords = np.asarray(data["x"], dtype=np.float64)
        y_coords = np.asarray(data["y"], dtype=np.float64)
    elif "origin_x" in keys and "origin_y" in keys and "resolution" in keys:
        ox = float(data["origin_x"])
        oy = float(data["origin_y"])
        res = float(data["resolution"])
        x_coords = ox + np.arange(nx, dtype=np.float64) * res
        y_coords = oy + np.arange(ny, dtype=np.float64) * res
    else:
        raise ValueError(
            "terrain.npz must provide ('x_coords', 'y_coords') or ('origin_x', 'origin_y', 'resolution')"
        )

    # Ensure ascending coordinate order
    if x_coords.size > 1 and x_coords[0] > x_coords[-1]:
        x_coords = x_coords[::-1]
        elev = elev[:, ::-1]
    if y_coords.size > 1 and y_coords[0] > y_coords[-1]:
        y_coords = y_coords[::-1]
        elev = elev[::-1, :]

    if x_coords.shape[0] != nx or y_coords.shape[0] != ny:
        raise ValueError(
            f"Coordinate lengths ({y_coords.shape[0]}, {x_coords.shape[0]}) do not match elevation shape {elev.shape}"
        )

    return TerrainGrid(elevation=elev, x_coords=x_coords, y_coords=y_coords)


def load_constraints_geojson(path: Optional[str | Path]) -> List[ConstraintFeature]:
    """Load GeoJSON FeatureCollection into structured ConstraintFeature objects."""
    if path is None:
        return []
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(f"Constraints file not found: {p}")

    with p.open("r", encoding="utf-8") as f:
        raw = json.load(f)

    features_raw = raw.get("features", []) if isinstance(raw, dict) else []
    parsed: List[ConstraintFeature] = []
    for idx, feat in enumerate(features_raw):
        geom_raw = feat.get("geometry")
        if not geom_raw:
            continue
        geom = shape(geom_raw)
        if geom.is_empty:
            continue
        props = feat.get("properties") or {}
        cat = str(props.get("category") or props.get("type") or props.get("layer") or "avoid").lower()
        name = str(props.get("name") or props.get("id") or f"{cat}_{idx}")
        penalty = props.get("penalty")
        penalty_val = float(penalty) if penalty is not None else None
        target_elev = props.get("target_elevation", props.get("elevation"))
        target_elev_val = float(target_elev) if target_elev is not None else None

        parsed.append(
            ConstraintFeature(
                geometry=geom,
                category=cat,
                name=name,
                penalty=penalty_val,
                target_elevation=target_elev_val,
                properties=props,
            )
        )
    return parsed


class CostField:
    """Computes and stores the composite 2D traversal cost grid and forbidden mask."""

    FORBIDDEN_CATEGORIES = {"forbidden", "no_go", "prohibited", "exclusion"}
    LAND_CATEGORIES = {"building", "demolition", "structure", "house", "factory"}
    AVOID_CATEGORIES = {"water", "river", "lake", "farmland", "wetland", "forest", "sensitive", "avoid"}
    EXISTING_CATEGORIES = {"existing_road", "road", "corridor"}
    CONTROL_CATEGORIES = {"control_point", "waypoint", "control"}

    def __init__(
        self,
        terrain: TerrainGrid,
        constraints: Optional[List[ConstraintFeature]] = None,
        weights: Optional[CostWeights] = None,
        custom_penalty_fn: Optional[
            Callable[[np.ndarray, np.ndarray, np.ndarray, np.ndarray], np.ndarray]
        ] = None,
    ) -> None:
        self.terrain = terrain
        self.constraints = constraints or []
        self.weights = weights or CostWeights()
        self.custom_penalty_fn = custom_penalty_fn

        self.slope_pct: np.ndarray = np.zeros_like(terrain.elevation)
        self.cost_grid: np.ndarray = np.ones_like(terrain.elevation)
        self.forbidden_mask: np.ndarray = np.isnan(terrain.elevation)
        self.avoid_penalty_grid: np.ndarray = np.zeros_like(terrain.elevation)
        self.land_penalty_grid: np.ndarray = np.zeros_like(terrain.elevation)
        self.control_points: List[Tuple[float, float, Optional[float], str]] = []

        self.build()

    def build(self, ref_elevation: Optional[float] = None) -> None:
        """Recompute slope, elevation deviation, and constraint penalties."""
        elev = self.terrain.elevation.copy()
        nodata_mask = np.isnan(elev)
        if np.any(nodata_mask):
            valid_vals = elev[~nodata_mask]
            if valid_vals.size > 0:
                try:
                    from scipy.ndimage import distance_transform_edt
                    _, nearest_idx = distance_transform_edt(nodata_mask, return_indices=True)
                    elev[nodata_mask] = elev[tuple(nearest_idx[:, nodata_mask])]
                except Exception:
                    elev[nodata_mask] = float(np.median(valid_vals))
            else:
                elev[nodata_mask] = 0.0

        # Gradient in y and x
        dy = abs(self.terrain.dy) if abs(self.terrain.dy) > 1e-9 else 1.0
        dx = abs(self.terrain.dx) if abs(self.terrain.dx) > 1e-9 else 1.0
        dz_dy, dz_dx = np.gradient(elev, dy, dx)
        self.slope_pct = np.hypot(dz_dx, dz_dy) * 100.0

        # Slope penalty: smooth quadratic growth above gentle threshold
        w = self.weights
        slope_ratio = self.slope_pct / max(w.max_slope_pct, 1e-3)
        slope_cost = w.w_slope * np.where(
            self.slope_pct <= w.max_slope_pct,
            slope_ratio ** 1.5,
            1.0 + 4.0 * (slope_ratio - 1.0) ** 2,
        )

        # Elevation deviation penalty
        if ref_elevation is None:
            ref_elevation = float(np.median(elev))
        elev_diff = np.abs(elev - ref_elevation)
        elev_scale = max(float(np.std(elev)), 5.0)
        elev_cost = w.w_elev * (elev_diff / elev_scale)

        # Rasterize constraints
        self.forbidden_mask = nodata_mask | (self.slope_pct > w.hard_slope_cutoff_pct)
        self.avoid_penalty_grid.fill(0.0)
        self.land_penalty_grid.fill(0.0)
        existing_bonus_grid = np.ones_like(elev)
        self.control_points.clear()

        for feat in self.constraints:
            cat = feat.category
            if cat in self.CONTROL_CATEGORIES and isinstance(feat.geometry, Point):
                self.control_points.append(
                    (float(feat.geometry.x), float(feat.geometry.y), feat.target_elevation, feat.name)
                )
                continue

            geom = feat.geometry
            if isinstance(geom, LineString) and w.avoid_buffer_m > 0:
                geom = geom.buffer(w.avoid_buffer_m)
            elif isinstance(geom, Point) and w.avoid_buffer_m > 0:
                geom = geom.buffer(w.avoid_buffer_m)

            mask = self._rasterize_geometry(geom)
            if not np.any(mask):
                continue

            if cat in self.FORBIDDEN_CATEGORIES:
                self.forbidden_mask |= mask
            elif cat in self.LAND_CATEGORIES:
                val = feat.penalty if feat.penalty is not None else w.w_land
                self.land_penalty_grid[mask] = np.maximum(self.land_penalty_grid[mask], val)
            elif cat in self.EXISTING_CATEGORIES:
                factor = feat.penalty if feat.penalty is not None else w.w_existing_bonus
                existing_bonus_grid[mask] = np.minimum(existing_bonus_grid[mask], max(0.2, factor))
            else:
                val = feat.penalty if feat.penalty is not None else w.w_avoid
                self.avoid_penalty_grid[mask] = np.maximum(self.avoid_penalty_grid[mask], val)

        total_cost = (
            w.w_base + slope_cost + elev_cost + self.avoid_penalty_grid + self.land_penalty_grid
        ) * existing_bonus_grid

        if self.custom_penalty_fn is not None:
            xx, yy = np.meshgrid(self.terrain.x_coords, self.terrain.y_coords)
            extra = self.custom_penalty_fn(xx, yy, elev, self.slope_pct)
            total_cost = total_cost + np.maximum(0.0, np.asarray(extra, dtype=np.float64))

        total_cost = np.maximum(total_cost, 0.1)
        total_cost[self.forbidden_mask] = np.inf
        self.cost_grid = total_cost

    def _rasterize_geometry(self, geom: BaseGeometry) -> np.ndarray:
        """Fast bounding-box rasterization of a Shapely geometry onto the terrain grid."""
        mask = np.zeros((self.terrain.ny, self.terrain.nx), dtype=bool)
        minx, miny, maxx, maxy = geom.bounds

        c0 = max(0, int(np.searchsorted(self.terrain.x_coords, minx, side="left")) - 1)
        c1 = min(self.terrain.nx, int(np.searchsorted(self.terrain.x_coords, maxx, side="right")) + 1)
        r0 = max(0, int(np.searchsorted(self.terrain.y_coords, miny, side="left")) - 1)
        r1 = min(self.terrain.ny, int(np.searchsorted(self.terrain.y_coords, maxy, side="right")) + 1)

        if r0 >= r1 or c0 >= c1:
            return mask

        prepared = prep(geom)
        xs = self.terrain.x_coords[c0:c1]
        ys = self.terrain.y_coords[r0:r1]

        for lr, y in enumerate(ys):
            row_idx = r0 + lr
            for lc, x in enumerate(xs):
                if prepared.contains(Point(float(x), float(y))):
                    mask[row_idx, c0 + lc] = True
        return mask
