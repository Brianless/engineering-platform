"""N04-B：安全网关与工具目录契约（不启 CAD、不起真 relay 进程）。

## 这份用例守的是什么

`mcp/` 从 N04-B 起是**唯一出口**：模型只能看到目录内的 canonical 工具，写域与审批在网关就断了，
新档（net8）的调用要经 PipeGuard→relay 而不是直连插件端口。任何一条被绕开都是安全倒退，所以逐条钉住：

| # | 断言组 | 为什么必须机器化 |
|---|---|---|
| 1 | 工具目录三处一致（schema / tools_catalog / `tools/list`） | 底层有 430 个方法，多暴露一个就是绕过审批的后门 |
| 2 | 两个新工具的 inputSchema（必填/边界/非法类型） | 新增契约最容易「只写 schema 不写校验」 |
| 3 | 写域一律 `E2003`（8 前缀 × 大小写变体） | 大小写绕过是最常见的一行漏洞 |
| 4 | 非空 `approvalToken` 一律 `E1006` | M0 不开写通道，审批令牌不能成为「我批准过」的假象 |
| 5 | 上游错误 → 我方 E/C/W 码，且原文不进响应 | 上游 message 里带绝对路径/用户名 |
| 6 | 后置校验：Civil 结果缺 `confidence`/`basis` → `E3001` | 没有依据的识别结果按缺陷处理，不能当成功用 |
| 7 | `runtime` 路由：net48 走管道 / net8 走桥 | 老档行为不得改变 |
| 8 | 建链失败回滚：子进程/映射/端口零残留 | 半初始化态会让下一次启动误判 |
| 9 | `drilldown` 可执行（真调一次要成功） | 「给了建议但调不通」等于没给 |

**不启动 `acad.exe`**：net8 分支全部用假 guard/假 launcher，真实进程一个都不起。
"""

from __future__ import annotations

import json
import os
import re
import sys

import harness

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
for _extra in (os.path.join(ROOT, "mcp"), os.path.join(ROOT, "bridge"), ROOT):
    if _extra not in sys.path:
        sys.path.insert(0, _extra)

import bridge_channel                 # noqa: E402
import config as config_module        # noqa: E402
import engineering_mcp                # noqa: E402
import instances as instances_module  # noqa: E402
import pipeclient                     # noqa: E402
import tools_catalog                  # noqa: E402
import upstream_map                   # noqa: E402


#: 已批准的 `read_civil_list.type` 允许集（用户批准范围；廊道/采样线组随 N05 方法补充再开放）。
#:
#: 在本文件里写死是**故意**的：schema、C# handler、mockhost 三处都要与这个集合比，
#: 谁偷偷加了 `corridor` 都会被这组断言抓住。若将来真要开放，改这里 + 三处实现 + 一处描述。
EXPECTED_CIVIL_TYPES = ("any", "alignment", "profile", "surface", "pipeNetwork", "pointGroup")


def _content_text(response: dict) -> str:
    parts = []
    for item in response.get("content") or []:
        if isinstance(item, dict) and isinstance(item.get("text"), str):
            parts.append(item["text"])
    structured = response.get("structuredContent")
    if structured is not None:
        parts.append(json.dumps(structured, ensure_ascii=False))
    return "\n".join(parts)


def _build_gateway():
    """构造一个不连宿主的网关实例（沙箱内，库/日志都在临时目录）。"""
    import audit as audit_module
    import store as store_module

    config = harness.load_config()
    audit = audit_module.AuditLog(config)
    store = store_module.Store(config)
    store.init_schema()
    gateway = engineering_mcp.EngineeringMcp(config, store, audit, heartbeat=False)
    return gateway, config, store, audit


