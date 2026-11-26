"""tests/qa/qa_contract_delta.py —— 第二轮契约新增字段的双端一致性（QA 独立复核）。

第二轮（Civil 3D 接入）往契约里加了字段，工程师的 673 条是「自己按自己实现写的断言」。
QA 要独立回答的是：**schema 声明的 required，mockhost / C# / MCP 三边是否真的都给了**。
这类字段最容易出的事故是「真实宿主有、mockhost 没有」——于是 mock 链路全绿、真机才炸。

本模块逐条验证：
  1. `handshake.schema.json#/$defs/HelloAck` 的 required 全字段，mockhost 的 hello_ack 是否齐全；
  2. `hostProduct` ∈ {AutoCAD, Civil3D}、`hostRelease` 形如 R24.3（不是随便一个字符串）；
  3. `capabilities.civilApi` 的 available/product/release/writeSupported 是否齐全且自洽
     （available=false 时 reason 必须非空——「说不可用」也得给理由）；
  4. `read_overview.civil3d` 段：schema required（detected/counts/truncated）+ 五类对象清单；
     mockhost 声明 detected=false 时必须给出 note 说明这是降级路径，不许静默；
  5. `ResponseEnvelope` 的 required 字段在真实响应里齐全（warnings 必须是数组而非 null）；
  6. `tools.schema.json` 新增的 `$defs/Basis` 能被 `$ref` 解析（不能悬空引用）；
  7. `toolCatalogHash` 双端同源：Python 侧哈希 == sha256(protocol/tools.schema.json 原始字节)[:16]，
     且 C# 侧读的是同一份嵌入资源（源码级核对）；
  8. mockhost 的 `hello_ack` 与真实宿主应答的**字段集合**必须一致（防 mock 漏字段）。
"""

from __future__ import annotations

import hashlib
import json
import re
import sys

import qa_common as qa

PRODUCT_VALUES = ("AutoCAD", "Civil3D")
RELEASE_RE = re.compile(r"^R2[0-9]\.[0-9]$")


def load_schema(rel_path: str) -> dict:
    return json.loads(qa.read_source(rel_path))


def refs_in(node, found=None):
    """递归收集 $ref 字符串。"""
    if found is None:
        found = []
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "$ref" and isinstance(value, str):
                found.append(value)
            else:
                refs_in(value, found)
    elif isinstance(node, list):
        for item in node:
            refs_in(item, found)
    return found


