"""工具目录（Python 侧镜像，由 protocol/tools.schema.json 驱动）。

唯一真相源是 protocol/tools.schema.json：本模块启动时读取其原始字节，
计算 toolCatalogHash = "sha256:" + sha256(bytes)[:16]，并用同一份 schema 校验入参，
避免「Python 手抄一份 schema 后与宿主漂移」。
"""

from __future__ import annotations

import hashlib
import json
import os

import config

_SCHEMA_PATH = os.path.join(config.project_root(), "protocol", "tools.schema.json")


class CatalogError(Exception):
    """工具目录不可用（部署缺陷）。"""


def _load() -> tuple[dict, bytes]:
    try:
        with open(_SCHEMA_PATH, "rb") as handle:
            raw = handle.read()
        document = json.loads(raw.decode("utf-8"))
    except (OSError, ValueError) as exc:
        raise CatalogError("无法读取工具目录 %s：%s" % (_SCHEMA_PATH, exc))
    if not isinstance(document, dict) or not isinstance(document.get("tools"), list):
        raise CatalogError("工具目录格式非法：缺少 tools 数组")
    return document, raw


_DOCUMENT, _RAW_BYTES = _load()

TOOL_CATALOG_HASH = "sha256:" + hashlib.sha256(_RAW_BYTES).hexdigest()[:16]
PROTOCOL_VERSION = _DOCUMENT.get("protocolVersion", "1.0")

#: name -> tool 定义（与 protocol/tools.schema.json 一一对应）
TOOLS = {tool["name"]: tool for tool in _DOCUMENT["tools"]}

#: 目录顺序（tools/list 返回顺序与 schema 一致）
TOOL_LIST = list(_DOCUMENT["tools"])

#: 写域前缀。`write_` 是 M1 自己的写工具命名空间（可放行，但仍需开关+令牌）；
#: 其余是底层方法名前缀 —— **永远**不该被模型直接调用。
WRITE_DOMAIN_PREFIXES = ("write_", "cad_", "civil_", "dyn_", "file_", "rev_", "p3d_", "calc_", "skill_")


def allowed_write_tool(name: str) -> bool:
    """是否属于「可以放行到宿主」的写工具：必须同时是 write_ 命名空间且在本目录里。

    底层方法名（cad_/civil_/…）即使命中目录也不放行 —— 它们不在 schema 里，
    且宿主侧 ToolRouter 还有第二道同样的判定。
    """
    if not isinstance(name, str) or not name.lower().startswith("write_"):
        return False
    return name in TOOLS

#: 需要声明 outputSchema 的工具（设计 §4.3：只在确定性强的工具上声明）
_OUTPUT_SCHEMA_TOOLS = ("read_overview", "read_anomalies")


# ---------------- 查询 ----------------


def exists(name: str) -> bool:
    return name in TOOLS


def tool(name: str) -> dict:
    return TOOLS.get(name) or {}


def schema(name: str) -> dict:
    return tool(name).get("inputSchema") or {"type": "object", "properties": {}}


def annotations(name: str) -> dict:
    value = tool(name).get("annotations")
    if isinstance(value, dict):
        return value
    return {
        "readOnlyHint": True,
        "destructiveHint": False,
        "idempotentHint": True,
        "openWorldHint": False,
    }


def domain(name: str) -> str:
    return tool(name).get("domain") or "unknown"


def description(name: str) -> str:
    return tool(name).get("description") or ""


def is_write_domain(name: str) -> bool:
    """是否属于写域（cad_/civil_/dyn_/file_/rev_/p3d_/calc_/skill_）。"""
    if not isinstance(name, str):
        return False
    lowered = name.lower()
    return any(lowered.startswith(prefix) for prefix in WRITE_DOMAIN_PREFIXES)


def _wants_output_schema(name: str) -> bool:
    if name in _OUTPUT_SCHEMA_TOOLS:
        return True
    return domain(name) == "sys"


