using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json.Nodes;

namespace EngineeringPlatform.Gateway;

internal sealed record ComCadInstance(
    int Pid,
    string Vendor,
    string ProgId,
    string Version,
    string Caption,
    string? ActiveDocumentName,
    string? ActiveDocumentFullName);

/// <summary>
/// 通用任意版本 CAD 后期绑定 COM 驱动（v3.1 多驱动架构 Driver 2）：
/// - 覆盖 AutoCAD 2010–2026 全版本 + 中望 ZWCAD + 浩辰 GstarCAD，零插件安装、零签名弹窗；
/// - 全部 COM 调用收敛在唯一专用 STA 线程 + 注册 OLE IMessageFilter（自动退避处理 RPC_E_CALL_REJECTED）；
/// - 通过 ROT + HWND→PID 实现多开实例精确去重与 UAC 权限不匹配自诊断；
/// - 对已保存图纸的大批量读取/GeoJSON 导出自动切换 ACadSharp 离线加速（避开 COM 万级图元慢的问题）。
/// </summary>
internal sealed class ComCadDriver : IDisposable
{
    private static readonly string[] KnownProgIds =
    [
        "AutoCAD.Application",
        "AutoCAD.Application.25",
        "AutoCAD.Application.24.3",
        "AutoCAD.Application.24.2",
        "AutoCAD.Application.24.1",
        "AutoCAD.Application.24",
        "AutoCAD.Application.23.1",
        "AutoCAD.Application.23",
        "AutoCAD.Application.22",
        "AutoCAD.Application.21",
        "AutoCAD.Application.20",
        "AutoCAD.Application.19.1",
        "AutoCAD.Application.19",
        "AutoCAD.Application.18",
        "ZWCAD.Application",
        "GCAD.Application",
        "GstarCAD.Application"
    ];

    private static readonly string[] KnownProcessNames = ["acad", "zwcad", "gcad", "gstarcad"];

    private readonly OfflineDwgDriver _offline;
    private readonly BlockingCollection<Action> _staQueue = new();
    private readonly Thread _staThread;
    private volatile bool _disposed;

    public ComCadDriver(OfflineDwgDriver offline)
    {
        _offline = offline;
        _staThread = new Thread(StaLoop)
        {
            IsBackground = true,
            Name = "ep-com-sta"
        };
        if (OperatingSystem.IsWindows())
        {
            _staThread.SetApartmentState(ApartmentState.STA);
        }
        _staThread.Start();
    }

    private void StaLoop()
    {
        if (OperatingSystem.IsWindows())
        {
            OleMessageFilter.Register();
        }
        try
        {
            foreach (var work in _staQueue.GetConsumingEnumerable())
            {
                try { work(); } catch { }
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                OleMessageFilter.Revoke();
            }
        }
    }

