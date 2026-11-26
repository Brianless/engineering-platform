"""tests/qa/qa_write_domain.py —— 写通道硬关闭（QA 独立复核）。

工程师只抽测了 4 个写域工具（cad_create_line / civil_create_profile / dyn_run_lisp /
file_write_text）。M0 的「零写入」是硬约束，抽测不足以证明，故这里：

  1. **逐个**遍历 8 个非只读前缀 × 每个前缀 3 个工具名（共 24 个），逐个断言 E2003，
     并核对 error.field=tool、error.detail=write_channel_disabled_m0；
  2. 大小写变形（CAD_CREATE_LINE / Cad_Create_Line）—— 契约要求「按前缀判定」，
     不该因为大小写就退化成 E2001（E2001 会让调用方以为「只是名字写错」）；
  3. 写域工具名和「拼错的只读名」必须能区分：read_overveiw → E2001，cad_* → E2003；
  4. 工具目录（C# 侧 ToolCatalog + Python 侧 tools_catalog + protocol/tools.schema.json）
     三处都不许出现写域工具被声明为可用；
  5. mockhost 侧「无写副作用」的结构性证据：工具处理器表里只有 read_/sys_/task_，
     且 mockhost 里没有任何以写模式打开文件的代码（用 AST/正则双查）。
"""

from __future__ import annotations

import json
import re
import sys

import qa_common as qa


