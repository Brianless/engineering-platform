using System.Text.Json.Nodes;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// 多驱动统一路由器（v3.1 一网关多驱动）：
/// 1. Driver 1（满血插件）：EngineeringHost.dll NamedPipe（Civil 3D / AutoCAD 2019–2026）；
/// 2. Driver 2（通用任意版 CAD）：STA 后期绑定 Windows COM（AutoCAD 2010–2026 / 中望 / 浩辰，零插件）；
/// 3. Driver 3（无 CAD 离线）：ACadSharp 原生读写 DWG/DXF（零 CAD 进程即可批处理与导出）。
/// 按 PID 自动去重，绝不让同一 CAD 进程在插件与 COM 之间重复冲突。
/// </summary>
internal sealed class BackendRouter : IDisposable
{
    private readonly PipeHostClient _plugin;
    private readonly ComCadDriver _com;
    private readonly OfflineDwgDriver _offline;
    private readonly string _preferredBackend; // auto | plugin | com | offline

    public BackendRouter(PipeHostClient plugin, ComCadDriver com, OfflineDwgDriver offline, string? preferredBackend = null)
    {
        _plugin = plugin;
        _com = com;
        _offline = offline;
        _preferredBackend = string.IsNullOrWhiteSpace(preferredBackend) ? "auto" : preferredBackend.Trim().ToLowerInvariant();
    }

    public string ActiveBackendHint => _preferredBackend;

    public async Task<JsonArray> ListUnifiedInstancesAsync(CancellationToken ct = default)
    {
        var list = new JsonArray();
        var pluginPids = new HashSet<int>();

        // 1) 插件驱动实例（Driver 1，仅展示存活实例，避免历史残留文件刷屏）
        foreach (var inst in _plugin.ListInstances())
        {
            bool alive = inst["alive"]?.GetValue<bool>() == true;
            if (!alive) continue;
            int pid = inst["pid"]?.GetValue<int>() ?? 0;
            if (pid > 0) pluginPids.Add(pid);
            inst["driver"] = "plugin";
            list.Add(inst);
        }

        // 2) COM 驱动实例（Driver 2，按 PID 去重，避免与插件实例重叠）
        if (OperatingSystem.IsWindows())
        {
            var comInsts = await _com.DiscoverInstancesAsync(ct);
            foreach (var c in comInsts)
            {
                if (c.Pid > 0 && pluginPids.Contains(c.Pid)) continue;
                list.Add(new JsonObject
                {
                    ["hostInstanceId"] = $"com-{c.Vendor}-{c.Pid}",
                    ["driver"] = "com_generic",
                    ["app"] = c.Vendor,
                    ["pid"] = c.Pid,
                    ["hostVersion"] = c.Version,
                    ["progId"] = c.ProgId,
                    ["caption"] = c.Caption,
                    ["activeDocument"] = c.ActiveDocumentName,
                    ["alive"] = true
                });
            }
        }

        // 3) 离线 ACadSharp 驱动（Driver 3，始终可用）
        list.Add(new JsonObject
        {
            ["hostInstanceId"] = "offline-acadsharp",
            ["driver"] = "offline_acadsharp",
            ["app"] = "dwg_offline",
            ["pid"] = Environment.ProcessId,
            ["hostVersion"] = "ACadSharp",
            ["alive"] = true
        });

        return list;
    }

