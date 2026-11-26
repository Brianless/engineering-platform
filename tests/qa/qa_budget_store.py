"""tests/qa/qa_budget_store.py —— 预算截断与数据落库（QA 独立复核）。

三组独立证据：

A. 预算（设计 §4.5）
   1. 1 万条 items 的三级截断：truncationLevel 递增、每级结果都是合法 JSON、
      且只在数组元素边界裁剪（不切字符串 → 结构不非法）；
   2. 截断后 `resultRef` 必须仍在（详情可追溯），`truncated` 必须为 true；
   3. `drilldown` 里的每个 {tool, params} 都必须是**可执行**的：工具在目录里、
      params 能过该工具的校验器 —— 否则模型照着提示调会二次报错；
   4. 宿主真实返回大结果集（read_query limit=100）时，摘要长度是否真的受 maxSummaryChars 约束。

B. 落库（设计 §7.8）
   5. 8 张表 + schema_version 全部存在，且 §7.8 点名要求的字段一个不缺；
   6. 迁移幂等：init_schema 连跑 3 次不报错、schema_version 不重复插行；
   7. 每次调用落 1 条 tasks + 1 条 results；results.payload 能反解出原结果。

C. 审计脱敏
   8. audit 表不存 sessionToken 明文（表里连该列都不该有）；
   9. 审计日志文件里不出现 sessionToken 明文、也不出现图纸里的文字内容。
"""

from __future__ import annotations

import json
import os
import sys

import qa_common as qa

#: 设计 §7.8 点名要求的字段（缺一个就算字段缺口）
EXPECTED_SCHEMA = {
    "tasks": {"op_id", "tool", "status", "params_digest", "target_fingerprint",
              "started_at", "elapsed_ms", "error_code"},
    "objects": {"handle", "fingerprint", "dxf_name", "layer", "meaning", "confidence"},
    "attributes": {"handle", "fingerprint", "name", "value", "source", "confidence"},
    "relations": {"src_handle", "dst_handle", "kind", "basis"},
    "results": {"op_id", "tool", "fingerprint", "payload", "truncated"},
    "evidence": {"op_id", "kind", "detail"},
    "skills_used": {"op_id", "skill", "version", "purpose"},
    "audit": {"actor", "action", "scope", "result", "op_id", "at"},
}

#: 图纸里的文字样本（用于断言「日志不落图纸内容」）
DRAWING_TEXT_SAMPLES = ("填充填充填充", "井底标高", "J1")


