"""tests/qa/qa_civil_type_matrix.py —— `read_civil_list.type` 的**三方矩阵**交叉校验（QA 独立版）。

## 为什么要另写一份，而不是复用工程师的 `test_gateway_n04.py`

工程师那边已有按层分布的 12 条断言（契约/文案/mock 规格/C# 源码/行为）。本文件的差异在**锚点独立**：

* 允许集 `ALLOWED` 在**本文件里独立写死**，**不 import** 工程师的 `EXPECTED_CIVIL_TYPES`；
* 三处实现（`protocol/tools.schema.json` 的 enum、`host/.../ReadToolHandlers.cs` 的 `case` 标签、
  `mockhost/mock_host.py` 的 `CIVIL_TYPES`）**各自与本文的常量比**，而不是互相比；
* 任何一方偷偷改动（加 `corridor`、删 `pointGroup`），只要与本文常量不一致就会红。

两份独立写死的常量互为对照 —— 这比单方加断言强：**同向锚定同一个事实，方向不同**。

## 判据级规则（本文件顺带钉住，适用于所有「枚举类」工具）

**「空集」不等于「未开放」**：合法类型但图上没有对象 → `ok=true, counts.total=0`（可接受）；
未支持的类型 → **必须** `E1001`（不得静默空集）。两者在响应上必须可区分，否则模型会把
「本功能没开放」读成「这张图没有」。
"""

from __future__ import annotations

import json
import os
import re
import sys

import qa_common as qa

#: **本文件独立写死**的允许集（不要改成从产品侧 import）
ALLOWED = {"any", "alignment", "profile", "surface", "pipeNetwork", "pointGroup"}
#: 明确**不支持**的类型（必须被拒，且不得出现在 enum / case 标签 / mock 规格里）
FORBIDDEN = ("corridor", "sampleLineGroup", "bogus")

SCHEMA = "protocol/tools.schema.json"
HOST = "host/Tools/ReadToolHandlers.cs"
MOCK = "mockhost/mock_host.py"

#: 报告里要附的「被测文件指纹」：mtime(**统一 UTC**) + sha256 前 8 位
#: 注：UTC 是产品静态规则要求的（`time.localtime` 会被判违规），我第一版用了本地时间，被他们抓到并已改。
PROVENANCE = (SCHEMA, HOST, MOCK, "mcp/engineering_mcp.py", "mcp/bridge_channel.py")


def fingerprint() -> list:
    """返回 [(路径, mtime, sha8)]，供报告标注「这份结果对应哪一版代码」。"""
    import hashlib
    import time

    rows = []
    for rel in PROVENANCE:
        path = os.path.join(qa.ROOT, rel.replace("/", os.sep))
        try:
            stamp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(os.path.getmtime(path)))
            with open(path, "rb") as handle:
                digest = hashlib.sha256(handle.read()).hexdigest()[:8]
            rows.append((rel, stamp, digest))
        except OSError:
            rows.append((rel, "missing", "-"))
    return rows


