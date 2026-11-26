"""内置脱敏样例「图纸」：图元树、图层、文字、块，含 2 个故意异常项。

用途：无 AutoCAD 环境下跑通全链路（T07）。数据完全是构造的，不含任何真实生产图纸内容；
故意保留以下异常，用于验证 read_anomalies：
    - duplicate_point：3 个点对象落在同一坐标
    - proxy_object：1 个 ACAD_PROXY_ENTITY（模拟 Civil 3D 廊道，未安装本体）
另含 zero_length / extreme_coord / extremely_long_text 便于覆盖更多分支。
"""

from __future__ import annotations

import hashlib
import random

SEED = 20261121

# 样例图层：名称 → (含义, 置信度百分比, 类别)
LAYERS = {
    "D-RD-中线": ("道路中线", 95, "roadway"),
    "D-SITE-边界": ("场地边界", 93, "site"),
    "D-SITE-等高线": ("等高线", 92, "terrain"),
    "D-DR-沟渠中线": ("排水沟中线", 94, "drainage"),
    "D-DR-管道": ("排水管道", 94, "drainage"),
    "D-DR-检查井": ("检查井", 92, "drainage"),
    "A-标注": ("标注", 90, "annotation"),
    "A-图框": ("图框", 92, "frame"),
    "C-CIVIL-廊道": (None, 0, "unknown"),   # 代理对象所在图层：不识别
    "0": ("未分类图元", 70, "unknown"),
}

MODEL_EXTENT = (0.0, 0.0, 120000.0, 80000.0)

#: 图层表里存在、但没有任何图元的图层（D7：read_overview.counts 要能把这类单列出来）。
#: 真实图纸几乎都有这种「建了没用」的图层，mock 必须同构，否则 counts.layers 的两种
#: 口径在 mock 环境里永远相等，缺陷就测不出来。
EMPTY_LAYER_NAMES = ("GD-备用图层", "DEFPOINTS")


def _fingerprint() -> str:
    body = "mock-drawing-v1|120000x80000|2026-11-20T09:15:00Z"
    return "fp1:" + hashlib.sha256(body.encode("utf-8")).hexdigest()[:32]


def _name_hash() -> str:
    return hashlib.sha256("脱敏样例-排水总图.dwg".upper().encode("utf-8")).hexdigest()[:8] + ".dwg"


