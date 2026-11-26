#!/usr/bin/env python3
"""tests/run_all.py —— 全部测试聚合器（零依赖，退出码即结论）。

用法：
    python tests/run_all.py              # 跑全部
    python tests/run_all.py framing      # 只跑名字含 framing 的套件
    python tests/run_all.py --list       # 列出套件

设计取舍：
    * 每个套件在**独立子进程**里跑 —— ctypes 管道句柄、线程、临时目录都不会互相污染；
    * 子进程输出 JSON 报告，父进程汇总，任一失败则退出码 1；
    * 每个套件都自带 sandbox（把 %LOCALAPPDATA% 指到临时目录），不碰真实用户数据。
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
if HERE not in sys.path:
    sys.path.insert(0, HERE)

#: 顺序：协议底座 → 宿主 → MCP → 静态审查（静态审查最后，因为它扫全仓）
SUITES = [
    ("framing", "test_framing.py", "分帧：半包/粘包/超长帧 E1001/零长度/编码规范"),
    ("handshake", "test_handshake.py", "握手：版本兼容/单会话/令牌/参数门禁"),
    ("tools", "test_tools.py", "16 个只读工具 + 写域 E2003 + 未知工具 E2001"),
    ("heartbeat", "test_heartbeat.py", "心跳判死 W1003 / 请求超时 / 断连快速失败"),
    ("budget", "test_budget.py", "paramsDigest 与三级预算截断"),
    ("store", "test_store.py", "SQLite 8 表 / 任务与结果落库 / 审计脱敏"),
    ("parity", "test_parity.py", "C#↔Python↔mockhost 契约一致性"),
    ("mcp_stdio", "test_mcp_stdio.py", "stdio 全链路：initialize→tools/list→tools/call→shutdown"),
    ("gateway_n04", "test_gateway_n04.py", "N04-B 安全网关：18 工具契约/写域/审批/错误映射/runtime 路由"),
    ("bridge_auth", "test_bridge_auth.py", "N03 桥层：Bearer 鉴权/端口隔离/映射文件（无 CAD）"),
    ("static_audit", "test_static_audit.py", "全局一致性静态审查（判据 4）"),
]

PASS = "\033[92mPASS\033[0m" if sys.stdout.isatty() else "PASS"
FAIL = "\033[91mFAIL\033[0m" if sys.stdout.isatty() else "FAIL"


def run_suite(name: str, filename: str, timeout: float = 300.0) -> dict:
    """在子进程里跑一个套件，返回其 JSON 报告。"""
    path = os.path.join(HERE, filename)
    started = time.time()
    try:
        completed = subprocess.run(
            [sys.executable, "-u", path],
            cwd=ROOT, capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return {"name": name, "passed": 0, "failed": 1,
                "failures": ["[%s] 套件超时（>%.0fs）" % (name, timeout)],
                "elapsedMs": int((time.time() - started) * 1000)}
    elapsed = int((time.time() - started) * 1000)
    stdout = completed.stdout.decode("utf-8", "replace")
    stderr = completed.stderr.decode("utf-8", "replace")

    report = None
    start = stdout.find("{")
    if start >= 0:
        try:
            report = json.loads(stdout[start:])
        except ValueError:
            report = None
    if not isinstance(report, dict):
        tail = (stdout.strip().splitlines() or [""])[-1][:400]
        detail = stderr.strip().splitlines()[-4:] if stderr.strip() else []
        return {"name": name, "passed": 0, "failed": 1,
                "failures": ["[%s] 套件未输出 JSON 报告（exit=%d）stdout尾: %s stderr尾: %s"
                             % (name, completed.returncode, tail, " | ".join(detail))],
                "elapsedMs": elapsed}
    report["elapsedMs"] = elapsed
    report.setdefault("name", name)
    if completed.returncode != 0 and report.get("failed", 0) == 0:
        report["failed"] = 1
        report.setdefault("failures", []).append(
            "[%s] 退出码非 0（%d）但报告显示全通过，视为失败"
            % (name, completed.returncode))
    return report


def main(argv) -> int:
    args = [item for item in argv[1:] if not item.startswith("-")]
    if "--list" in argv:
        for name, filename, description in SUITES:
            print("%-14s %-24s %s" % (name, filename, description))
        return 0

    selected = [item for item in SUITES
                if not args or any(pattern in item[0] for pattern in args)]
    if not selected:
        sys.stderr.write("没有匹配的套件：%s\n" % " ".join(args))
        return 2

    print("=" * 78)
    print("Engineering Platform M0 —— 测试总装（%d 个套件）" % len(selected))
    print("=" * 78)

    reports = []
    started = time.time()
    for name, filename, description in selected:
        sys.stdout.write("▶ %-14s %s\n" % (name, description))
        sys.stdout.flush()
        report = run_suite(name, filename)
        reports.append(report)
        status = PASS if report.get("failed", 1) == 0 else FAIL
        print("  %s  %3d passed / %3d failed   %5d ms"
              % (status, report.get("passed", 0), report.get("failed", 0),
                 report.get("elapsedMs", 0)))
        for failure in report.get("failures") or []:
            print("    ✗ %s" % failure.replace("\n", "\n      "))

    total_passed = sum(item.get("passed", 0) for item in reports)
    total_failed = sum(item.get("failed", 0) for item in reports)
    elapsed = time.time() - started

    print("-" * 78)
    print("总计：%d passed / %d failed，用时 %.1fs" % (total_passed, total_failed, elapsed))
    if total_failed == 0:
        print("IS_PASS: YES —— 协议、宿主替身、MCP 链路与静态审查全部通过")
        return 0
    print("IS_PASS: NO —— 先修上面的失败项再交付")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