def resolve_ref(schema: dict, ref: str):
    """只支持本文件内 #/$defs/Xxx 形式。"""
    if not ref.startswith("#/"):
        return None
    node = schema
    for part in ref[2:].split("/"):
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def run() -> dict:
    suite = qa.harness.Suite("qa_contract_delta")

    handshake_schema = load_schema("protocol/handshake.schema.json")
    envelope_schema = load_schema("protocol/envelope.schema.json")
    tools_schema = load_schema("protocol/tools.schema.json")

    with qa.harness.sandbox("qa-delta") as _tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)
            client = qa.harness.connect(server)
            try:

                # ---------------- 1/2. HelloAck required ----------------
                def hello_ack_required():
                    ack = client.handshake_info or {}
                    suite.is_true(isinstance(ack, dict) and bool(ack),
                                  "拿到 mockhost 的 hello_ack（来自 PipeClient.handshake_info）",
                                  "%r" % (type(ack),))
                    required = (handshake_schema["$defs"]["HelloAck"]["required"])
                    missing = [key for key in required if key not in ack]
                    qa.eqd(suite, sorted(missing), [],
                           "hello_ack 覆盖 schema 声明的全部 %d 个 required 字段（缺 %r）"
                           % (len(required), sorted(missing)),
                           "实际字段=%r" % (sorted(ack),))

                    suite.ok(ack.get("hostProduct") in PRODUCT_VALUES,
                             "hostProduct ∈ {AutoCAD, Civil3D}",
                             "实际=%r" % (ack.get("hostProduct"),))
                    suite.matches(ack.get("hostRelease") or "", RELEASE_RE.pattern,
                                  "hostRelease 形如 R24.3")
                    suite.ok(ack.get("hostVersion") != ack.get("hostRelease"),
                             "hostVersion（插件版本）与 hostRelease（CAD 版本）语义不混用",
                             "hostVersion=%r hostRelease=%r"
                             % (ack.get("hostVersion"), ack.get("hostRelease")))

                suite.case("HelloAck required", hello_ack_required)

                # ---------------- 3. capabilities.civilApi 自洽 ----------------
                def civil_capabilities():
                    caps = (client.handshake_info or {}).get("capabilities") or {}
                    civil = caps.get("civilApi") or {}
                    for key in ("available", "product", "release", "writeSupported",
                                "probe", "reason"):
                        suite.ok(key in civil,
                                 "capabilities.civilApi 含字段 %s" % key,
                                 "keys=%r" % (sorted(civil),))
                    suite.is_false(civil.get("writeSupported"),
                                   "civilApi.writeSupported=false（M0 不实现任何 Civil 写 API）")
                    if not civil.get("available"):
                        suite.is_true(bool(civil.get("reason")),
                                      "civilApi.available=false 时必须给 reason（不许静默降级）",
                                      "%r" % (civil.get("reason"),))
                        suite.ok(civil.get("product") in (None, "") or
                                 civil.get("product") in PRODUCT_VALUES,
                                 "不可用时 product 为空或合法值（不瞎报 Civil3D）",
                                 "%r" % (civil.get("product"),))

                suite.case("civilApi 自洽", civil_capabilities)

                # ---------------- 4. read_overview.civil3d ----------------
                def overview_civil3d():
                    response = client.call("read_overview", {})
                    result = response.get("result") or {}
                    civil3d = result.get("civil3d")
                    suite.is_true(isinstance(civil3d, dict),
                                  "read_overview 返回 civil3d 段（第二轮新增输出）",
                                  "顶层字段=%r" % (sorted(result),))
                    civil3d = civil3d or {}
                    for key in ("detected", "counts", "truncated"):
                        suite.ok(key in civil3d,
                                 "civil3d 含 required 字段 %s" % key,
                                 "civil3d 字段=%r" % (sorted(civil3d),))
                    suite.is_true(isinstance(civil3d.get("counts"), dict),
                                  "civil3d.counts 是对象（不是数组/标量）")
                    counts = civil3d.get("counts") or {}
                    for key in ("surfaces", "alignments", "profiles",
                                "pipeNetworks", "pointGroups", "total"):
                        suite.ok(key in counts,
                                 "civil3d.counts 含五类对象计数与 total：%s" % key,
                                 "counts=%r" % (sorted(counts),))
                    if counts:
                        total = counts.get("total")
                        parts = sum(int(counts.get(k) or 0) for k in
                                    ("surfaces", "alignments", "profiles",
                                     "pipeNetworks", "pointGroups"))
                        suite.eq(total, parts,
                                 "civil3d.counts.total == 五类之和（计数自洽）")
                    if civil3d.get("detected") is False:
                        suite.is_true(bool(civil3d.get("note")),
                                      "detected=false 时必须给 note 说明这是降级路径",
                                      "%r" % (civil3d.get("note"),))
                        suite.is_false(civil3d.get("truncated"),
                                       "detected=false 时 truncated 应为 false（没对象可截）")
                    if civil3d.get("objectCap"):
                        suite.is_true(int(civil3d["objectCap"]) > 0,
                                      "civil3d.objectCap 是正整数（有上限才谈得上截断）")

                suite.case("read_overview.civil3d", overview_civil3d)

                # ---------------- 5. ResponseEnvelope required ----------------
                def envelope_required():
                    response = client.call("sys_ping", {})
                    required = envelope_schema["$defs"]["ResponseEnvelope"]["required"]
                    missing = [key for key in required if key not in response]
                    qa.eqd(suite, sorted(missing), [],
                           "响应封套覆盖 schema 声明的 %d 个 required 字段（缺 %r）"
                           % (len(required), sorted(missing)),
                           "实际字段=%r" % (sorted(response),))
                    suite.is_true(isinstance(response.get("warnings"), list),
                                  "warnings 恒为数组（不是 null）—— 客户端不必做空值分支",
                                  "%r" % (response.get("warnings"),))
                    suite.is_true(isinstance(response.get("ok"), bool),
                                  "ok 恒为布尔（不是 0/1/字符串）")
                    suite.matches(str(response.get("startedAt") or ""),
                                  r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
                                  "startedAt 是带毫秒的 UTC ISO 串")

                suite.case("ResponseEnvelope required", envelope_required)

                # ---------------- 6. tools.schema.json 的 $defs/Basis ----------------
                def schema_refs():
                    defs = tools_schema.get("$defs") or {}
                    suite.is_true("Basis" in defs,
                                  "tools.schema.json 新增 $defs/Basis",
                                  "现有 $defs=%r" % (sorted(defs),))
                    all_refs = refs_in(tools_schema)
                    dangling = []
                    for ref in all_refs:
                        if ref.startswith("#/") and resolve_ref(tools_schema, ref) is None:
                            dangling.append(ref)
                    qa.eqd(suite, sorted(set(dangling)), [],
                           "tools.schema.json 内所有本文件 $ref 都能解析（无悬空引用）",
                           "悬空=%r" % (sorted(set(dangling)),))
                    if "Basis" in defs:
                        suite.is_true(isinstance(defs["Basis"], dict) and
                                      "properties" in defs["Basis"],
                                      "$defs/Basis 是带 properties 的 object 定义")

                suite.case("schema $ref", schema_refs)

                # ---------------- 7. toolCatalogHash 双端同源 ----------------
                def catalog_hash_source():
                    raw = open(qa.ROOT + "/protocol/tools.schema.json", "rb").read()
                    expected = "sha256:" + hashlib.sha256(raw).hexdigest()[:16]
                    actual = ((client.handshake_info or {}).get("capabilities") or {}) \
                        .get("toolCatalogHash")
                    suite.eq(actual, expected,
                             "mockhost 的 toolCatalogHash == sha256(schema 原始字节)[:16]")
                    # C# 侧读同一份嵌入资源（源码级核对：不能是另算了一份）
                    host_source = qa.read_source("host/Protocol/ToolCatalog.cs")
                    suite.contains(host_source, '"protocol/tools.schema.json"',
                                   "C# 侧从嵌入资源 protocol/tools.schema.json 取原始字节")
                    suite.contains(host_source, "Sha256Hex(bytes)",
                                   "C# 侧对原始字节做 sha256（不是对序列化结果算）")
                    props = qa.read_source("host/EngineeringHost.csproj")
                    suite.contains(props, "EmbeddedResource",
                                   "tools.schema.json 以 EmbeddedResource 打进程序集（双端同一份字节）")

                suite.case("toolCatalogHash 同源", catalog_hash_source)

                # ---------------- 8. 与真实宿主的字段集合对比 ----------------
                def live_field_parity():
                    live = __import__("qa_live_cad_probe")
                    instances = live.find_instances()
                    if not instances:
                        suite.ok(True,
                                 "SKIP：无**存活的 acad.exe** 实例，跳过「mockhost vs 真机字段集合」"
                                 "对比（PID 复用会造成假阳性，故按进程映像名判定）",
                                 "需要 AutoCAD/Civil 3D 在跑")
                        return
                    import pipeclient
                    config = qa.harness.load_config()
                    probe = pipeclient.PipeClient(instances[-1]["pipeName"], config,
                                                  logger=None, heartbeat_enabled=False)
                    try:
                        probe.connect()
                        real_ack = probe.handshake({"name": "qa-delta", "version": "1.0.0"})
                    except pipeclient.HandshakeError as exc:
                        # 真实宿主同一时刻只允许一个会话：若工程师的 e2e 正在跑，
                        # 这里必然拿到 bye(protocol_violation/session_busy)。
                        # 这是**环境占用**，不是代码缺陷 → 明确记 SKIP，不判失败。
                        suite.ok(True,
                                 "SKIP：真实宿主被其他客户端占用（单会话语义），"
                                 "本对比需独占会话",
                                 "HandshakeError=%r" % (exc,))
                        return
                    except (pipeclient.HostUnreachable, pipeclient.PipeClosed,
                            pipeclient.PipeError) as exc:
                        suite.ok(True, "SKIP：真实宿主已在握手前退出",
                                 "%r" % (exc,))
                        return
                    finally:
                        try:
                            probe.close()
                        except Exception:                     # noqa: BLE001
                            pass
                    mock_ack = client.handshake_info or {}
                    mock_keys = set(mock_ack)
                    real_keys = set(real_ack)
                    print("真机 hello_ack 独有字段：%r" % (sorted(real_keys - mock_keys),))
                    print("mockhost 独有字段：%r" % (sorted(mock_keys - real_keys),))
                    qa.eqd(suite, sorted(real_keys - mock_keys), [],
                           "真实宿主没有 mockhost 缺失的字段（防 mock 漏字段导致假绿）",
                           "真机独有=%r" % (sorted(real_keys - mock_keys),))

                # ---------------- N04-B：read_civil_list 的 type 契约 ----------------
                def civil_type_contract():
                    """三端必须对「未支持的 type」给同一个答案，且不能把「全部对象」当答案。

                    2026-11-22 实测发现三端不一致：
                      * schema enum  = [any, alignment, profile, surface, pipeNetwork, pointGroup]
                      * description  = 「路线/纵断面/曲面/廊道/管网/采样线组」（有廊道/采样线组、**没有**点编组）
                      * mockhost     = 未知 type → 静默返回 0 条
                      * C#（真宿主）= CivilRecordsOf 的 default 分支 → **返回全部 5 类**
                    「问廊道却拿到曲面+路线」比报错危险得多（模型会当成廊道用）。
                    """
                    tool = next(item for item in (tools_schema.get("tools") or [])
                                if item.get("name") == "read_civil_list")
                    props = (tool.get("inputSchema") or {}).get("properties") or {}
                    enum_values = list((props.get("type") or {}).get("enum") or [])
                    # 描述必须把**两处**都算上：tool.description 与 type 属性的 description。
                    # 只查一处会得出「说明缺失」的假结论（工程师那 4 条红就是这么来的）。
                    desc = "%s。%s" % (str(tool.get("description") or ""),
                                       str((props.get("type") or {}).get("description") or ""))
                    supported = [value for value in enum_values if value != "any"]
                    suite.ok("read_overview" in desc,
                             "描述指路 read_overview 的 civil3d 段（而不是让模型用空结果推断「没有」）",
                             "描述里没提 read_overview")

                    # ① 描述与 enum 对齐（**严格版 + 白名单句式**，team-lead 2026-11-22 裁定）：
                    #    受支持类型必须逐个点名；未支持类型**只允许**出现在含「未开放/尚未开放」的说明句里
                    #    （那句是给模型的纠错指引，不是承诺）。除该句式外出现任何未支持类型 → 红。
                    sentences = [part for part in re.split(r"[。；\n]", desc) if part.strip()]
                    zh_map = {"路线": "alignment", "纵断面": "profile", "曲面": "surface",
                              "廊道": "corridor", "管网": "pipeNetwork", "采样线组": "sampleLineGroup",
                              "点编组": "pointGroup"}
                    english = ("any", "alignment", "profile", "surface", "pipeNetwork",
                               "pointGroup", "corridor", "sampleLineGroup")
                    mentioned = set()
                    leaks = []
                    for sentence in sentences:
                        names = {code for word, code in zh_map.items() if word in sentence}
                        names |= {code for code in english
                                  if re.search(r"\b%s\b" % code, sentence)}
                        mentioned |= names
                        unsupported_here = names - set(supported) - {"any"}
                        if unsupported_here and not ("未开放" in sentence or "尚未开放" in sentence):
                            leaks.append((sentence.strip()[:48], sorted(unsupported_here)))
                    qa.eqd(suite, leaks, [],
                           "描述里未支持的类型**只**出现在「未开放」说明句里（白名单句式）",
                           "越界句=%r" % (leaks,))
                    qa.eqd(suite, sorted(set(supported) - mentioned), [],
                           "描述点名了 enum 里**每一个**受支持类型（含 pointGroup）",
                           "未点名=%r" % (sorted(set(supported) - mentioned),))

