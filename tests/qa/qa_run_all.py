"""tests/qa/qa_run_all.py —— QA 独立复核套件的聚合入口。

与 tests/run_all.py 并列、互不依赖：工程师那套跑的是「他自己写的判据」，
这里跑的是「QA 另写的边角判据」。两者都通过才算真的可用。

用法：
    python tests/qa/qa_run_all.py              # 跑全部 QA 套件
    python tests/qa/qa_run_all.py framing      # 只跑名字含 framing 的套件
    python tests/qa/qa_run_all.py --list

每个套件在独立子进程里跑（ctypes 管道句柄、临时目录、mockhost 线程不会互相污染），
子进程输出 JSON 报告，父进程汇总；任一失败退出码 1。
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

SUITES = [
    ("qa_framing_edge", "分帧边角：零长度/半包断连/0x7FFFFFFF/粘3帧/中文劈开/数组帧"),
    ("qa_handshake_edge", "握手边角：单会话/旧令牌/版本矩阵/缺字段/missingFeatures/W1002"),
    ("qa_params_edge", "参数边角：类型钻空子/null/上下界/缺省/中文 layer 与 digest"),
    ("qa_concurrency", "并发：4 请求突发/双线程交错/串行化/重复 opId/混合批"),
    ("qa_budget_store", "预算与落库：三级截断/drilldown 可执行/8 表字段/审计脱敏"),
    ("qa_write_domain", "写通道：8 前缀×多工具逐个 E2003/大小写/无副作用"),
    ("qa_error_codes", "错误码可达性：14 E + 5 C + 9 W 的死码清单"),
    ("qa_contract_delta", "契约 delta：hello_ack/civil3d/civilApi 新字段双端一致 + catalogHash 同源"),
    ("qa_mcp_stdio_edge", "MCP stdio 边角：-32002/版本协商/arguments null/协议错误/stdout 洁净"),
    ("qa_static_review", "静态审查：Pipe 无 CAD/写操作面/硬编码路径/import 白名单/BOM"),
    ("qa_round2_verify", "第三轮复验：D1–D7 反向判据 + 三端契约漂移 + 夹具门禁与证据交叉"),
    ("qa_bridge_attack", "N03 对抗复验：真 relay 鉴权矩阵 / fail-closed / 端口隔离 / 三补丁复现"),
    ("qa_civil_type_matrix", "read_civil_list.type 三方矩阵：schema enum / C# case / mock 规格 同集 + 行为"),
]


def run_suite(name: str, timeout: float = 300.0) -> dict:
    path = os.path.join(HERE, name + ".py")
    started = time.time()
    try:
        completed = subprocess.run([sys.executable, "-u", path],
                                   cwd=ROOT, capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return {"name": name, "passed": 0, "failed": 1,
                "failures": ["[%s] 套件超时（>%.0fs）" % (name, timeout)],
                "elapsedMs": int((time.time() - started) * 1000)}
    elapsed = int((time.time() - started) * 1000)
    stdout = completed.stdout.decode("utf-8", "replace")
    stderr = completed.stderr.decode("utf-8", "replace")

    # 报告是出本文件最后一段 JSON：前面可能有人性化输出（含 '{' 字符），
    # 所以从后往前找「单独一行 {」作为报告起点，而不是 find("{")。
    report = None
    lines = stdout.splitlines()
    for index in range(len(lines) - 1, -1, -1):
        if lines[index].strip() != "{":
            continue
        try:
            report = json.loads("\n".join(lines[index:]))
            break
        except ValueError:
            continue
    if not isinstance(report, dict):
        tail = (stdout.strip().splitlines() or [""])[-1][:400]
        detail = stderr.strip().splitlines()[-4:] if stderr.strip() else []
        return {"name": name, "passed": 0, "failed": 1,
                "failures": ["[%s] 未输出 JSON 报告（exit=%d）stdout尾: %s stderr尾: %s"
                             % (name, completed.returncode, tail, " | ".join(detail))],
                "elapsedMs": elapsed}
    report["elapsedMs"] = elapsed
    if completed.returncode != 0 and report.get("failed", 0) == 0:
        report["failed"] = 1
        report.setdefault("failures", []).append(
            "[%s] 退出码非 0（%d）但报告全通过，视为失败" % (name, completed.returncode))
    return report


def main(argv) -> int:
    if "--list" in argv:
        for name, description in SUITES:
            print("%-20s %s" % (name, description))
        return 0
    patterns = [item for item in argv[1:] if not item.startswith("-")]
    selected = [item for item in SUITES
                if not patterns or any(pattern in item[0] for pattern in patterns)]
    if not selected:
        sys.stderr.write("没有匹配的套件：%s\n" % " ".join(patterns))
        return 2

    print("=" * 78)
    print("Engineering Platform M0 —— QA 独立复核（%d 个套件）" % len(selected))
    print("=" * 78)

    reports = []
    started = time.time()
    for name, description in selected:
        sys.stdout.write("▶ %-20s %s\n" % (name, description))
        sys.stdout.flush()
        report = run_suite(name)
        reports.append(report)
        status = "PASS" if report.get("failed", 1) == 0 else "FAIL"
        print("  %s  %3d passed / %3d failed   %5d ms"
              % (status, report.get("passed", 0), report.get("failed", 0),
                 report.get("elapsedMs", 0)))
        for failure in report.get("failures") or []:
            print("    ✗ %s" % failure.replace("\n", "\n      "))

    total_passed = sum(item.get("passed", 0) for item in reports)
    total_failed = sum(item.get("failed", 0) for item in reports)
    elapsed = time.time() - started
    print("-" * 78)
    print("QA 总计：%d passed / %d failed，用时 %.1fs"
          % (total_passed, total_failed, elapsed))
    print("IS_PASS: %s" % ("YES" if total_failed == 0 else "NO"))
    return 0 if total_failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