    public async Task<JsonObject> DispatchAsync(
        string tool,
        JsonObject args,
        string opId,
        CancellationToken ct = default)
    {
        // 支持单次调用级多 CAD 目标路由参数（调用前从 args 剔除，保持宿主 schema 纯净）
        string mode = _preferredBackend;
        if (args["_backend"] != null)
        {
            mode = (args["_backend"]?.GetValue<string>() ?? mode).ToLowerInvariant();
            args.Remove("_backend");
        }

        int? targetPid = null;
        if (args["targetPid"] != null)
        {
            try { targetPid = args["targetPid"]!.GetValue<int>(); } catch { }
            args.Remove("targetPid");
        }

        string? targetInstanceId = null;
        if (args["hostInstanceId"] != null)
        {
            targetInstanceId = args["hostInstanceId"]?.GetValue<string>();
            args.Remove("hostInstanceId");
        }

        // 若 hostInstanceId 形如 com-<vendor>-<pid> 或 offline-acadsharp，直接解析目标后端与 PID
        if (!string.IsNullOrEmpty(targetInstanceId))
        {
            if (targetInstanceId.Equals("offline-acadsharp", StringComparison.OrdinalIgnoreCase))
            {
                mode = "offline";
            }
            else if (targetInstanceId.StartsWith("com-", StringComparison.OrdinalIgnoreCase))
            {
                mode = "com";
                int lastDash = targetInstanceId.LastIndexOf('-');
                if (lastDash > 0 && int.TryParse(targetInstanceId[(lastDash + 1)..], out int parsedPid))
                {
                    targetPid ??= parsedPid;
                }
            }
        }

        // 离线 DWG 快捷通道：如果传了 dwgPath 直接走 ACadSharp
        string? dwgPath = args["dwgPath"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(dwgPath))
        {
            args.Remove("dwgPath");
            return WrapOk(ExecuteOfflineTool(tool, dwgPath, args));
        }

        var alivePlugins = _plugin.ListInstances().Where(i => i["alive"]?.GetValue<bool>() == true).ToList();
        bool hasAlivePlugin = alivePlugins.Count > 0;

        // 若指定了 targetPid 但不在插件列表中，在 auto 模式下自动转给 COM 驱动按 PID 精准路由
        bool pluginMatchesTarget = hasAlivePlugin && (
            (targetPid == null && string.IsNullOrEmpty(targetInstanceId)) ||
            alivePlugins.Any(i =>
                (targetPid != null && (i["pid"]?.GetValue<int>() ?? 0) == targetPid.Value) ||
                (!string.IsNullOrEmpty(targetInstanceId) &&
                 string.Equals(i["hostInstanceId"]?.GetValue<string>(), targetInstanceId, StringComparison.OrdinalIgnoreCase))));

        // 1) 优先 Driver 1（满血插件），超时支持环境变量 EP_TOOL_TIMEOUT_MS / EP_HEAVY_TIMEOUT_MS 覆盖
        if (mode == "plugin" || (mode == "auto" && pluginMatchesTarget))
        {
            int normalMs = ParseTimeoutEnv("EP_TOOL_TIMEOUT_MS", 60000);
            int heavyMs = ParseTimeoutEnv("EP_HEAVY_TIMEOUT_MS", 180000);
            int pluginTimeoutMs = tool is "write_eval_csharp" or "export_surface_grid" or "write_alignment_plan" or "batch_side_database"
                ? heavyMs
                : normalMs;
            return await _plugin.CallToolAsync(tool, args, opId, pluginTimeoutMs, ct, targetPid, targetInstanceId);
        }

        // 2) batch_side_database 在无插件运行时自动落入 Driver 3（ACadSharp 离线，零 CAD 依赖）
        if (tool == "batch_side_database" && (mode == "offline" || mode == "auto"))
        {
            return WrapOk(_offline.ExecuteBatchSideDatabase(args));
        }

        // 3) Driver 2（任意版普通 CAD 通用 COM 驱动，支持 targetPid 精准多开路由）
        if (mode == "com" || mode == "auto")
        {
            try
            {
                var comRes = await _com.CallToolAsync(tool, args, targetPid, ct);
                return WrapOk(comRes);
            }
            catch (ComToolUnsupportedException ex)
            {
                return WrapFail("E2001", ex.Message);
            }
            catch (ComBusyException ex)
            {
                return WrapFail("E1003", ex.Message);
            }
        }

        // 4) 纯离线模式兜底
        if (mode == "offline")
        {
            if (tool == "sys_ping")
            {
                return WrapOk(new JsonObject
                {
                    ["pong"] = true,
                    ["serverTimeUtc"] = DateTime.UtcNow.ToString("O"),
                    ["hostInstanceId"] = "offline-acadsharp",
                    ["backend"] = "offline_acadsharp"
                });
            }
            if (tool == "sys_capabilities")
            {
                return WrapOk(new JsonObject
                {
                    ["summary"] = "【离线模式 · ACadSharp】零 CAD 进程直接读写原生 DWG/DXF（支持 batch_side_database 及传入 dwgPath 的 read_overview / read_layers / export_layer_geojson / write_entities）。",
                    ["backend"] = "offline_acadsharp",
                    ["writeEnabled"] = true,
                    ["domains"] = new JsonArray("sys", "read", "export", "write", "batch")
                });
            }
            return WrapFail("E2001", $"离线模式下调用 {tool} 需传入 dwgPath 参数，或改用 batch_side_database。");
        }

        throw new HostUnavailableException("C1001", "未发现可用的插件宿主或 COM CAD 实例。");
    }

    private JsonObject ExecuteOfflineTool(string tool, string dwgPath, JsonObject args)
    {
        return tool switch
        {
            "read_overview" => _offline.ReadOverview(dwgPath),
            "read_layers" => _offline.ReadLayers(
                dwgPath,
                args["namePattern"]?.GetValue<string>(),
                args["limit"]?.GetValue<int>() ?? 200),
            "export_layer_geojson" => _offline.ExportLayerGeoJson(
                dwgPath,
                (args["layers"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s != "").ToList(),
                ParseCatMap(args["layerCategoryMap"] as JsonObject),
                args["limit"]?.GetValue<int>() ?? 20000,
                args["outPath"]?.GetValue<string>()),
            "write_entities" => _offline.WriteEntities(dwgPath, args),
            _ => throw new InvalidOperationException($"offline_dwg_tool_unsupported:{tool}")
        };
    }

    private static Dictionary<string, string>? ParseCatMap(JsonObject? node)
    {
        if (node == null) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in node)
        {
            if (kv.Value != null) map[kv.Key] = kv.Value.GetValue<string>();
        }
        return map;
    }

    private static JsonObject WrapOk(JsonObject result) => new()
    {
        ["ok"] = true,
        ["status"] = "verified",
        ["result"] = result
    };

    private static JsonObject WrapFail(string code, string detail) => new()
    {
        ["ok"] = false,
        ["status"] = "failed",
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["detail"] = detail
        }
    };

    private static int ParseTimeoutEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) && v >= 5000 && v <= 600000 ? v : fallback;
    }

    public void Dispose()
    {
        _plugin.Dispose();
        _com.Dispose();
    }
}
