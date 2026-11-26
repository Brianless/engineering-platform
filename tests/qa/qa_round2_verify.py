"""tests/qa/qa_round2_verify.py —— 第三轮交付（D1–D7 收口）的**独立复验**（QA 严过关）。
## 为什么要有这个文件

上一轮我报的 10 条失败全部指向 D1/D3/D4/D5/D6 与 D5 的双端差异。工程师第三轮说「全部关闭」，
我上一轮的断言现在确实全绿了 —— 但「原来的断言变绿」只证明**它改了**，不证明**它改对了**：

  * 原来断言「缺 clientInfo 被拒绝」，他可能只加了一句 `if not clientInfo: continue`；
  * 原来断言「大写工具名 → E2003」，他可能把**所有**名字都归一化成写域（那就把 E2001 吞了）。

所以本文件不重复上一轮的判据，而是加**反向/边界/证据交叉**三类判据：

  A. D1 反向：拒绝时**不得签发 sessionToken**（拒一半等于没拒）；且 detail 必须点名字段；
  B. D6 反向：拼错的只读工具名必须仍是 E2001（防「什么都当写域」）；
  C. D3/D4/D5/D7 的行为断言 + 契约不变量（`layers + emptyLayers == layerTableRecords`）；
  D. 契约漂移：`protocol/` 三个 schema ↔ C# ↔ mockhost ↔ MCP（stdio 实跑，验证不被中间层吃掉）；
  E. 夹具（`ENG_DEVFIXTURE`）**安全门禁**的静态核查 + 证据 JSON 交叉核对；
  F. `docs/04` 的「已实测」措辞与证据数字是否一致（防止顺手夸大）。

## 结论口径

本文件允许变红：红的那条就是**当前仍未收口**的缺陷，标签里带缺陷号（如 `D8`）。
绿的那条必须有可复现的观测值（不是「跑通了」）。
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time

import qa_common as qa

MCP_ENTRY = os.path.join(qa.MCP_DIR, "engineering_mcp.py")
HANDSHAKE_CS = "host/Protocol/Handshake.cs"
PIPE_CONN_CS = "host/Pipe/PipeConnection.cs"
READ_HANDLERS_CS = "host/Tools/ReadToolHandlers.cs"
FIXTURE_CS = "host/Commands/DevFixtureCommand.cs"
DOC04 = "docs/04-可行性清单-第0阶段.md"

#: 夹具证据（工程师 16:18 那次真机运行，我按文件交叉核对，不采信其结论文字）
FIXTURE_DIR = "docs/evidence/20261121-161828-展点图-fixture"
FIXTURE_JSON = FIXTURE_DIR + "/fixture-20261121161749.json"
FIXTURE_SELFTEST = FIXTURE_DIR + "/selftest-20261121161826.json"


# ============================================================================
# 小工具
# ============================================================================


def _line_of(text: str, needle: str) -> int:
    """返回 needle 首次出现的行号（1 基）；找不到返回 -1。证据里必须给行号。"""
    index = text.find(needle)
    return -1 if index < 0 else text.count("\n", 0, index) + 1


def _hello(server, payload, timeout: float = 3.0):
    """发一个自定义 hello 帧，返回 (响应 dict | None, 异常 repr | None)。"""
    with qa.raw_client(server, handshake=False) as raw:
        raw.send_frame(payload)
        try:
            return raw.read_json(timeout=timeout), None
        except Exception as exc:  # noqa: BLE001
            return None, repr(exc)


def _base_hello(**overrides):
    frame = {
        "kind": "hello",
        "protocolVersion": "1.0",
        "clientInfo": {"name": "qa-round2", "version": "1.0.0"},
        "clientNonce": "0123456789abcdef",
    }
    frame.update(overrides)
    return frame


def _error_of(response) -> dict:
    return (response or {}).get("error") or {}


def _token_of(response):
    return (response or {}).get("sessionToken")


# ============================================================================
# A. D1 —— 畸形 hello 必须被拒，且拒得干净
# ============================================================================


def section_d1(suite, server):
    cases = [
        ("缺 clientInfo", _base_hello(clientInfo=None), "clientInfo"),
        ("clientInfo 非对象（字符串）", _base_hello(clientInfo="not-an-object"), "clientInfo"),
        ("clientInfo 非对象（数组）", _base_hello(clientInfo=[{"name": "x"}]), "clientInfo"),
        ("clientInfo 缺 name", _base_hello(clientInfo={"version": "1.0.0"}), "clientInfo.name"),
        ("clientInfo 缺 version", _base_hello(clientInfo={"name": "qa"}), "clientInfo.version"),
        ("clientInfo.name 为空串", _base_hello(clientInfo={"name": "", "version": "1"}),
         "clientInfo.name"),
        ("缺 clientNonce", _base_hello(clientNonce=None), "clientNonce"),
        ("clientNonce 非字符串", _base_hello(clientNonce=16), "clientNonce"),
        ("nonce 长 4", _base_hello(clientNonce="abcd"), "clientNonce"),
        ("nonce 长 65", _base_hello(clientNonce="a" * 65), "clientNonce"),
        ("nonce 非 hex", _base_hello(clientNonce="z" * 16), "clientNonce"),
        ("nonce 带连字符", _base_hello(clientNonce="0123-4567-89ab"), "clientNonce"),
    ]
    for label, payload, expected_field in cases:
        response, failure = _hello(server, payload)
        if not suite.ok(response is not None, "D1 %s → 有响应帧（不是挂死）" % label, failure):
            continue
        error = _error_of(response)
        suite.eq(error.get("code"), "E1001", "D1 %s → E1001" % label)
        suite.eq(error.get("field"), expected_field,
                 "D1 %s → field 精确指向 %s" % (label, expected_field),
                 )
        detail = str(error.get("detail") or "")
        leaf = expected_field.split(".")[-1]
        suite.ok(leaf in detail,
                 "D1 %s → detail 里点了名（%s）" % (label, leaf),
                 "detail=%r" % detail)
        suite.ok(not _token_of(response),
                 "D1 %s → **拒绝时不得签发 sessionToken**" % label,
                 "sessionToken=%r（拒一半等于没拒）" % (_token_of(response),))

    # 大写 hex 是合法 nonce（契约是 16–64 hex，不是「小写」）
    response, failure = _hello(server, _base_hello(clientNonce="ABCDEF0123456789"))
    suite.ok(response is not None and response.get("kind") == "hello_ack",
             "D1 大写 hex nonce（16 位）→ 允许握手（契约只要求 hex，不要求小写）", failure)

    # 对照组：完全合法必须拿到令牌，否则上面那些「被拒」可能只是全拒
    response, failure = _hello(server, _base_hello())
    suite.ok(response is not None and bool(_token_of(response)),
             "D1 对照组：合法 hello → 签发令牌", failure)

    # 双端同一套 detail 字面量（mockhost 与 C# 的拒绝原因不能各说各话）
    handshake_src = qa.read_source(HANDSHAKE_CS)
    mock_src = qa.read_source("mockhost/mock_host.py")
    for detail in ("clientInfo_missing", "clientInfo.name_missing",
                   "clientInfo.version_missing", "clientNonce_missing",
                   "clientNonce_invalid"):
        where = []
        for name, src in (("C#", handshake_src), ("mockhost", mock_src)):
            if detail in src:
                where.append(name)
        suite.eq(sorted(where), ["C#", "mockhost"],
                 "D1 双端都有 detail=%s" % detail)


# ============================================================================
# B. D3 —— W1002 真的上线（不是只写日志）
# ============================================================================


def section_d3(suite, server):
    response, failure = _hello(server, _base_hello(protocolVersion="1.9"))
    if suite.ok(response is not None, "D3 次版本超前 1.9 → 有响应", failure):
        suite.eq(response.get("kind"), "hello_ack", "D3 1.9 → 允许连接（继续握手而不是拒绝）")
        warnings = response.get("warnings") or []
        codes = [w.get("code") for w in warnings]
        suite.ok("W1002" in codes,
                 "D3 1.9 → hello_ack.warnings 里真的带 W1002（以前只构造不上线）",
                 "warnings=%s" % json.dumps(warnings, ensure_ascii=False))
        # 结构化要求：必须是对象（不是字符串 "W1002"），且 code/message/detail 三键齐全
        first = warnings[0] if warnings else None
        suite.ok(isinstance(first, dict),
                 "D3 W1002 是**结构化对象**而不是裸字符串", repr(first))
        if isinstance(first, dict):
            for key in ("code", "message", "detail"):
                suite.ok(key in first, "D3 W1002 对象里有 %s 字段" % key,
                         "keys=%s" % sorted(first.keys()))
        suite.ok(any("1.9" in str(w.get("detail") or "") for w in warnings),
                 "D3 1.9 → W1002 的 detail 里带客户端版本（可定位）",
                 json.dumps(warnings, ensure_ascii=False))

    response, failure = _hello(server, _base_hello(protocolVersion="1.0"))
    if suite.ok(response is not None, "D3 版本相等 1.0 → 有响应", failure):
        suite.eq(response.get("warnings"), [], "D3 1.0 → warnings 为空数组（不是缺字段）")

    response, failure = _hello(server, _base_hello(protocolVersion="2.0"))
    if suite.ok(response is not None, "D3 主版本不符 2.0 → 有响应", failure):
        error = _error_of(response)
        suite.eq(error.get("code"), "E4001", "D3 2.0 → E4001（不是 W1002）")
        # detail 未被契约枚举（errors.json 的 E4001 无 details 列表），故只要求「非空且能读出原因」；
        # 双端字面量不同（C# `major_mismatch` / mockhost `protocol_version_incompatible:2.0`）已记为 NIT。
        detail = str(error.get("detail") or "")
        suite.ok(bool(detail), "D3 2.0 → detail 非空（可诊断）", repr(detail))
        suite.ok("major" in detail or "protocol_version" in detail,
                 "D3 2.0 → detail 表达了「主版本不兼容」这一语义", repr(detail))
        suite.ok(not _token_of(response), "D3 2.0 → 不签发令牌")

    # 静态：ack 里无条件写 warnings；死码 _clientAhead 必须已删
    handshake_src = qa.read_source(HANDSHAKE_CS)
    pipe_src = qa.read_source(PIPE_CONN_CS)
    suite.ok('ack["warnings"] = warningList;' in handshake_src,
             "D3 C# ack 里写入 warnings（Handshake.cs:%d）"
             % _line_of(handshake_src, 'ack["warnings"] = warningList;'))
    suite.ok("_clientAhead" not in pipe_src,
             "D3 PipeConnection 里「只赋值从不读」的 _clientAhead 死码已删除")


# ============================================================================
# C. D4 —— missingFeatures 不得把工具名当能力
# ============================================================================


def section_d4(suite, server):
    payload = _base_hello(requestedFeatures=["preview", "read_overview", "nope_xyz",
                                             "sys_health", "heartbeat"])
    response, failure = _hello(server, payload)
    if not suite.ok(response is not None, "D4 features 混工具名 → 有响应", failure):
        return
    missing = response.get("missingFeatures")
    if not suite.ok(isinstance(missing, list), "D4 missingFeatures 是数组", repr(missing)):
        return
    suite.eq(missing, ["nope_xyz"],
             "D4 只报真正不认识的能力（工具名 read_overview/sys_health 不得误报）")

    response, failure = _hello(server, _base_hello(
        requestedFeatures=["preview", "cancel", "heartbeat", "layers", "query"]))
    if suite.ok(response is not None, "D4 全合法 features → 有响应", failure):
        suite.eq(response.get("missingFeatures"), [],
                 "D4 全合法能力 → missingFeatures 为空数组")

    # 静态：两端都必须有「排除工具名/仅认白名单」的判据，而不是「凡是没见过的能力都报」
    handshake_src = qa.read_source(HANDSHAKE_CS)
    suite.ok("ToolCatalog.Exists" in handshake_src,
             "D4 C# 侧用 ToolCatalog.Exists 排除工具名（Handshake.cs:%d）"
             % _line_of(handshake_src, "ToolCatalog.Exists"))


# ============================================================================
# D. D6 —— 写域大小写不敏感，但**不得把只读工具误判成写域**
# ============================================================================


def section_d6(suite, client):
    write_variants = ["CAD_CREATE_LINE", "Cad_Create_Line", "cad_CREATE_line",
                      "CIVIL_CREATE_PROFILE", "FILE_WRITE_TEXT", "SKILL_INSTALL"]
    for name in write_variants:
        response = client.call(name, {})
        error = _error_of(response)
        suite.eq(error.get("code"), "E2003",
                 "D6 写域大小写变形 %r → E2003（不是 E2001）" % name)
        suite.eq(error.get("detail"), "write_channel_disabled_m0",
                 "D6 %r → detail=write_channel_disabled_m0" % name)

    # 带首尾空白的名字：两端都**不做 trim**，落 E2001（不是被当成写域放行，也不是被执行）
    # —— 这条不是缺陷，是「归一化的边界」：空白名不在目录里 = 未知工具。
    response = client.call("  cad_create_line  ", {})
    error = _error_of(response)
    suite.eq(error.get("code"), "E2001",
             "D6 边界：带空白前缀的名字不被当写域（不做 trim，落 E2001）")
    suite.ok(not response.get("ok"), "D6 边界：带空白的写域名字**没有被执行**",
             json.dumps(response, ensure_ascii=False)[:200])

    # 反向断言（工程师也提了这条，我独立再打一遍）：拼错的只读名必须仍是 E2001
    for name in ("read_overveiw", "READ_OVERVIEW", "Sys_Ping", "sys_pingX"):
        response = client.call(name, {})
        error = _error_of(response)
        suite.eq(error.get("code"), "E2001",
                 "D6 反向：%r 不是写域 → E2001（防「归一化过头」）" % name)


# ============================================================================
# E. D5 —— truncated 就必须给可执行的 drilldown
# ============================================================================


def _tool_shaped_payloads():
    """三个 read 工具「被截断但自身没有 nextCursor」的载荷形状（无游标是最坏情形）。"""
    layers = [{"name": "L-%03d" % i, "entityCount": i, "types": ["LINE"],
               "confidence": 0.5, "confidenceBand": "hint"} for i in range(60)]
    items = [{"handle": "%04X" % i, "type": "LINE", "layer": "D-DR-管道"} for i in range(400)]
    anomalies = [{"handle": "%04X" % i, "kind": "proxy", "layer": "GCD"} for i in range(200)]
    return {
        "read_layers": {"summary": "QA：60 个图层", "total": 60, "layers": layers,
                        "truncated": True},
        "read_query": {"summary": "QA：400 个对象", "total": 400, "items": items,
                       "truncated": True},
        "read_anomalies": {"summary": "QA：200 条异常", "total": 200, "items": anomalies,
                           "counts": {"proxy": 200}, "truncated": True},
    }


def section_d5(suite, client):
    import budget

    for tool, payload in _tool_shaped_payloads().items():
        _trimmed, meta = budget.truncate(dict(payload), 2000)
        tag = "D5[%s]" % tool
        if not suite.is_true(meta.get("truncated"), "%s 构造成功：确实被截断" % tag,
                             json.dumps({k: meta.get(k) for k in ("truncated", "truncationLevel")},
                                        ensure_ascii=False)):
            continue
        suite.ok("nextCursor" not in payload,
                 "%s 前提成立：载荷里**没有** nextCursor（最坏情形）" % tag)
        drilldown = meta.get("drilldown") or []
        suite.ok(len(drilldown) >= 2,
                 "%s 被截断且无游标 → drilldown **≥2 条**（D5 核心）" % tag,
                 "truncationLevel=%r drilldown=%s"
                 % (meta.get("truncationLevel"), json.dumps(drilldown, ensure_ascii=False)[:400]))
        for index, entry in enumerate(drilldown):
            suite.ok(isinstance(entry, dict) and entry.get("tool")
                     and isinstance(entry.get("params"), dict),
                     "%s drilldown[%d] 形如 {tool, params, ...}（可直接执行）"
                     % (tag, index), json.dumps(entry, ensure_ascii=False))
        if drilldown:
            first = drilldown[0]
            response = client.call(first["tool"], first.get("params") or {})
            suite.ok(response.get("ok") is True,
                     "%s 照着 drilldown[0] 真调一次能跑通（%s）" % (tag, first.get("tool")),
                     qa.describe_error(response))

    # 真实工具路径：read_anomalies 现在必须有 drilldown 字段（以前整个字段缺）
    anomalies = client.call("read_anomalies", {}).get("result") or {}
    suite.ok("drilldown" in anomalies,
             "D5[real] read_anomalies 结果里**有** drilldown 字段",
             "keys=%s" % sorted(anomalies.keys()))
    suite.ok(isinstance(anomalies.get("drilldown"), list),
             "D5[real] read_anomalies.drilldown 是数组（哪怕是空的）",
             repr(anomalies.get("drilldown")))

    query = client.call("read_query", {"limit": 2}).get("result") or {}
    if query.get("truncated"):
        suite.ok(len(query.get("drilldown") or []) >= 1,
                 "D5[real] read_query 被截断时 drilldown 非空（回归）",
                 json.dumps(query.get("drilldown"), ensure_ascii=False)[:300])


# ============================================================================
# F. D7 —— layers 口径统一 + 三字段等式
# ============================================================================


def section_d7(suite, client):
    overview = client.call("read_overview", {}).get("result") or {}
    layers = client.call("read_layers", {}).get("result") or {}
    counts = overview.get("counts") or {}

    used = counts.get("layers")
    table_records = counts.get("layerTableRecords")
    empty = counts.get("emptyLayers")
    total = layers.get("total")

    suite.eq(used, total,
             "D7 counts.layers == read_layers.total（口径统一，缺陷 D7 的核心）")
    suite.eq((used or 0) + (empty or 0), table_records,
             "D7 counts.layers + counts.emptyLayers == counts.layerTableRecords（契约等式）")
    suite.ok(isinstance(empty, int) and empty >= 1,
             "D7 counts.emptyLayers 存在且 ≥1（mock 故意造了 2 个空图层）",
             "emptyLayers=%r" % (empty,))
    suite.eq(len(layers.get("layers") or []), total,
             "D7 read_layers.layers 条数 == total")

    # 真机夹具证据交叉：同一套等式在 Civil 3D 真实回报里也成立
    try:
        with open(os.path.join(qa.ROOT, FIXTURE_SELFTEST), "r", encoding="utf-8") as handle:
            selftest = json.load(handle)
        civil = selftest.get("civil3d") or {}
        civil_counts = civil.get("counts") or {}
        parts = sum(civil_counts.get(key, 0) for key in
                    ("surfaces", "alignments", "profiles", "pipeNetworks", "pointGroups"))
        suite.eq(civil_counts.get("total"), parts,
                 "D7 真机证据：civil3d.counts.total == 五类之和")
        suite.eq(civil.get("detected"), True, "D7 真机证据：civil3d.detected=true（Civil 3D 宿主）")
    except (OSError, ValueError) as exc:
        suite.ok(False, "D7 真机证据可读", repr(exc))

    # --- 空图层的「真实性」审计：mock 的 emptyLayers 是不是为了让断言通过而硬造？ ---
    import mock_drawing

    empty_names = list(getattr(mock_drawing, "EMPTY_LAYER_NAMES", ()))
    suite.ok(len(empty_names) == empty,
             "D7 mock 的 emptyLayers 数量 == 其声明的空图层名数量（是**派生**的，不是写死 2）",
             "emptyLayers=%r EMPTY_LAYER_NAMES=%r" % (empty, empty_names))
    listed = {item.get("name") for item in (layers.get("layers") or [])}
    suite.ok(not (set(empty_names) & listed),
             "D7 空图层名**不出现**在 read_layers.layers 里（「有图元」与「表记录」口径没混）",
             "空图层名=%r 已列出=%r" % (empty_names, sorted(listed)))
    suite.ok("len(EMPTY_LAYER_NAMES)" in qa.read_source("mockhost/mock_drawing.py"),
             "D7 mock 侧 layerTableRecords 由 len(EMPTY_LAYER_NAMES) 派生")
    suite.ok("layerTableRecords - usedLayers" in qa.read_source(READ_HANDLERS_CS),
             "D7 C# 侧 emptyLayers = layerTableRecords - usedLayers（派生，不是硬编码）")

    # 真机真实数据：这张图 emptyLayers 实际是 0 —— 如实记录，不拿 mock 的 2 冒充真机证据
    try:
        with open(os.path.join(qa.ROOT, FIXTURE_SELFTEST), "r", encoding="utf-8") as handle:
            real = json.load(handle)
        real_counts = next((item.get("result", {}).get("counts") for item in real["toolResults"]
                            if item.get("tool") == "read_overview"), {}) or {}
        suite.ok((real_counts.get("layers") or 0) + (real_counts.get("emptyLayers") or 0)
                 == real_counts.get("layerTableRecords"),
                 "D7 真机真实图纸也满足 layers + emptyLayers == layerTableRecords",
                 "真机 counts=%s" % json.dumps(real_counts, ensure_ascii=False))
        print("NOTE 真机真实图纸的 emptyLayers=%r（该分支在真机上未被走到；mock 的 2 是 mock 专属造数）"
              % (real_counts.get("emptyLayers"),))
    except (OSError, ValueError, KeyError) as exc:
        suite.ok(False, "D7 真机 counts 可读", repr(exc))


# ============================================================================
# G. 契约漂移：schema ↔ C# ↔ mockhost（存在性 + 必填性 + 不被中间层吃掉）
# ============================================================================


def _load_schema(name):
    with open(os.path.join(qa.PROTOCOL_DIR, name), "r", encoding="utf-8") as handle:
        return json.load(handle)


def section_contract(suite):
    handshake = _load_schema("handshake.schema.json")
    envelope = _load_schema("envelope.schema.json")
    tools = _load_schema("tools.schema.json")

    for label, schema, def_name in (
            ("handshake.schema.json", handshake, "HelloAck"),
            ("envelope.schema.json", envelope, "HandshakeResponse")):
        node = (schema.get("$defs") or {}).get(def_name) or {}
        required = node.get("required") or []
        for field in ("hostProduct", "hostRelease"):
            suite.ok(field in required, "%s#/$defs/%s.required 含 %s" % (label, def_name, field),
                     "required=%s" % required)
            suite.ok(field in (node.get("properties") or {}),
                     "%s#/$defs/%s 定义了 %s" % (label, def_name, field))

    ack_props = ((handshake.get("$defs") or {}).get("HelloAck") or {}).get("properties") or {}
    suite.eq((ack_props.get("hostProduct") or {}).get("enum"), ["AutoCAD", "Civil3D"],
             "hostProduct 的 enum 只有两档（不许出现第三档）")
    suite.ok(bool((ack_props.get("hostRelease") or {}).get("pattern")),
             "hostRelease 有 pattern（R24.3 形态可校验）")

    # read_overview.counts 三字段 + civil3d 段
    overview = None
    for tool in tools.get("tools") or []:
        if tool.get("name") == "read_overview":
            overview = tool
            break
    suite.ok(overview is not None, "tools.schema.json 里有 read_overview")
    if overview:
        out = overview.get("outputSchema") or {}
        props = out.get("properties") or {}
        counts_schema = (props.get("counts") or {}).get("properties") or {}
        for field in ("layers", "layerTableRecords", "emptyLayers"):
            suite.ok(field in counts_schema,
                     "read_overview.counts 声明了 %s" % field,
                     "counts 现有键=%s" % sorted(counts_schema.keys()))
        civil = props.get("civil3d") or {}
        for field in ("detected", "counts", "truncated"):
            suite.ok(field in (civil.get("required") or []),
                     "read_overview.civil3d.required 含 %s" % field,
                     "required=%s" % (civil.get("required") or []))
        civil_counts = ((civil.get("properties") or {}).get("counts") or {}).get("properties") or {}
        for field in ("surfaces", "alignments", "profiles", "pipeNetworks", "pointGroups", "total"):
            suite.ok(field in civil_counts, "civil3d.counts 声明了 %s" % field)

    # C# / mockhost 同名字段必须真的产出（不是 schema 里写了代码没写）
    handlers = qa.read_source(READ_HANDLERS_CS)
    handshake_src = qa.read_source(HANDSHAKE_CS)
    mock_src = qa.read_source("mockhost/mock_host.py")
    mock_drawing = qa.read_source("mockhost/mock_drawing.py")
    suite.ok('counts["emptyLayers"]' in handlers, "C# 产出 counts.emptyLayers（ReadToolHandlers.cs:%d）"
             % _line_of(handlers, 'counts["emptyLayers"]'))
    suite.ok('counts["layerTableRecords"]' in handlers,
             "C# 产出 counts.layerTableRecords")
    suite.ok('ack["hostProduct"]' in handshake_src, "C# 产出 hello_ack.hostProduct")
    suite.ok('"emptyLayers"' in mock_drawing, "mockhost 产出 emptyLayers")
    suite.ok('"layerTableRecords"' in mock_drawing, "mockhost 产出 layerTableRecords")
    suite.ok("emptyLayers" in mock_src, "mockhost 的 summary 侧也读 emptyLayers（不吞字段）")


def section_mcp_passthrough(suite, tmp):
    """MCP 中间层最容易「吃掉」新字段：真起一个 stdio MCP，看结果里还在不在。"""
    env = dict(os.environ)
    env["PYTHONIOENCODING"] = "utf-8"
    env["EP_MOCK"] = "1"
    env["EP_CONFIG"] = os.path.join(tmp, "config.json")
    env["EP_LOG_LEVEL"] = "WARN"

    with qa.harness.mock_server() as (server, _host):
        qa.wait_for_pipe(server)
        process = subprocess.Popen(
            [sys.executable, "-u", MCP_ENTRY, "--no-heartbeat"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            cwd=qa.ROOT, env=env)
        try:
            def send(message):
                process.stdin.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
                process.stdin.flush()

            def read_one(timeout: float = 20.0):
                deadline = time.time() + timeout
                while time.time() < deadline:
                    line = process.stdout.readline()
                    if not line:
                        return None
                    text = line.decode("utf-8", "replace").strip()
                    if text:
                        return json.loads(text)
                return None

            send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                  "params": {"protocolVersion": "2025-06-18",
                             "clientInfo": {"name": "qa-r2", "version": "1.0"},
                             "capabilities": {}}})
            read_one()
            send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            send({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                  "params": {"name": "read_overview", "arguments": {}}})
            response = read_one() or {}
            result = ((response.get("result") or {}).get("structuredContent")
                      or (response.get("result") or {}).get("content") or [])
            if isinstance(result, list):
                text = ""
                if result and isinstance(result[0], dict):
                    text = result[0].get("text") or ""
                payload = json.loads(text) if text.strip().startswith("{") else {}
            else:
                payload = result if isinstance(result, dict) else {}
            payload = payload.get("result", payload) if isinstance(payload, dict) else {}
            counts = (payload.get("counts") or {})
            suite.ok("emptyLayers" in counts,
                     "MCP 层不吞字段：read_overview.counts.emptyLayers 透传到 Agent",
                     "MCP 拿到的 counts=%s" % sorted(counts.keys()))
            suite.ok("layerTableRecords" in counts,
                     "MCP 层不吞字段：read_overview.counts.layerTableRecords 透传")
        finally:
            try:
                process.stdin.close()
            except OSError:
                pass
            process.terminate()


# ============================================================================
# H. ENG_DEVFIXTURE —— 安全门禁（静态）+ 证据交叉
# ============================================================================


def section_fixture(suite):
    src = qa.read_source(FIXTURE_CS)

    guard_call = _line_of(src, "if (!IsInsideSandbox(sandbox))")
    create_dir = _line_of(src, "Directory.CreateDirectory(sandbox)")
    suite.ok(guard_call > 0, "H 存在 IsInsideSandbox 硬门禁（DevFixtureCommand.cs:%d）" % guard_call)
    suite.ok(0 < guard_call < create_dir,
             "H 门禁在**任何建目录/写盘之前**（门禁 :%d < CreateDirectory :%d）"
             % (guard_call, create_dir))
    suite.ok("output_not_in_sandbox" in src,
             "H 越界返回 E1001 output_not_in_sandbox（可定位到原因）")
    suite.ok("Path.GetFullPath(outputDir)" in src,
             "H 先把入参规范化为全路径再判界（防 ../ 绕过）")
    suite.ok("EndsWith(Path.DirectorySeparatorChar.ToString()" in src
             and "OrdinalIgnoreCase" in src,
             "H 判界用「补尾部分隔符 + 忽略大小写」（防 sandbox-evil 前缀绕过）")
    suite.ok("E1001 output_dir_missing" in src, "H 空路径 → E1001 output_dir_missing")

    # 夹具不许被 Agent 调到：三处目录都不能出现它
    for label, path in (("tools.schema.json", "protocol/tools.schema.json"),
                        ("tools_catalog.py", "mcp/tools_catalog.py"),
                        ("mockhost", "mockhost/mock_host.py")):
        suite.ok("devfixture" not in qa.read_source(path).lower(),
                 "H ENG_DEVFIXTURE 不在 %s（Agent 不可达）" % label)

    # --- D8：夹具的「写」落在**当前活动文档**上，必须有独立的「活动文档门禁」---
    gate_call = _line_of(src, "CheckActiveDocumentInsideSandbox();")
    gate_def = _line_of(src, "internal static string CheckActiveDocumentInsideSandbox()")
    guard_out = _line_of(src, "if (!IsInsideSandbox(sandbox))")
    create_dir_line = _line_of(src, "Directory.CreateDirectory(sandbox)")
    first_save_as = _line_of(src, "doc.Database.SaveAs(")

    suite.ok(gate_def > 0, "D8 存在活动文档门禁实现 CheckActiveDocumentInsideSandbox（:%d）" % gate_def)
    suite.ok(0 < guard_out < gate_call < create_dir_line < first_save_as,
             "D8 两道门禁都在**写动作之前**（输出目录 :%d → 活动文档 :%d → 建目录 :%d → SaveAs :%d）"
             % (guard_out, gate_call, create_dir_line, first_save_as))

    gate_body = src[src.find("internal static string CheckActiveDocumentInsideSandbox()"):]
    gate_body = gate_body[:gate_body.find("\n        }")]
    suite.ok("IsInsideSandbox(fullName)" in gate_body,
             "D8 门禁确实拿**活动文档全路径**去判 sandbox（不是只判输出目录）")
    suite.ok("doc == null" in gate_body and "active_doc_unnamed" in gate_body,
             "D8 对「无文档 / 未命名文档」也拒绝（fail-closed）")
    suite.ok(gate_body.count("return \"") >= 5 and "return null;" in gate_body,
             "D8 门禁只在**全部通过**时返回 null（多个失败分支各自返回原因）",
             "失败分支数=%d" % gate_body.count("return \""))
    suite.ok("active_doc_not_in_sandbox" in gate_body,
             "D8 越界时返回 active_doc_not_in_sandbox（可定位）")
    suite.ok("catch (Exception ex)" in gate_body and "active_doc_unavailable" in gate_body,
             "D8 取活动文档抛异常也按拒绝处理（CAD 环境异常时不冒险）")

    # 证据交叉（不采信结论文字，只认文件里的数字）
    try:
        with open(os.path.join(qa.ROOT, FIXTURE_JSON), "r", encoding="utf-8") as handle:
            report = json.load(handle)
    except (OSError, ValueError) as exc:
        suite.ok(False, "H 夹具证据 JSON 可读", repr(exc))
        return

    # 注意：harness.sandbox() 会把 LOCALAPPDATA 重定向到临时目录，
    # 所以这里不能拿 os.environ 现算根目录，只能按路径片段判「在 EngineeringPlatform\sandbox 之下」。
    marker = os.path.join("engineeringplatform", "sandbox") + os.sep
    for field in ("sandboxDir", "dwgPath"):
        value = str(report.get(field) or "").lower()
        suite.ok(marker in value,
                 "H 证据：%s 落在 EngineeringPlatform\\sandbox 之下" % field, value)
        suite.ok("desktop" not in value and "文档" not in value,
                 "H 证据：%s 不在用户桌面/文档目录" % field, value)
    suite.ok(int(report.get("dwgBytes") or 0) > 0,
             "H 证据：DWG 有实际字节数", report.get("dwgBytes"))

    readback = report.get("readback") or {}
    suite.ok(int(readback.get("triangles") or 0) > 0, "H 证据：三角面数 > 0", readback)
    suite.ok(int(readback.get("pointsAdded") or 0) > 0, "H 证据：加点数 > 0", readback)
    suite.eq(readback.get("pointsAdded"), report.get("fixturePoints"),
             "H 证据：加点数 == 造点总数（没有静默少加）")

    # 路线确实没建出来 —— 要求「有原因、不静默」
    attempts = " | ".join(str(a) for a in (report.get("attempts") or []))
    suite.ok(report.get("alignmentName") is None,
             "H 证据：本次夹具**没有**造出路线（alignmentName=null，如实上报）")
    suite.ok("Alignment.Create" in attempts,
             "H 证据：路线失败原因留了痕（styleId/styleName 反射限制）", attempts[:300])
    suite.ok(report.get("surfaceName"), "H 证据：曲面名非空（EP-FIXTURE-TIN）",
             report.get("surfaceName"))


def section_fixture_guard_binary(suite, tmp):
    """在**真实产物 DLL** 上直接调用 IsInsideSandbox（不启动 CAD、不写任何文件）。

    为什么不直接调 Run()：Run() 的方法体在 JIT 时会解析 CAD 托管程序集，
    无 CAD 进程内一进方法就 SEHException（我实测过，见 05 文档 1.11）。
    所以退一步测「决策点」本身 —— 这是安全的边界所在，Run() 只是把
    Path.GetFullPath 的结果喂给它（:80 → :86）。
    """
    dll = os.path.join(qa.ROOT, "host", "bin", "Release", "net48", "EngineeringHost.dll")
    if not os.path.exists(dll):
        # 不要 `print + return` 静默跳过：那样「快照里没带 bin/」会让 11 条断言无声消失，
        # 报告仍然全绿（这正是我在 05 §1.16 Q6 里记过的坑）。这里显式记一条 **SKIP** 断言。
        suite.ok(True, "SKIP：产物 DLL 不存在，D8 的二进制级门禁验证未执行（快照若不含 host/bin 就会这样）",
                 "路径=%s" % dll)
        return

    script = os.path.join(tmp, "guard_probe.ps1")
    with open(script, "w", encoding="ascii", newline="\r\n") as handle:
        handle.write("\n".join([
            "$ErrorActionPreference='Stop'",
            "$asm=[Reflection.Assembly]::LoadFrom('%s')" % dll,
            "$t=$asm.GetType('EngineeringPlatform.Host.Commands.DevFixtureCommand')",
            "$g=$t.GetMethod('IsInsideSandbox',[Reflection.BindingFlags]'Static,NonPublic')",
            # 关键：根目录由 PowerShell 自己按 .NET 的 Known Folder 算法算，
            # 与 C# 侧 Environment.GetFolderPath(LocalApplicationData) 完全同源，
            # 不受 harness.sandbox() 重定向 LOCALAPPDATA 的影响。
            "$lap=[Environment]::GetFolderPath('LocalApplicationData')",
            "$sandbox=Join-Path $lap 'EngineeringPlatform\\sandbox'",
            "$desk=[Environment]::GetFolderPath('Desktop')",
            "Write-Output ('ROOT ' + $sandbox)",
            "$cases=@(",
            " @{n='sandbox_root';p=$sandbox},",
            " @{n='sandbox_child';p=(Join-Path $sandbox 'out')},",
            " @{n='sandbox_deep';p=(Join-Path $sandbox 'a\\b\\c')},",
            " @{n='desktop';p=$desk},",
            " @{n='desktop_child';p=(Join-Path $desk 'ep-not-here')},",
            " @{n='prefix_evil';p=($sandbox + '-evil\\out')},",
            " @{n='traversal';p=(Join-Path $sandbox '..\\..\\..\\Windows\\Temp')},",
            " @{n='unc';p='\\\\localhost\\C$\\Windows\\Temp'},",
            " @{n='relative';p='out'},",
            " @{n='system_temp';p=$env:TEMP})",
            "foreach($c in $cases){",
            "  $full=[IO.Path]::GetFullPath([string]$c.p)",
            "  $v=[string]$g.Invoke($null,@($full))",
            "  Write-Output ('GUARD ' + $c.n + ' -> ' + $v)",
            "}",
            "",
        ]))
    command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script]
    try:
        completed = subprocess.run(command, capture_output=True, timeout=120)
    except (OSError, subprocess.SubprocessError) as exc:
        suite.ok(False, "H2 能在真实 DLL 上调用门禁函数", repr(exc))
        return
    stdout = completed.stdout.decode("utf-8", "replace")
    observed = {}
    for line in stdout.splitlines():
        if line.startswith("GUARD "):
            name, _, value = line[len("GUARD "):].partition(" -> ")
            observed[name.strip()] = value.strip()

    suite.ok(bool(observed), "H2 门禁函数在真实 DLL 上被调用成功（未启动 CAD）",
             stdout.strip()[:300] or completed.stderr.decode("utf-8", "replace")[:300])

    expect_true = ("sandbox_root", "sandbox_child", "sandbox_deep")
    expect_false = ("desktop", "desktop_child", "prefix_evil", "traversal", "unc",
                    "relative", "system_temp")
    for name in expect_true:
        suite.eq(observed.get(name), "True",
                 "H2 放行：%s 判为 sandbox 内" % name)
    for name in expect_false:
        suite.eq(observed.get(name), "False",
                 "H2 拒绝：%s 判为越界（%s）" % (name, {"desktop": "桌面",
                                                    "desktop_child": "桌面子目录",
                                                    "prefix_evil": "sandbox-evil 前缀绕过",
                                                    "traversal": "../ 上跳",
                                                    "unc": "UNC 路径",
                                                    "relative": "相对路径",
                                                    "system_temp": "系统临时目录"}.get(name, "")))


# ============================================================================
# J. tools/mcp_live_probe.py —— 将来要交到用户手里的脚本，**绝不允许自己拉起 CAD**
# ============================================================================


def section_live_probe_static(suite):
    rel = "tools/mcp_live_probe.py"
    path = os.path.join(qa.ROOT, rel.replace("/", os.sep))
    if not suite.ok(os.path.exists(path), "J 探针脚本存在（%s）" % rel):
        return
    src = qa.read_source(rel)

    # 1) 进程启动面：一个都不许有
    forbidden = ("subprocess", "Popen", "os.system", "os.startfile", "Start-Process",
                 "CreateProcess", "ShellExecute", "win32process", "win32api.ShellExecute",
                 "ctypes.windll", "runas")
    hits = [token for token in forbidden if token in src]
    suite.eq(hits, [],
             "J **没有任何进程启动 API**（subprocess/Popen/os.system/Start-Process/CreateProcess/ShellExecute）")

    # 2) 不重试、不轮询、不常驻
    retry_tokens = ("for attempt in range", "while True", "time.sleep(", "retry", "backoff",
                    "max_attempts", "重试次数")
    retry_hits = [token for token in retry_tokens if token in src]
    suite.eq(retry_hits, [], "J 没有重试/轮询/退避逻辑（单次尝试）")

    # 3) 只连一次
    suite.eq(src.count("client.connect()"), 1, "J 只调用一次 client.connect()（单次连接）")

    # 4) 只读白名单：写域工具必须被拒
    sys.path.insert(0, os.path.join(qa.ROOT, "tools"))
    try:
        import mcp_live_probe as probe  # noqa: WPS433 - 故意在函数内导入，避免污染其它套件
    except Exception as exc:  # noqa: BLE001
        suite.ok(False, "J 探针可被导入（用于校验白名单）", repr(exc))
        return
    finally:
        sys.path.pop(0)

    for name in ("cad_create_line", "civil_create_profile", "dyn_run_lisp", "file_write_text"):
        try:
            probe._selected_tools(name)
        except ValueError:
            suite.ok(True, "J 写域工具 %s 被白名单拒绝" % name)
        else:
            suite.ok(False, "J 写域工具 %s 被白名单拒绝" % name, "竟然被接受了")
    suite.ok(probe._selected_tools("read_overview,read_layers") == ["read_overview", "read_layers"],
             "J 只读工具可通过白名单")

    # 5) 无宿主 → 快速失败 C1001 / 退出码 2，且提示「请手工启动」
    suite.eq(probe.EXIT_NO_INSTANCE, 2, "J 无实例的退出码是 2")
    suite.ok("本脚本不会启动 CAD" in src, "J 无实例时明确告知用户「本脚本不会启动 CAD」")
    instances_src = qa.read_source("mcp/instances.py")
    suite.ok('"C1001"' in instances_src and "未发现可用宿主实例" in instances_src,
             "J 实例发现为空 → C1001")

    # 6) 落盘是显式选项，默认不写
    suite.ok("if args.json_out:" in src, "J 只在显式 --json-out 时才落盘（默认零写盘）")
    suite.ok(src.count("open(") <= 2,
             "J 全脚本 open() 不超过 2 处（json-out + 无其它写盘）",
             "open( 出现 %d 次" % src.count("open("))


# ============================================================================
# I. docs/04 的措辞与证据是否一致（防「顺手夸大」）
# ============================================================================


def section_doc04(suite):
    text = qa.read_source(DOC04)

    rows = {}
    for line in text.splitlines():
        if line.startswith("| ") and "|" in line[2:]:
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if cells:
                rows[cells[0]] = line
    surface_row = rows.get("曲面属性（三角面数/点数/高程范围）", "")
    suite.ok("已实测" in surface_row or "属性级证据已取得" in surface_row,
             "I 曲面属性行：已升级为「已实测」（有夹具证据支撑）")
    for number in ("50", "36", "7.702"):
        suite.ok(number in surface_row,
                 "I 曲面属性行引用的数字 %s 与证据 JSON 一致" % number)
    suite.ok("surfaceType" in surface_row and "false" in surface_row,
             "I 曲面属性行如实标出 surfaceType=false（没写成全绿）")

    for label in ("路线（Alignment）枚举 + 起止桩号", "纵断面（Profile）枚举 + 所属路线",
                  "管网（PipeNetwork）枚举 + 管段/结构数", "点编组（PointGroup）枚举 + 点数"):
        row = rows.get(label, "")
        suite.ok("待补" in row, "I %s 仍标「待补」（没有跟着曲面一起升级）" % label)
        suite.ok("已实测" not in row, "I %s 没有冒充「已实测」" % label)


# ============================================================================
# 入口
# ============================================================================


def run() -> dict:
    suite = qa.harness.Suite("qa_round2_verify")

    with qa.harness.sandbox("qa-r2") as tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)
            section_d1(suite, server)
            section_d3(suite, server)
            section_d4(suite, server)
            with qa.harness.connect(server) as client:
                section_d6(suite, client)
                section_d5(suite, client)
                section_d7(suite, client)
        section_contract(suite)
        section_mcp_passthrough(suite, tmp)
        section_fixture(suite)
        section_fixture_guard_binary(suite, tmp)
        section_live_probe_static(suite)
        section_doc04(suite)

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