def list_tools() -> list:
    """tools/list 返回体（M0 一次返回全部，≤20 个，无需 cursor）。"""
    result = []
    for item in TOOL_LIST:
        name = item["name"]
        entry = {
            "name": name,
            "title": item.get("title") or name,
            "description": item.get("description") or "",
            "inputSchema": item.get("inputSchema") or {"type": "object", "properties": {}},
            "annotations": annotations(name),
        }
        if _wants_output_schema(name) and isinstance(item.get("outputSchema"), dict):
            entry["outputSchema"] = item["outputSchema"]
        result.append(entry)
    return result


# ---------------- 入参校验（JSON Schema 子集） ----------------

_TYPE_MAP = {
    "object": dict,
    "array": list,
    "string": str,
    "boolean": bool,
}


def _type_ok(value, expected) -> bool:
    if expected == "null":
        return value is None
    if expected == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if expected == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if expected == "boolean":
        return isinstance(value, bool)
    if expected == "string":
        return isinstance(value, str)
    if expected == "object":
        return isinstance(value, dict)
    if expected == "array":
        return isinstance(value, list)
    return True


def _validate(value, schema_node: dict, path: str, errors: list) -> None:
    """按 schema 子集递归校验；错误累积到 errors（形如 "params.limit: must be in [1,100]"）。"""
    if not isinstance(schema_node, dict):
        return

    # anyOf / oneOf：任一分支通过即可
    for combinator in ("anyOf", "oneOf"):
        branches = schema_node.get(combinator)
        if isinstance(branches, list) and branches:
            best = None
            for branch in branches:
                branch_errors = []
                _validate(value, branch, path, branch_errors)
                if not branch_errors:
                    return
                if best is None or len(branch_errors) < len(best):
                    best = branch_errors
            errors.extend(best or [])
            return

    if "const" in schema_node:
        if value != schema_node["const"]:
            errors.append("%s: must equal %r" % (path, schema_node["const"]))
            return

    expected_type = schema_node.get("type")
    if expected_type is not None:
        types = expected_type if isinstance(expected_type, list) else [expected_type]
        if not any(_type_ok(value, item) for item in types):
            errors.append("%s: must be %s" % (path, "/".join(str(item) for item in types)))
            return

    enum_values = schema_node.get("enum")
    if isinstance(enum_values, list) and value not in enum_values:
        errors.append("%s: must be one of %s" % (path, enum_values))

    if isinstance(value, str):
        max_length = schema_node.get("maxLength")
        if isinstance(max_length, int) and len(value) > max_length:
            errors.append("%s: maxLength=%d, got %d" % (path, max_length, len(value)))
        min_length = schema_node.get("minLength")
        if isinstance(min_length, int) and len(value) < min_length:
            errors.append("%s: minLength=%d, got %d" % (path, min_length, len(value)))
        pattern = schema_node.get("pattern")
        if isinstance(pattern, str) and pattern:
            import re

            try:
                if re.search(pattern, value) is None:
                    errors.append("%s: does not match pattern %s" % (path, pattern))
            except re.error:
                pass

    if isinstance(value, (int, float)) and not isinstance(value, bool):
        minimum = schema_node.get("minimum")
        if isinstance(minimum, (int, float)) and value < minimum:
            errors.append("%s: must be >= %s" % (path, minimum))
        maximum = schema_node.get("maximum")
        if isinstance(maximum, (int, float)) and value > maximum:
            errors.append("%s: must be <= %s" % (path, maximum))

    if isinstance(value, dict):
        properties = schema_node.get("properties") or {}
        required = schema_node.get("required") or []
        for key in required:
            if key not in value:
                errors.append("%s.%s: required" % (path, key))
        if schema_node.get("additionalProperties") is False:
            for key in value.keys():
                if key not in properties:
                    errors.append("%s.%s: unexpected field" % (path, key))
        for key, child_schema in properties.items():
            if key in value:
                _validate(value[key], child_schema, "%s.%s" % (path, key), errors)

    if isinstance(value, list):
        max_items = schema_node.get("maxItems")
        if isinstance(max_items, int) and len(value) > max_items:
            errors.append("%s: maxItems=%d, got %d" % (path, max_items, len(value)))
        min_items = schema_node.get("minItems")
        if isinstance(min_items, int) and len(value) < min_items:
            errors.append("%s: minItems=%d, got %d" % (path, min_items, len(value)))
        item_schema = schema_node.get("items")
        if isinstance(item_schema, dict):
            for index, item in enumerate(value):
                _validate(item, item_schema, "%s[%d]" % (path, index), errors)


