"""External autonomous route-solver engine for Civil 3D engineering-platform."""

from .cost_field import CostField, CostWeights, load_terrain_npz, load_constraints_geojson
from .horizontal_solver import HorizontalSolver, HorizontalPlan, DesignSpeedSpec
from .vertical_solver import VerticalSolver, VerticalPlan
from .evaluator import RouteEvaluator, PlanMetrics

__all__ = [
    "CostField",
    "CostWeights",
    "load_terrain_npz",
    "load_constraints_geojson",
    "HorizontalSolver",
    "HorizontalPlan",
    "DesignSpeedSpec",
    "VerticalSolver",
    "VerticalPlan",
    "RouteEvaluator",
    "PlanMetrics",
]
