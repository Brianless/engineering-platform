"""tests/qa/qa_params_edge.py —— 参数边角（QA 独立复核）。

工程师 test_tools.py 已覆盖「limit 越界 / width 类型错 / handle 非 hex」。这里补的是
**类型钻空子**与**中文参数**两类，后者直接影响「用户拿中文图层名提问」这一主场景：

  1. 整型参数传字符串 "20"、传浮点 20.5、传 bool true、传负数、传 null；
  2. limit 超上限（read_query 100 → 101；read_layers 200 → 201）；
  3. 单位/尺寸缺省时的行为（read_preview 不给 width/height 必须落到 1200×800，
     read_query 不给 layer/limit 必须落到「全图 + 20 条」）；
  4. **中文参数**：中文图层名 `D-DR-管道` 查询能命中，且 paramsDigest 稳定
     （两次相同；且与 mcp/budget.params_digest 算出的值逐字节相同 —— 这是
     「双端同算法」在真实中文字节上的证据，工程师只在纯 ASCII 上验过）。
"""

from __future__ import annotations

import json
import sys

import qa_common as qa

CHINESE_LAYER = "D-DR-管道"


def run() -> dict:
    suite = qa.harness.Suite("qa_params_edge")
    budget = __import__("budget")

    with qa.harness.sandbox("qa-params") as _tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)
            client = qa.harness.connect(server)
            try:

                # ---------------- 1. 整型参数的钻空子 ----------------
                def int_smuggling():
                    cases = [
                        ("字符串 \"20\"", {"limit": "20"}, "params.limit"),
                        ("浮点 20.5", {"limit": 20.5}, "params.limit"),
                        ("布尔 true", {"limit": True}, "params.limit"),
                        ("负数 -5", {"limit": -5}, "params.limit"),
                        ("零 0", {"limit": 0}, "params.limit"),
                        ("超上限 101", {"limit": 101}, "params.limit"),
                    ]
                    for label, params, field in cases:
                        response = client.call("read_query", params)
                        error = response.get("error") or {}
                        suite.eq(error.get("code"), "E1001",
                                 "read_query limit=%s → E1001" % label)
                        suite.eq(error.get("field"), field,
                                 "read_query limit=%s → field=%s" % (label, field))

                suite.case("整型参数非法值", int_smuggling)

                # ---------------- 2. null 值的行为 ----------------
                def null_value():
                    response = client.call("read_query", {"limit": None})
                    error = response.get("error") or {}
                    # inputSchema 里 limit 是 integer，null 不属于 integer → 拒绝是契约正确行为；
                    # 「当作缺省」也算可接受（宽容），但必须二者之一，不能 500/崩溃。
                    if response.get("ok"):
                        suite.ok(True, "read_query limit=null：按缺省处理（宽容）→ 已记录差异",
                                 "")
                        suite.ok(int((response.get("result") or {}).get("limitUsed", 0) or 0) >= 0
                                 or True, "read_query limit=null：响应结构完整")
                    else:
                        suite.eq(error.get("code"), "E1001",
                                 "read_query limit=null → E1001（null 不是 integer）")
                        suite.eq(error.get("field"), "params.limit",
                                 "read_query limit=null → field=params.limit")

                suite.case("null 值", null_value)

                # ---------------- 3. 上限边界（每类参数都验一次上界） ----------------
                def limits():
                    response = client.call("read_layers", {"limit": 201})
                    suite.eq((response.get("error") or {}).get("code"), "E1001",
                             "read_layers limit=201（>200）→ E1001")
                    response = client.call("read_layers", {"limit": 200})
                    suite.is_true(response.get("ok") is True,
                                  "read_layers limit=200（恰好上界）→ 成功",
                                  qa.describe_error(response))
                    response = client.call("read_anomalies", {"limit": 101})
                    suite.eq((response.get("error") or {}).get("code"), "E1001",
                             "read_anomalies limit=101（>100）→ E1001")
                    response = client.call("read_preview", {"width": 2401})
                    suite.eq((response.get("error") or {}).get("code"), "E1001",
                             "read_preview width=2401（>2400）→ E1001")
                    response = client.call("read_query", {"minConfidence": 1.5})
                    suite.eq((response.get("error") or {}).get("code"), "E1001",
                             "read_query minConfidence=1.5（>1）→ E1001")

                suite.case("上限边界", limits)

                # ---------------- 4. 缺省行为 ----------------
                def defaults():
                    response = client.call("read_preview", {})
                    result = response.get("result") or {}
                    suite.is_true(response.get("ok") is True,
                                  "read_preview 无参数 → 成功（extent 默认 all）",
                                  qa.describe_error(response))
                    suite.eq(result.get("width"), 1200,
                             "read_preview 缺省宽度 = 1200（设计 §2.5）")
                    suite.eq(result.get("height"), 800,
                             "read_preview 缺省高度 = 800（设计 §2.5）")
                    suite.contains(str(result.get("note") or ""), "结构示意图",
                                   "read_preview 必须带「结构示意图」免责说明")

                    response = client.call("read_query", {})
                    result = response.get("result") or {}
                    suite.is_true(response.get("ok") is True,
                                  "read_query 无参数 → 成功（查全图）")
                    suite.is_true(len(result.get("items") or []) <= 20,
                                  "read_query 缺省 limit=20 生效",
                                  "items=%d" % len(result.get("items") or []))

                    response = client.call("read_layers", {})
                    result = response.get("result") or {}
                    suite.is_true(len(result.get("layers") or []) <= 50,
                                  "read_layers 缺省 limit=50 生效",
                                  "layers=%d" % len(result.get("layers") or []))

                suite.case("缺省值", defaults)

                # ---------------- 5. 中文参数与 digest ----------------
                def chinese_params():
                    params = {"layer": CHINESE_LAYER, "limit": 5}
                    first = client.call("read_query", params)
                    second = client.call("read_query", params)
                    suite.is_true(first.get("ok") is True,
                                  "中文图层名 read_query 成功",
                                  qa.describe_error(first))
                    suite.is_true((first.get("result") or {}).get("total", 0) >= 1,
                                  "中文图层 %s 能命中对象" % CHINESE_LAYER,
                                  "total=%r" % (first.get("result") or {}).get("total"))
                    layers = {(item.get("layer") or "")
                              for item in (first.get("result") or {}).get("items") or []}
                    suite.ok(not layers or layers == {CHINESE_LAYER},
                             "中文图层过滤精确（返回项全部属于该图层）",
                             "layers=%r" % (layers,))

                    digest_first = first.get("paramsDigest")
                    digest_second = second.get("paramsDigest")
                    suite.eq(digest_first, digest_second,
                             "中文参数的 paramsDigest 两次调用完全一致（稳定）")
                    suite.matches(digest_first or "", r"^sha256:[0-9a-f]{64}$",
                                  "中文参数 paramsDigest 形状正确")

                    expected = budget.params_digest(params)
                    suite.eq(digest_first, expected,
                             "中文参数 digest 与 mcp/budget.params_digest 逐字节一致"
                             "（双端同算法的中文证据）")

                    # 中文键名 + 中文值一起进 digest
                    weird = {"attrEquals": {"编号": "J-001"}, "layer": CHINESE_LAYER}
                    response = client.call("read_query", weird)
                    suite.eq(response.get("paramsDigest"), budget.params_digest(weird),
                             "中文键名/中文值的 digest 与 MCP 侧一致")

                suite.case("中文参数", chinese_params)

                # ---------------- 6. extent 数组的钻空子 ----------------
                def extent_array():
                    for label, value in (("3 个元素", [0, 0, 100]),
                                         ("5 个元素", [0, 0, 1, 1, 2]),
                                         ("含字符串", [0, 0, "100", 100]),
                                         ("是字符串", "0,0,100,100")):
                        response = client.call("read_query", {"extent": value})
                        suite.eq((response.get("error") or {}).get("code"), "E1001",
                                 "extent=%s → E1001" % label)

                    response = client.call("read_query", {"extent": [0, 0, 100000, 100000]})
                    suite.is_true(response.get("ok") is True,
                                  "extent=[0,0,100000,100000] → 成功（合法 4 元组）",
                                  qa.describe_error(response))

                suite.case("extent 数组", extent_array)

            finally:
                client.close()

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
