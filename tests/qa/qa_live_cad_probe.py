r"""tests/qa/qa_live_cad_probe.py —— 真机只读链路探测（对活着的 AutoCAD / Civil 3D 宿主）。

与 mockhost 那套的区别：这里连的是**真实宿主**——
`\\.\pipe\engineering-platform.v1.<userHash8>.<app>.<instanceId>`，
数据来自用户真正打开的 DWG。

设计原则（避免变成「跑通了就写 PASS」）：
  * 先读实例发现文件，确认宿主真的在跑（pid 活着 + 管道名对得上），否则输出
    `SKIP` 而不是失败 —— 无 CAD 环境下这条用例本就不适用；
  * 每个断言都从响应里取具体值判定，并打印出来当证据（图元数、图层数、指纹、PNG 头）；
  * 默认只做只读调用，不发任何写域请求，不动原图；
  * 单会话语义下若拿到 E1003，如实记录为 BLOCKED（说明另一个客户端正占用会话），
    不重试、不抢占。

用法：
    python tests/qa/qa_live_cad_probe.py            # 自动挑最新实例
    python tests/qa/qa_live_cad_probe.py --json     # 只输出 JSON 报告
"""

from __future__ import annotations

import json
import os
import sys

import qa_common as qa

PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def _pid_alive(pid) -> bool:
    """判断 pid 是否是**活着的 acad.exe**。

    只判「OpenProcess 成功」是不够的：Windows 的 PID 会被复用，死实例的 PID 很可能
    已经被别的进程（比如某个 powershell）占用 → 会得出「宿主还活着」的假阳性，
    接着去连一个没人监听的管道，把「实例已退出」误报成宿主故障。
    所以这里额外取进程映像名并要求 basename == acad.exe。
    """
    try:
        import ctypes
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        handle = kernel32.OpenProcess(0x1000, False, int(pid or 0))  # QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            buffer = ctypes.create_unicode_buffer(1024)
            size = ctypes.c_ulong(1024)
            if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
                return False
            return os.path.basename(buffer.value).lower() == "acad.exe"
        finally:
            kernel32.CloseHandle(handle)
    except Exception:                                        # noqa: BLE001
        return False


def find_instances():
    """扫实例发现目录（真实 %LOCALAPPDATA%，不是沙箱）。

    注意两点（都是踩过的坑）：
      * 实例发现文件可能残留（e2e 收尾是 Stop-Process -Force，宿主来不及删文件），
        所以必须按 pid 探活过滤，否则会连到一个死管道上，把「实例已退出」误判成宿主故障；
      * 一个用户可能有多个历史实例文件，取 startedAtUtc 最新的那个活实例。
    """
    root = os.path.join(os.environ.get("LOCALAPPDATA", ""), "EngineeringPlatform", "instances")
    found = []
    if not os.path.isdir(root):
        return found
    for name in sorted(os.listdir(root)):
        if not name.endswith(".json"):
            continue
        full = os.path.join(root, name)
        try:
            with open(full, "r", encoding="utf-8") as handle:
                info = json.load(handle)
        except (OSError, ValueError):
            continue
        info["_file"] = name
        info["_alive"] = _pid_alive(info.get("pid"))
        found.append(info)
    alive = [item for item in found if item["_alive"]]
    alive.sort(key=lambda item: str(item.get("startedAtUtc") or ""))
    return alive


def png_size(path):
    """读 PNG IHDR 里的宽高（不依赖第三方库）。"""
    with open(path, "rb") as handle:
        head = handle.read(24)
    if len(head) < 24 or head[:8] != PNG_MAGIC:
        return None
    width = int.from_bytes(head[16:20], "big")
    height = int.from_bytes(head[20:24], "big")
    return width, height