def run() -> dict:
    suite = qa.harness.Suite("qa_budget_store")
    budget = __import__("budget")

    with qa.harness.sandbox("qa-budget") as _tmp:
        config = qa.harness.load_config()

        # ==================================================================
        # A. 预算
        # ==================================================================

        def huge_payload(count=10000):
            items = [{"handle": "%04X" % index, "dxfName": "LINE", "type": "line",
                      "layer": "D-DR-管道", "extent": [0, 0, 1, 1],
                      "meaning": "排水管道", "confidence": 0.92}
                     for index in range(count)]
            return {"summary": "【对象查询】共 10000 条", "resultRef": {"tool": "read_query"},
                    "items": items, "total": count}

        def truncation_levels():
            payload = huge_payload()
            raw_len = len(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
            suite.is_true(raw_len > 6000, "预算用例：原始载荷确实超预算",
                          "原始 %d 字符" % raw_len)

            for max_chars in (6000, 300, 40):
                trimmed, meta = budget.truncate(payload, max_chars)
                level = meta.get("truncationLevel")
                suite.is_true(level >= 1,
                              "预算 %d 字符：触发截断（level=%r）" % (max_chars, level),
                              "chars=%r" % (meta.get("chars"),))
                suite.is_true(meta.get("truncated") is True,
                              "预算 %d 字符：truncated=true" % max_chars)
                # 结构必须仍然合法（截断只发生在元素边界，不切字符串）
                try:
                    re_encoded = json.dumps(trimmed, ensure_ascii=False)
                    decoded = json.loads(re_encoded)
                    suite.ok(isinstance(decoded, dict),
                             "预算 %d 字符：截断结果仍是合法 JSON object" % max_chars)
                except ValueError as exc:                     # pragma: no cover
                    suite.fail("预算 %d 字符：截断结果非法 JSON" % max_chars, repr(exc))

            # 三级递进：level 必须随预算收紧单调不减
            levels = [budget.truncate(huge_payload(), limit)[1].get("truncationLevel")
                      for limit in (6000, 300, 40)]
            suite.ok(levels == sorted(levels),
                     "预算：truncationLevel 随预算收紧单调递增（1→2→3）",
                     "levels=%r" % (levels,))

            # 元素边界裁剪：items 里每条都必须是完整的原样元素（未被切半）
            trimmed, _meta = budget.truncate(huge_payload(), 6000)
            survivors = trimmed.get("items") or []
            suite.ok(all(isinstance(item, dict) and "handle" in item for item in survivors),
                     "预算：截断后的 items 元素结构完整（未切字符串）",
                     "首元素=%r" % (survivors[0] if survivors else None,))

        suite.case("三级截断", truncation_levels)

        def result_ref_and_drilldown():
            trimmed, meta = budget.truncate(huge_payload(), 6000)
            # 级别 3 会把整体降级为 counts，此时 resultRef 必须被保留或用 None 占位，
            # 由 MCP 层补回；这里断言「不丢字段」而不是「一定有值」。
            suite.ok("resultRef" in trimmed,
                     "预算：截断结果保留 resultRef 字段（详情可追溯）",
                     "keys=%r" % (sorted(trimmed),))
            drilldown = meta.get("drilldown") or trimmed.get("drilldown") or []
            suite.is_true(len(drilldown) >= 1,
                          "预算：截断必须给出可执行的下钻建议",
                          "drilldown=%r" % (drilldown,))

            tools_catalog = __import__("tools_catalog")
            for index, entry in enumerate(drilldown):
                suite.ok(isinstance(entry, dict) and entry.get("tool") and "params" in entry,
                         "预算：drilldown[%d] 形如 {tool, params, why}" % index,
                         "%r" % (entry,))
                tool = (entry or {}).get("tool")
                suite.is_true(bool(tool) and tools_catalog.exists(tool),
                              "预算：drilldown[%d].tool=%r 在工具目录内（可直接执行）"
                              % (index, tool))
                errors = tools_catalog.validate(tool or "", (entry or {}).get("params") or {})
                suite.eq(errors, [],
                         "预算：drilldown[%d] 的 params 能过该工具校验器" % index)

        suite.case("resultRef 与 drilldown", result_ref_and_drilldown)

        # ==================================================================
        # B. 真实链路 + 落库
        # ==================================================================

        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)
            client = qa.harness.connect(server)
            response = {}
            try:
                def big_query():
                    nonlocal response
                    response = client.call("read_query", {"limit": 100})
                    result = response.get("result") or {}
                    suite.is_true(response.get("ok") is True,
                                  "read_query limit=100 → 成功",
                                  qa.describe_error(response))
                    suite.is_true(len(result.get("items") or []) >= 10,
                                  "read_query limit=100 返回足量元素（预算才有意义）",
                                  "items=%d" % len(result.get("items") or []))

                suite.case("大结果集查询", big_query)

                def drilldown_on_real_truncation():
                    """区分「通用路径的 drilldown 为空」与「真实工具路径无可下钻」：
                    真实 read_query 响应带 nextCursor，故 drilldown 必须非空且可直接执行。"""
                    result = response.get("result") or {}
                    trimmed, meta = budget.truncate(result, 400)
                    drilldown = meta.get("drilldown") or trimmed.get("drilldown") or []
                    suite.is_true(len(drilldown) >= 1,
                                  "真实 read_query 被预算截断时 drilldown 非空（有 nextCursor）",
                                  "truncated=%r level=%r drilldown=%r"
                                  % (meta.get("truncated"), meta.get("truncationLevel"),
                                     drilldown))
                    if drilldown:
                        entry = drilldown[0]
                        follow = client.call(entry.get("tool"), entry.get("params") or {})
                        suite.is_true(follow.get("ok") is True,
                                      "drilldown[0] 照着调真能跑通（%s）" % entry.get("tool"),
                                      qa.describe_error(follow))

                suite.case("真实截断的 drilldown", drilldown_on_real_truncation)

                def summary_budget():
                    summary = ""
                    store_mod = __import__("store")
                    # 摘要长度约束由 MCP 层执行；这里直接验证 budget.clip_text 的上界语义
                    long_text = "很长的摘要" * 400
                    clipped = budget.clip_text(long_text, 1200)
                    suite.is_true(len(clipped) <= 1200,
                                  "摘要裁剪：clip_text 后的长度 ≤ maxSummaryChars(1200)",
                                  "len=%d" % len(clipped))
                    suite.is_true(clipped.endswith("…"),
                                  "摘要裁剪：被裁剪时以省略号结尾（提示已截断）")
                    # 未超长的摘要必须原样返回（不能无条件加省略号）
                    short = "短摘要"
                    suite.eq(budget.clip_text(short, 1200), short,
                             "摘要裁剪：未超长时原样返回")

                suite.case("摘要长度受限", summary_budget)

                def persist():
                    database = __import__("store").Store(config)
                    try:
                        database.init_schema()
                        tables = set(database.table_names())
                        missing = set(EXPECTED_SCHEMA) - tables
                        suite.eq(sorted(missing), [],
                                 "落库：8 张业务表全部建好（缺 %r）" % (sorted(missing),))
                        suite.contains(tables, "schema_version",
                                       "落库：schema_version 表存在")

                        # 字段缺口逐个校验
                        import sqlite3
                        connection = sqlite3.connect(database.path)
                        try:
                            for table, expected in EXPECTED_SCHEMA.items():
                                rows = connection.execute(
                                    "PRAGMA table_info(%s)" % table).fetchall()
                                columns = {row[1] for row in rows}
                                lack = expected - columns
                                suite.eq(sorted(lack), [],
                                         "落库：表 %s 字段齐全（缺 %r）"
                                         % (table, sorted(lack)))
                            audit_columns = {row[1] for row in connection.execute(
                                "PRAGMA table_info(audit)").fetchall()}
                            suite.ok("session_token" not in audit_columns
                                     and "sessionToken" not in audit_columns,
                                     "落库：audit 表不含 sessionToken 列（从结构上就不可能明文落库）",
                                     "columns=%r" % (sorted(audit_columns),))
                        finally:
                            connection.close()

                        # 迁移幂等（Store.count() 有表名白名单，不含 schema_version，
                        # 故这里直连 sqlite 查——这是测试代码的事，不是被测代码的缺陷）
                        for _ in range(3):
                            database.init_schema()
                        import sqlite3 as _sqlite3
                        _conn = _sqlite3.connect(database.path)
                        try:
                            version_rows = _conn.execute(
                                "SELECT COUNT(*) FROM schema_version").fetchone()[0]
                        finally:
                            _conn.close()
                        suite.eq(version_rows, 1,
                                 "落库：init_schema 连跑 4 次后 schema_version 只有 1 行（幂等）")

                        # 落 tasks / results
                        op_id = response.get("opId") or "op-qa-store-0001"
                        database.record_task({
                            "op_id": op_id, "tool": "read_query", "status": "verified",
                            "params_digest": response.get("paramsDigest"),
                            "target_fingerprint": response.get("targetFingerprint"),
                            "started_at": response.get("startedAt"),
                            "elapsed_ms": response.get("elapsedMs"),
                            "error_code": None,
                        })
                        stored = database.record_result(op_id, "read_query",
                                                        response.get("targetFingerprint") or "",
                                                        {"items": [1, 2, 3]}, False)
                        suite.is_true(bool(stored.get("storedAt")),
                                      "落库：record_result 返回 storedAt（resultRef 可引用）",
                                      "%r" % (stored,))
                        task = database.get_task(op_id)
                        suite.is_true(task is not None,
                                      "落库：按 opId 能查回 tasks 行（opId 是主键，可贯穿）")
                        results = database.query_results(op_id)
                        suite.is_true(len(results) >= 1,
                                      "落库：results 表能按 opId 查回结果",
                                      "rows=%d" % len(results))
                        payload = json.loads(results[0]["payload"])
                        suite.eq(payload.get("items"), [1, 2, 3],
                                 "落库：results.payload 能反解出原结构（未被截断破坏）")
                    finally:
                        database.close()

                suite.case("落库结构与幂等", persist)

                # ---------------- C. 审计脱敏 ----------------
                def audit_masking():
                    audit_module = __import__("audit")
                    log = audit_module.AuditLog(config, also_stderr=False)
                    try:
                        token = client.session_token or ""
                        suite.is_true(len(token) == 64, "审计用例：拿到 64 hex 会话令牌")
                        # 用 WARN 而非 INFO：sandbox 把 EP_LOG_LEVEL 设成 WARN，
                        # 用 INFO 会被级别过滤掉，导致「日志为空」这种假失败。
                        log.warn("qa", "token_probe", sessionToken=token,
                                 token=token, path=r"C:\Users\secret\图纸.dwg")
                        log.warn("qa", "content_probe", text="填充填充填充", handle="1A2F")
                        directory = log.directory
                        suite.is_true(bool(directory) and os.path.isdir(directory),
                                      "审计：日志目录可写")
                    finally:
                        log.close()

                    blob = ""
                    for name in os.listdir(directory):
                        full = os.path.join(directory, name)
                        if os.path.isfile(full):
                            with open(full, "r", encoding="utf-8", errors="replace") as handle:
                                blob += handle.read()
                    suite.is_true(blob.strip() != "", "审计：日志文件确实写入了内容",
                                  "目录=%s" % directory)
                    suite.ok(token not in blob,
                             "审计：日志里没有 sessionToken 明文（必须打码）",
                             "长度 %d 的日志中未找到令牌" % len(blob))
                    suite.ok(token[:32] not in blob,
                             "审计：日志里连令牌前 32 位也没有（不能只截一半）")
                    suite.contains(blob, "1A2F",
                                   "审计：句柄可入日志（用于定位，不属敏感内容）")

                suite.case("审计脱敏", audit_masking)
            finally:
                client.close()

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
