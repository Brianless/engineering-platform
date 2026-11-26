"""项目库与审计测试（设计 §5 / §7.5）。

覆盖：
    1. SQLite 8 张表 + schema_version 全部建出来（objects/attributes/relations/
       results/tasks/evidence/skills_used/audit）；
    2. tasks UPSERT（同 opId 覆盖）、按状态查询、results 落库返回 storedAt 引用；
    3. audit 表写入与查询；
    4. 审计日志（JSONL）：**不得出现 sessionToken 明文**，路径只留 basename+hash；
    5. 关系表/证据表/技能表的写入与计数。
"""

from __future__ import annotations

import json
import os
import sqlite3

import harness
import audit as audit_module
import store as store_module


EXPECTED_TABLES = {
    "schema_version", "objects", "attributes", "relations", "results",
    "tasks", "evidence", "skills_used", "audit",
}


def run() -> dict:
    suite = harness.Suite("store")

    with harness.sandbox("store") as tmp:
        config = harness.load_config()

        # ---------- 1. 表结构 ----------
        def schema():
            db_path = config.db_path()
            database = store_module.Store(config)
            database.init_schema()
            try:
                connection = sqlite3.connect(db_path)
                try:
                    rows = connection.execute(
                        "SELECT name FROM sqlite_master WHERE type='table'").fetchall()
                    tables = {row[0] for row in rows}
                    for name in EXPECTED_TABLES:
                        suite.contains(tables, name, "建表 %s" % name)
                    indexes = connection.execute(
                        "SELECT name FROM sqlite_master WHERE type='index'").fetchall()
                    suite.is_true(len(indexes) >= 4, "至少 4 个索引",
                                  "indexes=%d" % len(indexes))
                    version = connection.execute(
                        "SELECT version FROM schema_version").fetchone()
                    suite.eq(version[0], 1, "schema_version.version=1")
                finally:
                    connection.close()
            finally:
                database.close()
            suite.is_true(os.path.isfile(db_path), "数据库文件已创建")
            suite.is_true(db_path.startswith(tmp), "数据库落在配置指定的目录内")

        # ---------- 2. tasks / results ----------
        def tasks_and_results():
            database = store_module.Store(config)
            database.init_schema()
            try:
                database.record_task({
                    "op_id": "op-store-0001", "tool": "read_overview",
                    "status": "queued", "params_digest": "sha256:" + "a" * 64,
                    "target_fingerprint": "fp1:" + "b" * 32,
                    "elapsed_ms": 0, "error_code": None,
                })
                first = database.get_task("op-store-0001")
                suite.eq(first.get("status"), "queued", "record_task 首次写入成功")

                database.record_task({
                    "op_id": "op-store-0001", "tool": "read_overview",
                    "status": "verified", "params_digest": "sha256:" + "a" * 64,
                    "target_fingerprint": "fp1:" + "b" * 32,
                    "elapsed_ms": 183, "error_code": None,
                })
                second = database.get_task("op-store-0001")
                suite.eq(second.get("status"), "verified", "record_task 同 opId UPSERT 覆盖")
                suite.eq(second.get("elapsed_ms"), 183, "UPSERT 更新 elapsed_ms")
                suite.eq(database.count("tasks"), 1, "UPSERT 不产生重复行")

                listed = database.query_tasks(limit=10)
                suite.eq(len(listed), 1, "query_tasks 返回 1 条")

                stored = database.record_result("op-store-0001", "read_overview",
                                                "fp1:" + "b" * 32,
                                                {"summary": "x", "counts": {"entities": 48}},
                                                False)
                suite.matches(stored.get("storedAt") or "",
                              r"^projects/default\.db#results/\d+$",
                              "record_result 返回 storedAt 引用")
            finally:
                database.close()

        # ---------- 3. 对象索引 / 关系 / 证据 / 技能 ----------
        def knowledge_tables():
            database = store_module.Store(config)
            database.init_schema()
            try:
                database.upsert_object(handle="1001", fingerprint="fp1:" + "c" * 32,
                                       dxf_name="LWPOLYLINE", layer="D-SITE-边界",
                                       meaning="场地边界", confidence=0.93,
                                       extents={"min": [0, 0, 0], "max": [1, 1, 0]})
                database.upsert_object(handle="1001", fingerprint="fp1:" + "c" * 32,
                                       dxf_name="LWPOLYLINE", layer="D-SITE-边界",
                                       meaning="场地边界", confidence=0.95,
                                       extents={"min": [0, 0, 0], "max": [2, 2, 0]})
                suite.eq(database.count("objects"), 1, "upsert_object 幂等（同 handle 不重复）")

                fingerprint = "fp1:" + "c" * 32
                database.record_attribute("1001", fingerprint, "图号", "D-01",
                                          source="drawing")
                suite.eq(database.count("attributes"), 1, "record_attribute 写入成功")

                try:
                    database.record_attribute("1001", fingerprint, "x", "y",
                                              source="invented_source")
                    suite.fail("非法 source 应当被拒绝")
                except Exception:
                    suite.ok(True, "非法 source 被拒绝（防止伪造证据来源）")

                database.record_relation("1001", "101C", "references", "同一条排水路径",
                                         fingerprint)
                suite.eq(database.count("relations"), 1, "record_relation 写入成功")

                database.record_evidence("op-store-0002", "layer_map",
                                         {"ref": "layer-map@1.0:#D-SITE-边界",
                                          "confidence": 0.93})
                suite.eq(database.count("evidence"), 1, "record_evidence 写入成功")

                database.record_skill_used("op-store-0002", "skill.site-grading",
                                           "0.1.0", "场地分级校核")
                suite.eq(database.count("skills_used"), 1, "record_skill_used 写入成功")
            finally:
                database.close()

        # ---------- 4. audit 表 ----------
        def audit_table():
            database = store_module.Store(config)
            database.init_schema()
            try:
                database.record_audit({
                    "actor": "mcp", "action": "read.read_overview",
                    "scope": "entities=48", "result": "ok", "op_id": "op-store-0003",
                })
                database.record_audit({
                    "actor": "mcp", "action": "write.cad_create_line",
                    "scope": "blocked", "result": "denied", "op_id": "op-store-0004",
                })
                rows = database.query_audit(limit=10)
                suite.eq(len(rows), 2, "audit 表写入 2 条")
                suite.contains([row.get("action") for row in rows], "read.read_overview",
                               "audit 记录读操作")
                suite.contains([row.get("result") for row in rows], "denied",
                               "audit 记录被拒的写操作")
            finally:
                database.close()

        # ---------- 5. 审计日志 JSONL ----------
        def audit_log():
            config.set_path("log.level", "INFO")     # 测试需要 INFO 级也能落盘
            log = audit_module.AuditLog(config)
            token = "9f2c1d4a7b0e3856" * 4      # 64 hex，模拟 sessionToken
            log.info("pipe", "handshake", sessionToken=token,
                     opId="op-store-0005", detail="hello_ok")
            log.info("pipe", "tool_ok", summary="图纸内容不应进日志",
                     result={"entities": 48})
            log.warn("pipe", "tool_error", code="E1001",
                     path=r"C:\Users\someone\Documents\某项目\排水总图.dwg")
            log.close()

            log_dir = config.log_dir()
            files = [name for name in os.listdir(log_dir)
                     if name.startswith("mcp-") and name.endswith(".log")]
            suite.is_true(bool(files), "审计日志文件已创建（mcp-YYYYMMDD.log）", str(files))
            content = ""
            for name in files:
                with open(os.path.join(log_dir, name), "r", encoding="utf-8") as handle:
                    content += handle.read()
            suite.is_true(bool(content), "审计日志非空")
            suite.is_false(token in content, "日志中不出现 sessionToken 明文",
                           "发现明文 token")
            suite.contains(content, "9f2c", "日志保留 token 前 4 位便于排查")
            suite.is_false("someone" in content, "日志中的用户名被打码")
            suite.is_false("Documents" in content, "日志不记目录部分（仅 basename#hash）")
            suite.is_false("图纸内容不应进日志" in content,
                           "summary 字段被 redacted（不记图纸内容）")
            suite.contains(content, "排水总图.dwg#", "路径脱敏为 basename#sha256[:8]")

            lines = [line for line in content.strip().splitlines() if line.strip()]
            suite.is_true(len(lines) >= 3, "至少写入 3 条日志", "lines=%d" % len(lines))
            for line in lines:
                parsed = json.loads(line)
                suite.ok("ts" in parsed and "level" in parsed,
                         "每条审计记录含 ts/level 字段")

            suite.is_true(log.is_writable(), "审计目录可写")

        for label, func in (
            ("表结构", schema),
            ("任务与结果", tasks_and_results),
            ("知识表", knowledge_tables),
            ("审计表", audit_table),
            ("审计日志脱敏", audit_log),
        ):
            suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
