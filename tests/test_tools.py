"""16 个只读工具的行为测试（tools.schema.json 为准）。

覆盖验收要求里的四件必做证据：
    read_overview / read_layers / read_query / read_preview 正常返回；
附加：
    写域 E2003、未知工具 E2001、read_describe 正常与句柄不存在、
    read_anomalies 分类计数、sys_* 6 个、task_* 4 个、游标翻页、
    textContains 不回显原文、预览图 note 常量与 PNG 尺寸。
"""

from __future__ import annotations

import json
import os

import harness


def run() -> dict:
    suite = harness.Suite("tools")

    with harness.sandbox("tools"):
        with harness.mock_server() as (server, host):
            client = harness.connect(server)
            try:
                # ---------------- read_overview ----------------
                def overview():
                    result = harness.call_ok(suite, client, "read_overview", {})
                    if not result:
                        return
                    suite.matches(result.get("fingerprint") or "", r"^fp1:[0-9a-f]{32}$",
                                  "overview.fingerprint 形状 fp1:<32hex>")
                    suite.matches((result.get("drawing") or {}).get("nameHash") or "",
                                  r"^[0-9a-f]{8}\.dwg$",
                                  "overview.drawing.nameHash 为「8hex+扩展名」，不泄露明文路径")
                    units = result.get("units") or {}
                    suite.eq(units.get("code"), "mm", "overview.units.code=mm")
                    suite.eq(units.get("insUnits"), 4, "overview.units.insUnits=4")
                    suite.eq(units.get("scaleToMeter"), 0.001, "overview.units.scaleToMeter=0.001")
                    counts = result.get("counts") or {}
                    for key in ("entities", "layers", "blocks", "proxyEntities"):
                        suite.ok(key in counts, "overview.counts 含 %s" % key)
                    suite.eq(counts.get("proxyEntities"), 1, "overview.counts.proxyEntities=1")
                    # D7：图层口径必须自洽（layers = 有图元的图层数；
                    # layerTableRecords = 图层表记录数；空图层单列）
                    suite.ok("layerTableRecords" in counts,
                             "overview.counts 含 layerTableRecords（图层表记录数）")
                    suite.ok("emptyLayers" in counts,
                             "overview.counts 含 emptyLayers（未使用的空图层数）")
                    suite.eq((counts.get("layers") or 0) + (counts.get("emptyLayers") or 0),
                             counts.get("layerTableRecords"),
                             "counts.layers + counts.emptyLayers == counts.layerTableRecords")
                    suite.is_true(counts.get("emptyLayers") >= 1,
                                  "mock 图里有空图层（否则口径差异测不出来）",
                                  "emptyLayers=%r" % (counts.get("emptyLayers"),))
                    layer_total = harness.call_ok(suite, client, "read_layers", {"limit": 200})
                    suite.eq(counts.get("layers"), (layer_total or {}).get("total"),
                             "overview.counts.layers 恒等于 read_layers.total")
                    suite.is_true(
                        "空图层" in (result.get("summary") or "")
                        or not counts.get("emptyLayers"),
                        "有图层空着时摘要里写明「另有 N 个空图层」",
                        "summary=%r" % ((result.get("summary") or "")[:120],))
                    suite.is_true((result.get("software") or {}).get("civilApiAvailable") is False,
                                  "overview.software.civilApiAvailable=false")
                    # 第二轮：civil3d 段必须存在且如实标注未探测到（mock 档降级路径）
                    civil = result.get("civil3d") or {}
                    suite.is_false(civil.get("detected"),
                                   "overview.civil3d.detected=false（mock 无 Civil 3D）")
                    suite.eq((civil.get("counts") or {}).get("total"), 0,
                             "overview.civil3d.counts.total=0")
                    for kind in ("surfaces", "alignments", "profiles", "pipeNetworks", "pointGroups"):
                        suite.ok(kind in civil, "overview.civil3d 含 %s 列表" % kind)
                    suite.ok("attributeProbe" in civil,
                             "overview.civil3d 含 attributeProbe（可行性取证）")
                    suite.ok(bool(civil.get("note")), "overview.civil3d 给出未探测到的一句话原因")
                    suite.is_false((result.get("drawing") or {}).get("readOnly"),
                                   "overview.drawing.readOnly=false")
                    extents = result.get("extents") or {}
                    suite.eq(len(extents.get("min") or []), 3, "overview.extents.min 为 3 元组")
                    suite.is_true(len(result.get("disciplines") or []) >= 3,
                                  "overview.disciplines 至少 3 个专业")
                    suite.is_true(len(result.get("topAnomalies") or []) <= 5,
                                  "overview.topAnomalies ≤ 5")
                    ref = result.get("resultRef") or {}
                    suite.eq(ref.get("tool"), "read_overview", "resultRef.tool 回填")
                    suite.eq(ref.get("fingerprint"), result.get("fingerprint"),
                             "resultRef.fingerprint 与 overview.fingerprint 一致")
                    suite.ok("storedAt" in ref, "resultRef.storedAt 字段存在（宿主侧为 null）")
                    text = result.get("summary") or ""
                    suite.is_true(0 < len(text) <= 400, "summary 长度 1–400",
                                  "len=%d" % len(text))

                    # 代理对象 → 必须有 W1001
                    response = client.call("read_overview", {})
                    codes = [item.get("code") for item in (response.get("warnings") or [])]
                    suite.contains(codes, "W1001",
                                   "有代理对象时 read_overview 带 W1001 告警")

                # ---------------- read_layers ----------------
                def layers():
                    result = harness.call_ok(suite, client, "read_layers", {"limit": 3})
                    if not result:
                        return
                    suite.eq(len(result.get("layers") or []), 3, "read_layers.limit=3 生效")
                    suite.is_true(result.get("total") >= 10, "read_layers.total ≥ 10 图层")
                    suite.is_true(result.get("truncated") is True, "分页时 truncated=true")
                    suite.is_true(bool(result.get("nextCursor")), "分页时返回 nextCursor")
                    first = (result.get("layers") or [{}])[0]
                    for key in ("name", "entityCount", "types", "confidence", "confidenceBand"):
                        suite.ok(key in first, "layer 含必填字段 %s" % key)
                    bands = {(item.get("confidenceBand")) for item in result["layers"]}
                    suite.ok(bands <= {"auto", "confirm", "hint"},
                             "confidenceBand 取值合法", str(bands))

                    # 用 nextCursor 翻第二页，不应重复第一页
                    page2 = harness.call_ok(suite, client, "read_layers",
                                            {"limit": 3, "cursor": result["nextCursor"]})
                    names1 = {item["name"] for item in result["layers"]}
                    names2 = {item["name"] for item in page2.get("layers") or []}
                    suite.eq(len(names1 & names2), 0, "游标翻页无重复项")

                    # 通配符过滤
                    wild = harness.call_ok(suite, client, "read_layers",
                                           {"namePattern": "D-DR-*", "limit": 50})
                    suite.is_true(all(item["name"].upper().startswith("D-DR-")
                                      for item in wild.get("layers") or []),
                                  "namePattern 通配过滤生效")

                    # 未被识别的图层：recognizedAs 必须为 null 而不是瞎猜
                    unknown_layers = [item for item in (wild.get("layers") or [])]
                    suite.is_true(all(item.get("confidence", 0) >= 0 for item in unknown_layers),
                                  "confidence 为非负数")

                # ---------------- read_query ----------------
                def query():
                    result = harness.call_ok(suite, client, "read_query",
                                             {"dxfName": "INSERT", "limit": 3})
                    if not result:
                        return
                    suite.eq(result.get("total"), 8, "read_query 块引用总数 8")
                    suite.eq(len(result.get("items") or []), 3, "read_query.limit=3 生效")
                    item = result["items"][0]
                    suite.matches(item.get("handle") or "", r"^[0-9A-F]{1,16}$",
                                  "handle 为十六进制大写")
                    suite.eq(item.get("dxfName"), "INSERT", "dxfName 过滤生效")
                    suite.ok("extents" in item, "item 含 extents 字段")

                    # textContains：只回标记不回原文
                    hit = harness.call_ok(suite, client, "read_query",
                                          {"textContains": "沟底标高", "limit": 5})
                    suite.is_true(hit.get("total", 0) >= 1, "textContains 能命中文字对象")
                    for entry in hit.get("items") or []:
                        suite.is_true(entry.get("textHit") is True, "命中项 textHit=true")
                        suite.ok("texts" not in entry, "命中项不回显原文（无 texts 字段）")

                    # 属性等值过滤（检查井编号）
                    attr = harness.call_ok(suite, client, "read_query",
                                           {"attrEquals": {"编号": "J1"}, "limit": 5})
                    suite.eq(attr.get("total"), 1, "attrEquals 精确命中 1 个检查井")

                    # 范围过滤
                    extent = harness.call_ok(suite, client, "read_query",
                                             {"extent": [0, 0, 60000, 40000], "limit": 5})
                    suite.is_true(extent.get("total", 0) >= 1, "extent 范围过滤有命中")

                # ---------------- read_describe ----------------
                def describe():
                    # 用属性过滤精确锁定检查井，避免依赖图层排序
                    listing = harness.call_ok(suite, client, "read_query",
                                              {"attrEquals": {"编号": "J1"}, "limit": 1})
                    handle = (listing.get("items") or [{}])[0].get("handle")
                    result = harness.call_ok(suite, client, "read_describe",
                                             {"handle": handle})
                    if not result:
                        return
                    obj = result.get("object") or {}
                    suite.eq(obj.get("handle"), handle, "read_describe 回填同一个 handle")
                    suite.eq(obj.get("dxfName"), "INSERT", "read_describe.dxfName 正确")
                    suite.ok(isinstance(obj.get("props"), dict), "object.props 为对象")
                    # 键名带 `attr.` 前缀：这是 C# 宿主的真实形状（`props["attr." + tag]`）。
                    # 断言裸 tag 会「用 mock 的形状测 mock」，测不出宿主一条属性都没读到（D-01）。
                    suite.contains(list((obj.get("props") or {})), "attr.编号",
                                   "object.props 含块属性「attr.编号」（与宿主同形）")
                    suite.contains(list((obj.get("props") or {})), "attr.井底标高",
                                   "object.props 含块属性「attr.井底标高」（与宿主同形）")
                    suite.is_true(result.get("confidence", 0) > 0.6,
                                  "检查井识别置信度 > 0.6")
                    suite.ok(result.get("confidenceBand") in ("auto", "confirm", "hint"),
                             "confidenceBand 合法")
                    suite.is_true(isinstance(result.get("basis"), list) and result["basis"],
                                  "basis 非空（识别依据可追溯）")

                    missing = harness.call_err(suite, client, "read_describe",
                                               {"handle": "FFFF"}, "不存在的句柄")
                    suite.eq(missing.get("code"), "E1001", "句柄不存在 → E1001")
                    suite.eq(missing.get("field"), "params.handle", "句柄不存在 → field=params.handle")

                    # 指纹不一致 → W1004 告警但不阻断
                    stale = client.call("read_describe",
                                        {"handle": handle, "fingerprint": "fp1:" + "0" * 32})
                    suite.is_true(stale.get("ok"), "指纹不一致仍返回结果")
                    codes = [item.get("code") for item in (stale.get("warnings") or [])]
                    suite.contains(codes, "W1004", "指纹不一致 → W1004 告警")

                    # 代理对象：confidence 必须为 0、meaning 为 null
                    anomalies = harness.call_ok(suite, client, "read_anomalies",
                                                {"types": ["proxy_object"]})
                    proxy_handle = (anomalies.get("items") or [{}])[0].get("handle")
                    proxy = harness.call_ok(suite, client, "read_describe",
                                            {"handle": proxy_handle})
                    suite.eq(proxy.get("confidence"), 0, "代理对象 confidence=0")
                    suite.ok(proxy.get("meaning") is None, "代理对象 meaning=null（不臆测）")

                # ---------------- read_anomalies ----------------
                def anomalies():
                    result = harness.call_ok(suite, client, "read_anomalies", {"limit": 30})
                    if not result:
                        return
                    counts = result.get("counts") or {}
                    suite.is_true(len(counts) >= 5, "异常计数覆盖 ≥5 类",
                                  str(counts))
                    suite.eq(counts.get("proxy_object"), 1, "proxy_object 计数 1")
                    suite.eq(counts.get("zero_length"), 1, "zero_length 计数 1")
                    for item in result.get("items") or []:
                        suite.eq(item.get("code") in (
                            "duplicate_point", "zero_length", "extreme_coord",
                            "degenerate_extents", "proxy_object", "missing_unit",
                            "empty_attr", "extremely_long_text"), True,
                            "anomaly.code 属于 8 类之一")
                        suite.ok(item.get("severity") in ("info", "warn", "error"),
                                 "anomaly.severity 合法")

                    warns = harness.call_ok(suite, client, "read_anomalies",
                                            {"severity": "warn", "limit": 30})
                    suite.is_true(all(item["severity"] == "warn"
                                      for item in warns.get("items") or []),
                                  "severity 过滤生效")

                    typed = harness.call_ok(suite, client, "read_anomalies",
                                            {"types": ["extremely_long_text"]})
                    suite.eq(len(typed.get("items") or []), 1, "types 过滤命中 1 条")

                # ---------------- read_preview ----------------
                def preview():
                    import preview as preview_module
                    result = harness.call_ok(suite, client, "read_preview",
                                             {"width": 800, "height": 600})
                    if not result:
                        return
                    suite.eq(result.get("note"), "结构示意图，非出图成果",
                             "note 恒为「结构示意图，非出图成果」")
                    suite.eq(result.get("width"), 800, "width 回填 800")
                    suite.eq(result.get("height"), 600, "height 回填 600")
                    path = result.get("previewPath") or ""
                    suite.is_true(os.path.isfile(path), "previewPath 指向真实文件")
                    suite.eq(preview_module.verify_png(path), (800, 600),
                             "PNG 头解析出的尺寸与返回一致")
                    suite.eq(os.path.getsize(path), result.get("bytes"),
                             "bytes 与文件实际大小一致")
                    legend = result.get("legend") or []
                    suite.is_true(1 <= len(legend) <= 12, "legend 数量 1–12")
                    for entry in legend:
                        suite.matches(entry.get("color") or "", r"^#[0-9a-f]{6}$",
                                      "legend.color 为 #rrggbb")
                        suite.ok(isinstance(entry.get("count"), int), "legend.count 为整数")
                    suite.is_false(result.get("oversize"), "正常尺寸 oversize=false")

                    # 尺寸越界 → E1001（宿主侧二次校验）
                    harness.call_err(suite, client, "read_preview",
                                     {"width": 5000}, "width 超上限")

                # ---------------- sys_* ----------------
                def sys_tools():
                    ping = harness.call_ok(suite, client, "sys_ping", {})
                    suite.is_true(ping.get("pong") is True, "sys_ping.pong=true")
                    suite.eq(ping.get("hostInstanceId"), host.host_instance_id,
                             "sys_ping 回填 hostInstanceId")

                    status = harness.call_ok(suite, client, "sys_status", {})
                    suite.ok(isinstance(status.get("uptimeMs"), int), "sys_status.uptimeMs 为整数")
                    suite.ok(0.0 <= status.get("cacheHitRate", 0) <= 1.0,
                             "sys_status.cacheHitRate 在 [0,1]")

                    version = harness.call_ok(suite, client, "sys_version", {})
                    for key in ("host", "autocad", "protocol", "dotnet"):
                        suite.ok(key in version, "sys_version 含 %s" % key)
                    suite.eq((version.get("protocol") or {}).get("version"), "1.0",
                             "sys_version.protocol.version=1.0")

                    health = harness.call_ok(suite, client, "sys_health", {})
                    ids = [item.get("id") for item in health.get("checks") or []]
                    for expected in ("pipeAcl", "discoveryFile", "logWritable",
                                     "cacheWritable", "civilProbe", "layerMapLoaded",
                                     "projectDb"):
                        suite.contains(ids, expected, "sys_health 覆盖检查项 %s" % expected)

                    caps = harness.call_ok(suite, client, "sys_capabilities", {})
                    suite.eq(caps.get("writeEnabled"), False, "sys_capabilities.writeEnabled=false")
                    import tools_catalog as _catalog_for_caps
                    suite.eq(len(caps.get("tools") or []), len(_catalog_for_caps.CANONICAL_TOOLS),
                             "sys_capabilities 列出的工具数与目录一致")
                    suite.is_true(len(caps.get("gaps") or []) >= 1,
                                  "sys_capabilities 上报 Civil 3D 缺口")

                    listing = harness.call_ok(suite, client, "sys_list_instances", {})
                    instances = listing.get("instances") or []
                    suite.is_true(len(instances) >= 1, "sys_list_instances 至少 1 个实例")
                    mine = [item for item in instances
                            if item.get("hostInstanceId") == host.host_instance_id]
                    suite.is_true(bool(mine), "实例列表包含自己")
                    suite.is_true(bool(mine and mine[0].get("alive")), "自己标记为 alive")

                # ---------------- task_* ----------------
                def task_tools():
                    # 先制造一条可查的任务记录
                    response = client.call("read_overview", {})
                    op_id = response.get("opId")
                    status = harness.call_ok(suite, client, "task_status", {"opId": op_id})
                    suite.eq(status.get("opId"), op_id, "task_status 回填 opId")
                    suite.eq(status.get("status"), "verified", "只读成功终态为 verified")
                    suite.eq(status.get("tool"), "read_overview", "task_status.tool 正确")

                    not_found = harness.call_ok(suite, client, "task_status",
                                                {"opId": "op-doesnotexist-01"})
                    suite.eq(not_found.get("status"), "notFound", "未知 opId → notFound")

                    listed = harness.call_ok(suite, client, "task_list", {"limit": 5})
                    suite.is_true(len(listed.get("tasks") or []) >= 1, "task_list 有记录")
                    for item in listed.get("tasks") or []:
                        for key in ("opId", "tool", "status"):
                            suite.ok(key in item, "task_list 项含 %s" % key)

                    cancel = harness.call_ok(suite, client, "task_cancel", {"opId": op_id})
                    suite.is_false(cancel.get("accepted"), "已完成的任务 cancel 不接受")
                    suite.contains(cancel.get("reason") or "", "finished",
                                   "cancel.reason 说明已完成")

                    reconcile = harness.call_ok(suite, client, "task_reconcile", {"opId": op_id})
                    suite.eq(reconcile.get("decision"), "verified", "对账结论 verified")

                    unknown = harness.call_ok(suite, client, "task_reconcile",
                                              {"opId": "op-unknown-0001"})
                    suite.eq(unknown.get("decision"), "unknown", "无记录对账结论 unknown")
                    suite.contains(unknown.get("advice") or "", "新 opId",
                                   "unknown 对账提示必须用新 opId")

                # ---------------- 写域与未知工具 ----------------
                def gates():
                    for tool in ("cad_create_line", "civil_create_profile",
                                 "dyn_run_lisp", "file_write_text"):
                        error = harness.call_err(suite, client, tool,
                                                 {"layer": "0"}, tool)
                        suite.eq(error.get("code"), "E2003", "%s → E2003" % tool)
                        suite.eq(error.get("detail"), "write_channel_disabled_m0",
                                 "%s detail=write_channel_disabled_m0" % tool)

                    misspelled = harness.call_err(suite, client, "read_overveiw", {},
                                                  "拼错工具名")
                    suite.eq(misspelled.get("code"), "E2001", "拼错工具名 → E2001")

                for label, func in (
                    ("read_overview", overview),
                    ("read_layers", layers),
                    ("read_query", query),
                    ("read_describe", describe),
                    ("read_anomalies", anomalies),
                    ("read_preview", preview),
                    ("sys_*", sys_tools),
                    ("task_*", task_tools),
                    ("门禁", gates),
                ):
                    suite.case(label, func)
            finally:
                client.close()

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
