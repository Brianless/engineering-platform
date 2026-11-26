"""预算截断与摘要测试（设计 §4.4 / §2.6）。

覆盖：
    1. paramsDigest 的 canonical 规则：键递归排序 + 紧凑分隔符 + ensure_ascii=False；
    2. 三级截断：① 数组减半 → ② 数组降到 3 条 → ③ 整体降级为 counts 分组；
    3. 截断只在元素边界裁，结构永远合法（resultRef/summary 必须还在）；
    4. summary_of 生成 ≤ 上限的自然语言摘要；
    5. 剪枝后必须给出 drilldown，告诉模型怎么继续下钻。
"""

from __future__ import annotations

import hashlib
import json

import harness
import budget as budget_module


def run() -> dict:
    suite = harness.Suite("budget")

    # ---------- 1. paramsDigest ----------
    def params_digest():
        params = {"limit": 20, "layer": "排水", "nested": {"z": 1, "a": [2, 3]}}
        expected_canonical = '{"layer":"排水","limit":20,"nested":{"a":[2,3],"z":1}}'
        suite.eq(budget_module.canonical_json(params), expected_canonical,
                 "canonical_json：键递归排序 + 紧凑分隔符 + 中文不转义")
        digest = budget_module.params_digest(params)
        suite.matches(digest, r"^sha256:[0-9a-f]{64}$", "params_digest 形状 sha256:<64hex>")
        manual = "sha256:" + hashlib.sha256(
            expected_canonical.encode("utf-8")).hexdigest()
        suite.eq(digest, manual, "params_digest = sha256(UTF-8 canonical)")

        key_order_a = {"a": 1, "b": {"c": 2, "d": 3}}
        key_order_b = {"b": {"d": 3, "c": 2}, "a": 1}
        suite.eq(budget_module.params_digest(key_order_a),
                 budget_module.params_digest(key_order_b),
                 "键顺序不同的等价参数得到同一摘要")

        suite.ne(budget_module.params_digest({"limit": 20}),
                 budget_module.params_digest({"limit": 21}),
                 "参数不同则摘要不同")

    # ---------- 2/3. 三级截断 ----------
    def truncation_levels():
        payload = {
            "summary": "共 500 个图层",
            "resultRef": {"tool": "read_layers", "opId": "op-x", "fingerprint": "fp1:" + "a" * 32},
            "total": 500,
            "layers": [{"name": "L%03d" % index, "entityCount": index,
                        "types": {"LINE": index}, "confidence": 0.9,
                        "confidenceBand": "auto", "basis": [{"kind": "layer_map"}]}
                       for index in range(200)],
            "truncated": False,
        }
        original_chars = budget_module._char_len(payload)
        suite.is_true(original_chars > 6000, "构造的载荷足够大", "chars=%d" % original_chars)

        first, meta1 = budget_module.truncate(json.loads(json.dumps(payload)), 6000)
        suite.is_true(len(first.get("layers") or []) < 200,
                      "第一级截断：数组被裁剪")
        suite.is_true(meta1.get("truncated") is True, "第一级截断标记 truncated=true")
        suite.ok("summary" in first and "resultRef" in first,
                 "截断后 summary/resultRef 仍在（结构合法）")
        suite.is_true(budget_module._char_len(first) <= 6000,
                      "第一级截断后不超过预算", "chars=%d" % budget_module._char_len(first))

        tiny, meta2 = budget_module.truncate(json.loads(json.dumps(payload)), 1200)
        suite.is_true(budget_module._char_len(tiny) <= 1200,
                      "小预算下也必须落进预算内",
                      "chars=%d" % budget_module._char_len(tiny))
        suite.is_true(meta2.get("truncationLevel", 0) >= 1,
                      "小预算触发更高截断级别")

        # 极端预算：整体降级为 counts 分组（第 3 级是尽力而为，不保证塞进任意小预算）
        tiny2, meta3 = budget_module.truncate(json.loads(json.dumps(payload)), 300)
        suite.eq(meta3.get("truncationLevel"), 3, "极端预算触发第 3 级（降级为 counts 分组）")
        suite.ok("layers" not in tiny2, "第 3 级不再返回明细数组")
        suite.eq((tiny2.get("counts") or {}).get("layers"), 200,
                 "第 3 级以计数代替明细（layers=200）")
        suite.is_true(meta3.get("chars", 0) < original_chars / 10,
                      "第 3 级体积降到原来的 1/10 以下",
                      "chars=%s original=%d" % (meta3.get("chars"), original_chars))
        suite.ok("resultRef" in tiny2, "极端截断后 resultRef 仍保留")
        suite.ok(bool(tiny2.get("drilldown")), "极端截断后仍给出 drilldown 建议")

        # 合法 JSON 往返
        text = json.dumps(tiny2, ensure_ascii=False)
        suite.eq(json.loads(text), tiny2, "截断结果仍是合法 JSON")

    # ---------- 4. summary ----------
    def summary_rules():
        payload = {
            "summary": "图纸含水 48 个图元、10 个图层，检测到 6 类异常。",
            "counts": {"entities": 48, "layers": 10},
        }
        text = budget_module.summary_of(payload, "【read_overview】已完成")
        suite.contains(text, "48", "summary_of 优先使用载荷自带的 summary")

        clipped = budget_module.clip_text("一" * 3000, 1200)
        suite.is_true(len(clipped) <= 1200, "clip_text 把摘要限制在上限内",
                      "len=%d" % len(clipped))

        fallback = budget_module.summary_of({}, "【sys_ping】已完成")
        suite.contains(fallback, "sys_ping", "无 summary 时回退到工具名提示")

    # ---------- 5. drilldown ----------
    def drilldown_hint():
        payload = {
            "summary": "x",
            "items": [{"handle": "%04X" % index} for index in range(50)],
            "truncated": False,
        }
        clipped, meta = budget_module.truncate(payload, 800)
        hints = clipped.get("drilldown") or meta.get("drilldown") or []
        suite.is_true(isinstance(hints, list), "drilldown 为数组")
        for hint in hints:
            suite.ok("tool" in hint and "params" in hint and "why" in hint,
                     "drilldown 项含 tool/params/why")

    for label, func in (
        ("paramsDigest", params_digest),
        ("三级截断", truncation_levels),
        ("摘要", summary_rules),
        ("下钻提示", drilldown_hint),
    ):
        suite.case(label, func)

    return suite.report()


if __name__ == "__main__":
    report = run()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report["failed"] == 0 else 1)