def run() -> dict:
    suite = qa.harness.Suite("qa_civil_type_matrix")

    print("=" * 78)
    print("被测文件指纹（快照/结果的版本依据）")
    print("=" * 78)
    for rel, stamp, digest in fingerprint():
        print("  %-42s %s  %s" % (rel, stamp, digest))

    # ---------------- 1. 契约侧：schema enum ----------------
    schema = json.loads(qa.read_source(SCHEMA))
    tool = next((item for item in (schema.get("tools") or [])
                 if item.get("name") == "read_civil_list"), None)
    if not suite.ok(tool is not None, "schema 里有 read_civil_list"):
        return suite.report()
    props = (tool.get("inputSchema") or {}).get("properties") or {}
    enum_values = list((props.get("type") or {}).get("enum") or [])
    qa.eqd(suite, sorted(enum_values), sorted(ALLOWED),
           "schema enum == QA 独立允许集（六项逐名，多一个少一个都红）",
           "schema=%r" % (sorted(enum_values),))
    qa.eqd(suite, [value for value in FORBIDDEN if value in enum_values], [],
           "schema enum 里不出现任何未支持类型（corridor/sampleLineGroup/bogus）")
    # 契约（2026-11-22 21:0x 定稿版）：type **可选**，但缺省语义必须**写在 schema 里**（default=any），
    # 且三端行为一致 —— 「有文档的默认」可以，「静默的默认」不行。
    required_list = (tool.get("inputSchema") or {}).get("required") or []
    suite.ok("type" not in required_list,
             "type 是可选的（required 不含 type）",
             "required=%r" % (required_list,))
    qa.eqd(suite, (props.get("type") or {}).get("default"), "any",
           "schema 里 type 的 default 显式写成 any（缺省语义有文档，不是靠实现猜）",
           "default=%r" % ((props.get("type") or {}).get("default"),))

    # ---------------- 2. C# 侧：case 标签集合 ----------------
    host_src = qa.read_source(HOST)
    start = host_src.find("private static List<CivilObjectRecord> CivilRecordsOf")
    if not suite.ok(start >= 0, "C# 能定位 CivilRecordsOf", "行号=%d" % start):
        return suite.report()
    body = host_src[start:]
    body = body[:body.find("\n        private static CivilObjectRecord FindCivilRecord")]
    cases = set(re.findall(r'case\s+"([A-Za-z]+)"\s*:', body))
    qa.eqd(suite, sorted(cases), sorted(ALLOWED),
           "C# CivilRecordsOf 的 case 标签 == QA 独立允许集（any 必须显式成一支）",
           "C#=%r" % (sorted(cases),))
    default_block = body.split("default:", 1)[-1] if "default:" in body else ""
    suite.ok("AddRange(snapshot." not in default_block,
             "C# default 分支不聚合任何一类（否则未支持类型会拿到全部对象）",
             "default 块片段=%r" % (default_block.strip()[:120],))
    suite.ok(("throw" in default_block) or ("rejected" in default_block),
             "C# default 分支拒绝未支持类型（throw 或交调用方抛）",
             "default 块片段=%r" % (default_block.strip()[:120],))

    # ---------------- 3. mock 侧：CIVIL_TYPES 规格 ----------------
    mock_src = qa.read_source(MOCK)
    match = re.search(r"CIVIL_TYPES\s*=\s*\(([^)]*)\)", mock_src)
    if not suite.ok(match is not None, "mock 里有 CIVIL_TYPES 元组"):
        return suite.report()
    mock_types = set(re.findall(r'"([A-Za-z]+)"', match.group(1)))
    qa.eqd(suite, sorted(mock_types), sorted(ALLOWED),
           "mock CIVIL_TYPES == QA 独立允许集",
           "mock=%r" % (sorted(mock_types),))
    suite.ok('("enum", CIVIL_TYPES)' in mock_src.replace(" ", "").replace('"enum",CIVIL_TYPES', '"enum", CIVIL_TYPES')
             or "PARAM_SPECS" in mock_src,
             "mock 的参数规格走通用 enum 校验（不是给单个工具打补丁）",
             "未找到 enum 规格接线")

    # ---------------- 4. 三处必须完全一致（矩阵闭合） ----------------
    suite.eq(sorted(cases), sorted(mock_types), "C# case 标签 == mock CIVIL_TYPES（两端同集）")
    suite.eq(sorted(mock_types), sorted(enum_values), "mock CIVIL_TYPES == schema enum（契约与替身同集）")

    # ---------------- 5. 行为：未支持 → E1001；合法空集 → ok+total=0 ----------------
    with qa.harness.sandbox("qa-civil-matrix") as _tmp:
        with qa.harness.mock_server() as (server, _host):
            qa.wait_for_pipe(server)
            with qa.harness.connect(server) as client:
                for bad in FORBIDDEN:
                    response = client.call("read_civil_list", {"type": bad})
                    error = response.get("error") or {}
                    result = response.get("result") or {}
                    suite.ok(response.get("ok") is False and error.get("code") == "E1001",
                             "未支持 type=%r → E1001（不得静默空集）" % bad,
                             "实际 ok=%r error=%r counts=%r"
                             % (response.get("ok"),
                                (error.get("code"), error.get("detail")), result.get("counts")))
                    qa.eqd(suite, len(result.get("items") or []), 0,
                           "type=%r 被拒时不得返回 items" % bad)

                # 合法类型：有对象 → ok；无对象 → ok=true 且 total=0（**空集是合法答案**）
                any_response = client.call("read_civil_list", {"type": "any"}).get("result") or {}
                suite.ok((any_response.get("counts") or {}).get("total", 0) >= 1,
                         "type=any 能枚举出对象（否则契约测试等于没验）",
                         "counts=%r" % (any_response.get("counts"),))
                empty_type = None
                for value in ("pointGroup", "profile"):
                    got = (client.call("read_civil_list", {"type": value}).get("result") or {})
                    if (got.get("counts") or {}).get("total") == 0:
                        empty_type = value
                        suite.ok(True,
                                 "合法类型 %s 无对象时返回 ok + total=0（**空集是合法答案**，不是错误）"
                                 % value)
                        break
                suite.ok(empty_type is not None,
                         "夹具覆盖了「合法类型但无对象」这一态（用于区分空集与未开放）",
                         "未找到 total=0 的合法类型")

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
