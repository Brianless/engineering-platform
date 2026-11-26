"""双端一致性测试（C# 宿主 ↔ Python MCP ↔ mockhost）。

协议里有若干「两边必须算出同一字节」的约定，错了不会报错、只会静默错连/错摘要，
所以单独立一组断言：

    1. userKey / userHash8 / 管道名 / 发现文件路径（host/Pipe/PipeName.cs ↔ pipeclient.py ↔ mockhost）
    2. toolCatalogHash（protocol/tools.schema.json 原始字节的 sha256[:16]）
    3. 分帧编码字节（长度前缀 + 紧凑 UTF-8）
    4. paramsDigest canonical 规则（与 host/Protocol/ParamsDigest.cs 同算法）
    5. 工具名/字段命名风格：工具 snake_case、字段 camelCase（不允许混用）
    6. 错误码/告警码只允许出现在 protocol/errors.json 里
"""

from __future__ import annotations

import hashlib
import json
import os
import re

import harness
import budget as budget_module
import config as config_module
import pipeclient
import tools_catalog

ROOT = harness.ROOT


def _read(rel_path: str) -> str:
    with open(os.path.join(ROOT, rel_path), "r", encoding="utf-8") as handle:
        return handle.read()


def run() -> dict:
    suite = harness.Suite("parity")

    # ---------- 1. 用户标识与管道名 ----------
    def identity():
        import mock_host
        key = pipeclient.user_key()
        suite.eq(mock_host.user_key(), key, "userKey 双端一致（USERDOMAIN\\USERNAME 大写）")
        suite.eq(mock_host.user_hash8(), pipeclient.user_hash8(),
                 "userHash8 双端一致（sha256(utf8(key))[:8]）")
        suite.matches(pipeclient.user_hash8(), r"^[0-9a-f]{8}$", "userHash8 为 8 位小写 hex")

        instance_id = "a1b2c3d4"
        suite.eq(mock_host.build_pipe_name("mock", instance_id),
                 pipeclient.pipe_name("mock", instance_id),
                 "管道名双端一致")
        expected_pipe = r"\\.\pipe\engineering-platform.v1.%s.mock.%s" % (
            pipeclient.user_hash8(), instance_id)
        suite.eq(pipeclient.pipe_name("mock", instance_id), expected_pipe,
                 "管道名符合 engineering-platform.v1.<userHash8>.<app>.<instance>")

        suite.eq(mock_host.discovery_file_path("mock", instance_id),
                 pipeclient.discovery_path("mock", instance_id),
                 "发现文件路径双端一致")
        suite.matches(os.path.basename(pipeclient.discovery_path("mock", instance_id)),
                      r"^[0-9a-f]{8}\.mock\.a1b2c3d4\.json$",
                      "发现文件名 <userHash8>.<app>.<instanceId>.json")

        # C# 侧同一算法的字面量（PipeName.cs 用 sha256 + hex[:8]）
        source = _read("host/Pipe/PipeName.cs")
        suite.contains(source, "sha256", "C# PipeName.cs 使用 sha256 派生 userHash8")
        suite.contains(source, "engineering-platform.v1", "C# 管道名前缀一致")

    # ---------- 2. toolCatalogHash ----------
    def catalog_hash():
        import mock_host
        schema_path = os.path.join(ROOT, "protocol", "tools.schema.json")
        with open(schema_path, "rb") as handle:
            raw = handle.read()
        expected = "sha256:" + hashlib.sha256(raw).hexdigest()[:16]
        suite.eq(tools_catalog.TOOL_CATALOG_HASH, expected,
                 "MCP tools_catalog.TOOL_CATALOG_HASH = sha256(schema)[:16]")
        suite.eq(mock_host.tool_catalog_hash(), expected,
                 "mockhost toolCatalogHash 与 MCP 一致")
        suite.is_true(len(tools_catalog.TOOL_LIST) >= 18,
                      "工具目录 ≥18 个（数量不写死；集合一致性由 gateway_n04 卡）",
                      "actual=%d" % len(tools_catalog.TOOL_LIST))

        host_source = _read("host/Protocol/ToolCatalog.cs")
        suite.contains(host_source, "protocol/tools.schema.json",
                       "C# 侧读取同一份 schema 资源（保证哈希一致）")

    # ---------- 3. 分帧字节 ----------
    def framing_bytes():
        import mock_host
        payload = {"tool": "read_overview", "params": {"refresh": False}, "注释": "中文"}
        suite.eq(mock_host.encode_frame(payload), pipeclient.encode_frame(payload),
                 "分帧字节双端一致（长度前缀 + 紧凑 UTF-8）")
        suite.eq(pipeclient.encode_frame("abc")[:4], (3).to_bytes(4, "little"),
                 "长度前缀为小端 uint32")
        suite.eq(config_module.MESSAGE_LIMIT_BYTES, 1048576, "消息上限 1 MiB")
        suite.eq(config_module.STDIO_LINE_LIMIT, 8 * 1024 * 1024, "stdio 单行上限 8 MiB")

        # C# 常量必须同值（源码写的是 1024 * 1024）
        framing_source = _read("host/Pipe/Framing.cs")
        suite.contains(framing_source, "MaxPayloadBytes = 1024 * 1024",
                       "C# Framing 上限为 1024 * 1024 = 1 MiB")
        suite.contains(framing_source, "DiscardBufferBytes",
                       "C# 超限帧先读完丢弃再回 E1001（防流错位）")

    # ---------- 4. paramsDigest ----------
    def params_digest():
        canonical_source = _read("host/Protocol/ParamsDigest.cs")
        suite.contains(canonical_source, "SerializeCanonical",
                       "C# 侧用 SerializeCanonical 生成规范化 JSON")
        suite.contains(canonical_source, "sha256", "C# 侧用 sha256")
        json_source = _read("host/Protocol/Json.cs")
        suite.contains(json_source, "sorted.Sort(StringComparer.Ordinal)",
                       "C# canonical 模式按序数比较排序键（与 Python sort_keys 等价）")
        suite.contains(json_source, "canonical", "C# JsonCodec 有 canonical 模式")
        # 与 Python 侧同一结果（算法级断言；C# 运行期一致性见报告「已知限制」）
        value = {"b": [1, 2], "a": {"y": True, "x": None}, "中文": "值"}
        canonical = budget_module.canonical_json(value)
        suite.eq(canonical, '{"a":{"x":null,"y":true},"b":[1,2],"中文":"值"}',
                 "canonical 规则：递归键排序 + 紧凑分隔符 + 中文不转义")
        suite.eq(budget_module.params_digest(value),
                 "sha256:" + hashlib.sha256(canonical.encode("utf-8")).hexdigest(),
                 "paramsDigest = sha256(UTF-8 canonical)")

    # ---------- 5. 命名风格 ----------
    def naming():
        offender_snake = []
        for tool in tools_catalog.TOOL_LIST:
            name = tool["name"]
            if not re.match(r"^(sys|read|task|write|export|preview|batch)_[a-z0-9_]+$", name):
                offender_snake.append(name)
            for key in tool.get("inputSchema", {}).get("properties", {}):
                if "_" in key:
                    suite.fail("入参字段出现 snake_case：%s.%s" % (name, key))
                if not re.match(r"^[a-z][A-Za-z0-9]*$", key):
                    suite.fail("入参字段不是 camelCase：%s.%s" % (name, key))
            for key in tool.get("outputSchema", {}).get("properties", {}):
                if "_" in key:
                    suite.fail("出参字段出现 snake_case：%s.%s" % (name, key))
        suite.eq(offender_snake, [], "工具名全部 snake_case（sys/read/task/write/export/preview/batch 命名空间）")

        # 合法平台域集合（底层域 cad/civil/... 仍不许出现）
        domains = {tool["name"].split("_")[0] for tool in tools_catalog.TOOL_LIST}
        suite.eq(domains, {"sys", "read", "task", "write", "export", "preview", "batch"},
                 "工具域只有 sys/read/task/write/export/preview/batch")

        # 写域判定必须覆盖 8 个前缀
        for prefix in ("cad_", "civil_", "dyn_", "file_", "rev_", "p3d_", "calc_", "skill_"):
            suite.is_true(tools_catalog.is_write_domain(prefix + "anything"),
                          "%s 判定为写域" % prefix)
        for name in ("read_overview", "sys_ping", "task_list"):
            suite.is_false(tools_catalog.is_write_domain(name), "%s 不是写域" % name)

    # ---------- 6. 错误码 ----------
    def error_codes():
        with open(os.path.join(ROOT, "protocol", "errors.json"), "r", encoding="utf-8") as handle:
            spec = json.load(handle)
        known = {item["code"] for item in spec["errors"]}
        warnings = {item["code"] for item in spec["warnings"]}
        suite.eq(len(known), 19, "errors.json 含 19 个错误码（14 E + 5 C）")
        suite.eq(len(warnings), 9, "errors.json 含 9 个告警码")

        # errors.json 必须覆盖设计列出的 M0 活跃码
        for code in ("E1001", "E1003", "E1004", "E1005", "E1006", "E2001", "E2003",
                     "E3002", "E4001", "E5001", "C1001", "C1002", "C1003", "C1004", "C1005"):
            suite.contains(known, code, "errors.json 覆盖 %s" % code)

        import mock_host
        for code in mock_host.ERROR_META:
            suite.contains(known, code, "mockhost 使用的 %s 在 errors.json 中定义" % code)
        for code in mock_host.WARNING_MESSAGES:
            suite.contains(warnings, code, "mockhost 使用的 %s 是合法告警码" % code)

    # ---------- 7. 参数边界三端一致（schema ↔ C# 校验器 ↔ mockhost 校验器） ----------
    def param_bounds():
        """同一入参的合法区间三处必须一致，否则会出现「MCP 放行、宿主拒绝」的错位。"""
        import mock_host
        catalog_source = _read("host/Protocol/ToolCatalog.cs")
        bounds = [
            ("read_layers", "limit", 1, 200),
            ("read_query", "limit", 1, 100),
            ("read_anomalies", "limit", 1, 100),
            ("read_preview", "width", 200, 2400),
            ("read_preview", "height", 200, 1600),
            ("task_list", "limit", 1, 200),
        ]
        for tool, param, low, high in bounds:
            node = (tools_catalog.schema(tool).get("properties") or {}).get(param) or {}
            suite.eq((node.get("minimum"), node.get("maximum")), (low, high),
                     "schema 边界 %s.%s = [%d,%d]" % (tool, param, low, high))

            rule = (mock_host.PARAM_SPECS.get(tool) or {}).get(param) or ()
            suite.eq((rule[1] if len(rule) > 2 else None,
                      rule[2] if len(rule) > 2 else None), (low, high),
                     "mockhost 边界 %s.%s = [%d,%d]" % (tool, param, low, high))

            suite.contains(catalog_source,
                           'GetInt("%s", %d, %d' % (param, low, high),
                           "C# 校验器边界 %s.%s = [%d,%d]" % (tool, param, low, high))

        # 枚举型入参也要三端一致
        severity = (tools_catalog.schema("read_anomalies").get("properties") or {})["severity"]
        for value in ("info", "warn", "error"):
            suite.contains(severity.get("enum") or [], value,
                           "read_anomalies.severity 枚举含 %s" % value)
        preview_format = (tools_catalog.schema("read_preview").get("properties") or {})["format"]
        suite.eq(preview_format.get("const"), "png", "read_preview.format 只能是 png")

    # ---------- 8. Civil 3D 契约三端一致（第二轮新增） ----------

    def civil3d_contract():
        """Civil 3D 是「探测而非假设」的能力，契约必须三端同时改，否则会出现
        「宿主说有、MCP 不认」或「schema 有、实现不填」的错位。"""
        import mock_host

        handshake = json.loads(_read("protocol/handshake.schema.json"))
        ack = handshake["$defs"]["HelloAck"]
        for field in ("hostProduct", "hostRelease"):
            suite.contains(ack["required"], field,
                           "handshake.schema HelloAck 必填 %s" % field)
        suite.eq(ack["properties"]["hostProduct"].get("enum"), ["AutoCAD", "Civil3D"],
                 "hostProduct 枚举只有 AutoCAD / Civil3D")
        suite.eq(ack["properties"]["hostRelease"].get("pattern"), r"^R[0-9]+\.[0-9]+$",
                 "hostRelease 形如 R24.3")
        # hostVersion 语义不得被复用为 CAD 版本（实例发现文件与 instances.py 依赖它）
        suite.contains(ack["properties"]["hostVersion"].get("description") or "",
                       "插件版本", "hostVersion 仍是插件版本语义")

        envelope = json.loads(_read("protocol/envelope.schema.json"))
        response = envelope["$defs"]["HandshakeResponse"]
        for field in ("hostProduct", "hostRelease"):
            suite.contains(response["required"], field,
                           "envelope.schema HandshakeResponse 必填 %s" % field)
        for field in ("product", "release", "writeSupported"):
            suite.contains(response["properties"]["capabilities"]["properties"]["civilApi"]["properties"],
                           field, "capabilities.civilApi 声明 %s" % field)

        # C# 侧真的填了这两个字段
        host_handshake = _read("host/Protocol/Handshake.cs")
        suite.contains(host_handshake, 'ack["hostProduct"]', "C# hello_ack 填 hostProduct")
        suite.contains(host_handshake, 'ack["hostRelease"]', "C# hello_ack 填 hostRelease")
        suite.contains(host_handshake, "CivilApiProbe.ProductName",
                       "hostProduct 取自反射探测结果，不是硬编码")

        # mockhost 侧也填了
        suite.eq(mock_host.HOST_PRODUCT, "AutoCAD", "mockhost 模拟 AutoCAD 档")
        suite.matches(mock_host.HOST_RELEASE, r"^R[0-9]+\.[0-9]+$", "mockhost hostRelease 形状合法")

        # read_overview 的 civil3d 段：schema ↔ C# handler ↔ mockhost
        tools = json.loads(_read("protocol/tools.schema.json"))
        entries = {item["name"]: item for item in tools["tools"]}
        civil = entries["read_overview"]["outputSchema"]["properties"].get("civil3d")
        suite.is_true(civil is not None, "tools.schema read_overview 输出声明 civil3d 段")
        for field in ("detected", "counts", "truncated"):
            suite.contains(civil.get("required") or [], field,
                           "civil3d 必填 %s" % field)
        for kind in ("surfaces", "alignments", "profiles", "pipeNetworks", "pointGroups"):
            suite.is_true(kind in civil["properties"], "civil3d 含 %s 列表" % kind)
        suite.is_true("attributeProbe" in civil["properties"],
                      "civil3d 含 attributeProbe（可行性清单取证）")

        handlers = _read("host/Tools/ReadToolHandlers.cs")
        suite.contains(handlers, 'result["civil3d"]', "C# read_overview 填 civil3d 段")
        suite.contains(_read("mockhost/mock_host.py"), '"civil3d": {',
                       "mockhost read_overview 填 civil3d 段")

        # 原生对象 confidence 必须 1.0（最高可信），且只读
        reader = _read("host/Semantic/Civil3dReader.cs")
        suite.contains(reader, "Confidence = 1.0D", "Civil 原生对象 confidence=1.0")
        for forbidden in ("OpenMode.ForWrite", "UpgradeOpen", "AppendEntity", ".Erase("):
            suite.is_false(forbidden in reader,
                           "Civil3dReader 只读（无 %s）" % forbidden)

        # 写通道仍然关闭（E2003 在 ToolRouter 里统一拒绝）
        router = _read("host/Protocol/ToolRouter.cs")
        suite.contains(router, "write_channel_disabled_m0",
                       "写域仍返回 E2003 write_channel_disabled_m0")

    # ---------- 8. 第三轮缺陷收口（D1/D3/D6/D7）----------
    def defect_closure_contract():
        """QA 复核挖出的 4 类缺陷，三端（schema / C# / mockhost）必须同口径。

        这些缺陷的共同特征：单看一端都是「自洽的」，只有把三端放一起才看得出分叉。
        """
        from_handshake = json.loads(_read("protocol/handshake.schema.json"))
        hello = from_handshake["$defs"]["Hello"]
        ack = from_handshake["$defs"]["HelloAck"]

        # D1：hello 必填项三端同口径（mockhost 以前只校验 protocolVersion）
        for field in ("clientInfo", "clientNonce"):
            suite.contains(hello["required"], field,
                           "D1 schema Hello 必填 %s" % field)
        suite.contains(hello["properties"]["clientInfo"]["required"], "version",
                       "D1 schema clientInfo 必填 version")

        handshake_src = _read("host/Protocol/Handshake.cs")
        mock_src = _read("mockhost/mock_host.py")
        for detail in ("clientInfo_missing", "clientInfo.name_missing",
                       "clientInfo.version_missing", "clientNonce_missing"):
            suite.contains(handshake_src, detail, "D1 C# 拒绝时给出 %s" % detail)
            suite.contains(mock_src, detail, "D1 mockhost 拒绝时给出 %s" % detail)

        # D3：W1002 必须真的上线（以前只构造、只写日志）
        suite.is_true("warnings" in ack["properties"],
                      "D3 schema HelloAck 声明 warnings")
        suite.contains(handshake_src, 'ack["warnings"]',
                       "D3 C# hello_ack 真的带 warnings")
        suite.contains(mock_src, '"warnings": warnings',
                       "D3 mockhost hello_ack 真的带 warnings")
        suite.contains(mock_src, 'make_warning("W1002"',
                       "D3 mockhost 次版本超前时产出 W1002")

        # D6：写域前缀大小写不敏感（三处都归一后再比）
        catalog_src = _read("host/Protocol/ToolCatalog.cs")
        suite.contains(catalog_src, "name.ToLowerInvariant()",
                       "D6 C# 写域判定先 ToLowerInvariant")
        catalog_py = _read("mcp/tools_catalog.py")
        suite.contains(catalog_py, "lowered = name.lower()",
                       "D6 MCP 写域判定先 .lower()")
        suite.contains(mock_src, "tool.upper().startswith(WRITE_DOMAIN_PREFIXES_UPPER)",
                       "D6 mockhost 写域判定先 .upper() 归一")
        suite.contains(mock_src, "WRITE_DOMAIN_PREFIXES_UPPER = tuple(",
                       "D6 mockhost 预计算大写前缀表（不逐次 upper 全表）")

        # D7：counts.layers 口径统一 = 有图元的图层数；空图层单列
        tools = json.loads(_read("protocol/tools.schema.json"))
        entries = {item["name"]: item for item in tools["tools"]}
        counts = entries["read_overview"]["outputSchema"]["properties"]["counts"]
        for field in ("layers", "layerTableRecords", "emptyLayers"):
            suite.is_true(field in counts["properties"],
                          "D7 tools.schema counts 声明 %s" % field)
        suite.contains(counts["properties"]["layers"].get("description", ""),
                       "read_layers.total",
                       "D7 schema 写明 layers == read_layers.total")

        handlers = _read("host/Tools/ReadToolHandlers.cs")
        suite.contains(handlers, 'counts["layerTableRecords"]',
                       "D7 C# 填 counts.layerTableRecords")
        suite.contains(handlers, 'counts["emptyLayers"]',
                       "D7 C# 填 counts.emptyLayers")
        suite.contains(handlers, 'counts["layers"] = usedLayers',
                       "D7 C# counts.layers 取自「有图元的图层数」")
        mock_drawing = _read("mockhost/mock_drawing.py")
        suite.contains(mock_drawing, '"layerTableRecords"',
                       "D7 mockhost 模型填 layerTableRecords")
        suite.contains(mock_drawing, '"emptyLayers"',
                       "D7 mockhost 模型填 emptyLayers")
        import mock_drawing as mock_drawing_module
        suite.is_true(len(mock_drawing_module.EMPTY_LAYER_NAMES) >= 1,
                      "D7 mockhost 至少有一个空图层（否则口径差异在 mock 里测不出来）")

        # D8：夹具在**活动文档**上建对象，必须另有「活动文档在 sandbox 内」的门禁，
        # 且门禁顺序必须在任何写动作之前（输出目录 → 活动文档 → 建目录 → 建对象 → SaveAs）。
        fixture_src = _read("host/Commands/DevFixtureCommand.cs")
        suite.contains(fixture_src, "CheckActiveDocumentInsideSandbox()",
                       "D8 夹具入口调用活动文档门禁")
        suite.contains(fixture_src, "internal static string CheckActiveDocumentInsideSandbox()",
                       "D8 活动文档门禁有独立实现")
        suite.contains(fixture_src, "active_doc_not_in_sandbox",
                       "D8 越界细节可定位")
        suite.contains(fixture_src, "active_doc_unnamed",
                       "D8 未命名文档也拒绝（fail-closed）")
        gate_call = fixture_src.find("CheckActiveDocumentInsideSandbox();")
        gate_body = fixture_src.find("internal static string CheckActiveDocumentInsideSandbox()")
        first_write = fixture_src.find("doc.Database.SaveAs")
        first_create = fixture_src.find("object createdSurface = null;")
        suite.is_true(0 < gate_call < first_create < first_write,
                      "D8 门禁在任何写动作之前（门禁:%d < 建对象:%d < SaveAs:%d）"
                      % (gate_call, first_create, first_write))
        suite.contains(fixture_src, "Path.GetFullPath(name)",
                       "D8 用活动文档全路径判 sandbox（不是只看输出目录）")
        suite.is_true(gate_body > gate_call,
                      "D8 门禁实现位于调用点之后（同一文件内）")

        # NIT-6：E4001 的 detail 双端同一组字面量（以前 C# major_mismatch / mock protocol_version_incompatible:2.0）
        errors = json.loads(_read("protocol/errors.json"))
        e4001 = [item for item in errors["errors"] if item.get("code") == "E4001"][0]
        for detail in ("invalid_protocol_version", "major_mismatch",
                       "handshake_required", "session_expired"):
            suite.contains(e4001["detailExamples"], detail,
                           "NIT-6 errors.json E4001 声明 detail=%s" % detail)
        suite.contains(mock_src, '"handshake_required"',
                       "NIT-6 mockhost 未握手 → handshake_required（与 C# 对齐）")
        suite.is_false('"protocol_version_incompatible' in mock_src,
                       "NIT-6 mockhost 不再自造 protocol_version_incompatible 字面量（注释里提到不算）")

        # NIT-7：预览也要给 drilldown（与其余四个读工具同形状）
        suite.contains(handlers, 'result["drilldown"] = previewDrilldown',
                       "NIT-7 C# read_preview 输出 drilldown")
        suite.contains(mock_src, '"drilldown": drilldown',
                       "NIT-7 mockhost read_preview 输出 drilldown")
        suite.contains(mock_src, '"tool": "read_preview"',
                       "NIT-7 mockhost 预览下钻里含「重画」建议")

    for label, func in (
        ("用户标识与管道名", identity),
        ("toolCatalogHash", catalog_hash),
        ("分帧字节", framing_bytes),
        ("paramsDigest", params_digest),
        ("命名风格", naming),
        ("错误码表", error_codes),
        ("参数边界", param_bounds),
        ("Civil 3D 契约三端一致", civil3d_contract),
        ("缺陷收口 D1/D3/D6/D7", defect_closure_contract),
    ):
        suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
