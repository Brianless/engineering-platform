"""tests/qa/qa_error_codes.py —— 错误码可达性审计（技术债清单，交下一阶段）。

QA 要回答的不是「errors.json 里有 14 个码」，而是「这 14 个码在代码里到底能不能被触发」。
死码（声明了但永不返回）会让调用方写出永远走不到的分支，也会让 M1 的契约变成空头承诺。

判定方法（可复现）：
  * 在 host/ mcp/ mockhost/ 里搜 `"E1xxx"` 字面量，或 C# 的 `ErrorCodes.E1004`
    常量引用（C# 侧统一走常量，只搜字符串会漏判成死码）；
  * 排除两处「只声明不抛出」的位置：
      - host/Protocol/ErrorCodes.cs（常量集中定义）
      - mockhost/mock_host.py 里的 ERROR_CODES 元数据表（`"E1002": ("文案", ...)` 形态）
  * 剩下还有字面量的 → 判定为可达；一处都不剩 → 判定为死码。

输出：可达/不可达两张清单 + 每条的依据行号。
"""

from __future__ import annotations

import json
import os
import re
import sys

import qa_common as qa

#: 设计 §2.2 明确说「M0 实际会返回」的集合
DESIGN_M0_SET = ("E1001", "E1003", "E1004", "E1005", "E1006",
                 "E2001", "E2003", "E3002", "E4001", "E5001")

#: 元数据表行的形态："E1002": ("目标图纸或对象与审批不符", False, "…")
_METADATA_LINE = re.compile(r'^\s*"(E\d{4}|W\d{4})"\s*:\s*\(')

#: 「真的会被抛出来」的用法形态。只出现在**码表/常量清单**里的不算可达 ——
#: 否则「错误码清单里列了一次」会被当成「实现了」，检测器就骗了自己。
#: 2026-11-22：E1002/E2002/E6001 就属于「只在码表里」（upstream_map 的元组 + mockhost 的字典），
#: 而 E3001 是真的被 `_error_envelope(...)` 返回的（mcp/bridge_channel.py）。
_REAL_USE = ("_error_envelope(", "make_error(", "WireError", "raise ", "return ",
             ".Of(", "new WireError")


def _is_code_table_line(line: str) -> bool:
    """一行里堆了 ≥2 个错误码 = 码表清单（如 upstream_map 的元组），不算可达。

    注意：`InstanceError(` 换行写 `    "C1001",` 这种**单独一个码**的行**不能**当清单排除，
    否则 C 码会全被判死（我第一版就这么误伤了 C1001/C1002/C1005）。
    """
    return len(re.findall(r'"(?:E|C|W)\d{4}"', line)) >= 2


def _scan_sites(code: str) -> list:
    """返回 [(相对路径, 行号, 行内容)]，已排除常量定义、元数据表与**码表清单**。"""
    sites = []
    for sub_dir in ("host", "mcp", "mockhost"):
        for rel_path in list(qa.iter_files(sub_dir, ".cs")) + list(qa.iter_files(sub_dir, ".py")):
            try:
                lines = qa.read_source(rel_path).splitlines()
            except OSError:                                    # pragma: no cover
                continue
            if rel_path.endswith("Protocol/ErrorCodes.cs") or \
                    rel_path.endswith("Protocol\\ErrorCodes.cs"):
                continue
            quoted = '"%s"' % code
            constant = "ErrorCodes.%s" % code
            for number, line in enumerate(lines, 1):
                if quoted not in line and constant not in line:
                    continue
                if _METADATA_LINE.match(line):
                    continue
                if _is_code_table_line(line):
                    continue
                sites.append((rel_path, number, line.strip()[:120]))
    return sites


def _listed_only(code: str) -> list:
    """只在码表/常量里出现、没有任何真实抛出点的码（用于报告，不参与可达性判定）。"""
    listed = []
    for sub_dir in ("mcp", "mockhost", "host"):
        for rel_path in list(qa.iter_files(sub_dir, ".py")) + list(qa.iter_files(sub_dir, ".cs")):
            try:
                lines = qa.read_source(rel_path).splitlines()
            except OSError:                                    # pragma: no cover
                continue
            if rel_path.endswith("ErrorCodes.cs"):
                continue
            for number, line in enumerate(lines, 1):
                if ('"%s"' % code) in line or ("ErrorCodes.%s" % code) in line:
                    listed.append((rel_path, number))
    return listed