def validate(name: str, arguments) -> list:
    """校验工具入参；返回错误字符串列表（空列表表示通过）。"""
    if not exists(name):
        return ["tool: unknown tool %r" % (name,)]
    args = arguments if arguments is not None else {}
    if not isinstance(args, dict):
        return ["arguments: must be object"]
    errors = []
    _validate(args, schema(name), "arguments", errors)
    return errors


# ============================================================================
# N04-B：18 个 canonical 工具 + 底层方法映射（网关是唯一出口）
#
# 三层口径，**必须一致**（`tests/test_gateway_n04.py` 逐层断言）：
#
#   ① `protocol/tools.schema.json` 的 `tools[]` —— 对外契约（模型唯一能看到的集合）
#   ② 本模块的 `CANONICAL_TOOLS` / `UPSTREAM_METHODS` —— 网关的映射表
#   ③ `tools/list` 的返回体 —— 实际送到模型手上的集合
#
# 底层 new-acad 有 430 个方法（`MethodNames.g.cs`），**一个都不进模型视野**：
# 模型只说 18 个 canonical 名，网关负责翻译。写域方法（create*/delete*/edit*…）
# 根本没有映射项，因此既不可能被调用，也不会因为「方法名恰好被拼出来」而被转发。
# ============================================================================

#: 对外唯一的工具集合（顺序 = schema 顺序 = tools/list 顺序）。
CANONICAL_TOOLS = tuple(tool["name"] for tool in TOOL_LIST)

#: 只在 net8 档（new-acad 插件）存在的两个工具。用户已批准新增。
#: net48 老宿主（`host/`）与它的替身 `mockhost/` 不实现它们 —— 这是**已知且显式**的差异，
#: 不是遗漏：B 侧移植属 N05/N06，届时 `host/` 补齐后本常量应清空并把 18 个都算作 NET48 覆盖。
NET8_ONLY_TOOLS = ("read_civil_list", "read_civil_object")

#: net48 老宿主实现的那 16 个（`host/Tools/**` 与 `mockhost/` 都覆盖它们）。
NET48_TOOLS = tuple(name for name in CANONICAL_TOOLS if name not in NET8_ONLY_TOOLS)

#: canonical 工具 → 底层方法名。值为 `None` 表示「网关本地合成，不打底层」。
#:
#: 为什么要 `None`：sys_* 与 sys_list_instances 的信息（实例目录、端口、令牌是否存在）
#: 本来就在网关手上，绕一圈去问插件只会更慢、更容易被伪造。
UPSTREAM_METHODS = {
    # ---- 网关本地合成 ----
    "sys_ping": None,
    "sys_list_instances": None,
    "sys_capabilities": None,
    "sys_version": None,
    # ---- 底层 new-acad 方法（`MethodNames.g.cs` 里逐个核对过） ----
    "sys_status": "getCivil3DHealth",
    "sys_health": "getCivil3DHealth",
    "read_overview": "getDrawingOverview",
    "read_layers": "getLayers",
    "read_query": "getEntitiesInfo",
    "read_describe": "getEntityInfo",
    "read_preview": "exportImageView",
    "read_civil_list": "getCivilObjectsInfo",
    "read_civil_object": "getCivilObjectInfo",
    # ---- 任务控制 ----
    "task_status": "getJobStatus",
    "task_cancel": "cancelJob",
    "task_reconcile": "getJobStatus",
    # ---- 无底层对应：显式不可用，绝不静默 ----
    # read_anomalies 是网关侧的语义分析（跨对象规则），底层没有等价方法。
    "read_anomalies": None,
    # task_list 在 net8 档没有等价概念（new-acad 只有 job 单查，没有列举）。
    "task_list": None,
    # ---- M1 写通道：**宿主自己实现**，不经底层（底层写能力永远不直连） ----
    "write_preview": None,
    "write_approve": None,
    "write_insert_points": None,
    "write_command": None,
    # B 路线：执行在基座（new-acad），网关到宿主，宿主再走 TCP 调基座；对底层插件不是同一进程
    "read_civil_surfaces": None,
    "read_civil_alignments": None,
    "write_surface_breakline": None,
    # ---- v3.0 Phase 1 & Phase 3 原子能力（均在宿主实现） ----
    "export_surface_grid": None,
    "export_layer_geojson": None,
    "read_active_selection": None,
    "read_viewport_snapshot": None,
    "read_object_xdata": None,
    "write_object_xdata": None,
    "write_undo": None,
    "write_alignment_plan": None,
    "write_eval_csharp": None,
    "write_entities": None,
    "preview_transient": None,
    "batch_side_database": None,
}