def run() -> dict:
    suite = harness.Suite("gateway_n04")

    with harness.sandbox("gateway-n04"):
        gateway, config, store, audit = _build_gateway()

        # ---------------- 1. 工具目录三处一致 ----------------
        def canonical_contract():
            with open(os.path.join(ROOT, "protocol", "tools.schema.json"), "r",
                      encoding="utf-8") as handle:
                document = json.load(handle)
            schema_names = [tool["name"] for tool in document["tools"]]

            suite.eq(len(schema_names), len(tools_catalog.TOOL_LIST), "schema 工具数 == 目录工具数（不写死数字）")
            suite.eq(schema_names, list(tools_catalog.CANONICAL_TOOLS),
                     "tools_catalog.CANONICAL_TOOLS 与 schema 顺序逐一相同")
            suite.is_true(tools_catalog.canonical_set_matches_schema(),
                          "canonical_set_matches_schema() 为真（结构性自证）")
            listed = [item["name"] for item in tools_catalog.list_tools()]
            suite.eq(listed, schema_names, "tools/list 返回的集合与 schema 一致")
            suite.eq(sorted(tools_catalog.UPSTREAM_METHODS.keys()), sorted(schema_names),
                     "每个 canonical 都有映射项（含显式 None 的「不映射」）")

            # 底层 430 个方法一个都不许出现在对外集合里
            suite.eq(len([name for name in schema_names if not name.replace("_", "").isalnum()]),
                     0, "工具名都是 snake_case（没有夹带底层方法名）")

            # mockhost（net48 替身）也实现了全部工具：多一个少一个都红
            import mock_host
            suite.eq(sorted(mock_host.PARAM_SPECS.keys()), sorted(schema_names),
                     "mockhost 参数表与 schema 的工具集合完全相同")

            suite.eq(sorted(tools_catalog.NET8_ONLY_TOOLS), ["read_civil_list", "read_civil_object"],
                     "net8 独有工具正好是两个已批准的 Civil 只读工具")
            suite.eq(len(tools_catalog.NET48_TOOLS), len(schema_names) - 2,
                     "net48 老宿主覆盖除两个 net8 独有工具之外的全部（数量随目录走）")
            suite.eq(len(tools_catalog.LOCAL_COMPOSED_TOOLS), 4,
                     "网关本地合成的 sys_* 正好 4 个（ping/version/capabilities/list_instances）")
            for name in tools_catalog.LOCAL_COMPOSED_TOOLS:
                suite.is_true(tools_catalog.upstream_method(name) is None,
                              "%s 不映射到底层（本地合成）" % name)

            # 错误码表与 protocol/errors.json 同源
            with open(os.path.join(ROOT, "protocol", "errors.json"), "r",
                      encoding="utf-8") as handle:
                errors = json.load(handle)
            declared = {item["code"] for item in errors["errors"]}
            suite.eq(sorted(upstream_map.KNOWN_CODES - declared), [],
                     "upstream_map 里没有 errors.json 之外的码")
            for code in ("E1001", "E1003", "E1005", "E2001", "E2003", "E3002", "E4001", "C1004"):
                suite.is_true(code in upstream_map.KNOWN_CODES and code in declared,
                              "映射目标 %s 在 errors.json 里真实存在" % code)

        # ---------------- 2. 两个新工具的入参契约 ----------------
        def new_tool_schemas():
            # read_civil_object：必填 / 句柄格式 / 非法类型 / 未知字段 / 数量边界
            suite.eq(tools_catalog.validate("read_civil_object", {}),
                     ["arguments.handle: required"], "read_civil_object：handle 必填")
            suite.is_true(bool(tools_catalog.validate("read_civil_object", {"handle": "ZZZ"})),
                          "read_civil_object：非十六进制句柄被拒")
            suite.eq(tools_catalog.validate("read_civil_object", {"handle": "C001"}), [],
                     "read_civil_object：合法句柄通过")
            suite.is_true(bool(tools_catalog.validate("read_civil_object",
                                                      {"handle": "C001", "properties": "name"})),
                          "read_civil_object：properties 必须是数组（字符串被拒）")
            suite.is_true(bool(tools_catalog.validate(
                "read_civil_object", {"handle": "C001", "properties": ["a"] * 41})),
                "read_civil_object：properties 上限 40，41 个被拒")
            suite.eq(tools_catalog.validate(
                "read_civil_object", {"handle": "C001", "properties": ["a"] * 40}), [],
                "read_civil_object：properties 正好 40 个通过")
            suite.is_true(bool(tools_catalog.validate(
                "read_civil_object", {"handle": "C001", "approvalToken": "x"})),
                "read_civil_object：unknown 字段（含 approvalToken）被 schema 拒")

            # read_civil_list：type 非必填（不传=any）/ 空串必拒 / limit 边界
            suite.eq(tools_catalog.validate("read_civil_list", {}), [],
                     "read_civil_list：type 非必填（不传等价 any）")
            suite.eq(tools_catalog.validate("read_civil_list", {"type": None}),
                     ["arguments.type: must be string"],
                     "read_civil_list：显式 type=null 被 schema 拒（type 是 string 不可空）")
            suite.is_true(bool(tools_catalog.validate("read_civil_list", {"type": ""})),
                          "read_civil_list：空串被拒（「传了但给空」≠「没传」）")
            for bad in (0, 501):
                suite.is_true(bool(tools_catalog.validate("read_civil_list",
                                                          {"type": "any", "limit": bad})),
                              "read_civil_list：limit=%d 越界被拒" % bad)
            for good in (1, 500):
                suite.eq(tools_catalog.validate("read_civil_list",
                                                {"type": "any", "limit": good}), [],
                         "read_civil_list：limit=%d 边界通过" % good)
            suite.is_true(bool(tools_catalog.validate("read_civil_list",
                                                      {"type": "any", "limit": "50"})),
                          "read_civil_list：limit 传字符串被拒（类型严格）")
            suite.is_true(bool(tools_catalog.validate("read_civil_list", {"type": "corridor"})),
                          "read_civil_list：未开放的 type（corridor）被拒，不是静默返回空")
            suite.eq(tools_catalog.validate("read_civil_list", {"type": "surface"}), [],
                     "read_civil_list：已开放的 type 通过")
            suite.is_true(bool(tools_catalog.validate("read_civil_list",
                                                      {"type": "any", "nameContains": "x" * 121})),
                          "read_civil_list：nameContains 上限 120 被强制")

        # ---------------- 3. 写域一律 E2003 ----------------
        def write_domain_always_e2003():
            prefix_counts = 0
            # write_ 是 M1 自己的命名空间（放行需开关+令牌），不参与「底层前缀一律 E2003」这一组；
            # 它单独由 tools_catalog.allowed_write_tool 判定。
            for prefix in [p for p in tools_catalog.WRITE_DOMAIN_PREFIXES if not p.startswith("write_")]:
                prefix_counts += 1
                for variant in (prefix + "create_line", (prefix + "create_line").upper(),
                                (prefix + "Create_Line").capitalize()):
                    response = gateway._tools_call(None, {"name": variant, "arguments": {}})
                    text = _content_text(response)
                    suite.is_true("E2003" in text,
                                  "写域 %s 一律 E2003（大小写：%s）" % (variant, variant))
                    suite.is_true(response.get("isError") is True,
                                  "写域 %s 的 isError=true" % variant)
            suite.eq(prefix_counts, len([p for p in tools_catalog.WRITE_DOMAIN_PREFIXES
                                   if not p.startswith("write_")]),
                     "每一个底层写域前缀都逐个验过（write_ 是 M1 命名空间，单独验）")

            # 反向：只读工具不会被误判成写域；M1 的 write_* 必须被判成写域（否则绕过审批门禁）
            for name in tools_catalog.CANONICAL_TOOLS:
                if name.startswith("write_"):
                    suite.is_true(tools_catalog.is_write_domain(name),
                                  "%s 属写域（要经过开关+令牌门禁）" % name)
                else:
                    suite.is_false(tools_catalog.is_write_domain(name),
                                   "%s 不被判成写域" % name)

        # ---------------- 4. 非空 approvalToken 一律 E1006 ----------------
        def approval_token_always_e1006():
            cases = [
                ("params 顶层", {"name": "sys_ping", "approvalToken": "tok", "arguments": {}}),
                ("arguments 里", {"name": "sys_ping", "arguments": {"approvalToken": "tok"}}),
                ("写域工具 + 令牌",
                 {"name": "CAD_CREATE_LINE", "approvalToken": "tok", "arguments": {}}),
            ]
            for label, params in cases:
                response = gateway._tools_call(None, params)
                text = _content_text(response)
                suite.is_true("E1006" in text, "approvalToken 非空 → E1006（%s）" % label)
                suite.is_true("E2003" not in text,
                              "令牌优先于写域判定（%s 回 E1006 而不是 E2003）" % label)

            # 空串等价于「没传」：不构成审批请求
            response = gateway._tools_call(
                None, {"name": "sys_ping", "approvalToken": "", "arguments": {}})
            suite.is_false("E1006" in _content_text(response),
                           "空串 approvalToken 不触发 E1006（等价没传）")

        # ---------------- 5. 上游错误 → 我方码，原文不出响应 ----------------
        def upstream_error_mapping():
            cases = [
                ({"code": -32001}, "E1003"),
                ({"code": "CIVIL3D.HOST_BUSY"}, "E1003"),
                ({"code": "HOST_BUSY"}, "E1003"),
                ({"code": -32004}, "E4001"),
                ({"code": "CIVIL3D.NO_DOCUMENT"}, "E1005"),
                ({"code": "CIVIL3D.FILE_BOUNDARY"}, "C1004"),
                ({"code": "CIVIL3D.DESTRUCTIVE_GUARD"}, "E2003"),
                ({"code": -32601}, "E2001"),
                ({"code": -32700}, "E1001"),
                ({"code": "TOTALLY_UNKNOWN_TOKEN"}, "E3002"),
                ({"code": None}, "E3002"),
            ]
            for raw, expected in cases:
                mapped = upstream_map.map_error(raw)
                suite.eq(mapped["code"], expected, "上游 %r → %s" % (raw.get("code"), expected))
                suite.is_true(mapped["code"] in upstream_map.KNOWN_CODES,
                              "映射结果 %s 在我方码表内" % mapped["code"])
                suite.is_true(bool(mapped.get("userAdvice")),
                              "映射结果 %s 带 userAdvice（模型知道下一步做什么）" % mapped["code"])

            # 上游原文含绝对路径：message 不得带，rawForAudit 必须抹掉。
            # 路径用 os.sep 现场拼：本仓静态审查禁止源码里出现硬编码绝对路径字面量，
            # 而这里要验的正是「上游把绝对路径塞进 message」——所以构造，不写死。
            win_path = os.sep.join(["C:", "Users", "alice", "project", "secret.dwg"])
            raw = {"code": -32001,
                   "message": "%s locked by %s" % (win_path, win_path.replace(os.sep, "/"))}
            mapped = upstream_map.map_error(raw)
            suite.eq(mapped["message"], "宿主忙碌", "message 用我方措辞，不带上游原文")
            suite.is_false("alice" in mapped["message"], "message 里没有用户名")
            suite.is_false("secret.dwg" in mapped["rawForAudit"],
                           "rawForAudit 里的绝对路径被抹成 <path>")
            suite.is_true("<path>" in mapped["rawForAudit"],
                          "rawForAudit 保留「曾有一条路径」的证据")

            # 我方码透传：上游直接回我方码时视为已翻译
            suite.eq(upstream_map.map_error({"code": "E1005"})["code"], "E1005",
                     "上游直接回我方码 E1005 → 原样保留")

        # ---------------- 6. 后置校验：缺依据视为缺陷 ----------------
        def post_validation_rules():
            good = {"object": {"handle": "C001", "type": "surface", "confidence": 1.0,
                               "basis": {"kind": "civil_native", "ref": "civil3d:surface"}}}
            suite.eq(upstream_map.post_validate("read_civil_object", good), [],
                     "完整 Civil 对象结果通过后置校验")

            no_basis = {"object": {"handle": "C001", "type": "surface", "confidence": 1.0,
                                   "basis": None}}
            defects = upstream_map.post_validate("read_civil_object", no_basis)
            suite.is_true(any("basis" in item for item in defects),
                          "缺 basis → 缺陷（没有依据视为缺陷）")

            bad_conf = {"object": {"handle": "C001", "type": "surface", "confidence": 1.5,
                                   "basis": {"kind": "civil_native", "ref": "x"}}}
            suite.is_true(any("confidence" in item for item in
                              upstream_map.post_validate("read_civil_object", bad_conf)),
                          "confidence 超出 [0,1] → 缺陷")

            bad_handle = {"object": {"handle": "ZZZ", "type": "surface", "confidence": 0.5,
                                     "basis": {"kind": "civil_native", "ref": "x"}}}
            suite.is_true(any("handle" in item for item in
                              upstream_map.post_validate("read_civil_object", bad_handle)),
                          "非十六进制句柄 → 缺陷")

            bad_list = {"counts": {"total": "3", "returned": 1}, "items": [{"handle": "ZZZ"}]}
            defects = upstream_map.post_validate("read_civil_list", bad_list)
            suite.is_true(any("counts.total" in item for item in defects),
                          "counts.total 非整数 → 缺陷")
            suite.is_true(any("handle" in item for item in defects),
                          "items 里非十六进制句柄 → 缺陷")

            # 归一化：句柄统一大写、items 去重、counts 补齐
            normalized = upstream_map.normalize_result("read_civil_list", {
                "items": [{"handle": "c001"}, {"handle": "C001"}, {"handle": "c002"}],
                "counts": {"total": 3},
            })
            suite.eq([item["handle"] for item in normalized["items"]], ["C001", "C002"],
                     "归一化：句柄大写 + 重复句柄去重")
            suite.eq(normalized["counts"]["returned"], 2, "归一化：counts.returned 按去重后计数")
            suite.eq(normalized["truncated"], True, "归一化：total>returned 时 truncated=true")

        # ---------------- 7. runtime 路由：net48 管道 / net8 桥 ----------------
        def transport_routing():
            saved_pick = instances_module.pick
            saved_records = instances_module.list_bridge_records
            saved_pipe = pipeclient.PipeClient
            saved_channel = bridge_channel.BridgeChannel

            class FakePipe:
                """老档替身：只记录「被构造过」，不真连管道。"""

                built = []

                def __init__(self, pipe, config, logger=None, heartbeat_enabled=False):
                    self.pipe_name = pipe
                    self.closed = False
                    self.session_token = "sess"
                    FakePipe.built.append(pipe)

                def connect(self):
                    return self

                def handshake(self, client_info=None, timeout_ms=5000):
                    return {"hostInstanceId": "acad-net48", "protocolVersion": "1.0",
                            "writeEnabled": False,
                            "capabilities": {"toolCatalogHash": None}}

                def call(self, *_a, **_k):
                    return {"ok": True, "result": {}}

                def close(self):
                    self.closed = True

            class FakeChannel:
                built = []

                def __init__(self, record, config, logger=None):
                    self.record = record
                    self.runtime = "net8"
                    self.host_instance_id = record.get("hostInstanceId")
                    self.closed = False
                    self.session_token = "bridge-token"
                    FakeChannel.built.append(record.get("hostInstanceId"))

                def connect(self):
                    return self

                def handshake(self, client_info=None, timeout_ms=5000):
                    return {"hostInstanceId": self.host_instance_id,
                            "protocolVersion": "1.0", "writeEnabled": False,
                            "capabilities": {"runtime": "net8", "toolCatalogHash": None,
                                             "buildStamp": "fake-1", "civilAvailable": True,
                                             "relayPort": 45001, "pluginPort": 45002}}

                def call(self, *_a, **_k):
                    return {"ok": True, "result": {}}

                def close(self):
                    self.closed = True

            try:
                # ① 无桥映射 → 走老档管道（老档行为不变）
                instances_module.list_bridge_records = lambda: []
                instances_module.pick = lambda cfg, **kw: {
                    "pipeName": r"\\.\pipe\ep-test", "hostInstanceId": "acad-net48",
                    "runtime": "net48"}
                pipeclient.PipeClient = FakePipe
                net48_client = gateway.ensure_client()
                suite.is_true(isinstance(net48_client, FakePipe),
                              "无桥映射 → 连老档命名管道（net48 分支）")
                suite.eq(FakePipe.built, [r"\\.\pipe\ep-test"], "老档用的是发现文件里的 pipeName")

                # ② 有桥映射 → 走 net8 桥
                gateway._close_client()
                instances_module.list_bridge_records = lambda: [
                    {"hostInstanceId": "acad-net8", "relayPort": 45001, "pluginPort": 45002,
                     "runtime": "net8", "pid": 4321}]
                bridge_channel.BridgeChannel = FakeChannel
                net8_client = gateway.ensure_client()
                suite.is_true(isinstance(net8_client, FakeChannel),
                              "有桥映射 → 走 net8 桥（PipeGuard→relay 分支）")
                suite.eq(FakeChannel.built, ["acad-net8"], "net8 用的是桥映射里的 hostInstanceId")

                # ③ preferredRuntime=net48 时即使有桥映射也回老档
                gateway._close_client()
                config.set_path("instances.preferredRuntime", "net48")
                forced = gateway.ensure_client()
                suite.is_true(isinstance(forced, FakePipe),
                              "preferredRuntime=net48 强制回老档（排查用开关）")
                suite.eq(gateway._pick_target().get("runtime"), "net48",
                         "_pick_target() 尊重 preferredRuntime")
                config.set_path("instances.preferredRuntime", None)
                gateway._close_client()
            finally:
                instances_module.pick = saved_pick
                instances_module.list_bridge_records = saved_records
                pipeclient.PipeClient = saved_pipe
                bridge_channel.BridgeChannel = saved_channel
                gateway._close_client()

        # ---------------- 8. 建链失败回滚：零残留 ----------------
        def bridge_rollback():
            state = {"terminated": 0, "killed": 0, "unregistered": 0, "registered": 0}

            class FakeProc:
                pid = 54321

                def terminate(self):
                    state["terminated"] += 1

                def wait(self, timeout=None):
                    return 0

                def kill(self):
                    state["killed"] += 1

            class FailingGuard:
                """判活不过的 guard：模拟 relay 起来了但看不到插件。"""

                def __init__(self, host_instance_id, runtime="net8", **kwargs):
                    self.host_instance_id = host_instance_id
                    self.runtime = runtime
                    self.token = "t" * 48
                    self.relay_port = 45101
                    self.plugin_port = 45102

                def allocate(self):
                    return self.relay_port, self.plugin_port

                def environments(self):
                    return {}

                def register(self, pid=None, extra=None):
                    state["registered"] += 1
                    return "x.json"

                def unregister(self):
                    state["unregistered"] += 1

                def probe_health(self, timeout=3.0):
                    return {"healthy": False, "state": "unreachable", "detail": "fake_down"}

                def call(self, *_a, **_k):
                    raise AssertionError("判活都没过，不应该走到 call()")

            saved_guard = bridge_channel.bridge_pipeguard.BridgeGuard
            saved_start = bridge_channel.bridge_relay_launcher.start_relay
            try:
                bridge_channel.bridge_pipeguard.BridgeGuard = FailingGuard
                bridge_channel.bridge_relay_launcher.start_relay = (
                    lambda guard, config=None, audit=None: FakeProc())
                channel = bridge_channel.BridgeChannel(
                    {"hostInstanceId": "acad-rollback", "relayPort": 45101}, config, logger=audit)
                raised = None
                try:
                    channel.connect()
                except pipeclient.HostUnreachable as exc:
                    raised = exc
                suite.is_true(raised is not None,
                              "判活不过 → HostUnreachable（不降级、不直连插件端口）")
                suite.eq(state["terminated"], 1, "回滚时子进程被 terminate 一次")
                suite.eq(state["unregistered"], 1, "回滚时映射文件被 unregister 一次")
                suite.eq(state["registered"], 0, "判活不过时不写映射（先判活后登记）")
                suite.is_true(channel._proc is None and channel._guard is None,
                              "回滚后不持有子进程/守卫（无半初始化态）")
                suite.is_true(channel.closed, "回滚后 channel.closed=True")
                suite.is_true(channel.session_token is None, "回滚后不再暴露令牌")

                # detach（atexit 路径）：只删映射、不杀进程
                state["unregistered"] = 0
                other = bridge_channel.BridgeChannel(
                    {"hostInstanceId": "acad-orphan", "relayPort": 45101}, config, logger=audit)
                other.connect = None
                other._proc = FakeProc()
                other._guard = FailingGuard("acad-orphan")
                info = other.detach()
                suite.eq(state["terminated"], 1, "detach() 不杀 relay（terminate 次数不变）")
                suite.eq(info.get("pid"), 54321, "detach() 回报孤儿 pid 供审计")
                suite.eq(state["unregistered"], 1, "detach() 仍删映射文件")

                # detach() **幂等**：第二次必须早退（不再 unregister、不再报孤儿 pid），
                # 否则 atexit 与 run() 的 finally 同时收尾就会重复删两次、审计里出现两条孤儿记录。
                state["unregistered"] = 0
                again = other.detach()
                suite.eq(again.get("pid"), None, "重复 detach() 返回 pid=None（不再报孤儿）")
                suite.eq(again.get("unregistered"), False, "重复 detach() 的 unregistered=False")
                suite.eq(state["unregistered"], 0, "重复 detach() 不再删映射（幂等）")
                suite.eq(state["terminated"], 1, "重复 detach() 仍不杀进程")

                # ---- 端到端：`ensure_client()` 走到 `_connect_bridge()` 判活失败 ----
                #
                # 上面验的是 channel 自己会不会回滚；这条验**网关有没有兜住**：
                # `_pick_target` 选中 net8 → `_connect_bridge` → connect() 抛 HostUnreachable
                # → 收敛成 C1003 且 `_client` 必须仍为 None（不能留一个半死的 client）。
                saved_records = instances_module.list_bridge_records
                try:
                    instances_module.list_bridge_records = lambda: [
                        {"hostInstanceId": "acad-e2e", "relayPort": 45101, "pluginPort": 45102,
                         "runtime": "net8", "pid": 1}]
                    state["terminated"] = 0
                    state["unregistered"] = 0
                    state["registered"] = 0
                    raised_e2e = None
                    try:
                        gateway.ensure_client()
                    except pipeclient.HostUnreachable as exc:
                        raised_e2e = exc
                    suite.is_true(raised_e2e is not None,
                                  "端到端：判活失败 → 网关抛 HostUnreachable（C1003 语义）")
                    suite.is_true(gateway._client is None,
                                  "端到端：失败后网关不持有 client（无半初始化态）")
                    suite.eq(state["registered"], 0, "端到端：判活不过时不写映射")
                    suite.eq(state["terminated"], 1, "端到端：relay 子进程被回收一次")
                    suite.eq(state["unregistered"], 1, "端到端：映射被 unregister 一次")
                finally:
                    instances_module.list_bridge_records = saved_records
                    gateway._close_client()
            finally:
                bridge_channel.bridge_pipeguard.BridgeGuard = saved_guard
                bridge_channel.bridge_relay_launcher.start_relay = saved_start

        # ---------------- 9. net8 本地合成的 sys_* ----------------
        def local_composed_payloads():
            class FakeClient:
                runtime = "net8"
                host_instance_id = "acad-net8"
                _ack = {"hostInstanceId": "acad-net8",
                        "capabilities": {"runtime": "net8", "buildStamp": "fake-1",
                                         "pluginVersion": "1.15.0", "hostProduct": "Civil3D",
                                         "civilAvailable": True, "relayPort": 45001,
                                         "pluginPort": 45002}}

            client = FakeClient()
            ping = gateway._local_payload("sys_ping", client)
            suite.eq(ping.get("pong"), True, "net8 sys_ping 本地回 pong")
            suite.matches(ping.get("serverTimeUtc") or "",
                          r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
                          "sys_ping.serverTimeUtc 是 UTC 毫秒精度（以 Z 结尾）")

            version = gateway._local_payload("sys_version", client)
            suite.eq((version.get("bridge") or {}).get("runtime"), "net8",
                     "sys_version 暴露 bridge.runtime=net8（不谎报 net48）")
            suite.eq((version.get("protocol") or {}).get("toolCatalogHash"),
                     tools_catalog.TOOL_CATALOG_HASH, "sys_version 的目录哈希与本地一致")

            caps = gateway._local_payload("sys_capabilities", client)
            suite.eq(len(caps.get("tools") or []), len(tools_catalog.CANONICAL_TOOLS),
                     "sys_capabilities 列出的工具数与目录一致")
            suite.eq(caps.get("writeEnabled"), False, "sys_capabilities.writeEnabled 恒 false")
            gaps = caps.get("gaps") or []
            suite.eq(len(gaps), 1, "Civil 三态以一条 gap 显式回报")
            suite.eq(gaps[0].get("available"), True, "civilAvailable=true 时 gap 报可用")
            suite.is_false("bridgeToken" in json.dumps(caps, ensure_ascii=False),
                           "sys_capabilities 不含令牌字段")

            unavailable = FakeClient()
            unavailable._ack = {"capabilities": {"civilAvailable": False}}
            gap = gateway._local_payload("sys_capabilities", unavailable)["gaps"][0]
            suite.eq(gap.get("available"), False, "civilAvailable=false 时 gap 报不可用（不静默）")

        # ---------------- 10. 大结果 drilldown 可执行 ----------------
        def drilldown_executable():
            config.set_path("budget.maxResultChars", 400)
            with harness.mock_server() as (server, _host):
                client = harness.connect(server)
                try:
                    response = client.call("read_layers", {"limit": 200})
                    built = gateway._build_tool_response("read_layers", {"limit": 200}, response)
                    suite.is_true(len(built.get("content") or []) >= 2,
                                  "响应含摘要 + 引用两块 content")
                    ref = json.loads(built["content"][1]["text"])
                    suite.is_true(bool(ref.get("drilldown")),
                                  "被截断时 drilldown 非空（给了可执行建议）",
                                  "truncated=%r drilldown=%r"
                                  % (ref.get("truncated"), ref.get("drilldown")))
                    first = (ref.get("drilldown") or [{}])[0]
                    tool_name = first.get("tool")
                    params = first.get("params") or {}
                    suite.is_true(tools_catalog.is_canonical(tool_name),
                                  "drilldown 指向的工具在 canonical 之内：%r" % tool_name)
                    follow = client.call(tool_name, params)
                    suite.is_true(bool(follow.get("ok")),
                                  "照第一条 drilldown 真调一次能成功（%s %r）"
                                  % (tool_name, params))
                finally:
                    client.close()
            config.set_path("budget.maxResultChars", 6000)

        # ---------------- 11. 新增 Civil 工具在 mock 上非空 ----------------
        def civil_tools_on_mock():
            with harness.mock_server() as (server, _host):
                client = harness.connect(server)
                try:
                    listed = client.call("read_civil_list", {"type": "any", "limit": 10})
                    payload = listed.get("result") or {}
                    suite.is_true(bool(payload.get("items")),
                                  "read_civil_list 在 mock 上非空（否则契约测试等于没验）",
                                  "items=%r" % (payload.get("items"),))
                    suite.eq(payload.get("counts", {}).get("total"), 3,
                             "mock 的最小 Civil 夹具是 3 件")
                    handle = (payload.get("items") or [{}])[0].get("handle")
                    suite.matches(handle or "", r"^[0-9A-F]{1,16}$",
                                  "枚举出来的句柄是大写十六进制")

                    described = client.call("read_civil_object", {"handle": handle})
                    obj = (described.get("result") or {}).get("object") or {}
                    suite.eq(obj.get("handle"), handle, "read_civil_object 回同一个句柄")
                    suite.is_true(isinstance(obj.get("confidence"), (int, float)),
                                  "read_civil_object 必带 confidence")
                    suite.is_true(isinstance(obj.get("basis"), dict) and obj["basis"].get("kind"),
                                  "read_civil_object 必带 basis.kind")

                    filtered = client.call("read_civil_list", {"type": "surface"})
                    suite.eq((filtered.get("result") or {}).get("counts", {}).get("total"), 1,
                             "按 type=surface 过滤后只剩 1 件")

                    paged = client.call("read_civil_list", {"type": "any", "limit": 1})
                    paged_payload = paged.get("result") or {}
                    suite.eq(paged_payload.get("truncated"), True, "limit=1 时 truncated=true")
                    suite.is_true(bool(paged_payload.get("nextCursor")),
                                  "truncated 时给 nextCursor（可继续翻页）")
                    second = client.call("read_civil_list",
                                         {"type": "any", "limit": 1,
                                          "cursor": paged_payload.get("nextCursor")})
                    second_handle = ((second.get("result") or {}).get("items") or [{}])[0].get("handle")
                    suite.is_true(second_handle != handle,
                                  "用 cursor 取第二页拿到不同对象（翻页真的生效）")
                finally:
                    client.close()

        # ---------------- 12. read_civil_list.type：三端一致（QA 的 N1/N2/N3） ----------------
        def civil_type_three_way():
            """`type` 的语义必须三端一致：**不传 = any**、**只认 6 个值**、**越界/空串必拒**。

            三个曾经的缺陷（QA 挖出、已修）：
              * N3：C# `CivilRecordsOf` 没有 `case "any"`，`default` 聚合五类 —— 合法值与非良值
                共用一支，任何校验顺序变化都会让 `type=corridor` 拿到全部五类（**错答**，比空集危险）；
              * N2：mockhost 只有 `params.get("type") or "any"`，非法值静默落到空集 ——
                把「未开放」伪装成「不存在」；
              * N1：描述承诺了 enum 里没有的廊道/采样线组，却漏了 enum 里有的 pointGroup。

            **`type` 不是必填**：不传等价 `any`（schema 的 default 语义）。但「传了空串」与「没传」
            不同，必须拒 —— 这条边界是本用例钉的重点之一。
            """
            allowed = list(EXPECTED_CIVIL_TYPES)

            # ① schema：enum 集合 + 非必填 + default=any
            schema = tools_catalog.schema("read_civil_list")
            type_node = (schema.get("properties") or {}).get("type") or {}
            suite.eq(sorted(type_node.get("enum") or []), sorted(allowed),
                     "schema 的 type enum 集合 = 已批准的 5 类 + any")
            suite.is_false("corridor" in (type_node.get("enum") or []),
                           "schema 的 enum 不含未开放的 corridor")
            suite.eq(schema.get("required") or [], [],
                     "schema 不把 type 标为必填（不传 = any 是合理 API）")
            suite.eq(type_node.get("default"), "any",
                     "schema 给 type 声明 default=any（不传的语义唯一）")

            # ② 描述分两层：工具级只点「已支持」；未开放的类型写在 type 字段说明里
            description = tools_catalog.description("read_civil_list")
            zh_supported = {"路线": "alignment", "纵断面": "profile", "曲面": "surface",
                            "管网": "pipeNetwork", "点编组": "pointGroup"}
            for word, code in zh_supported.items():
                suite.is_true(code in description and word in description,
                              "工具级描述同时给出 %s 与中文「%s」" % (code, word))
            for banned in ("廊道", "采样线组"):
                suite.is_false(banned in description,
                               "工具级描述不列未开放的「%s」（那是假承诺）" % banned)
            suite.is_true("E1001" in description,
                          "工具级描述写明未支持的类型回 E1001（不是空集）")

            type_desc = type_node.get("description") or ""
            suite.is_true("尚未开放" in type_desc,
                          "type 字段说明用「尚未开放」显式标注其它类型")
            for banned in ("廊道", "采样线组"):
                suite.is_true(banned in type_desc,
                              "type 字段说明点明「%s」未开放（比默默不提供更清楚）" % banned)
            suite.is_true("read_overview" in type_desc,
                          "type 字段说明指路 read_overview 的 civil3d 段（不用空结果推断）")
            suite.is_true("E1001" in type_desc,
                          "type 字段说明写明越界回 E1001")
            for code in EXPECTED_CIVIL_TYPES:
                suite.is_true(code in type_desc, "type 字段说明列出 enum 取值 %s" % code)

            # ③ mockhost：enum 规格；type 不在必填表里；空串与越界都拒
            import mock_host
            spec = mock_host.PARAM_SPECS["read_civil_list"]["type"]
            suite.eq(spec[0], "enum", "mockhost 的 type 规格是 enum（不是裸 str）")
            suite.eq(sorted(spec[1]), sorted(allowed), "mockhost 的 enum 取值 = 已批准的允许集")
            suite.is_false("type" in (mock_host.REQUIRED_PARAMS.get("read_civil_list") or ()),
                           "mockhost 没把 type 放进必填表（与 schema 一致）")
            for bad in ("corridor", "sampleLineGroup", "bogus", ""):
                issues = mock_host.validate_params("read_civil_list", {"type": bad})
                suite.is_true(bool(issues) and issues[0][1].startswith("not in enum:"),
                              "mockhost 拒绝 type=%r 且 detail 与 C# 同措辞" % (bad,))
            suite.eq(mock_host.validate_params("read_civil_list", {}), [],
                     "mockhost 允许不传 type（等价 any）")

            # ④ C# 源码：case 标签集合 == 允许集；default 只抛不聚合（位置不写死）
            #
            # 这组断言刻意**不**把「某个字符串必须出现在哪个函数里」写死 —— 上一版就吃过亏：
            # 断言把 `invalid_civil_type` 钉在 default 分支内，实现一重构（抛异常上移到调用方）就红，
            # 而代码其实是对的。现在改成「结构性 + 行为」双轨：结构断言只管
            # 「default 不许聚合、必须抛」，具体消息文本全文件出现即可。
            with open(os.path.join(ROOT, "host", "Tools", "ReadToolHandlers.cs"),
                      "r", encoding="utf-8") as handle:
                handlers = handle.read()
            body = handlers.split("private static List<CivilObjectRecord> CivilRecordsOf", 1)
            suite.eq(len(body), 2, "C# 里能定位到 CivilRecordsOf 实现（否则下面的断言无意义）")
            records_of = body[1].split("private static CivilObjectRecord FindCivilRecord", 1)[0]
            cases = re.findall(r'case\s+"([A-Za-z]+)"\s*:', records_of)
            suite.eq(sorted(cases), sorted(allowed),
                     "C# CivilRecordsOf 的 case 标签 = 允许集（any 必须显式成一支）")
            default_block = records_of.split("default:", 1)[-1]
            suite.is_false("AddRange(snapshot." in default_block,
                           "C# default 分支不聚合任何一类（否则 corridor 会拿到全部五类）")
            suite.is_true("throw" in default_block,
                          "C# default 分支必须抛错（不能静默返回空列表）")
            suite.is_true("allowed:" in default_block,
                          "C# default 分支的 detail 带允许集（模型不用试错）")
            suite.is_true("not in enum:" in handlers,
                          "C# 用 not in enum 措辞（与 ParamValidator.GetEnumString 一致）")

            # ⑤ C# 目录校验器：enum 校验 + 单一允许集；不再有 required
            catalog = open(os.path.join(ROOT, "host", "Protocol", "ToolCatalog.cs"),
                           "r", encoding="utf-8").read()
            suite.is_true('v.GetEnumString("type", CivilTypes)' in catalog,
                          "C# 参数校验器用 GetEnumString(type, CivilTypes)")
            suite.is_false('AddIssue("params.type", "required")' in catalog,
                           "C# 不再把 type 当必填（已收回该要求）")
            suite.is_true("private static readonly string[] CivilTypes" in catalog,
                          "C# 有一份 CivilTypes 允许集（校验器与 handler 同源）")

            # ⑥ 行为层：mock 上真调
            with harness.mock_server() as (server, _host):
                client = harness.connect(server)
                try:
                    for bad in ("corridor", "sampleLineGroup", "bogus", ""):
                        response = client.call("read_civil_list", {"type": bad})
                        suite.is_false(bool(response.get("ok")),
                                       "mock 上 type=%r 必须失败（不静默空集、不聚合）" % (bad,))
                        suite.eq((response.get("error") or {}).get("code"), "E1001",
                                 "mock 上 type=%r → E1001" % (bad,))
                        suite.eq((response.get("error") or {}).get("field"), "params.type",
                                 "mock 上 type=%r 的 field 指向 params.type" % (bad,))

                    omitted = client.call("read_civil_list", {})
                    suite.eq((omitted.get("result") or {}).get("counts", {}).get("total"), 3,
                             "不传 type = any，返回全部 3 件夹具")

                    everything = client.call("read_civil_list", {"type": "any"})
                    suite.eq((everything.get("result") or {}).get("counts", {}).get("total"), 3,
                             "type=any 聚合五类，返回全部 3 件夹具")
                    surface = client.call("read_civil_list", {"type": "surface"})
                    suite.eq((surface.get("result") or {}).get("counts", {}).get("total"), 1,
                             "type=surface 只返回 1 件")
                    point_group = client.call("read_civil_list", {"type": "pointGroup"})
                    suite.eq((point_group.get("result") or {}).get("counts", {}).get("total"), 0,
                             "type=pointGroup 合法但本图没有 → 空集（不是 E1001）")
                finally:
                    client.close()

        # ---------------- 13. read_describe 的属性形状（D-01 防回潮） ----------------
        def describe_attribute_shape():
            """钉住「块属性真的被读到、且以宿主形状出现」——**正向 + 反向都必须有**。

            背景（D-01）：net48 宿主的属性读取从 M0 起一直是**静默失败**（`ObjectId.GetObject`
            脱离事务抛异常被 `catch { continue; }` 吞掉），而 740/0 全绿完全看不出来 ——
            因为**没有任何一条断言检查过 `read_describe.props` 应该长什么样**。

            远端（C#）由静态审查的「禁止脱离事务的 GetObject」守卫；这里守的是**输出形状**：
            正向证明「有属性的块 → props 里有 attr.<tag> 且值正确、texts 非空」，
            反向证明「无属性的块 → 一个 attr. 键都不许有」。
            **只有正向**的话，一个「一律返回假属性」的实现也能过。
            """
            with harness.mock_server() as (server, _host):
                client = harness.connect(server)
                try:
                    # 正向：101C 是带属性的检查井（夹具固定 ELEV=352.13）
                    described = client.call("read_describe", {"handle": "101C"})
                    obj = (described.get("result") or {}).get("object") or {}
                    props = obj.get("props") or {}
                    suite.eq(props.get("attr.ELEV"), "352.13",
                             "有属性块：props[attr.ELEV] == 夹具值 352.13")
                    suite.is_true(isinstance(props.get("attr.编号"), str),
                                  "有属性块：props 里带 attr.编号（键名与宿主同形）")
                    attr_keys = [key for key in props if key.startswith("attr.")]
                    suite.is_true(len(attr_keys) >= 3,
                                  "有属性块：attr. 前缀键 ≥3 个（编号/井底标高/ELEV）",
                                  "keys=%r" % (sorted(attr_keys),))
                    texts = obj.get("texts") or []
                    suite.is_true(bool(texts), "有属性块：texts 非空（属性值并入 texts）",
                                  "texts=%r" % (texts,))
                    suite.is_true("352.13" in [str(item) for item in texts],
                                  "有属性块：texts 里能看到 ELEV 的值")

                    # 反向：1029 是「指北针」，没有任何属性
                    plain = client.call("read_describe", {"handle": "1029"})
                    plain_obj = (plain.get("result") or {}).get("object") or {}
                    plain_props = plain_obj.get("props") or {}
                    leaked = sorted(key for key in plain_props if key.startswith("attr."))
                    suite.eq(leaked, [],
                             "无属性块：不许出现任何 attr. 键（防「一律返回假属性」过测试）")
                    suite.eq(plain_obj.get("texts") or [], [],
                             "无属性块：texts 为空（属性值不该凭空出现）")

                    # 形状自证：前缀是 `attr.` 而不是裸 tag —— 裸 tag 的实现在这条上会红
                    bare = sorted(key for key in props
                                  if not key.startswith("attr.") and key in ("编号", "井底标高", "ELEV"))
                    suite.eq(bare, [], "有属性块：props 里不出现裸 tag（必须带 attr. 前缀）")
                finally:
                    client.close()

        for label, func in (
            ("工具目录三处一致", canonical_contract),
            ("新工具入参契约", new_tool_schemas),
            ("写域一律 E2003", write_domain_always_e2003),
            ("非空审批令牌 E1006", approval_token_always_e1006),
            ("上游错误映射", upstream_error_mapping),
            ("后置校验与归一化", post_validation_rules),
            ("runtime 路由", transport_routing),
            ("建链失败回滚", bridge_rollback),
            ("net8 本地合成 sys_*", local_composed_payloads),
            ("drilldown 可执行", drilldown_executable),
            ("mock 上的 Civil 工具", civil_tools_on_mock),
            ("Civil type 三端一致", civil_type_three_way),
            ("describe 属性形状（正/反向）", describe_attribute_shape),
        ):
            suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