def run() -> dict:
    suite = qa.harness.Suite("qa_error_codes")

    contract = json.loads(qa.read_source("protocol/errors.json"))
    codes = [item["code"] for item in contract["errors"]]
    warnings = [item["code"] for item in contract["warnings"]]

    # 口径（2026-11-22 改）：**不再写死 14/5/9 这类计数** —— 写死的数字迟早过期
    # （工具目录 16→18 就是这么过期的）。改成三条**不变量**，它们不随数量漂移，但一旦契约形状变了就会红：
    #   ① 码不重复、格式统一（E/C/W + 4 位）；
    #   ② `m0Active=false` 的**集合**必须等于「M0 预留清单」（这是决策，不是计数）；
    #   ③ 告警必须全部 m0Active=true（告警不存在"预留"一说，构造了就该能发）。
    suite.eq(len(codes), len(set(codes)), "errors.json 的错误码不重复")
    bad_format = [code for code in codes + warnings
                  if not re.match(r"^[ECW]\d{4}$", code)]
    suite.eq(bad_format, [], "所有错误码/告警码符合 ^[ECW]\\d{4}$")

    #: 预留（`m0Active=false`）的**决策清单**：只有码表常量、没有真实触发点。
    #: E3001 于 2026-11-22 移出此清单（N04-B 读路径后置校验会真实返回它）。
    #: E1002 于 2026-11-22 23:5x 移出（M1 审批令牌绑定后会真实返回：摘要/指纹/文档不符）。
    M0_RESERVED = ["E2002", "E6001"]
    falsy = sorted(item["code"] for item in contract["errors"]
                   if item.get("m0Active") is not True)
    qa.eqd(suite, falsy, M0_RESERVED,
           "m0Active=false 的集合 == M0 预留清单（多一个/少一个都要有人来解释）",
           "实际=%r" % (falsy,))
    dead_warnings = [item["code"] for item in contract["warnings"]
                     if item.get("m0Active") is not True]
    suite.eq(dead_warnings, [], "9 条告警全部 m0Active=true（不存在「预留告警」）")

    reachable = {}
    dead = {}
    for code in codes:
        sites = _scan_sites(code)
        if sites:
            reachable[code] = sites
        else:
            dead[code] = code

    print("=" * 78)
    print("错误码可达性清单（依据：源码中排除定义/元数据表后的字面量出现）")
    print("=" * 78)
    for code in codes:
        if code in reachable:
            first = reachable[code][0]
            print("  %-6s REACHABLE  %2d 处  首个：%s:%d"
                  % (code, len(reachable[code]), first[0], first[1]))
        else:
            print("  %-6s DEAD       —— 源码中无任何抛出点" % code)
    print("-" * 78)

    # m0Active=true 的码必须可达；m0Active=false 的码属于「契约预留」，
    # 当前死码是**符合契约**的，只列入技术债清单，不判失败（否则 M1 真实现时反而炸）。
    for item in contract["errors"]:
        code = item["code"]
        if item.get("m0Active") is True:
            suite.is_true(code in reachable,
                          "%s（m0Active=true）可达（有抛出点）" % code,
                          "源码中找不到任何 \"%s\"/ErrorCodes.%s 抛出点 → 死码"
                          % (code, code))
        else:
            suite.ok(True, "%s（m0Active=false）为契约预留，当前死码符合契约" % code,
                     "listed_as_debt")

    # 设计 §2.2 点名的集合必须全可达（少一个就是「设计承诺了但实现没做」）
    for code in DESIGN_M0_SET:
        suite.is_true(code in reachable,
                      "设计 §2.2 承诺 M0 会返回的 %s 确实可达" % code,
                      "该码在实现里没有抛出点")

    # C 码的可达性（C1001..C1005 由 MCP 层产生）
    for code in [item for item in codes if item.startswith("C")]:
        suite.is_true(code in reachable, "C 码 %s 可达（有抛出点）" % code,
                      "源码中找不到 \"%s\" 抛出点" % code)

    # 告警可达性：W1002 曾被发现「构造了但没上线」，这里把它列为显式检查项
    warning_dead = [code for code in warnings if not _scan_sites(code)]
    print("告警可达性：DEAD = %r" % (warning_dead,))
    suite.ok(warning_dead == [],
             "9 条告警全部有构造点（DEAD 则说明告警承诺未兑现）",
             "死告警=%r" % (warning_dead,))

    # 元数据自洽：m0Active=false 的码如果反而**真的被抛出来**，说明契约与实现不同步
    mismatches = []
    for item in contract["errors"]:
        code = item["code"]
        if item.get("m0Active") is False and code in reachable:
            first = reachable[code][0]
            mismatches.append((code, "m0Active=false 但真实可达 @%s:%d" % (first[0], first[1])))
        if item.get("m0Active") is True and code in dead:
            mismatches.append((code, "m0Active=true 但死码（只在码表里出现 %d 处）"
                               % len(_listed_only(code))))
    suite.ok(mismatches == [],
             "errors.json 的 m0Active 标记与**真实可达性**一致"
             "（码表里列一次不算可达）",
             "不一致=%r" % (mismatches,))

    print("-" * 78)
    print("死码清单（技术债，需 M1 前决定「实现」还是「从契约删除」）：")
    for code in dead:
        entry = next(item for item in contract["errors"] if item["code"] == code)
        print("  %s  %s  m0Active=%r" % (code, entry.get("message"), entry.get("m0Active")))
    print("=" * 78)

    return suite.report()


if __name__ == "__main__":
    sys.exit(qa.emit(run()))
