# route_solver — 外置自动选线算力引擎

零 CAD 依赖的纯 Python 自动选线算力库，通过标准文件（`terrain.npz`、`constraints.geojson`、`plan_N.json`）与 Civil 3D 底座解耦协作。

## 目录结构

- `cost_field.py`：多因子代价场构建（地形坡度、高程偏离、GeoJSON 避让区/拆迁区/禁行区）
- `horizontal_solver.py`：双向/加权 A* 走廊寻优、K 差异化多方案生成、Douglas-Peucker 交点提取与平曲线（圆曲线 + 缓和曲线）合法性拟合
- `vertical_solver.py`：原地面线采样与动态规划自动拉坡（满足纵坡、坡长、竖曲线半径及控制标高约束，优化挖填方平衡）
- `evaluator.py`：工程指标评估与多方案对比打分
- `cli.py`：统一命令行入口，供 AI Agent 或自动化脚本直接调用

## 快速开始

```bash
pip install -r requirements.txt

# 运行端到端测试
python -m unittest discover -s tests -v

# 命令行调用示例
python -m route_solver.cli \
  --terrain terrain.npz \
  --constraints constraints.geojson \
  --start 500100,3100100 \
  --end 500900,3100850 \
  --design-speed 60 \
  --num-plans 3 \
  --out-dir ./ep_workspace
```

## 输入数据约定

1. **`terrain.npz`**：支持以下键名组合：
   - `elevation` 或 `Z`（2D `float64` 数组，形状 `(ny, nx)`，`nodata` 可为 `NaN`）
   - `x_coords`、`y_coords`（1D 数组，长度分别为 `nx`、`ny`，升序）**或** `origin_x`、`origin_y`、`resolution`
2. **`constraints.geojson`**：标准 GeoJSON `FeatureCollection`，Feature `properties` 支持：
   - `category` / `type`：`forbidden`（禁行区）、`building` / `demolition`（拆迁区）、`water` / `farmland` / `sensitive`（敏感避让区）、`existing_road`（既有道路，可设低代价或复用）、`control_point`（必经控制点）
   - `penalty`（可选数值权重覆盖）、`name`（区域名称）