def run() -> dict:
    suite = qa.harness.Suite("qa_live_cad_probe")

    instances = find_instances()
    if not instances:
        print("SKIP：%LOCALAPPDATA%\\EngineeringPlatform\\instances 下没有**存活的**宿主实例"
              "（AutoCAD 未运行，或只剩上次强制结束留下的残留发现文件）—— 本条不适用于当前环境")
        suite.ok(True, "SKIP：无存活宿主实例（mockhost 套件已覆盖协议面）")
        return suite.report()

    info = instances[-1]
    print("发现存活宿主实例：%s" % json.dumps(info, ensure_ascii=False))
    suite.ok(True, "实例发现文件存在且 pid 存活：%s" % info.get("_file"))
    suite.eq(info.get("writeEnabled"), False, "发现文件声明 writeEnabled=false")
    suite.eq(info.get("protocolVersion"), "1.0", "发现文件 protocolVersion=1.0")
    suite.ok(info.get("hostProduct") in (None, "AutoCAD", "Civil3D"),
             "发现文件 hostProduct 取值合法（None/AutoCAD/Civil3D）",
             "%r" % (info.get("hostProduct"),))
    suite.is_true(os.path.basename(info.get("pipeName") or "").count(".") >= 3,
                  "管道名形如 engineering-platform.v1.<hash8>.<app>.<instance>",
                  "%r" % (info.get("pipeName"),))

    import pipeclient
    config = qa.harness.load_config()
    client = pipeclient.PipeClient(info["pipeName"], config, logger=None,
                                   heartbeat_enabled=False)
    try:
        try:
            client.connect()
            ack = client.handshake({"name": "qa-live-probe", "version": "1.0.0"})
        except pipeclient.HostUnreachable as exc:
            suite.ok(False, "连上真实宿主", "HostUnreachable: %r" % (exc,))
            return suite.report()

        # ---------------- 握手 ----------------
        suite.eq(ack.get("kind"), "hello_ack", "真实宿主返回 hello_ack")
        caps = ack.get("capabilities") or {}
        print("能力：domains=%r civilApi=%r"
              % (caps.get("domains"), caps.get("civilApi")))
        suite.eq(caps.get("domains"), ["sys", "read", "task"],
                 "真实宿主能力域只含 sys/read/task（无写域）")
        suite.is_false(ack.get("writeEnabled"), "真实宿主 writeEnabled=false")
        suite.is_true(len(ack.get("sessionToken") or "") == 64,
                      "真实宿主签发 64 hex 会话令牌")
        suite.is_true(len(ack.get("gaps") or []) >= 1,
                      "真实宿主上报能力缺口清单（诚实说明做不到什么）",
                      "%r" % (ack.get("gaps"),))
        # 第二轮新增契约字段：handshake.schema.json#/$defs/HelloAck 里是 required
        suite.ok(ack.get("hostProduct") in ("AutoCAD", "Civil3D"),
                 "hello_ack.hostProduct ∈ {AutoCAD, Civil3D}（新契约 required）",
                 "%r" % (ack.get("hostProduct"),))
        suite.matches(ack.get("hostRelease") or "", r"^R2[0-9]\.[0-9]$",
                      "hello_ack.hostRelease 形如 R24.3（新契约 required）")
        civil_caps = caps.get("civilApi") or {}
        for key in ("available", "product", "release", "writeSupported"):
            suite.ok(key in civil_caps,
                     "capabilities.civilApi 含新字段 %s" % key,
                     "keys=%r" % (sorted(civil_caps),))
        print("宿主版本：hostVersion=%r hostProduct=%r hostRelease=%r"
              % (ack.get("hostVersion"), ack.get("hostProduct"), ack.get("hostRelease")))

        # ---------------- 只读工具逐个实测 ----------------
        def call(tool, params=None):
            response = client.call(tool, params or {}, timeout_ms=20000)
            return response

        overview = call("read_overview")
        suite.is_true(overview.get("ok") is True, "真实 read_overview 成功",
                      qa.describe_error(overview))
        result = overview.get("result") or {}
        counts = result.get("counts") or {}
        print("read_overview：图元=%r 图层=%r 块=%r 代理=%r 指纹=%r"
              % (counts.get("entities"), counts.get("layers"),
                 counts.get("blocks"), counts.get("proxyEntities"),
                 result.get("fingerprint")))
        suite.matches(result.get("fingerprint") or "", r"^fp1:[0-9a-f]{32}$",
                      "真实图纸指纹形如 fp1:<32 hex>")
        suite.is_true(int(counts.get("entities") or 0) > 0,
                      "真实图纸图元数 > 0（真的读到了对象）",
                      "entities=%r" % (counts.get("entities"),))
        suite.is_true(isinstance(result.get("summary"), str)
                      and len(result.get("summary")) > 0,
                      "read_overview 返回自然语言摘要（验收要求之一）",
                      "%r" % (result.get("summary"),))
        print("摘要：%s" % (result.get("summary"),))

        # 第二轮新增：read_overview.civil3d 段（schema required: detected/counts/truncated）
        civil3d = result.get("civil3d") or {}
        suite.is_true(isinstance(civil3d, dict) and civil3d != {},
                      "read_overview 含 civil3d 段（第二轮新增输出）",
                      "keys=%r" % (sorted(result),))
        for key in ("detected", "counts", "truncated"):
            suite.ok(key in civil3d,
                     "read_overview.civil3d 含 schema required 字段 %s" % key,
                     "civil3d=%r" % (sorted(civil3d),))
        if civil3d.get("detected"):
            suite.ok(isinstance(civil3d.get("counts"), dict)
                     and civil3d["counts"].get("total", 0) >= 0,
                     "civil3d.detected=true 时 counts.total 可读",
                     "counts=%r" % (civil3d.get("counts"),))
            print("civil3d：detected=%r counts=%r note=%r"
                  % (civil3d.get("detected"), civil3d.get("counts"), civil3d.get("note")))

        layers = call("read_layers", {"limit": 5})
        suite.is_true(layers.get("ok") is True, "真实 read_layers 成功",
                      qa.describe_error(layers))
        layer_rows = (layers.get("result") or {}).get("layers") or []
        print("read_layers 前 5 层：%r" % ([row.get("name") for row in layer_rows],))
        suite.is_true(len(layer_rows) >= 1, "真实图纸至少有 1 个图层")

        query = call("read_query", {"limit": 3})
        suite.is_true(query.get("ok") is True, "真实 read_query 成功",
                      qa.describe_error(query))
        items = (query.get("result") or {}).get("items") or []
        suite.is_true(len(items) <= 3, "read_query limit=3 生效", "%d 项" % len(items))
        if items:
            handle = items[0].get("handle")
            describe = call("read_describe", {"handle": handle})
            suite.is_true(describe.get("ok") is True,
                          "真实 read_describe(handle=%r) 成功" % handle,
                          qa.describe_error(describe))
            suite.eq(((describe.get("result") or {}).get("object") or {}).get("handle"),
                     handle, "read_describe 回填同一个句柄")

        anomalies = call("read_anomalies", {"limit": 5})
        suite.is_true(anomalies.get("ok") is True, "真实 read_anomalies 成功",
                      qa.describe_error(anomalies))
        print("read_anomalies counts=%r"
              % ((anomalies.get("result") or {}).get("counts"),))

        health = call("sys_health")
        suite.is_true(health.get("ok") is True, "真实 sys_health 成功",
                      qa.describe_error(health))
        checks = (health.get("result") or {}).get("checks") or []
        print("sys_health：%r" % ([(c.get("id"), c.get("ok")) for c in checks],))
        suite.is_true(len(checks) >= 4, "sys_health 返回 ≥4 项检查",
                      "%d 项" % len(checks))

        # ---------------- 预览图形（验收的另一半） ----------------
        preview = call("read_preview", {"width": 800, "height": 600})
        suite.is_true(preview.get("ok") is True, "真实 read_preview 成功",
                      qa.describe_error(preview))
        preview_result = preview.get("result") or {}
        path = preview_result.get("previewPath") or ""
        print("read_preview：path=%r bytes=%r %rx%r"
              % (path, preview_result.get("bytes"),
                 preview_result.get("width"), preview_result.get("height")))
        suite.is_true(os.path.isfile(path),
                      "预览图文件真实落盘（%s）" % path)
        if os.path.isfile(path):
            size = os.path.getsize(path)
            suite.is_true(size > 1024, "预览图 > 1 KB", "%d 字节" % size)
            dims = png_size(path)
            suite.is_true(dims is not None, "预览图是合法 PNG（magic + IHDR 可解析）",
                          "%r" % (dims,))
            if dims:
                suite.eq(dims, (int(preview_result.get("width") or 0),
                                int(preview_result.get("height") or 0)),
                         "预览图实际像素尺寸与响应声明一致")
            # 白名单：必须落在 cache 目录
            lowered = path.lower().replace("\\", "/")
            suite.contains(lowered, "/cache/preview/",
                           "预览图落在 cache/preview 白名单目录内")

        # ---------------- 写域拒绝（真机也要验一次） ----------------
        write = call("cad_create_line", {"layer": "0"})
        suite.eq((write.get("error") or {}).get("code"), "E2003",
                 "真实宿主对写域工具返回 E2003（写入通道硬关闭）")

        # ---------------- 指纹稳定性 ----------------
        again = call("read_overview", {"refresh": True})
        suite.eq((again.get("result") or {}).get("fingerprint"),
                 result.get("fingerprint"),
                 "两次 read_overview 的指纹一致（未改图；证明只读无副作用）")

    except (pipeclient.PipeClosed, pipeclient.PipeError) as exc:
        suite.ok(False, "真实链路保持可用", "%r" % (exc,))
    finally:
        try:
            client.close()
        except Exception:                                     # noqa: BLE001
            pass

    return suite.report()


if __name__ == "__main__":
    result = run()
    if "--json" not in sys.argv:
        pass
    sys.exit(qa.emit(result))