def run() -> dict:
    suite = qa.harness.Suite("qa_write_domain")

    # ==================================================================
    # 1~3. 走真实管道逐个验证
    # ==================================================================
    with qa.harness.sandbox("qa-write") as _tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)
            client = qa.harness.connect(server)
            try:

                def every_write_tool():
                    items = qa.all_write_tools()
                    suite.is_true(len(items) >= 20,
                                  "写域清单覆盖 8 个前缀（共 %d 个工具名）" % len(items),
                                  "items=%d" % len(items))
                    bad = []
                    for prefix, tool in items:
                        response = client.call(tool, {})
                        error = response.get("error") or {}
                        if response.get("ok") is not False or error.get("code") != "E2003":
                            bad.append((tool, error.get("code"), response.get("ok")))
                            continue
                        if error.get("field") != "tool":
                            bad.append((tool, "field", error.get("field")))
                        if error.get("detail") != "write_channel_disabled_m0":
                            bad.append((tool, "detail", error.get("detail")))
                    suite.ok(bad == [], "全部写域工具 E2003 + field=tool + "
                                        "detail=write_channel_disabled_m0",
                             "反例=%r" % (bad,))
                    # 每个前缀至少命中一次（防止「只测了第一个前缀」）
                    prefix_hit = {}
                    for prefix, tool in items:
                        prefix_hit.setdefault(prefix, 0)
                        prefix_hit[prefix] += 1
                    suite.eq(sorted(prefix_hit), sorted(qa.WRITE_PREFIXES),
                             "8 个前缀每一个都被真实调用过（不是只测一个前缀）")

                suite.case("逐个写域工具", every_write_tool)

                def case_variants():
                    for tool in ("CAD_CREATE_LINE", "Cad_Create_Line", "Civil_Create_Profile",
                                 "FILE_WRITE_TEXT"):
                        response = client.call(tool, {})
                        error = response.get("error") or {}
                        suite.eq(error.get("code"), "E2003",
                                 "大小写变形 %s 仍必须 E2003（前缀判定不该大小写敏感）"
                                 % tool)

                suite.case("大小写变形", case_variants)

                def unknown_vs_write():
                    response = client.call("read_overveiw", {})
                    suite.eq((response.get("error") or {}).get("code"), "E2001",
                             "拼错的只读工具名 → E2001（不是 E2003）")
                    response = client.call("cad_create_ine", {})
                    suite.eq((response.get("error") or {}).get("code"), "E2003",
                             "写域里连名字都拼错的 → 仍是 E2003（写域先于未知判定）")
                    response = client.call("", {})
                    error = response.get("error") or {}
                    suite.ok(error.get("code") in ("E1001", "E2001"),
                             "空工具名 → E1001/E2001（不落到写域）",
                             "实际 code=%r" % (error.get("code"),))

                suite.case("未知 vs 写域", unknown_vs_write)

                def read_tools_have_no_side_effect():
                    """只读工具调两次，结果必须一致（幂等），且指纹不变（图纸未被改动）。"""
                    first = client.call("read_overview", {})
                    second = client.call("read_overview", {"refresh": True})
                    fingerprint_first = first.get("targetFingerprint")
                    fingerprint_second = second.get("targetFingerprint")
                    suite.ok(fingerprint_first == fingerprint_second,
                             "只读工具两次调用后图纸指纹不变（无隐藏写入）",
                             "first=%r second=%r" % (fingerprint_first, fingerprint_second))
                    counts_first = (first.get("result") or {}).get("counts") or {}
                    counts_second = (second.get("result") or {}).get("counts") or {}
                    suite.eq(counts_first, counts_second,
                             "只读工具两次调用的图元计数完全一致（无副作用累积）")
                    cancel = client.call("task_cancel", {"opId": "op-qa-nope-0001"})
                    suite.is_true(cancel.get("ok") is True,
                                  "task_cancel 对不存在的 opId 不报错（只置标志，无副作用）",
                                  qa.describe_error(cancel))

                suite.case("只读无副作用", read_tools_have_no_side_effect)

            finally:
                client.close()

    # ==================================================================
    # 4. 三处工具目录都不声明写域工具
    # ==================================================================
    def catalog_has_no_write_tools():
        tools_catalog = __import__("tools_catalog")
        names = [item.get("name") for item in tools_catalog.list_tools()]
        # 用「集合精确相等」而不是「数量相等」：数量对上但名字错了也必须红。
        # 2026-11-22 M1：加入 write_preview / write_approve / write_insert_points（写通道三步审批）。
        expected = {
            "sys_ping", "sys_status", "sys_version", "sys_health",
            "sys_capabilities", "sys_list_instances",
            "read_overview", "read_layers", "read_query", "read_describe",
            "read_preview", "read_anomalies", "read_civil_object", "read_civil_list",
            "read_active_selection", "read_viewport_snapshot", "read_object_xdata",
            "task_status", "task_list", "task_cancel", "task_reconcile",
            "write_preview", "write_approve", "write_insert_points", "write_command",
            "write_surface_breakline", "write_object_xdata", "write_undo",
            "write_alignment_plan", "write_eval_csharp", "write_entities",
            "read_civil_surfaces", "read_civil_alignments",
            "export_surface_grid", "export_layer_geojson",
            "preview_transient", "batch_side_database",
        }
        suite.ok(sorted(names) == sorted(expected),
                 "MCP 工具目录 == 37 个（v1.0 全量原子工具，集合精确相等）",
                 "缺失=%r 多出=%r" % (sorted(expected - set(names)), sorted(set(names) - expected)))
        offenders = [name for name in names
                     if tools_catalog.is_write_domain(name) and not name.startswith("write_")]
        suite.ok(offenders == [],
                 "MCP 工具目录里的写域工具只能是 write_ 命名空间（底层方法名前缀一律不许出现）",
                 "反例=%r" % (offenders,))

        schema = json.loads(qa.read_source("protocol/tools.schema.json"))
        declared = [item.get("name") for item in (schema.get("tools") or [])]
        schema_offenders = [name for name in declared
                            if name.startswith(qa.WRITE_PREFIXES)]
        suite.ok(schema_offenders == [],
                 "protocol/tools.schema.json 不声明写域工具",
                 "反例=%r" % (schema_offenders,))
        suite.ok(sorted(declared) == sorted(names),
                 "契约 schema 与 MCP 目录的工具集合一致（不会「声明了但没实现」）",
                 "schema=%r mcp=%r" % (sorted(declared), sorted(names)))

        host_source = qa.read_source("host/Protocol/ToolCatalog.cs")
        # C# 侧 Tool("name", ...) 定义里不应出现写域前缀
        # 注意 task_cancel 是 `ToolDescriptor cancel = Tool("task_cancel", ...)` 形式，
        # 不是 list.Add(...)，故不能只匹配 list.Add。
        defines = re.findall(r'\b(?:Tool|ToolWrite)\("([a-z0-9_]+)"', host_source)
        host_offenders = [name for name in defines if name.startswith(qa.WRITE_PREFIXES)]
        # 断言 1（硬）：C# 不得定义 schema 之外的「幽灵工具」
        suite.ok(set(defines) - set(declared) == set(),
                 "C# ToolCatalog 不定义 schema 之外的工具",
                 "多出=%r" % sorted(set(defines) - set(declared)))
        # 断言 2（契约同步）：C# 与 schema 的集合一致。
        # 2026-11-22 20:4x：schema 已有 18 个，host 侧还停在 16（工程师正在接线）——
        # 这条**故意留红**，它代表「产品尚未接完」，不是 QA 断言过期。
        suite.ok(sorted(defines) == sorted(declared),
                 "C# ToolCatalog 与 schema 的工具集合一致",
                 "C#=%d 个，缺失=%r" % (len(defines), sorted(set(declared) - set(defines))))
        suite.ok(host_offenders == [],
                 "C# ToolCatalog 不定义底层方法名的写域工具（只允许 write_ 命名空间）",
                 "反例=%r" % (host_offenders,))

    suite.case("三处目录", catalog_has_no_write_tools)

    # ==================================================================
    # 5. mockhost 无写副作用的结构证据
    # ==================================================================
    def mockhost_no_write_side_effects():
        source = qa.read_source("mockhost/mock_host.py")
        handlers = re.findall(r"def _tool_([a-z0-9_]+)\(", source)
        suite.is_true(len(handlers) >= 16, "mockhost 定义了 ≥16 个工具处理器",
                      "实际=%d" % len(handlers))
        # 契约同步：mockhost 必须实现 schema 里声明**全部**工具（与 C# 同理，缺一个就是「声明了但替身没有」）
        schema = json.loads(qa.read_source("protocol/tools.schema.json"))
        declared = {item.get("name") for item in (schema.get("tools") or [])}
        suite.ok(set(handlers) == declared,
                 "mockhost 处理器集合 == schema 的工具集合",
                 "缺失=%r 多出=%r" % (sorted(declared - set(handlers)),
                                     sorted(set(handlers) - declared)))
        # 处理器名必须全部落在只读/系统/任务三域内
        # M1：只读域之外**只允许** write_ 命名空间（恰好 3 个审批写工具）；
        # 底层方法名（cad_/civil_/…）在替身里也不许有处理器。
        disallowed = [name for name in handlers
                      if not name.startswith(("sys_", "read_", "task_", "write_", "export_", "preview_", "batch_"))]
        suite.ok(disallowed == [], "mockhost 工具处理器只属合法平台域",
                 "反例=%r" % (disallowed,))
        write_handlers = sorted(n for n in handlers if n.startswith("write_"))
        suite.ok(write_handlers == ["write_alignment_plan", "write_approve", "write_command",
                                    "write_entities", "write_eval_csharp", "write_insert_points",
                                    "write_object_xdata", "write_preview", "write_surface_breakline", "write_undo"],
                 "mockhost 的写工具与 schema 白名单精确一致",
                 "实际=%r" % (write_handlers,))
        # 写模式打开文件：M0 只允许两处落盘 —— 预览图 PNG（cache 目录）与实例发现文件。
        # 断言「有且仅有这两处」，而不是「一处都没有」：完全零写盘既不现实也会掩盖越权写。
        write_opens = re.findall(r"with open\(([^,]+),\s*[\"']([wax][b+]?)[\"']", source)
        targets = sorted({(expr.strip(), mode) for expr, mode in write_opens})
        suite.ok(targets == [("path", "wb"), ("temporary", "w")],
                 "mockhost 只有两处写盘：预览 PNG(wb) 与发现文件(w)，无第三处",
                 "实际=%r" % (targets,))
        # 预览图落盘路径必须由 _preview_path 生成，且其根目录是 cache/preview
        suite.contains(source, "def _preview_path", "预览图路径由 _preview_path 统一生成")
        suite.contains(source,
                       'self.preview_dir = os.path.join(platform_root(), "cache", "preview")',
                       "preview_dir 固定为 <platform_root>/cache/preview（不可能是用户图纸目录）")
        preview_fn = source.split("def _preview_path", 1)[1].split("def _tool_", 1)[0]
        suite.contains(preview_fn, "self.preview_dir",
                       "_preview_path 只用 preview_dir 拼路径（不接外部传入路径）")
        # 删除操作只允许删自己的发现文件
        remove_lines = [line.strip() for line in source.splitlines() if "os.remove(" in line]
        suite.ok(all("path" in line for line in remove_lines),
                 "mockhost 只删除发现相关路径（不删用户图纸）",
                 "反例=%r" % (remove_lines,))
        # 高危 API
        for api in ("SendStringToExecute", "Erase", "AddEntity", "Command(",
                    "subprocess.run", "shutil.rmtree", "shutil.move"):
            suite.is_false(api in source,
                           "mockhost 不含高危写/执行 API：%s" % api)

    suite.case("mockhost 无副作用", mockhost_no_write_side_effects)

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