    private Task<T> RunOnStaAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ComCadDriver));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _staQueue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, ct);
        return tcs.Task;
    }

    /// <summary>枚举本机所有通过 COM/ROT 暴露的运行中 CAD 实例（按 PID 去重）。</summary>
    public Task<List<ComCadInstance>> DiscoverInstancesAsync(CancellationToken ct = default)
    {
        return RunOnStaAsync(DiscoverInstancesOnSta, ct);
    }

    public Task<JsonObject> CallToolAsync(string tool, JsonObject args, int? preferredPid = null, CancellationToken ct = default)
    {
        return RunOnStaAsync(() => ExecuteToolOnSta(tool, args, preferredPid), ct);
    }

    private List<ComCadInstance> DiscoverInstancesOnSta()
    {
        var results = new List<ComCadInstance>();
        if (!OperatingSystem.IsWindows()) return results;

        var seenPids = new HashSet<int>();
        foreach (var (app, progId) in EnumerateComAppsOnSta())
        {
            try
            {
                int pid = ResolvePid(app);
                if (pid > 0 && !seenPids.Add(pid)) continue;

                string caption = SafeGetString(app, "Caption") ?? progId;
                string version = SafeGetString(app, "Version") ?? "unknown";
                string vendor = DetectVendor(progId, caption);

                string? docName = null;
                string? docFull = null;
                object? doc = SafeGetProperty(app, "ActiveDocument");
                if (doc != null)
                {
                    docName = SafeGetString(doc, "Name");
                    docFull = SafeGetString(doc, "FullName");
                }

                results.Add(new ComCadInstance(pid, vendor, progId, version, caption, docName, docFull));
            }
            catch
            {
                // 跳过瞬时不可达 COM 句柄
            }
        }
        return results;
    }

    private JsonObject ExecuteToolOnSta(string tool, JsonObject args, int? preferredPid)
    {
        var apps = EnumerateComAppsOnSta();
        object? app = null;
        string progId = "AutoCAD.Application";

        foreach (var candidate in apps)
        {
            if (preferredPid == null || ResolvePid(candidate.App) == preferredPid.Value)
            {
                app = candidate.App;
                progId = candidate.ProgId;
                break;
            }
        }
        if (app == null && apps.Count > 0)
        {
            app = apps[0].App;
            progId = apps[0].ProgId;
        }

        if (app == null)
        {
            bool cadProcessRunning = HasRunningCadProcess(out string procList);
            if (cadProcessRunning)
            {
                throw new HostUnavailableException("C1002",
                    $"检测到 CAD 进程正在运行（{procList}），但 COM/ROT 无法挂载——通常是因为 CAD 以「管理员身份」运行而 ep-mcp 为普通权限。请以同等权限启动 ep-mcp.exe 或取消 CAD 的管理员权限。");
            }
            throw new HostUnavailableException("C1001", "未发现运行中的 AutoCAD / 中望 / 浩辰 COM 实例。");
        }

        string version = SafeGetString(app, "Version") ?? "unknown";
        string caption = SafeGetString(app, "Caption") ?? progId;
        string vendor = DetectVendor(progId, caption);
        int pid = ResolvePid(app);

        try
        {
            switch (tool)
            {
                case "sys_ping":
                    return new JsonObject
                    {
                        ["pong"] = true,
                        ["serverTimeUtc"] = DateTime.UtcNow.ToString("O"),
                        ["hostInstanceId"] = $"com-{vendor}-{pid}",
                        ["backend"] = "com_generic"
                    };

                case "sys_version":
                    return new JsonObject
                    {
                        ["summary"] = $"【版本信息 · 通用 COM】{vendor} {version}（PID {pid}）｜零插件后期绑定模式",
                        ["backend"] = "com_generic",
                        ["host"] = new JsonObject { ["assembly"] = "ep-mcp-com", ["version"] = McpServer.ServerVersion },
                        ["autocad"] = new JsonObject { ["product"] = caption, ["vendor"] = vendor, ["version"] = version, ["pid"] = pid },
                        ["protocol"] = new JsonObject { ["version"] = "1.0" },
                        ["dotnet"] = new JsonObject { ["runtime"] = Environment.Version.ToString() }
                    };

                case "sys_status":
                case "sys_health":
                    return new JsonObject
                    {
                        ["summary"] = $"【COM 驱动健康】已连接 {vendor} {version}（PID {pid}），STA 线程 + IMessageFilter 正常。",
                        ["backend"] = "com_generic",
                        ["pid"] = pid,
                        ["vendor"] = vendor,
                        ["version"] = version,
                        ["idlePumping"] = true,
                        ["checks"] = new JsonArray(
                            new JsonObject { ["id"] = "comRot", ["ok"] = true, ["detail"] = $"{progId} (PID {pid})" },
                            new JsonObject { ["id"] = "staMessageFilter", ["ok"] = true, ["detail"] = "OLE IMessageFilter active" })
                    };

                case "sys_capabilities":
                    return new JsonObject
                    {
                        ["summary"] = $"【能力与缺口 · 通用 COM】当前连接 {vendor} {version}（任意版免插件模式）；支持通用读写/LISP/GeoJSON/撤销，Civil 3D 专业曲面路线与 Roslyn 需插件驱动。",
                        ["backend"] = "com_generic",
                        ["writeEnabled"] = true,
                        ["writeAutoApprove"] = true,
                        ["domains"] = new JsonArray("sys", "read", "export", "write"),
                        ["unsupportedInCom"] = new JsonArray("export_surface_grid", "write_eval_csharp", "preview_transient", "read_civil_list", "read_civil_object")
                    };

                case "read_overview":
                    return ComReadOverview(app, vendor, version, pid);

                case "read_layers":
                    return ComReadLayers(app, args);

                case "read_query":
                    return ComReadQuery(app, args);

                case "read_describe":
                    return ComReadDescribe(app, args);

                case "read_active_selection":
                    return ComReadActiveSelection(app, args);

                case "read_viewport_snapshot":
                    return ComReadViewportSnapshot(app, vendor, version, pid, args);

                case "read_anomalies":
                    return ComReadAnomalies(app, vendor, version, pid);

                case "read_object_xdata":
                    return ComReadObjectXData(app, args);

                case "export_layer_geojson":
                    return ComExportLayerGeoJson(app, args);

                case "write_insert_points":
                    return ComWriteInsertPoints(app, args);

                case "write_command":
                    return ComWriteCommand(app, args);

                case "write_undo":
                    return ComWriteUndo(app, args);

                case "write_object_xdata":
                    return ComWriteObjectXData(app, args);

                case "write_alignment_plan":
                    return ComWriteAlignmentPlan(app, args);

                case "write_entities":
                    return ComWriteEntities(app, args);

                case "ui_read_cmdline":
                    return ComReadCmdLine(app, vendor, version, pid);

                case "write_eval_csharp":
                    throw new ComToolUnsupportedException(
                        "当前为通用 COM 驱动（免插件模式），不支持 Roslyn C# 内存编译；请改用 write_command 投递 AutoLISP 表达式实现动态逻辑，或使用 Civil 3D 插件驱动。");

                case "export_surface_grid":
                case "read_civil_list":
                case "read_civil_object":
                case "read_civil_surfaces":
                case "read_civil_alignments":
                case "write_surface_breakline":
                    throw new ComToolUnsupportedException(
                        $"工具 {tool} 需要 Civil 3D 原生 AeccDbMgd 插件（Driver 1）；当前连接的是通用 COM 驱动（{vendor} {version}）。");

                case "preview_transient":
                    throw new ComToolUnsupportedException(
                        "瞬态 3D 预览需要进程内 TransientManager 插件（Driver 1）；COM 模式下可在临时图层绘制多段线后用 write_undo 撤回。");

                default:
                    throw new ComToolUnsupportedException($"工具 {tool} 暂未在通用 COM 驱动中实现。");
            }
        }
        catch (COMException cex) when ((uint)cex.ErrorCode == 0x80010001 || (uint)cex.ErrorCode == 0x8001010A)
        {
            throw new ComBusyException($"CAD 正忙或处于交互命令/弹窗中（COM HRESULT=0x{(uint)cex.ErrorCode:X8}），请结束当前命令后重试。");
        }
    }

    private object RequireActiveDoc(object app)
    {
        object? doc = SafeGetProperty(app, "ActiveDocument");
        if (doc == null)
        {
            throw new InvalidOperationException("no_open_document");
        }
        return doc;
    }

    /// <summary>混合加速检查：若图纸已保存且磁盘文件可读，优先用 ACadSharp 毫秒级读取避免 COM 万级图元慢。</summary>
    private string? TryGetSavedFileForHybridRead(object doc)
    {
        try
        {
            bool saved = Convert.ToBoolean(SafeGetProperty(doc, "Saved") ?? false, CultureInfo.InvariantCulture);
            string? full = SafeGetString(doc, "FullName");
            if (saved && !string.IsNullOrEmpty(full) && File.Exists(full))
            {
                return full;
            }
        }
        catch { }
        return null;
    }

    private JsonObject ComReadCmdLine(object app, string vendor, string version, int pid)
    {
        object doc = RequireActiveDoc(app);
        int cmdActive = 0;
        string cmdNames = "";
        string lastPrompt = "";
        int osMode = 0;
        string clayer = "";
        try { cmdActive = Convert.ToInt32(SafeInvoke(doc, "GetVariable", "CMDACTIVE") ?? 0, CultureInfo.InvariantCulture); } catch { }
        try { cmdNames = Convert.ToString(SafeInvoke(doc, "GetVariable", "CMDNAMES"), CultureInfo.InvariantCulture) ?? ""; } catch { }
        try { lastPrompt = Convert.ToString(SafeInvoke(doc, "GetVariable", "LASTPROMPT"), CultureInfo.InvariantCulture) ?? ""; } catch { }
        try { osMode = Convert.ToInt32(SafeInvoke(doc, "GetVariable", "OSMODE") ?? 0, CultureInfo.InvariantCulture); } catch { }
        try { clayer = Convert.ToString(SafeInvoke(doc, "GetVariable", "CLAYER"), CultureInfo.InvariantCulture) ?? ""; } catch { }
        return new JsonObject
        {
            ["cmdActive"] = cmdActive,
            ["cmdNames"] = cmdNames,
            ["lastPrompt"] = lastPrompt,
            ["osMode"] = osMode,
            ["clayer"] = clayer,
            ["quiescent"] = (cmdActive == 0),
            ["backend"] = "com_generic",
            ["pid"] = pid,
            ["summary"] = $"【命令行状态 · COM】{vendor} {version} (PID {pid})｜CMDACTIVE={cmdActive}｜命令='{cmdNames}'｜当前提示='{lastPrompt}'｜OSMODE={osMode}｜CLAYER={clayer}"
        };
    }

    private JsonObject ComReadOverview(object app, string vendor, string version, int pid)
    {
        object doc = RequireActiveDoc(app);
        string? savedFile = TryGetSavedFileForHybridRead(doc);
        if (savedFile != null)
        {
            try
            {
                var fast = _offline.ReadOverview(savedFile);
                fast["backend"] = "com_generic+acadsharp_fast";
                fast["comHost"] = new JsonObject { ["vendor"] = vendor, ["version"] = version, ["pid"] = pid };
                return fast;
            }
            catch
            {
                // 混合读取失败时无缝回退 COM 实时读取
            }
        }

        string name = SafeGetString(doc, "Name") ?? "Drawing.dwg";
        string full = SafeGetString(doc, "FullName") ?? "";
        object? ms = SafeGetProperty(doc, "ModelSpace");
        object? layers = SafeGetProperty(doc, "Layers");
        object? blocks = SafeGetProperty(doc, "Blocks");
        int entCount = ms != null ? Convert.ToInt32(SafeGetProperty(ms, "Count") ?? 0) : 0;
        int lyrCount = layers != null ? Convert.ToInt32(SafeGetProperty(layers, "Count") ?? 0) : 0;
        int blkCount = blocks != null ? Convert.ToInt32(SafeGetProperty(blocks, "Count") ?? 0) : 0;
        int insUnits = Convert.ToInt32(SafeInvoke(doc, "GetVariable", "INSUNITS") ?? 0);

        return new JsonObject
        {
            ["summary"] = $"【图纸总览 · 通用 COM】{vendor} {version}（PID {pid}）｜{name}｜图元 {entCount}｜图层 {lyrCount}｜块 {blkCount}",
            ["backend"] = "com_generic",
            ["software"] = new JsonObject { ["name"] = vendor, ["version"] = version, ["civilApiAvailable"] = false },
            ["drawing"] = new JsonObject
            {
                ["nameHash"] = CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(name)).Substring(0, 8) + ".dwg",
                ["name"] = name,
                ["fullPath"] = full,
                ["readOnly"] = Convert.ToBoolean(SafeGetProperty(doc, "ReadOnly") ?? false)
            },
            ["units"] = OfflineDwgDriver.UnitsNode(insUnits),
            ["counts"] = new JsonObject
            {
                ["entities"] = entCount,
                ["layers"] = lyrCount,
                ["blocks"] = blkCount,
                ["proxyEntities"] = 0
            }
        };
    }

    private JsonObject ComReadLayers(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        string? pattern = args["namePattern"]?.GetValue<string>();
        int limit = args["limit"]?.GetValue<int>() ?? 200;

        string? savedFile = TryGetSavedFileForHybridRead(doc);
        if (savedFile != null)
        {
            try
            {
                var fast = _offline.ReadLayers(savedFile, pattern, limit);
                fast["backend"] = "com_generic+acadsharp_fast";
                return fast;
            }
            catch { }
        }

        object? layers = SafeGetProperty(doc, "Layers");
        var arr = new JsonArray();
        int total = 0;
        if (layers is IEnumerable en)
        {
            foreach (var lyr in en)
            {
                if (lyr == null) continue;
                string name = SafeGetString(lyr, "Name") ?? "";
                if (!string.IsNullOrEmpty(pattern) && !OfflineDwgDriver.WildcardMatch(pattern, name)) continue;
                total++;
                if (arr.Count >= Math.Clamp(limit, 1, 500)) continue;
                arr.Add(new JsonObject
                {
                    ["name"] = name,
                    ["isOn"] = Convert.ToBoolean(SafeGetProperty(lyr, "LayerOn") ?? true),
                    ["isFrozen"] = Convert.ToBoolean(SafeGetProperty(lyr, "Freeze") ?? false),
                    ["isLocked"] = Convert.ToBoolean(SafeGetProperty(lyr, "Lock") ?? false)
                });
            }
        }
        return new JsonObject
        {
            ["summary"] = $"【图层列表 · 通用 COM】共 {total} 个图层，返回 {arr.Count} 个。",
            ["backend"] = "com_generic",
            ["total"] = total,
            ["layers"] = arr,
            ["truncated"] = total > arr.Count
        };
    }

    private JsonObject ComReadQuery(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        string? layerFilter = args["layer"]?.GetValue<string>();
        string? dxfFilter = args["dxfName"]?.GetValue<string>();
        int limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 20, 1, 200);

        object? ms = SafeGetProperty(doc, "ModelSpace");
        var items = new JsonArray();
        int matched = 0;
        int scanned = 0;
        const int comScanCap = 8000;

        if (ms is IEnumerable en)
        {
            foreach (var ent in en)
            {
                if (ent == null) continue;
                scanned++;
                if (scanned > comScanCap && items.Count >= limit) break;

                string lyr = SafeGetString(ent, "Layer") ?? "0";
                if (!string.IsNullOrEmpty(layerFilter) && !OfflineDwgDriver.WildcardMatch(layerFilter, lyr)) continue;
                string objName = SafeGetString(ent, "ObjectName") ?? "";
                if (!string.IsNullOrEmpty(dxfFilter) && !objName.Contains(dxfFilter, StringComparison.OrdinalIgnoreCase)) continue;

                matched++;
                if (items.Count < limit)
                {
                    items.Add(new JsonObject
                    {
                        ["handle"] = SafeGetString(ent, "Handle") ?? "",
                        ["dxfName"] = objName,
                        ["layer"] = lyr
                    });
                }
            }
        }
        return new JsonObject
        {
            ["summary"] = $"【对象查询 · 通用 COM】命中 {matched}（扫描 {scanned}），返回 {items.Count} 条。",
            ["backend"] = "com_generic",
            ["total"] = matched,
            ["items"] = items,
            ["truncated"] = matched > items.Count
        };
    }

    private JsonObject ComReadDescribe(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        string handle = (args["handle"]?.GetValue<string>() ?? "").Trim();
        if (string.IsNullOrEmpty(handle)) throw new ArgumentException("handle_required");

        object? ent = SafeInvoke(doc, "HandleToObject", handle);
        if (ent == null) throw new InvalidOperationException("handle_not_found:" + handle);

        var obj = new JsonObject
        {
            ["handle"] = SafeGetString(ent, "Handle") ?? handle,
            ["objectName"] = SafeGetString(ent, "ObjectName") ?? "",
            ["layer"] = SafeGetString(ent, "Layer") ?? "0"
        };
        string? txt = SafeGetString(ent, "TextString");
        if (txt != null) obj["text"] = txt;
        object? len = SafeGetProperty(ent, "Length");
        if (len != null) obj["length"] = Convert.ToDouble(len, CultureInfo.InvariantCulture);
        object? area = SafeGetProperty(ent, "Area");
        if (area != null) obj["area"] = Convert.ToDouble(area, CultureInfo.InvariantCulture);

        return new JsonObject
        {
            ["summary"] = $"【对象详情 · 通用 COM】句柄 {handle}｜{obj["objectName"]}｜图层 {obj["layer"]}",
            ["backend"] = "com_generic",
            ["object"] = obj
        };
    }

    private JsonObject ComReadActiveSelection(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        int limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 200, 1, 2000);
        object? pss = SafeGetProperty(doc, "PickfirstSelectionSet");
        var items = new JsonArray();
        int total = 0;
        if (pss is IEnumerable en)
        {
            foreach (var ent in en)
            {
                if (ent == null) continue;
                total++;
                if (items.Count >= limit) continue;
                items.Add(new JsonObject
                {
                    ["handle"] = SafeGetString(ent, "Handle") ?? "",
                    ["dxfName"] = SafeGetString(ent, "ObjectName") ?? "",
                    ["layer"] = SafeGetString(ent, "Layer") ?? "0"
                });
            }
        }
        return new JsonObject
        {
            ["summary"] = $"【实时选区 · 通用 COM】当前选中 {total} 个实体，返回 {items.Count} 条。",
            ["backend"] = "com_generic",
            ["count"] = total,
            ["returned"] = items.Count,
            ["items"] = items
        };
    }

    private JsonObject ComReadObjectXData(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        string handle = (args["handle"]?.GetValue<string>() ?? "").Trim();
        object? ent = SafeInvoke(doc, "HandleToObject", handle);
        if (ent == null) throw new InvalidOperationException("handle_not_found:" + handle);

        object?[] callArgs = ["", null, null];
        ParameterModifier pm = new(3);
        pm[1] = true;
        pm[2] = true;
        ent.GetType().InvokeMember("GetXData", BindingFlags.InvokeMethod, null, ent, callArgs, [pm], CultureInfo.InvariantCulture, null);

        var xdata = new JsonObject();
        if (callArgs[1] is Array types && callArgs[2] is Array vals)
        {
            string curApp = "_default";
            JsonArray? curList = null;
            for (int i = 0; i < Math.Min(types.Length, vals.Length); i++)
            {
                short code = Convert.ToInt16(types.GetValue(i), CultureInfo.InvariantCulture);
                object? v = vals.GetValue(i);
                if (code == 1001)
                {
                    curApp = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "_default";
                    curList = new JsonArray();
                    xdata[curApp] = curList;
                    continue;
                }
                curList ??= (xdata[curApp] = new JsonArray()).AsArray();
                curList.Add(new JsonObject { ["code"] = code, ["value"] = Convert.ToString(v, CultureInfo.InvariantCulture) });
            }
        }

        return new JsonObject
        {
            ["summary"] = $"【对象元数据 · 通用 COM】句柄 {handle}｜XData 应用 {xdata.Count} 组。",
            ["backend"] = "com_generic",
            ["handle"] = handle.ToUpperInvariant(),
            ["xdata"] = xdata,
            ["xrecords"] = new JsonObject()
        };
    }

    private JsonObject ComExportLayerGeoJson(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        var layers = (args["layers"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s != "").ToList();
        Dictionary<string, string>? catMap = null;
        if (args["layerCategoryMap"] is JsonObject cm)
        {
            catMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in cm)
            {
                if (kv.Value != null) catMap[kv.Key] = kv.Value.GetValue<string>();
            }
        }
        int limit = args["limit"]?.GetValue<int>() ?? 20000;
        string? outPath = args["outPath"]?.GetValue<string>();

        // 优先走 ACadSharp 磁盘直读（混合加速，避免万级图元 COM 超慢）
        string? savedFile = TryGetSavedFileForHybridRead(doc);
        if (savedFile != null)
        {
            try
            {
                var fast = _offline.ExportLayerGeoJson(savedFile, layers, catMap, limit, outPath);
                fast["backend"] = "com_generic+acadsharp_fast";
                return fast;
            }
            catch { }
        }

        // 未保存图纸：COM 实时导出核心实体（LwPolyline/Line/Circle/Point/BlockRef）
        if (string.IsNullOrWhiteSpace(outPath))
        {
            string ws = Path.Combine(Path.GetTempPath(), "ep_workspace");
            Directory.CreateDirectory(ws);
            outPath = Path.Combine(ws, "constraints.geojson");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        }

        var sw = Stopwatch.StartNew();
        object? ms = SafeGetProperty(doc, "ModelSpace");
        var features = new JsonArray();
        int scanned = 0;

        if (ms is IEnumerable en)
        {
            foreach (var ent in en)
            {
                if (ent == null) continue;
                scanned++;
                if (features.Count >= Math.Clamp(limit, 1, 20000)) break;

                string lyr = SafeGetString(ent, "Layer") ?? "0";
                if (layers != null && layers.Count > 0 && !layers.Any(pat => OfflineDwgDriver.WildcardMatch(pat, lyr))) continue;

                var geom = ComEntityToGeoJson(ent);
                if (geom == null) continue;
                string handle = SafeGetString(ent, "Handle") ?? scanned.ToString(CultureInfo.InvariantCulture);
                string category = OfflineDwgDriver.ResolveCategory(lyr, catMap);

                features.Add(new JsonObject
                {
                    ["type"] = "Feature",
                    ["properties"] = new JsonObject
                    {
                        ["handle"] = handle,
                        ["layer"] = lyr,
                        ["dxfName"] = SafeGetString(ent, "ObjectName") ?? "",
                        ["category"] = category,
                        ["name"] = $"{lyr}_{handle}"
                    },
                    ["geometry"] = geom
                });
            }
        }

        var fc = new JsonObject { ["type"] = "FeatureCollection", ["features"] = features };
        File.WriteAllText(outPath, CanonicalJson.SerializeCompact(fc), new UTF8Encoding(false));
        sw.Stop();

        return new JsonObject
        {
            ["summary"] = $"【图层 GeoJSON 导出 · 通用 COM】导出 {features.Count} 要素（扫描 {scanned}）→ {outPath}｜耗时 {sw.ElapsedMilliseconds}ms",
            ["backend"] = "com_generic",
            ["outPath"] = outPath,
            ["featureCount"] = features.Count,
            ["scannedEntities"] = scanned,
            ["fileBytes"] = new FileInfo(outPath).Length,
            ["elapsedMs"] = sw.ElapsedMilliseconds
        };
    }

    private JsonObject ComWriteInsertPoints(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        var pts = args["points"] as JsonArray;
        if (pts == null || pts.Count == 0) throw new ArgumentException("points_required");
        string layer = args["layer"]?.GetValue<string>() ?? "EP-STAKEOUT";

        EnsureComLayer(doc, layer);
        object ms = SafeGetProperty(doc, "ModelSpace")!;
        SafeInvoke(doc, "StartUndoMark");
        var handles = new JsonArray();
        try
        {
            foreach (var item in pts)
            {
                if (item is not JsonObject po) continue;
                double x = po["e"]?.GetValue<double>() ?? po["x"]?.GetValue<double>() ?? 0;
                double y = po["n"]?.GetValue<double>() ?? po["y"]?.GetValue<double>() ?? 0;
                double z = po["z"]?.GetValue<double>() ?? 0;
                object? ptObj = SafeInvoke(ms, "AddPoint", new double[] { x, y, z });
                if (ptObj != null)
                {
                    SafeSetProperty(ptObj, "Layer", layer);
                    if (handles.Count < 200) handles.Add(SafeGetString(ptObj, "Handle") ?? "");
                }
            }
        }
        finally
        {
            SafeInvoke(doc, "EndUndoMark");
        }

        return new JsonObject
        {
            ["summary"] = $"【放点 · 通用 COM】已新增 {pts.Count} 个点位到图层 {layer}（支持 write_undo 撤销）。",
            ["backend"] = "com_generic",
            ["created"] = pts.Count,
            ["layer"] = layer,
            ["handles"] = handles,
            ["saved"] = false
        };
    }

    private JsonObject ComWriteCommand(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        string cmd = (args["command"]?.GetValue<string>() ?? "").Trim();
        if (string.IsNullOrEmpty(cmd)) throw new ArgumentException("command_required");
        bool wrapUndo = args["wrapUndoGroup"]?.GetValue<bool>() ?? true;

        WaitUntilComQuiescent(doc, 1500);

        string toSend = cmd.StartsWith('(')
            ? (wrapUndo
                ? $"(progn (command \"_.UNDO\" \"_Begin\") {cmd} (command \"_.UNDO\" \"_End\") (princ))\n"
                : $"(progn {cmd} (princ))\n")
            : cmd + "\n";

        SafeInvoke(doc, "SendCommand", toSend);
        return new JsonObject
        {
            ["summary"] = $"【命令执行 · 通用 COM】已投递命令：{cmd}",
            ["backend"] = "com_generic",
            ["queued"] = true,
            ["command"] = cmd,
            ["saved"] = false
        };
    }

    private JsonObject ComWriteUndo(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        int steps = Math.Clamp(args["steps"]?.GetValue<int>() ?? 1, 1, 50);
        WaitUntilComQuiescent(doc, 1500);
        SafeInvoke(doc, "SendCommand", $"_.UNDO {steps}\n");
        return new JsonObject
        {
            ["summary"] = $"【撤销 · 通用 COM】已提交撤销最近 {steps} 步操作。",
            ["backend"] = "com_generic",
            ["queued"] = true,
            ["undoneSteps"] = steps
        };
    }

    private JsonObject ComWriteObjectXData(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        string handle = (args["handle"]?.GetValue<string>() ?? "").Trim();
        string regApp = args["regApp"]?.GetValue<string>() ?? "EP_META";
        var entries = args["entries"] as JsonObject;
        if (entries == null || entries.Count == 0) throw new ArgumentException("entries_required");

        object? ent = SafeInvoke(doc, "HandleToObject", handle);
        if (ent == null) throw new InvalidOperationException("handle_not_found:" + handle);

        // 先确保 RegApp 已注册到图纸 RegisteredApplications 表，避免新图纸 SetXData 报 Key not found
        EnsureComRegApp(doc, regApp);

        var types = new List<short> { 1001 };
        var values = new List<object> { regApp };
        foreach (var kv in entries)
        {
            types.Add(1000);
            values.Add($"{kv.Key}={kv.Value}");
        }
        SafeInvoke(ent, "SetXData", types.ToArray(), values.ToArray());

        return new JsonObject
        {
            ["summary"] = $"【元数据写入 · 通用 COM】已向句柄 {handle} 写入 {entries.Count} 条 XData（RegApp={regApp}）。",
            ["backend"] = "com_generic",
            ["handle"] = handle.ToUpperInvariant(),
            ["regApp"] = regApp,
            ["writtenEntries"] = entries.Count
        };
    }

    private static void WaitUntilComQuiescent(object doc, int maxWaitMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            try
            {
                object? v = SafeInvoke(doc, "GetVariable", "CMDACTIVE");
                int active = v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
                if (active == 0) return;
            }
            catch
            {
                return;
            }
            Thread.Sleep(100);
        }
        throw new ComBusyException("CAD 当前存在未结束的交互命令（CMDACTIVE!=0），请稍候或结束当前命令后重试。");
    }

    private static void EnsureComRegApp(object doc, string regApp)
    {
        try
        {
            object? regApps = SafeGetProperty(doc, "RegisteredApplications");
            if (regApps != null) SafeInvoke(regApps, "Add", regApp);
        }
        catch { }
    }

    private JsonObject ComReadAnomalies(object app, string vendor, string version, int pid)
    {
        object doc = RequireActiveDoc(app);
        string? fullName = SafeGetString(doc, "FullName");
        bool saved = SafeGetProperty(doc, "Saved") is bool sb && sb;
        if (saved && !string.IsNullOrEmpty(fullName) && File.Exists(fullName))
        {
            var ov = _offline.ReadOverview(fullName);
            var cnt = ov["counts"] as JsonObject;
            int emptyLayers = cnt?["emptyLayers"]?.GetValue<int>() ?? 0;
            int proxyCount = cnt?["proxyEntities"]?.GetValue<int>() ?? 0;
            var items = new JsonArray();
            if (emptyLayers > 0)
            {
                items.Add(new JsonObject { ["kind"] = "empty_layers", ["severity"] = "info", ["count"] = emptyLayers, ["detail"] = $"存在 {emptyLayers} 个空图层" });
            }
            if (proxyCount > 0)
            {
                items.Add(new JsonObject { ["kind"] = "proxy_entities", ["severity"] = "warning", ["count"] = proxyCount, ["detail"] = $"存在 {proxyCount} 个专业代理对象" });
            }
            return new JsonObject
            {
                ["summary"] = $"【图面体检 · 通用 COM+ACadSharp】{vendor} {version}（PID {pid}）检出 {items.Count} 类提示。",
                ["backend"] = "com_generic",
                ["anomalies"] = items
            };
        }

        object? layers = SafeGetProperty(doc, "Layers");
        int layerCount = layers != null ? Convert.ToInt32(SafeGetProperty(layers, "Count") ?? 0, CultureInfo.InvariantCulture) : 0;
        return new JsonObject
        {
            ["summary"] = $"【图面体检 · 通用 COM】{vendor} {version}（PID {pid}），图层总数 {layerCount}（未保存图纸跳过深度扫描）。",
            ["backend"] = "com_generic",
            ["anomalies"] = new JsonArray()
        };
    }

    private JsonObject ComReadViewportSnapshot(object app, string vendor, string version, int pid, JsonObject args)
    {
        object? hwndObj = SafeGetProperty(app, "HWND");
        IntPtr hwnd = hwndObj != null ? new IntPtr(Convert.ToInt64(hwndObj, CultureInfo.InvariantCulture)) : IntPtr.Zero;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("com_window_handle_unavailable");

        if (!GetWindowRect(hwnd, out RECT rc)) throw new InvalidOperationException("get_window_rect_failed");
        int w = Math.Max(1, rc.Right - rc.Left);
        int h = Math.Max(1, rc.Bottom - rc.Top);

        string outPath = args["outPath"]?.GetValue<string>()
            ?? Path.Combine(Path.GetTempPath(), $"ep_com_snap_{pid}_{DateTime.UtcNow:yyyyMMddHHmmss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        byte[] bgra = CaptureWindowBgra(hwnd, w, h);
        WritePngFromBgra(outPath, bgra, w, h);

        long size = new FileInfo(outPath).Length;
        return new JsonObject
        {
            ["summary"] = $"【视口截图 · 通用 COM】已捕获 {vendor} {version}（PID {pid}）窗口 {w}x{h} → {outPath}（{size} B）",
            ["backend"] = "com_generic",
            ["imagePath"] = outPath,
            ["width"] = w,
            ["height"] = h,
            ["fileBytes"] = size
        };
    }

    private static byte[] CaptureWindowBgra(IntPtr hwnd, int width, int height)
    {
        IntPtr hdcSrc = GetWindowDC(hwnd);
        IntPtr hdcMem = CreateCompatibleDC(hdcSrc);
        IntPtr hBmp = CreateCompatibleBitmap(hdcSrc, width, height);
        IntPtr hOld = SelectObject(hdcMem, hBmp);
        try
        {
            bool ok = PrintWindow(hwnd, hdcMem, 2 /* PW_RENDERFULLCONTENT */);
            if (!ok) ok = PrintWindow(hwnd, hdcMem, 0);
            if (!ok) BitBlt(hdcMem, 0, 0, width, height, hdcSrc, 0, 0, 0x00CC0020 /* SRCCOPY */);

            var bmi = new BITMAPINFO
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFO>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };
            byte[] buf = new byte[width * height * 4];
            GetDIBits(hdcMem, hBmp, 0, (uint)height, buf, ref bmi, 0);
            return buf;
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBmp);
            DeleteDC(hdcMem);
            ReleaseDC(hwnd, hdcSrc);
        }
    }

    private static void WritePngFromBgra(string path, byte[] bgra, int width, int height)
    {
        // 纯托管无依赖 PNG 编码器（RGB24 + ZLibStream）
        using var fs = File.Create(path);
        fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        byte[] ihdr = new byte[13];
        WriteBe32(ihdr, 0, width);
        WriteBe32(ihdr, 4, height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // color type: RGB
        WritePngChunk(fs, "IHDR", ihdr);

        using var idatMs = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(idatMs, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[1 + width * 3];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // filter: None
                int srcRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
                    int d = 1 + x * 3;
                    row[d] = bgra[s + 2];     // R
                    row[d + 1] = bgra[s + 1]; // G
                    row[d + 2] = bgra[s];     // B
                }
                zlib.Write(row, 0, row.Length);
            }
        }
        WritePngChunk(fs, "IDAT", idatMs.ToArray());
        WritePngChunk(fs, "IEND", []);
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        byte[] lenBytes = new byte[4];
        WriteBe32(lenBytes, 0, data.Length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(lenBytes);
        s.Write(typeBytes);
        if (data.Length > 0) s.Write(data);

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, typeBytes);
        crc = UpdateCrc32(crc, data);
        byte[] crcBytes = new byte[4];
        WriteBe32(crcBytes, 0, (int)(crc ^ 0xFFFFFFFFu));
        s.Write(crcBytes);
    }

    private static uint UpdateCrc32(uint crc, byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
        }
        return crc;
    }

    private static void WriteBe32(byte[] buf, int offset, int val)
    {
        buf[offset] = (byte)((val >> 24) & 0xFF);
        buf[offset + 1] = (byte)((val >> 16) & 0xFF);
        buf[offset + 2] = (byte)((val >> 8) & 0xFF);
        buf[offset + 3] = (byte)(val & 0xFF);
    }

    private JsonObject ComWriteAlignmentPlan(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        JsonObject planData = args;
        string? planPath = args["planPath"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(planPath) && File.Exists(planPath))
        {
            planData = JsonNode.Parse(File.ReadAllText(planPath, Encoding.UTF8))?.AsObject() ?? args;
        }
        string name = args["name"]?.GetValue<string>() ?? $"EP_Route_Plan_{planData["plan_id"]?.GetValue<int>() ?? 1}";
        string layer = args["layer"]?.GetValue<string>() ?? "EP-ALIGNMENT";

        var ptsNode = planData["sampled_centerline"] as JsonArray
            ?? planData["sampledCenterline"] as JsonArray
            ?? planData["points"] as JsonArray
            ?? (planData["alignment"] as JsonObject)?["jds"] as JsonArray;
        if (ptsNode == null || ptsNode.Count < 2) throw new ArgumentException("alignment_needs_at_least_two_points");

        var flat = new List<double>(ptsNode.Count * 2);
        double totalLen = 0;
        double px = 0, py = 0;
        for (int i = 0; i < ptsNode.Count; i++)
        {
            if (ptsNode[i] is not JsonObject p) continue;
            double x = p["x"]?.GetValue<double>() ?? p["e"]?.GetValue<double>() ?? 0;
            double y = p["y"]?.GetValue<double>() ?? p["n"]?.GetValue<double>() ?? 0;
            if (flat.Count >= 2)
            {
                double dx = x - px, dy = y - py;
                totalLen += Math.Sqrt(dx * dx + dy * dy);
            }
            flat.Add(x);
            flat.Add(y);
            px = x;
            py = y;
        }

        EnsureComLayer(doc, layer);
        object ms = SafeGetProperty(doc, "ModelSpace")!;
        SafeInvoke(doc, "StartUndoMark");
        string handle = "";
        try
        {
            object? pl = SafeInvoke(ms, "AddLightWeightPolyline", flat.ToArray());
            if (pl != null)
            {
                SafeSetProperty(pl, "Layer", layer);
                handle = SafeGetString(pl, "Handle") ?? "";
            }
        }
        finally
        {
            SafeInvoke(doc, "EndUndoMark");
        }

        return new JsonObject
        {
            ["summary"] = $"【路线落盘 · 通用 COM】已在普通 CAD 创建路线多段线「{name}」（长 {totalLen:0.##}m，句柄 {handle}；原生 Civil3D Alignment 需 Civil 3D 插件）。",
            ["backend"] = "com_generic",
            ["name"] = name,
            ["layer"] = layer,
            ["pointCount"] = flat.Count / 2,
            ["length"] = Math.Round(totalLen, 3),
            ["polylineHandle"] = handle,
            ["nativeCivilCreated"] = false
        };
    }

    private JsonObject ComWriteEntities(object app, JsonObject args)
    {
        object doc = RequireActiveDoc(app);
        var entities = args["entities"] as JsonArray;
        if (entities == null || entities.Count == 0) throw new ArgumentException("entities_required");
        if (entities.Count > 5000) throw new ArgumentException("entities_exceed_limit:5000");

        string defaultLayer = args["layer"]?.GetValue<string>() ?? "0";
        if (string.IsNullOrWhiteSpace(defaultLayer)) defaultLayer = "0";
        bool strict = args["strict"]?.GetValue<bool>() == true;
        bool zoomExtents = args["zoomExtents"]?.GetValue<bool>() == true;

        object ms = SafeGetProperty(doc, "ModelSpace")!;
        var ensuredLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdObjects = new List<object>();
        var handles = new JsonArray();
        var failedItems = new JsonArray();
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        int createdCount = 0;

        SafeInvoke(doc, "StartUndoMark");
        try
        {
            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i] is not JsonObject spec)
                {
                    if (strict) throw new InvalidOperationException($"write_entities_strict_failed:index={i}:entity_must_be_object");
                    failedItems.Add(new JsonObject { ["index"] = i, ["type"] = "unknown", ["error"] = "entity_must_be_object" });
                    continue;
                }

                string rawType = (spec["type"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                try
                {
                    var (ent, canonicalType) = CreateComEntity(ms, spec, rawType);
                    if (ent == null)
                    {
                        throw new InvalidOperationException("com_entity_creation_returned_null");
                    }

                    string lyr = spec["layer"]?.GetValue<string>() ?? defaultLayer;
                    if (string.IsNullOrWhiteSpace(lyr)) lyr = defaultLayer;
                    if (ensuredLayers.Add(lyr)) EnsureComLayer(doc, lyr);
                    SafeSetProperty(ent, "Layer", lyr);

                    if (TryJsonDouble(spec["color"], out double colorVal))
                    {
                        int ci = (int)Math.Round(colorVal);
                        if (ci >= 1 && ci <= 255)
                        {
                            try { SafeSetProperty(ent, "Color", ci); } catch { }
                        }
                    }

                    createdObjects.Add(ent);
                    createdCount++;
                    byType[canonicalType] = byType.GetValueOrDefault(canonicalType) + 1;
                    if (handles.Count < 500)
                    {
                        handles.Add(SafeGetString(ent, "Handle") ?? "");
                    }
                }
                catch (Exception ex)
                {
                    if (strict)
                    {
                        foreach (var obj in createdObjects)
                        {
                            try { SafeInvoke(obj, "Delete"); } catch { }
                        }
                        throw new InvalidOperationException($"write_entities_strict_failed:index={i}:{ex.Message}");
                    }
                    failedItems.Add(new JsonObject
                    {
                        ["index"] = i,
                        ["type"] = string.IsNullOrEmpty(rawType) ? "unknown" : rawType,
                        ["error"] = ex.Message
                    });
                }
            }
        }
        finally
        {
            SafeInvoke(doc, "EndUndoMark");
        }

        if (zoomExtents && createdCount > 0)
        {
            try { SafeInvoke(app, "ZoomExtents"); } catch { }
        }

        var byTypeNode = new JsonObject();
        foreach (var kv in byType) byTypeNode[kv.Key] = kv.Value;

        return new JsonObject
        {
            ["summary"] = $"【原生批量建实体 · 通用 COM】请求 {entities.Count}，成功 {createdCount}，失败 {failedItems.Count}（默认图层 {defaultLayer}，支持 write_undo 撤销）。",
            ["backend"] = "com_generic",
            ["requestedCount"] = entities.Count,
            ["createdCount"] = createdCount,
            ["failedCount"] = failedItems.Count,
            ["failedItems"] = failedItems,
            ["layer"] = defaultLayer,
            ["handles"] = handles,
            ["handlesTruncated"] = createdCount > handles.Count,
            ["byType"] = byTypeNode,
            ["saved"] = false
        };
    }

    private static (object? Entity, string CanonicalType) CreateComEntity(object ms, JsonObject spec, string rawType)
    {
        if (string.IsNullOrEmpty(rawType)) throw new ArgumentException("type_required");

        switch (rawType)
        {
            case "line":
            {
                if (!TryJsonPoint3(spec, out double[] p1, "start", "p1", "from"))
                {
                    if (TryJsonDouble(spec["x1"], out double x1) && TryJsonDouble(spec["y1"], out double y1))
                        p1 = [x1, y1, TryJsonDouble(spec["z1"], out double z1) ? z1 : 0.0];
                    else throw new ArgumentException("line_requires_start_and_end");
                }
                if (!TryJsonPoint3(spec, out double[] p2, "end", "p2", "to"))
                {
                    if (TryJsonDouble(spec["x2"], out double x2) && TryJsonDouble(spec["y2"], out double y2))
                        p2 = [x2, y2, TryJsonDouble(spec["z2"], out double z2) ? z2 : 0.0];
                    else throw new ArgumentException("line_requires_start_and_end");
                }
                return (SafeInvoke(ms, "AddLine", p1, p2), "line");
            }
            case "polyline":
            case "lwpolyline":
            case "pline":
            {
                var pts = spec["points"] as JsonArray ?? spec["vertices"] as JsonArray;
                if (pts == null || pts.Count < 2) throw new ArgumentException("polyline_requires_at_least_two_points");
                var flat = new List<double>(pts.Count * 2);
                var bulges = new List<(int Index, double Bulge)>();
                for (int k = 0; k < pts.Count; k++)
                {
                    if (!TryJsonVertex2(pts[k], out double vx, out double vy, out double bulge))
                        throw new ArgumentException($"invalid_polyline_vertex:{k}");
                    flat.Add(vx);
                    flat.Add(vy);
                    if (Math.Abs(bulge) > 1e-9) bulges.Add((k, bulge));
                }
                object? pl = SafeInvoke(ms, "AddLightWeightPolyline", flat.ToArray());
                if (pl != null)
                {
                    if (spec["closed"]?.GetValue<bool>() == true) SafeSetProperty(pl, "Closed", true);
                    if (TryJsonDouble(spec["elevation"], out double elev) && Math.Abs(elev) > 1e-12) SafeSetProperty(pl, "Elevation", elev);
                    if (TryJsonDouble(spec["width"], out double w) && w > 0) SafeSetProperty(pl, "ConstantWidth", w);
                    foreach (var (idx, b) in bulges)
                    {
                        try { SafeInvoke(pl, "SetBulge", idx, b); } catch { }
                    }
                }
                return (pl, "polyline");
            }
            case "rectangle":
            case "rect":
            {
                double x1, y1, x2, y2;
                double elev = TryJsonDouble(spec["elevation"], out double ev) ? ev : 0.0;
                if (TryJsonPoint3(spec, out double[] a, "p1", "min", "start") && TryJsonPoint3(spec, out double[] b, "p2", "max", "end"))
                {
                    x1 = a[0]; y1 = a[1]; x2 = b[0]; y2 = b[1];
                    if (Math.Abs(elev) < 1e-12) elev = a[2];
                }
                else if (TryJsonDouble(spec["width"], out double w) && TryJsonDouble(spec["height"], out double h) && w > 0 && h > 0)
                {
                    if (TryJsonPoint3(spec, out double[] org, "origin", "p1", "min"))
                    {
                        x1 = org[0]; y1 = org[1]; x2 = x1 + w; y2 = y1 + h;
                        if (Math.Abs(elev) < 1e-12) elev = org[2];
                    }
                    else if (TryJsonPoint3(spec, out double[] ctr, "center", "at"))
                    {
                        x1 = ctr[0] - w * 0.5; y1 = ctr[1] - h * 0.5; x2 = ctr[0] + w * 0.5; y2 = ctr[1] + h * 0.5;
                        if (Math.Abs(elev) < 1e-12) elev = ctr[2];
                    }
                    else if (TryJsonDouble(spec["x"], out x1) && TryJsonDouble(spec["y"], out y1))
                    {
                        x2 = x1 + w; y2 = y1 + h;
                    }
                    else throw new ArgumentException("rectangle_requires_origin_or_center");
                }
                else throw new ArgumentException("rectangle_requires_p1_p2_or_origin_width_height");

                if (Math.Abs(x2 - x1) < 1e-9 || Math.Abs(y2 - y1) < 1e-9) throw new ArgumentException("rectangle_degenerate");
                double[] coords = [x1, y1, x2, y1, x2, y2, x1, y2];
                object? pl = SafeInvoke(ms, "AddLightWeightPolyline", coords);
                if (pl != null)
                {
                    SafeSetProperty(pl, "Closed", true);
                    if (Math.Abs(elev) > 1e-12) SafeSetProperty(pl, "Elevation", elev);
                }
                return (pl, "rectangle");
            }
            case "circle":
            {
                if (!TryJsonPoint3(spec, out double[] center, "center", "at") && !TryJsonInlinePoint3(spec, out center))
                    throw new ArgumentException("circle_requires_center");
                if (!(TryJsonDouble(spec["radius"], out double r) || TryJsonDouble(spec["r"], out r)) || r <= 1e-9)
                    throw new ArgumentException("circle_requires_positive_radius");
                return (SafeInvoke(ms, "AddCircle", center, r), "circle");
            }
            case "arc":
            {
                if (!TryJsonPoint3(spec, out double[] center, "center", "at") && !TryJsonInlinePoint3(spec, out center))
                    throw new ArgumentException("arc_requires_center");
                if (!(TryJsonDouble(spec["radius"], out double r) || TryJsonDouble(spec["r"], out r)) || r <= 1e-9)
                    throw new ArgumentException("arc_requires_positive_radius");
                if (!TryJsonAngleRad(spec, "startAngle_deg", "startAngle", "startAngle_rad", out double sRad) ||
                    !TryJsonAngleRad(spec, "endAngle_deg", "endAngle", "endAngle_rad", out double eRad))
                    throw new ArgumentException("arc_requires_startAngle_and_endAngle");
                return (SafeInvoke(ms, "AddArc", center, r, sRad, eRad), "arc");
            }
            case "ellipse":
            {
                if (!TryJsonPoint3(spec, out double[] center, "center", "at") && !TryJsonInlinePoint3(spec, out center))
                    throw new ArgumentException("ellipse_requires_center");
                double[] majorAxis;
                double ratio;
                if (TryJsonPoint3(spec, out double[] maj, "majorAxis"))
                {
                    majorAxis = maj;
                    ratio = TryJsonDouble(spec["radiusRatio"], out double rr) ? rr : 0.5;
                }
                else
                {
                    if (!(TryJsonDouble(spec["majorRadius"], out double rx) || TryJsonDouble(spec["rx"], out rx)) ||
                        !(TryJsonDouble(spec["minorRadius"], out double ry) || TryJsonDouble(spec["ry"], out ry)) ||
                        rx <= 1e-9 || ry <= 1e-9)
                        throw new ArgumentException("ellipse_requires_majorAxis_or_majorRadius_minorRadius");
                    double rot = TryJsonAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out double rt) ? rt : 0.0;
                    majorAxis = [rx * Math.Cos(rot), rx * Math.Sin(rot), 0.0];
                    ratio = ry / rx;
                }
                double majLen = Math.Sqrt(majorAxis[0] * majorAxis[0] + majorAxis[1] * majorAxis[1] + majorAxis[2] * majorAxis[2]);
                if (majLen <= 1e-9 || ratio <= 1e-9) throw new ArgumentException("ellipse_invalid_axes");
                if (ratio > 1.0)
                {
                    majorAxis = [-majorAxis[1] * ratio, majorAxis[0] * ratio, majorAxis[2]];
                    ratio = 1.0 / ratio;
                }
                object? ell = SafeInvoke(ms, "AddEllipse", center, majorAxis, ratio);
                if (ell != null)
                {
                    if (TryJsonDouble(spec["startParam"], out double sp) || TryJsonAngleRad(spec, "startAngle_deg", null, "startAngle_rad", out sp))
                        try { SafeSetProperty(ell, "StartParameter", sp); } catch { }
                    if (TryJsonDouble(spec["endParam"], out double ep) || TryJsonAngleRad(spec, "endAngle_deg", null, "endAngle_rad", out ep))
                        try { SafeSetProperty(ell, "EndParameter", ep); } catch { }
                }
                return (ell, "ellipse");
            }
            case "text":
            case "dbtext":
            {
                string content = spec["text"]?.GetValue<string>() ?? spec["content"]?.GetValue<string>() ?? spec["value"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(content)) throw new ArgumentException("text_requires_content");
                if (!TryJsonPoint3(spec, out double[] pos, "position", "insert", "at") && !TryJsonInlinePoint3(spec, out pos))
                    throw new ArgumentException("text_requires_position");
                double h = TryJsonDouble(spec["height"], out double hv) && hv > 1e-9 ? hv : 2.5;
                object? txt = SafeInvoke(ms, "AddText", content, pos, h);
                if (txt != null && TryJsonAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out double rot))
                {
                    try { SafeSetProperty(txt, "Rotation", rot); } catch { }
                }
                return (txt, "text");
            }
            case "mtext":
            {
                string content = spec["text"]?.GetValue<string>() ?? spec["content"]?.GetValue<string>() ?? spec["value"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(content)) throw new ArgumentException("mtext_requires_content");
                if (!TryJsonPoint3(spec, out double[] pos, "position", "insert", "at") && !TryJsonInlinePoint3(spec, out pos))
                    throw new ArgumentException("mtext_requires_position");
                double h = TryJsonDouble(spec["height"], out double hv) && hv > 1e-9 ? hv : 2.5;
                double w = TryJsonDouble(spec["width"], out double wv) && wv > 0 ? wv : 0.0;
                object? mt = SafeInvoke(ms, "AddMText", pos, w, content);
                if (mt != null)
                {
                    try { SafeSetProperty(mt, "Height", h); } catch { }
                    if (TryJsonAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out double rot))
                    {
                        try { SafeSetProperty(mt, "Rotation", rot); } catch { }
                    }
                }
                return (mt, "mtext");
            }
            case "point":
            case "dbpoint":
            {
                if (!TryJsonPoint3(spec, out double[] pos, "position", "point", "at") && !TryJsonInlinePoint3(spec, out pos))
                    throw new ArgumentException("point_requires_position");
                return (SafeInvoke(ms, "AddPoint", pos), "point");
            }
            default:
                throw new ArgumentException("unsupported_entity_type:" + rawType);
        }
    }

    private static bool TryJsonDouble(JsonNode? node, out double val)
    {
        val = 0.0;
        if (node == null) return false;
        try { val = node.GetValue<double>(); return true; }
        catch
        {
            try
            {
                string? s = node.GetValue<string>();
                return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
            }
            catch { return false; }
        }
    }

    private static bool TryJsonInlinePoint3(JsonObject obj, out double[] pt)
    {
        pt = [0, 0, 0];
        bool hasX = TryJsonDouble(obj["x"], out double x) || TryJsonDouble(obj["e"], out x);
        bool hasY = TryJsonDouble(obj["y"], out double y) || TryJsonDouble(obj["n"], out y);
        if (!hasX || !hasY) return false;
        double z = TryJsonDouble(obj["z"], out double zv) ? zv : 0.0;
        pt = [x, y, z];
        return true;
    }

    private static bool TryJsonPoint3(JsonObject obj, out double[] pt, params string[] keys)
    {
        pt = [0, 0, 0];
        foreach (string k in keys)
        {
            JsonNode? n = obj[k];
            if (n == null) continue;
            if (n is JsonArray arr && arr.Count >= 2 &&
                TryJsonDouble(arr[0], out double x) && TryJsonDouble(arr[1], out double y))
            {
                double z = arr.Count >= 3 && TryJsonDouble(arr[2], out double zv) ? zv : 0.0;
                pt = [x, y, z];
                return true;
            }
            if (n is JsonObject sub && TryJsonInlinePoint3(sub, out pt)) return true;
        }
        return false;
    }

    private static bool TryJsonVertex2(JsonNode? node, out double x, out double y, out double bulge)
    {
        x = 0; y = 0; bulge = 0;
        if (node is JsonArray arr && arr.Count >= 2 &&
            TryJsonDouble(arr[0], out x) && TryJsonDouble(arr[1], out y))
        {
            if (arr.Count >= 3) TryJsonDouble(arr[2], out bulge);
            return true;
        }
        if (node is JsonObject obj)
        {
            bool hasX = TryJsonDouble(obj["x"], out x) || TryJsonDouble(obj["e"], out x);
            bool hasY = TryJsonDouble(obj["y"], out y) || TryJsonDouble(obj["n"], out y);
            if (!hasX || !hasY) return false;
            TryJsonDouble(obj["bulge"], out bulge);
            return true;
        }
        return false;
    }

    private static bool TryJsonAngleRad(JsonObject obj, string? degKey, string? fallbackDegKey, string? radKey, out double rad)
    {
        rad = 0;
        if (degKey != null && TryJsonDouble(obj[degKey], out double d)) { rad = d * Math.PI / 180.0; return true; }
        if (radKey != null && TryJsonDouble(obj[radKey], out double r)) { rad = r; return true; }
        if (fallbackDegKey != null && TryJsonDouble(obj[fallbackDegKey], out double fd)) { rad = fd * Math.PI / 180.0; return true; }
        return false;
    }

    private static JsonObject? ComEntityToGeoJson(object ent)
    {
        string objName = (SafeGetString(ent, "ObjectName") ?? "").ToLowerInvariant();
        if (objName.Contains("polyline"))
        {
            if (SafeGetProperty(ent, "Coordinates") is double[] coords && coords.Length >= 4)
            {
                int stride = objName.Contains("3d") ? 3 : 2;
                var pts = new JsonArray();
                for (int i = 0; i + 1 < coords.Length; i += stride)
                {
                    pts.Add(new JsonArray(coords[i], coords[i + 1]));
                }
                bool closed = Convert.ToBoolean(SafeGetProperty(ent, "Closed") ?? false);
                if (closed && pts.Count >= 3)
                {
                    pts.Add(pts[0]!.DeepClone());
                    return new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray(pts) };
                }
                return new JsonObject { ["type"] = "LineString", ["coordinates"] = pts };
            }
        }
        else if (objName.Contains("line"))
        {
            if (SafeGetProperty(ent, "StartPoint") is double[] s && SafeGetProperty(ent, "EndPoint") is double[] e && s.Length >= 2 && e.Length >= 2)
            {
                return new JsonObject
                {
                    ["type"] = "LineString",
                    ["coordinates"] = new JsonArray(new JsonArray(s[0], s[1]), new JsonArray(e[0], e[1]))
                };
            }
        }
        else if (objName.Contains("circle"))
        {
            if (SafeGetProperty(ent, "Center") is double[] c && c.Length >= 2)
            {
                double r = Convert.ToDouble(SafeGetProperty(ent, "Radius") ?? 0.0, CultureInfo.InvariantCulture);
                if (r > 1e-6)
                {
                    var ring = new JsonArray();
                    for (int i = 0; i <= 24; i++)
                    {
                        double a = 2.0 * Math.PI * i / 24.0;
                        ring.Add(new JsonArray(c[0] + r * Math.Cos(a), c[1] + r * Math.Sin(a)));
                    }
                    return new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray(ring) };
                }
            }
        }
        else if (objName.Contains("point") || objName.Contains("blockreference"))
        {
            object? ptObj = SafeGetProperty(ent, "Coordinates") ?? SafeGetProperty(ent, "InsertionPoint");
            if (ptObj is double[] p && p.Length >= 2)
            {
                return new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(p[0], p[1]) };
            }
        }
        return null;
    }

    private static void EnsureComLayer(object doc, string layerName)
    {
        try
        {
            object? layers = SafeGetProperty(doc, "Layers");
            if (layers != null) SafeInvoke(layers, "Add", layerName);
        }
        catch { }
    }

    // ---------------- ROT + ActiveObject 枚举与 P/Invoke ----------------

    private static List<(object App, string ProgId)> EnumerateComAppsOnSta()
    {
        var found = new List<(object App, string ProgId)>();
        var seenComPtrs = new HashSet<IntPtr>();

        // 1) 优先按 ProgID 探测已注册的活跃 COM 实例（覆盖 AutoCAD 2010–2026 + ZWCAD + GstarCAD）
        if (!OperatingSystem.IsWindows()) return found;
        foreach (string progId in KnownProgIds)
        {
            object? obj = TryGetActiveObject(progId);
            if (obj == null) continue;
            IntPtr unk = Marshal.GetIUnknownForObject(obj);
            try
            {
                if (seenComPtrs.Add(unk))
                {
                    found.Add((obj, progId));
                }
            }
            finally
            {
                Marshal.Release(unk);
            }
        }

        // 2) 扫描 Windows ROT 捕获多开实例
        try
        {
            if (GetRunningObjectTable(0, out IRunningObjectTable rot) == 0 && rot != null)
            {
                rot.EnumRunning(out IEnumMoniker enumMoniker);
                if (enumMoniker != null)
                {
                    IMoniker[] monikers = new IMoniker[1];
                    CreateBindCtx(0, out IBindCtx bindCtx);
                    while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                    {
                        try
                        {
                            monikers[0].GetDisplayName(bindCtx, null, out string displayName);
                            if (string.IsNullOrEmpty(displayName)) continue;
                            if (!displayName.Contains("AutoCAD", StringComparison.OrdinalIgnoreCase) &&
                                !displayName.Contains("ZWCAD", StringComparison.OrdinalIgnoreCase) &&
                                !displayName.Contains("GCAD", StringComparison.OrdinalIgnoreCase) &&
                                !displayName.Contains("GstarCAD", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            rot.GetObject(monikers[0], out object rotObj);
                            if (rotObj == null) continue;
                            // 若 ROT 项是 Document，取其 Application 属性
                            object appCandidate = SafeGetProperty(rotObj, "Application") ?? rotObj;
                            if (SafeGetProperty(appCandidate, "Version") == null) continue;

                            IntPtr unk = Marshal.GetIUnknownForObject(appCandidate);
                            try
                            {
                                if (seenComPtrs.Add(unk))
                                {
                                    found.Add((appCandidate, displayName));
                                }
                            }
                            finally
                            {
                                Marshal.Release(unk);
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        return found;
    }

    private static object? TryGetActiveObject(string progId)
    {
        try
        {
            if (CLSIDFromProgID(progId, out Guid clsid) != 0) return null;
            if (GetActiveObject(ref clsid, IntPtr.Zero, out object obj) == 0) return obj;
        }
        catch { }
        return null;
    }

    private static int ResolvePid(object app)
    {
        try
        {
            object? hwndObj = SafeGetProperty(app, "HWND");
            if (hwndObj != null)
            {
                IntPtr hwnd = new(Convert.ToInt64(hwndObj, CultureInfo.InvariantCulture));
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid > 0) return (int)pid;
                }
            }
        }
        catch { }
        return 0;
    }

    private static bool HasRunningCadProcess(out string names)
    {
        var list = new List<string>();
        foreach (string pn in KnownProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(pn);
                if (procs.Length > 0) list.Add($"{pn}({procs.Length})");
            }
            catch { }
        }
        names = string.Join(", ", list);
        return list.Count > 0;
    }

    private static string DetectVendor(string progId, string caption)
    {
        string all = (progId + " " + caption).ToLowerInvariant();
        if (all.Contains("zwcad") || all.Contains("中望")) return "zwcad";
        if (all.Contains("gcad") || all.Contains("gstarcad") || all.Contains("浩辰")) return "gcad";
        if (all.Contains("civil 3d")) return "civil3d";
        return "autocad";
    }

    private static object? SafeGetProperty(object target, string name)
    {
        try { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static string? SafeGetString(object target, string name)
    {
        object? v = SafeGetProperty(target, name);
        return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    private static void SafeSetProperty(object target, string name, object value)
    {
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);
    }

    private static object? SafeInvoke(object target, string method, params object[] args)
    {
        return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);
    }

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable pprot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _staQueue.CompleteAdding();
    }

    // ---------------- OLE IMessageFilter（防 RPC_E_CALL_REJECTED） ----------------

    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    private sealed class OleMessageFilter : IOleMessageFilter
    {
        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

        public static void Register()
        {
            try { CoRegisterMessageFilter(new OleMessageFilter(), out _); } catch { }
        }

        public static void Revoke()
        {
            try { CoRegisterMessageFilter(null, out _); } catch { }
        }

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            // SERVERCALL_RETRYLATER (2)：CAD 正忙时在 2.5s 内每 120ms 自动重试，超时则放弃交由上层报 E1003
            if (dwRejectType == 2 && dwTickCount < 2500) return 120;
            return -1;
        }

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2; // PENDINGMSG_WAITDEFPROCESS
    }
}

internal sealed class ComToolUnsupportedException : Exception
{
    public ComToolUnsupportedException(string message) : base(message) { }
}

internal sealed class ComBusyException : Exception
{
    public ComBusyException(string message) : base(message) { }
}