class _Builder:
    """按句柄递增构造图元。"""

    def __init__(self):
        self.entities = []
        self._next = 0x1001

    def _handle(self) -> str:
        value = self._next
        self._next += 1
        return "%X" % value

    def add(self, dxf_name, dotnet_type, layer, min_xyz, max_xyz, geometry,
            meaning, confidence, basis_kind, basis_ref, texts=None, attributes=None,
            is_proxy=False, is_block=False, block_name=None, length=0.0, raw_text_length=0):
        meaning_value, confidence_value, band = _recognize(layer, meaning, confidence)
        entity = {
            "handle": self._handle(),
            "dxfName": dxf_name,
            "dotNetType": dotnet_type,
            "layer": layer,
            "linetype": "ByLayer",
            "colorIndex": 256,
            "visible": True,
            "hasExtents": True,
            "min": [float(min_xyz[0]), float(min_xyz[1]), 0.0],
            "max": [float(max_xyz[0]), float(max_xyz[1]), 0.0],
            "meaning": meaning_value,
            "confidence": confidence_value,
            "band": band,
            "basis": [{"kind": basis_kind, "ref": basis_ref}] if basis_ref else [],
            "geometry": geometry,
            "texts": texts or [],
            "attributes": attributes or {},
            "isProxy": is_proxy,
            "isBlockReference": is_block,
            "blockName": block_name,
            "isFromExternalReference": False,
            "length": float(length),
            "rawTextLength": int(raw_text_length),
        }
        self.entities.append(entity)
        return entity

    def line(self, layer, x0, y0, x1, y1, meaning=None, confidence=None,
             basis_kind="geometry_rule", basis_ref="geometry_rule:line", length=None):
        computed = length if length is not None else ((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5
        return self.add(
            "LINE", "Line", layer,
            (min(x0, x1), min(y0, y1)), (max(x0, x1), max(y0, y1)),
            {"start": [float(x0), float(y0), 0.0], "end": [float(x1), float(y1), 0.0],
             "length": round(computed, 3)},
            meaning, confidence, basis_kind, basis_ref, length=computed)

    def polyline(self, layer, points, closed, meaning=None, confidence=None,
                 basis_kind="geometry_rule", basis_ref="geometry_rule:closed_polyline"):
        xs = [p[0] for p in points]
        ys = [p[1] for p in points]
        length = 0.0
        for index in range(len(points) - 1):
            length += ((points[index + 1][0] - points[index][0]) ** 2
                       + (points[index + 1][1] - points[index][1]) ** 2) ** 0.5
        return self.add(
            "LWPOLYLINE", "Polyline", layer,
            (min(xs), min(ys)), (max(xs), max(ys)),
            {"vertexCount": len(points), "closed": bool(closed), "length": round(length, 3)},
            meaning, confidence, basis_kind, basis_ref, length=length)

    def text(self, layer, x, y, value, height=250.0, meaning=None, confidence=None,
             basis_kind="text_rule", basis_ref=None):
        return self.add(
            "TEXT", "DBText", layer, (x, y), (x + 1000.0, y + height),
            {"height": height, "rotation_deg": 0.0, "position": [float(x), float(y), 0.0]},
            meaning, confidence, basis_kind, basis_ref or "text_rule:keyword",
            texts=[value[:80]], raw_text_length=len(value))

    def point(self, layer, x, y):
        return self.add(
            "POINT", "DBPoint", layer, (x, y), (x, y),
            {"position": [float(x), float(y), 0.0]},
            None, None, "inference", "unclassified:POINT")

    def block(self, layer, x, y, name, attributes=None, meaning=None, confidence=None):
        return self.add(
            "INSERT", "BlockReference", layer, (x, y), (x + 42000.0, y + 29700.0),
            {"blockName": name, "isXref": False},
            meaning, confidence, "block_map", "block-map@1.0:#" + name,
            attributes=attributes or {}, is_block=True, block_name=name)

    def proxy(self, layer, x0, y0, x1, y1):
        entity = self.add(
            "ACAD_PROXY_ENTITY", "ProxyEntity", layer, (x0, y0), (x1, y1),
            {}, None, None, "inference", "proxy_object:unrecognized",
            is_proxy=True)
        entity["meaning"] = None
        entity["confidence"] = 0.0
        entity["band"] = "hint"
        return entity


def _recognize(layer, meaning, confidence):
    """按图层语义补全含义（与宿主侧 layer_map 优先级一致）。"""
    layer_meaning, layer_confidence, _category = LAYERS.get(layer, (None, 0, "unknown"))
    if meaning is not None:
        value = meaning
        percent = confidence if confidence is not None else layer_confidence or 70
    elif layer_meaning:
        value = layer_meaning
        percent = layer_confidence
    else:
        value = None
        percent = 45 if meaning is None and confidence is None else 0
    confidence_value = round((percent or 0) / 100.0, 3)
    if value is None:
        band = "hint"
    elif confidence_value >= 0.9:
        band = "auto"
    elif confidence_value >= 0.6:
        band = "confirm"
    else:
        band = "hint"
    return value, confidence_value, band


def _layer_basis(layer):
    _meaning, _confidence, _category = LAYERS.get(layer, (None, 0, "unknown"))
    return "layer-map@1.0:#%s" % layer


def build_model() -> dict:
    """构造样例图（确定性：同 seed 每次一致）。"""
    rng = random.Random(SEED)
    builder = _Builder()

    # 1) 场地边界（闭合多段线）
    builder.polyline(
        "D-SITE-边界",
        [(5000, 5000), (115000, 5000), (115000, 75000), (5000, 75000), (5000, 5000)],
        True, meaning="场地边界", confidence=93,
        basis_kind="layer_map", basis_ref=_layer_basis("D-SITE-边界"))

    # 2) 道路中线
    builder.line("D-RD-中线", 5000, 40000, 115000, 40000,
                 meaning="道路中线", confidence=95,
                 basis_kind="layer_map", basis_ref=_layer_basis("D-RD-中线"))

    # 3) 排水沟中线（折线）
    channel_points = [(10000, 62000), (30000, 61000), (60000, 60000), (95000, 60500)]
    for index in range(len(channel_points) - 1):
        x0, y0 = channel_points[index]
        x1, y1 = channel_points[index + 1]
        builder.line("D-DR-沟渠中线", x0, y0, x1, y1,
                     meaning="排水沟中线", confidence=94,
                     basis_kind="layer_map", basis_ref=_layer_basis("D-DR-沟渠中线"))

    # 4) 排水管道（横断面方向的支管）
    for index in range(12):
        y = 12000 + index * 3500
        builder.line("D-DR-管道", 20000, y, 90000, y,
                     meaning="排水管道", confidence=94,
                     basis_kind="layer_map", basis_ref=_layer_basis("D-DR-管道"))

    # 5) 等高线（用多段线模拟）
    for index in range(10):
        base_y = 8000 + index * 2500
        points = [(6000 + step * 9000, base_y + rng.randint(-400, 400)) for step in range(12)]
        builder.polyline("D-SITE-等高线", points, False,
                         meaning="等高线", confidence=92,
                         basis_kind="layer_map", basis_ref=_layer_basis("D-SITE-等高线"))

    # 6) 检查井（按块引用 + 圆）
    #
    # 属性标签是「输出形状」的一部分：C# 宿主把块属性放进 `props` 时会给键加 `attr.` 前缀
    # （`props["attr." + tag]`），因此 mock 必须给出同样的形状，否则 740/0 全绿也证明不了
    # 「属性真的被读到了」—— D-01（M0 起属性一条都没读到）就是这么藏住的。
    # 第一个井额外带一个 `ELEV` 标签，作为契约测试的**定点夹具**（值固定、可断言）。
    for index in range(6):
        x = 22000 + index * 12000
        attributes = {"编号": "J%d" % (index + 1), "井底标高": "%.2f" % (1280.5 + index)}
        if index == 0:
            attributes["ELEV"] = "352.13"
        builder.block("D-DR-检查井", x, 60000, "检查井-Φ1000", attributes,
                      meaning="检查井", confidence=92)

    # 7) 标注文字（含关键字，命中 text_rule）
    annotations = [
        (12000, 63500, "沟底标高 1280.50"),
        (38000, 63500, "桩号 K0+120 沟底标高 1279.80"),
        (66000, 63500, "纵坡 0.3%"),
        (94000, 63500, "沟宽 600"),
        (12000, 42000, "道路中线 半径 R=300"),
        (60000, 20000, "说明：本图为脱敏样例，仅用于联调"),
    ]
    for x, y, value in annotations:
        builder.text("A-标注", x, y, value, meaning=None, confidence=70)

    # 8) 图框 + 指北针
    builder.block("A-图框", 2000, 2000, "A1图框", {"图号": "D-01", "比例": "1:1000"},
                  meaning="图框", confidence=92)
    builder.block("A-图框", 100000, 70000, "指北针", {})

    # 9) 故意异常 1：重复点（3 个点同坐标）
    for _ in range(3):
        builder.point("0", 48000.0, 36000.0)

    # 10) 故意异常 2：代理对象（Civil 3D 廊道，本体未安装）
    builder.proxy("C-CIVIL-廊道", 15000, 44000, 105000, 52000)

    # 11) 零长度线段
    builder.line("0", 30000, 30000, 30000, 30000, length=0.0)

    # 12) 极值坐标
    builder.line("0", 2.0e9, 2.0e9, 2.0e9 + 100, 2.0e9 + 100)

    # 13) 超长文字（>500 字符，只计数不回显）
    builder.text("A-标注", 5000, 3000, "填充" * 300)

    entities = builder.entities
    entities.sort(key=lambda item: int(item["handle"], 16))

    layers = _build_layers(entities)
    anomalies = _build_anomalies(entities)

    return {
        "fingerprint": _fingerprint(),
        "drawingBaseName": "脱敏样例-排水总图.dwg",
        "nameHash": _name_hash(),
        "modifiedUtc": "2026-11-20T09:15:00.000Z",
        "readOnly": False,
        "insUnits": 4,
        "unitCode": "mm",
        "scaleToMeter": 0.001,
        "softwareName": "AutoCAD",
        "softwareVersion": "24.1s",
        "civilApiAvailable": False,
        "hasExtents": True,
        "min": [MODEL_EXTENT[0], MODEL_EXTENT[1], 0.0],
        "max": [MODEL_EXTENT[2], MODEL_EXTENT[3], 0.0],
        "counts": {
            "entities": len(entities),
            # D7：layers = **有图元的图层数**，恒等于 read_layers.total；
            # 图层表里还有未使用的图层，单列在 layerTableRecords / emptyLayers，
            # 免得模型把「表格记录数」当成「有内容的图层数」而误判丢数据。
            "layers": len(layers),
            "layerTableRecords": len(layers) + len(EMPTY_LAYER_NAMES),
            "emptyLayers": len(EMPTY_LAYER_NAMES),
            "blocks": 12,
            "blockReferences": sum(1 for item in entities if item["isBlockReference"]),
            "paperSpaces": 1,
            "proxyEntities": sum(1 for item in entities if item["isProxy"]),
            "entityLimitReached": False,
        },
        "entities": entities,
        "layers": layers,
        "anomalies": anomalies,
        "disciplines": _build_disciplines(layers),
        # N04-B：Civil 只读夹具（最小可用集合，三类各一件）。
        #
        # 为什么与 `civilApiAvailable=False` 并存而不矛盾：
        #   * `read_overview.civil3d` 仍按**降级档**上报 `detected=false` + W1001 —— 那是「真实机器没装
        #     Civil 3D」时宿主该有的表现，566 条既有断言依赖这条口径；
        #   * 但 `read_civil_list` / `read_civil_object` 的**读路径**必须有可枚举对象，否则 18 工具契约里
        #     这两个只能验空集，等于没验。
        # 所以这里单独给夹具，只被这两个工具消费，不参与 overview 的 Civil 三态统计。
        #
        # ★ **这是 mock 专属的事实分离，真机不会出现**：真机（Civil 3D 2025 已装）上
        #   `read_overview.civil3d.detected == true`，且 `counts.total` 与 `read_civil_list` 的
        #   `counts.total` 一致；两者不一致只在「没装 C3D」的降级档才成立，而 mockhost 正好在模拟那一档。
        #   若将来有人看到 mock 里「overview 说 0 件、list 说有 3 件」而怀疑产品有 bug，先读这段。
        #   反向保护见 `tests/test_gateway_n04.py::civil_tools_on_mock`（断言 detected=false 时 list 仍非空）。
        "civilObjects": [
            {
                "kind": "surface",
                "kindDisplay": "曲面",
                "name": "现状地形 TIN",
                "handle": "C001",
                "typeName": "Autodesk.Civil.DatabaseServices.TinSurface",
                "props": {"surfaceStyle": "地形-三角网", "pointCount": 18422,
                          "minElevation": 3.215, "maxElevation": 8.904},
                "confidence": 1.0,
                "band": "auto",
                "basis": {"kind": "civil_native", "ref": "civil3d:surface;handle=C001"},
            },
            {
                "kind": "alignment",
                "kindDisplay": "路线",
                "name": "滨江路主线",
                "handle": "C002",
                "typeName": "Autodesk.Civil.DatabaseServices.Alignment",
                "props": {"startStation": 0.0, "endStation": 1284.5,
                          "length": 1284.5, "designSpeed": 40},
                "confidence": 1.0,
                "band": "auto",
                "basis": {"kind": "civil_native", "ref": "civil3d:alignment;handle=C002"},
            },
            {
                "kind": "pipeNetwork",
                "kindDisplay": "管网",
                "name": "雨水管网 YS-1",
                "handle": "C003",
                "typeName": "Autodesk.Civil.DatabaseServices.Network",
                "props": {"partCount": 37, "pipeCount": 24, "structureCount": 13},
                "confidence": 1.0,
                "band": "auto",
                "basis": {"kind": "civil_native", "ref": "civil3d:pipeNetwork;handle=C003"},
            },
        ],
    }


def _build_layers(entities) -> list:
    buckets = {}
    for entity in entities:
        name = entity["layer"]
        bucket = buckets.setdefault(name, {"count": 0, "types": {}, "meanings": {}})
        bucket["count"] += 1
        bucket["types"][entity["dxfName"]] = bucket["types"].get(entity["dxfName"], 0) + 1
        meaning = entity["meaning"]
        if meaning:
            bucket["meanings"][meaning] = bucket["meanings"].get(meaning, 0) + 1

    result = []
    for name in sorted(buckets.keys()):
        bucket = buckets[name]
        meaning, confidence, category = LAYERS.get(name, (None, 0, "unknown"))
        if meaning:
            basis = [{"kind": "layer_map", "ref": _layer_basis(name)}]
            band = "auto" if confidence >= 90 else ("confirm" if confidence >= 60 else "hint")
            confidence_value = round(confidence / 100.0, 3)
        else:
            top_meaning = None
            top_count = 0
            for candidate, count in bucket["meanings"].items():
                if count > top_count:
                    top_meaning = candidate
                    top_count = count
            if top_meaning and top_count * 2 >= bucket["count"]:
                meaning = top_meaning
                confidence_value = 0.7
                band = "confirm"
                basis = [{"kind": "inference", "ref": "layer_majority:%s" % name}]
            else:
                meaning = None
                confidence_value = 0.3
                band = "hint"
                basis = [{"kind": "inference", "ref": "layer_unmapped:%s" % name}]
        result.append({
            "name": name,
            "entityCount": bucket["count"],
            "types": bucket["types"],
            "meaning": meaning,
            "confidence": confidence_value,
            "band": band,
            "basis": basis,
            "category": category,
        })
    return result


def _build_anomalies(entities) -> list:
    anomalies = []
    point_groups = {}
    for entity in entities:
        if entity["isProxy"]:
            anomalies.append({
                "code": "proxy_object",
                "severity": "warn",
                "handle": entity["handle"],
                "layer": entity["layer"],
                "message": "代理对象（ACAD_PROXY_ENTITY）：Civil 3D 未安装，无法识别为原生专业对象，置信度 0",
                "evidence": {"dxfName": entity["dxfName"], "dotNetType": entity["dotNetType"]},
            })
        if entity["dxfName"] in ("LINE", "LWPOLYLINE", "ARC") and entity["length"] < 1e-6:
            anomalies.append({
                "code": "zero_length",
                "severity": "warn",
                "handle": entity["handle"],
                "layer": entity["layer"],
                "message": "零长度几何（%s）：长度 < 1e-6，可能是残留图元" % entity["dxfName"],
                "evidence": {"length": entity["length"]},
            })
        if entity["hasExtents"] and (abs(entity["min"][0]) > 1e9 or abs(entity["min"][1]) > 1e9
                                     or abs(entity["max"][0]) > 1e9 or abs(entity["max"][1]) > 1e9):
            anomalies.append({
                "code": "extreme_coord",
                "severity": "error",
                "handle": entity["handle"],
                "layer": entity["layer"],
                "message": "坐标绝对值超过 1e9，可能是错误基准点或误输入",
                "evidence": {"minX": entity["min"][0], "minY": entity["min"][1]},
            })
        if entity["rawTextLength"] > 500:
            anomalies.append({
                "code": "extremely_long_text",
                "severity": "warn",
                "handle": entity["handle"],
                "layer": entity["layer"],
                "message": "文字长度 %d 超过 500 字符；按涉密处理，不回显内容" % entity["rawTextLength"],
                "evidence": {"length": entity["rawTextLength"]},
            })
        for tag, value in (entity["attributes"] or {}).items():
            if not value:
                anomalies.append({
                    "code": "empty_attr",
                    "severity": "info",
                    "handle": entity["handle"],
                    "layer": entity["layer"],
                    "message": "块属性为空：%s" % tag,
                    "evidence": {"tag": tag},
                })
        if entity["dxfName"] == "POINT":
            key = "%.3f,%.3f" % (entity["geometry"]["position"][0], entity["geometry"]["position"][1])
            point_groups.setdefault(key, []).append(entity)

    for key, group in point_groups.items():
        if len(group) < 2:
            continue
        first = group[0]
        anomalies.append({
            "code": "duplicate_point",
            "severity": "info",
            "handle": first["handle"],
            "layer": first["layer"],
            "message": "同一坐标存在 %d 个点对象（重复点）" % len(group),
            "evidence": {
                "count": len(group),
                "position": first["geometry"]["position"],
                "handles": [item["handle"] for item in group[:5]],
            },
        })

    anomalies.append({
        "code": "missing_unit",
        "severity": "warn",
        "handle": None,
        "layer": None,
        "message": "图纸未设置 INSUNITS（=0），长度单位不可推断；所有长度按图纸单位原样返回",
        "evidence": {"insUnits": 0},
    })
    return anomalies


def _build_disciplines(layers) -> list:
    names = {
        "roadway": "道路", "site": "场地", "terrain": "场地/地形", "drainage": "排水",
        "annotation": "标注", "frame": "图框", "unknown": "未识别",
    }
    buckets = {}
    for layer in layers:
        key = names.get(layer["category"], "未识别")
        bucket = buckets.setdefault(key, {"count": 0, "confidence": 0.0})
        bucket["count"] += layer["entityCount"]
        bucket["confidence"] += layer["confidence"] * layer["entityCount"]
    result = []
    for name, bucket in buckets.items():
        result.append({
            "name": name,
            "entityCount": bucket["count"],
            "confidence": round(bucket["confidence"] / bucket["count"], 3) if bucket["count"] else 0.0,
        })
    result.sort(key=lambda item: item["entityCount"], reverse=True)
    return result


if __name__ == "__main__":  # 手工自检：python mockhost/mock_drawing.py
    import json

    model = build_model()
    print(json.dumps({
        "entities": model["counts"]["entities"],
        "layers": model["counts"]["layers"],
        "proxy": model["counts"]["proxyEntities"],
        "anomalies": len(model["anomalies"]),
        "codes": sorted({item["code"] for item in model["anomalies"]}),
    }, ensure_ascii=False, indent=2))