#: 网关本地合成、但**不**打底层的工具（`UPSTREAM_METHODS[x] is None` 的子集，
#: 其中 read_anomalies / task_list 属「本地也做不了」的一类，需区分）。
LOCAL_COMPOSED_TOOLS = ("sys_ping", "sys_list_instances", "sys_capabilities", "sys_version")

#: 由**网关**转调基座（new-acad / civil3d-mcp）完成的工具（B 路线，docs/10 §十五）。
#:
#: 为什么不在宿主里做：宿主的主线程一旦同步等基座，就会与基座的主线程队列互等
#: （2026-11-23 01:07 实测死锁，基座日志 `listSurfaces [1] cancelled durationMs=15005`）。
#: 网关是独立进程，没有这个问题；审计/预算/落库也都在这里。
GATEWAY_BASE_TOOLS = ("read_civil_surfaces", "read_civil_alignments")

#: net8 档下**明确不可用**的 canonical 工具 → 原因。网关返回 E2001 + 该 detail。
NET8_UNAVAILABLE = {
    "read_anomalies": "anomaly_analysis_requires_net48_host",
    "task_list": "job_enumeration_not_supported_on_net8",
}


def canonical() -> tuple:
    """对外可见的 18 个工具名（供断言与 `tools/list` 交叉核对）。"""
    return CANONICAL_TOOLS


def is_canonical(name: str) -> bool:
    return name in CANONICAL_TOOLS


def upstream_method(name: str):
    """canonical → 底层方法名；`None` 表示网关本地合成或明确不可用。"""
    return UPSTREAM_METHODS.get(name)


def requires_net8(name: str) -> bool:
    """该工具是否只在 net8 档存在（net48 老宿主会回 E2001）。"""
    return name in NET8_ONLY_TOOLS


def net8_unavailable_reason(name: str):
    """net8 档下不可用的原因；可用时返回 None。"""
    return NET8_UNAVAILABLE.get(name)


def write_domain_hint(name: str):
    """写域拒绝时给模型的可执行提示（说明它命中了哪个前缀）。"""
    if not isinstance(name, str):
        return None
    lowered = name.lower()
    for prefix in WRITE_DOMAIN_PREFIXES:
        if lowered.startswith(prefix):
            if lowered.startswith("write_"):
                return "写通道未开启；write_* 只在 config.json 的 write.enabled=true 时可用"
            return "命中底层方法名前缀 %r；这些能力永远不直接暴露给模型" % prefix
    return None


def domains_catalog() -> dict:
    """schema 的 `domains` 段（网关本地合成 `sys_capabilities` 时用，避免手抄一份域定义）。"""
    value = _DOCUMENT.get("domains")
    return dict(value) if isinstance(value, dict) else {}


def canonical_set_matches_schema() -> bool:
    """自证：`CANONICAL_TOOLS` 恰好等于 schema 里的工具名集合（多一个少一个都是 False）。

    这是给静态审查与契约测试用的结构性断言入口 —— 防止将来有人「顺手」把某个底层方法
    塞进目录，或反过来漏登记。
    """
    return tuple(name for name in CANONICAL_TOOLS) == tuple(tool["name"] for tool in _DOCUMENT["tools"])