# ② 行为：未支持的 type 必须被拒（E1001），不能「静默空集」也不能「返回全部」
                    for bad in ("corridor", "sampleLineGroup", "bogus"):
                        response = client.call("read_civil_list", {"type": bad})
                        error = response.get("error") or {}
                        result = response.get("result") or {}
                        suite.ok(response.get("ok") is False and error.get("code") == "E1001",
                                 "read_civil_list type=%r 必须被拒（E1001），而非静默返回" % bad,
                                 "实际 ok=%r error=%r counts=%r"
                                 % (response.get("ok"), (error.get("code"), error.get("detail")),
                                    result.get("counts")))
                        suite.eq(len(result.get("items") or []), 0,
                                 "read_civil_list type=%r 被拒时不得返回任何 items" % bad)

                    # ④ `type` 的**缺省语义**三端一致（2026-11-22 21:0x 定稿：可选 + default=any）
                    #    允许「有文档的默认」，禁止「静默的默认」：schema 必须写 default=any，
                    #    且 mock 上「不传」与「传 any」的结果必须完全相同。
                    required_list = (tool.get("inputSchema") or {}).get("required") or []
                    suite.ok("type" not in required_list,
                             "read_civil_list.type 是可选的（required 不含 type）",
                             "required=%r" % (required_list,))
                    qa.eqd(suite, (props.get("type") or {}).get("default"), "any",
                           "schema 里 type 的 default 显式写成 any（缺省语义有文档，不靠实现猜）",
                           "default=%r" % ((props.get("type") or {}).get("default"),))
                    absent = client.call("read_civil_list", {}).get("result") or {}
                    explicit = client.call("read_civil_list", {"type": "any"}).get("result") or {}
                    suite.eq((absent.get("counts") or {}).get("total"),
                             (explicit.get("counts") or {}).get("total"),
                             "不传 type 与传 any 的条数相同（缺省==any，行为一致）")
                    qa.eqd(suite, len(absent.get("items") or []), len(explicit.get("items") or []),
                           "不传 type 与传 any 的 items 数相同",
                           "absent=%d explicit=%d" % (len(absent.get("items") or []),
                                                      len(explicit.get("items") or [])))

                    # ③ 静态：C# 的未知 kind 不得走「返回全部类型」的 default 分支
                    host_src = qa.read_source("host/Tools/ReadToolHandlers.cs")
                    start = host_src.find("private static List<CivilObjectRecord> CivilRecordsOf")
                    body = host_src[start:start + 2600] if start >= 0 else ""
                    default_pos = body.find("default:")
                    tail = body[default_pos:default_pos + 500] if default_pos >= 0 else ""
                    has_any_case = 'case "any"' in body
                    suite.ok(has_any_case,
                             "C# CivilRecordsOf 有独立的 case \"any\"（any 与未知 kind 不能混同一分支）",
                             "没找到 case \"any\" → 合法值 any 与非法值共用 default 分支")
                    aggregating = tail.count("records.AddRange") >= 3
                    suite.ok(not aggregating,
                             "C# CivilRecordsOf 的 default 分支不得把「全部类型」当作未知 kind 的答案",
                             "default 分支里 AddRange 次数=%d（≥3 即视为聚合全部；未知 kind 应报 E1001）"
                             % tail.count("records.AddRange"))

                suite.case("read_civil_list type 契约", civil_type_contract)
                suite.case("真机字段集合对比", live_field_parity)

            finally:
                client.close()

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
