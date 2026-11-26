using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// 单体 C# MCP 网关核心（v3.0 Phase 4）：
/// - 零 Python 依赖，同时支持 Streamable HTTP (http://127.0.0.1:18080/mcp) 与 --stdio；
/// - 协议内聚：通过 initialize.instructions 自动下发完整操作规范与选线闭环指南，免外部 Skill 配对；
/// - 防跨站窃图（v3.0 §5.6）：强制校验 Host 头、拒绝携带 Origin 的浏览器跨域请求、强制 Bearer Token 鉴权。
/// </summary>
internal sealed class McpServer : IDisposable
{
    public const string ServerName = "ep-mcp";
    public const string ServerVersion = "1.0.0";
    private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private const string AutonomousInstructions =
        "【Engineering Platform v1.0.0 · Civil 3D + 全版本 CAD 一体化全自主工程引擎】\n" +
        "0. 一网关三驱动自动路由：ep-mcp.exe 自动识别 (1) Civil 3D/AutoCAD 2017–2026 满血插件驱动（NamedPipe）(2) 任意版 AutoCAD 2010–2026/中望/浩辰免插件 COM 驱动 (3) 无 CAD 离线 ACadSharp DWG/DXF 驱动。先调 sys_list_instances 查看所有可用实例；多开 CAD 时可在任意工具参数中附加 targetPid 或 hostInstanceId 精准指定目标窗口。\n" +
        "1. 读图顺序：先调用 read_overview 确认单位/范围/驱动模式，再按需下钻 read_layers / read_query / read_describe / read_civil_list / read_civil_surfaces / read_civil_alignments / read_active_selection（离线 DWG 可直接传 dwgPath 或使用 batch_side_database）。\n" +
        "2. 静默全自主写入与原生批量建实体：优先使用 write_entities（单事务批量原生创建 line/polyline/rectangle/circle/arc/ellipse/text/mtext/point，支持插件/COM/离线三驱动，同步返回精确创建数/句柄/包围盒/失败明细，零 LISP entmake 静默丢弃）；当宿主 write.autoApprove=true 或走通用 COM 驱动时，所有 write_* 可直接连续执行，无需人工弹窗审批；支持 write_undo 精确回滚。\n" +
        "3. 外置自动选线 5 步闭环：export_surface_grid + export_layer_geojson → route_solver（已融合部署在 %LOCALAPPDATA%\\EngineeringPlatform\\route_solver） → preview_transient / write_alignment_plan → write_undo 迭代收敛 → read_viewport_snapshot。\n" +
        "4. 100% 底层自由度：插件模式下使用 write_eval_csharp（支持 shadowDryRun=true）；通用 COM 模式下可用 write_command 执行任意 AutoLISP 表达式。\n" +
        "5. 南方 CASS / 飞时达 FastTFT / Autodesk Civil 3D 全自动规范（零点击闭环）：\n" +
        "   (a) 先用 ui_cass_commands 按中文关键词查命令名与避坑提示（同时涵盖南方 CASS、飞时达 FastTFT V15.x 与 Autodesk Civil 3D 2019–2026 全部 12 大核心模块 + BIM 交付）；\n" +
        "   (b) CASS 多步选实体命令须先设 OSMODE=0 并局部缩放聚焦到边界边中点，再通过 write_command(allowDuringCommand=true) 投递 X,Y 坐标与 sendEnter=true；飞时达 TFCREATEREGION 推荐先 OSMODE=0 后走 D(跟踪描绘) 逐点单次发送顶点坐标→C→N，且 TFAUTOFG/TFGATHERNATURE/TFDESIGNIMPORT/TFFGCALVOL 完成后须发 cancelCommand=true 退出；\n" +
        "   (c) 遇到 MFC 模态框时用 ui_wait_dialog / ui_list_dialogs 读取完整控件树，再用 ui_interact_dialog 自动填参；**严禁对文件路径框只做 set_text**——必须用 browse_file 点击旁边的 [...] 按钮自动走二级文件框；多选项卡用 select_tab；导入/计算结束后若弹出二级提示框须读取其 Static 文本并点击确定关闭；\n" +
        "   (d) 通用 .dat 格式为：点号,,AutoCAD东坐标X,AutoCAD北坐标Y,高程H；CASS GCD 高程点 XData 为 RegApp='SOUTH', 1000='202101'；AutoCAD 2017–2026 均支持满血插件模式；\n" +
        "   (e) **Civil 3D 设计院级 + LOD 300 BIM 全自动铁律**：(1) TIN 曲面导入海量点后必须设置 MaximumTriangleLength 裁剪凹包长边；(2) 山区盘山公路优先采用 route_solver(30–100 km/h，符合 JTG D20-2017 纵坡坡长限制/缓和坡段/隧道≤3%/平包竖协调 + 概算经济比选 + 调配)；(3) 纵断面必须通过 AddFreeSymmetricParabolaByPVIAndCurveLength 生成平滑抛物线竖曲线，严禁尖角折线；(4) 山区放坡部件须用 Subassembly.DaylightMaxWidth 限宽（CutSlope/FillSlope/MaxWidth，切勿用默认 1:4 放坡的 BasicSideSlopeCutDitch），限宽段自动配重力式挡墙、深谷配桥梁、路肩配护栏；(5) 体积曲面 TinVolumeSurface 须分两事务：Tx1 建 CorridorSurface 并 Rebuild，Tx2 设 MaximumTriangleLength 裁剪 Datum 后再创建体积曲面；(6) BIM 三维表现用 Corridor.ExportSolids 导出实体 + 分层 PBR Material 材质 + 纵横断面标线/护栏/桥墩/挡墙三维模型 + Realistic 视觉样式 + PropertySet/IFC4.3；(7) 中文表格文字须建 TrueType Microsoft YaHei 样式防乱码，纬地报表优先复用本机 HintCAD\\模板\\表格\\*.xls（N=Y北坐标, E=X东坐标）；\n" +
        "   (f) **纬地 HintCAD V8.0 全自动铁律**：(1) 纬地 .JD/.pm/交点导入表与逐桩坐标表采用大地坐标（X=北N，Y=东E），与 AutoCAD 图形空间（X=东E，Y=北N）互换，跨系统必须转换；(2) 优先用 route_solver.hintcad_io 直接生成 .Prj/.ZDM/.DMX/.ctr/.wid/.sta/_JD_import.txt 纯文本工程，避免脆性手工录入；(3) 凡改动平面/纵断/超高/加宽/桩号，必须先跑 CHECK_HDM_DMX 再重跑 LJSJ 生成 .lj 中间数据，才能运行 HDM_NEW；(4) 遇 ERR*.txt 报边坡不交地面线时先用 HDTM_EXHDM 补测加宽或在 .ctr 设挡墙/桥梁，可用 route_solver.hintcad_io diagnose 诊断；(5) AutoCAD 2014 纬地通过通用 COM + Win32DialogDriver 协同（需同权限同桌面），且严禁 ACadSharp 原地覆写含 HINTCAD_HHDM* 自定义对象的图纸。";

    private readonly BackendRouter _router;
    private readonly PipeHostClient _host;
    private readonly ResultStore _store;
    private readonly Win32DialogDriver _uiDialogs = new();
    private readonly IdempotencyCache _idempotency = new(TimeSpan.FromMinutes(3));
    private readonly List<JsonObject> _tools;
    private readonly string _catalogHash;

    public McpServer(BackendRouter router, PipeHostClient host, ResultStore store)
    {
        _router = router;
        _host = host;
        _store = store;
        (_tools, _catalogHash) = LoadEmbeddedTools();
    }

    public string CatalogHash => _catalogHash;
    public int ToolCount => _tools.Count;

    public async Task<JsonObject?> HandleJsonRpcAsync(JsonObject msg, CancellationToken ct)
    {
        string? method = msg["method"]?.GetValue<string>();
        bool hasId = msg.ContainsKey("id");
        JsonNode? id = msg["id"]?.DeepClone();
        var parameters = msg["params"] as JsonObject ?? new JsonObject();

        if (string.IsNullOrEmpty(method))
        {
            return hasId ? MakeRpcError(id, -32600, "method_required") : null;
        }

        if (method == "notifications/initialized" || method == "notifications/cancelled")
        {
            return null;
        }

        if (method == "initialize")
        {
            string requested = parameters["protocolVersion"]?.GetValue<string>() ?? SupportedProtocols[0];
            string negotiated = SupportedProtocols.Contains(requested) ? requested : SupportedProtocols[0];
            var result = new JsonObject
            {
                ["protocolVersion"] = negotiated,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false }
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = ServerName,
                    ["version"] = ServerVersion
                },
                ["instructions"] = AutonomousInstructions
            };
            return MakeRpcResult(id, result);
        }

        if (method == "ping")
        {
            return MakeRpcResult(id, new JsonObject());
        }

        if (method == "tools/list")
        {
            var arr = new JsonArray();
            foreach (var t in _tools) arr.Add(t.DeepClone());
            return MakeRpcResult(id, new JsonObject { ["tools"] = arr });
        }

        if (method == "tools/call")
        {
            string? toolName = parameters["name"]?.GetValue<string>();
            var args = parameters["arguments"] as JsonObject ?? new JsonObject();
            if (string.IsNullOrEmpty(toolName))
            {
                return MakeRpcError(id, -32602, "tool_name_required");
            }
            var callResult = await ExecuteToolAsync(toolName, args, ct);
            return MakeRpcResult(id, callResult);
        }

        if (method == "shutdown")
        {
            return MakeRpcResult(id, new JsonObject());
        }

        return hasId ? MakeRpcError(id, -32601, "method_not_found:" + method) : null;
    }

    private async Task<JsonObject> ExecuteToolAsync(string toolName, JsonObject args, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string opId = "op-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" +
                      Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        string digest = CanonicalJson.ComputeParamsDigest(args);

        // 本地快速路径：sys_list_instances 汇总插件 + COM（AutoCAD/中望/浩辰）+ 离线 ACadSharp 全部后端
        if (toolName == "sys_list_instances")
        {
            var insts = await _router.ListUnifiedInstancesAsync(ct);
            var localPayload = new JsonObject
            {
                ["summary"] = $"【多驱动实例列表】共发现 {insts.Count} 个后端（含插件 / 通用 COM / 离线 ACadSharp，网关 {ServerVersion}）。",
                ["instances"] = insts
            };
            return WrapToolResult(localPayload, false);
        }

        // 本地快速路径：南方 CASS 命令目录与自动化知识库检索
        if (toolName == "ui_cass_commands")
        {
            var payload = CassCommandCatalog.Search(args);
            sw.Stop();
            _store.RecordAudit(opId, toolName, digest, true, sw.ElapsedMilliseconds, null);
            return WrapToolResult(payload, false);
        }

        // 本地快速路径：读取 CAD 当前命令行提示与 CMDACTIVE（支持插件 + COM 双驱动）
        if (toolName == "ui_read_cmdline")
        {
            try
            {
                int? targetPid = null;
                if (args["targetPid"] != null) { try { targetPid = args["targetPid"]!.GetValue<int>(); } catch { } }
                var alivePlugins = _host.ListInstances().Where(i => i["alive"]?.GetValue<bool>() == true).ToList();
                bool usePlugin = alivePlugins.Any(i => targetPid == null || (i["pid"]?.GetValue<int>() ?? 0) == targetPid.Value);

                if (usePlugin)
                {
                    var forwardArgs = args.DeepClone().AsObject();
                    forwardArgs.Remove("targetPid");
                    var hostResp = await _host.CallToolAsync("ui_read_cmdline", forwardArgs, opId, 10000, ct, targetPid, null);
                    sw.Stop();
                    if (hostResp["ok"]?.GetValue<bool>() == true && hostResp["result"] is JsonObject hr)
                    {
                        _store.RecordAudit(opId, toolName, digest, true, sw.ElapsedMilliseconds, null);
                        return WrapToolResult(hr.DeepClone().AsObject(), false);
                    }
                    var errObj = hostResp["error"] as JsonObject;
                    string code = errObj?["code"]?.GetValue<string>() ?? "E1004";
                    string detail = errObj?["detail"]?.GetValue<string>() ?? "cmdline_read_failed";
                    _store.RecordAudit(opId, toolName, digest, false, sw.ElapsedMilliseconds, code);
                    return WrapToolResult(new JsonObject
                    {
                        ["ok"] = false, ["code"] = code, ["detail"] = detail,
                        ["selfGuide"] = code == "E1004" ? "CAD 正弹出模态对话框，请改用 ui_list_dialogs 查看弹窗。" : SelfGuideForError(code, toolName, detail)
                    }, true);
                }

                // 无匹配插件时走统一路由器（COM 驱动直读系统变量）
                var comResp = await _router.DispatchAsync("ui_read_cmdline", args.DeepClone().AsObject(), opId, ct);
                sw.Stop();
                if (comResp["ok"]?.GetValue<bool>() == true && comResp["result"] is JsonObject cr)
                {
                    _store.RecordAudit(opId, toolName, digest, true, sw.ElapsedMilliseconds, null);
                    return WrapToolResult(cr.DeepClone().AsObject(), false);
                }
                var cErr = comResp["error"] as JsonObject;
                string cCode = cErr?["code"]?.GetValue<string>() ?? "E1004";
                string cDetail = cErr?["detail"]?.GetValue<string>() ?? "cmdline_read_failed";
                _store.RecordAudit(opId, toolName, digest, false, sw.ElapsedMilliseconds, cCode);
                return WrapToolResult(new JsonObject { ["ok"] = false, ["code"] = cCode, ["detail"] = cDetail }, true);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _store.RecordAudit(opId, toolName, digest, false, sw.ElapsedMilliseconds, "C1002");
                return WrapToolResult(new JsonObject { ["ok"] = false, ["code"] = "C1002", ["detail"] = ex.Message }, true);
            }
        }

        // 本地快速路径 + 宿主进程内后台线程双引擎兜底：
        // 1) 优先用 ep-mcp 本地 Win32DialogDriver（交互桌面下零开销，且支持无插件 COM CAD）；
        // 2) 若本地受 Windows 桌面隔离/UIPI 看不到窗口且有存活插件实例，自动穿透 NamedPipe 由 acad.exe 后台线程直读 MFC 弹窗！
        if (toolName is "ui_list_dialogs" or "ui_wait_dialog" or "ui_interact_dialog")
        {
            try
            {
                int? targetPid = null;
                if (args["targetPid"] != null)
                {
                    try { targetPid = args["targetPid"]!.GetValue<int>(); } catch { }
                }

                JsonObject? uiPayload = null;
                bool tryHostFallback = false;

                try
                {
                    uiPayload = toolName switch
                    {
                        "ui_list_dialogs" => _uiDialogs.ListDialogs(args),
                        "ui_wait_dialog" => await _uiDialogs.WaitDialogAsync(args, ct),
                        _ => await _uiDialogs.InteractDialogAsync(args, ct)
                    };
                    if (toolName == "ui_list_dialogs" && (uiPayload["count"]?.GetValue<int>() ?? 0) == 0)
                        tryHostFallback = true;
                    else if (toolName == "ui_wait_dialog" && uiPayload["found"]?.GetValue<bool>() != true)
                        tryHostFallback = true;
                }
                catch
                {
                    tryHostFallback = true;
                }

                if (tryHostFallback)
                {
                    bool hasPlugin = _host.ListInstances().Any(i =>
                        i["alive"]?.GetValue<bool>() == true &&
                        (targetPid == null || (i["pid"]?.GetValue<int>() ?? 0) == targetPid.Value));
                    if (hasPlugin)
                    {
                        var forwardArgs = args.DeepClone().AsObject();
                        forwardArgs.Remove("targetPid");
                        var hostResp = await _host.CallToolAsync(toolName, forwardArgs, opId, 35000, ct, targetPid, null);
                        if (hostResp["ok"]?.GetValue<bool>() == true && hostResp["result"] is JsonObject hr)
                        {
                            uiPayload = hr.DeepClone().AsObject();
                        }
                    }
                }

                if (uiPayload == null)
                {
                    throw new InvalidOperationException("dialog_not_found: 未检测到目标 CAD 的可见 MFC 模态对话框。");
                }

                sw.Stop();
                _store.RecordAudit(opId, toolName, digest, true, sw.ElapsedMilliseconds, null);
                return WrapToolResult(uiPayload, false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _store.RecordAudit(opId, toolName, digest, false, sw.ElapsedMilliseconds, "E1001");
                return WrapToolResult(new JsonObject
                {
                    ["ok"] = false,
                    ["opId"] = opId,
                    ["code"] = "E1001",
                    ["detail"] = ex.Message,
                    ["selfGuide"] = "先调用 ui_list_dialogs 确认目标 CAD 是否已弹出模态对话框及控件标签/ctrlId。"
                }, true);
            }
        }

        // 幂等缓存：当调用方提供显式 idempotencyKey 时，防止重试造成重复写入
        string? idemKey = args["idempotencyKey"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(idemKey))
        {
            args.Remove("idempotencyKey");
            if (_idempotency.TryGet(toolName + ":" + idemKey, out var cached) && cached != null)
            {
                return cached;
            }
        }

        try
        {
            var hostResp = await _router.DispatchAsync(toolName, args, opId, ct);
            sw.Stop();

            bool ok = hostResp["ok"]?.GetValue<bool>() == true;
            string? fp = hostResp["targetFingerprint"]?.GetValue<string>();
            if (!ok)
            {
                var errObj = hostResp["error"] as JsonObject;
                string code = errObj?["code"]?.GetValue<string>() ?? "E3002";
                string detail = errObj?["detail"]?.GetValue<string>() ?? "host_error";
                _store.RecordAudit(opId, toolName, digest, false, sw.ElapsedMilliseconds, code);
                var guided = new JsonObject
                {
                    ["ok"] = false,
                    ["opId"] = opId,
                    ["code"] = code,
                    ["detail"] = detail,
                    ["selfGuide"] = SelfGuideForError(code, toolName, detail)
                };
                return WrapToolResult(guided, true);
            }

            var resultNode = (hostResp["result"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
            // Q-15/Q-16：sys_capabilities.tools 与网关实际 tools/list 严格同源（含 5 个进程外 ui_* 工具）
            if (toolName == "sys_capabilities")
            {
                var gwTools = new JsonArray();
                foreach (var t in _tools)
                {
                    string? tn = t["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(tn)) gwTools.Add(tn);
                }
                int hostCount = (resultNode["tools"] as JsonArray)?.Count ?? 0;
                resultNode["hostToolsCount"] = hostCount;
                resultNode["gatewayToolsCount"] = gwTools.Count;
                resultNode["tools"] = gwTools;
                resultNode["catalogHash"] = _catalogHash;
                string? oldSum = resultNode["summary"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(oldSum))
                {
                    resultNode["summary"] = $"{oldSum}｜网关暴露工具 {gwTools.Count} 个（与 tools/list 一致）";
                }
            }
            string payloadJson = CanonicalJson.SerializeCompact(resultNode);
            string storedAt = _store.SaveResult(opId, toolName, fp, digest, "verified", payloadJson);
            _store.RecordAudit(opId, toolName, digest, true, sw.ElapsedMilliseconds, null);

            if (resultNode["resultRef"] is JsonObject rref)
            {
                rref["storedAt"] = storedAt;
            }
            else
            {
                resultNode["resultRef"] = new JsonObject
                {
                    ["tool"] = toolName,
                    ["opId"] = opId,
                    ["fingerprint"] = fp ?? "",
                    ["storedAt"] = storedAt
                };
            }

            var wrapped = WrapToolResult(resultNode, false);
            if (!string.IsNullOrEmpty(idemKey))
            {
                _idempotency.Put(toolName + ":" + idemKey, wrapped);
            }
            return wrapped;
        }
        catch (HostUnavailableException hue)
        {
            sw.Stop();
            _store.RecordAudit(opId, toolName, digest, false, sw.ElapsedMilliseconds, hue.Code);
            var errPayload = new JsonObject
            {
                ["ok"] = false,
                ["opId"] = opId,
                ["code"] = hue.Code,
                ["detail"] = hue.Message,
                ["selfGuide"] = SelfGuideForError(hue.Code, toolName, hue.Message)
            };
            return WrapToolResult(errPayload, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _store.RecordAudit(opId, toolName, digest, false, sw.ElapsedMilliseconds, "C1003");
            var errPayload = new JsonObject
            {
                ["ok"] = false,
                ["opId"] = opId,
                ["code"] = "C1003",
                ["detail"] = ex.Message,
                ["selfGuide"] = "连接宿主异常，网关下次调用将自动重连；可先调用 sys_list_instances 确认 CAD 实例存活状态。"
            };
            return WrapToolResult(errPayload, true);
        }
    }

    /// <summary>状态机自引导纠错提示（v3.0 协议内聚）。</summary>
    private static string SelfGuideForError(string code, string tool, string detail) => code switch
    {
        "C1001" => "未发现运行中的 AutoCAD / Civil 3D 进程。请启动 Civil 3D 并打开目标图纸，网关会自动发现并重连。",
        "C1002" => "宿主管道不可达（可能正在启动或刚重启）。等待 2 秒后直接重试即可自动重连。",
        "E1001" => $"工具 {tool} 入参不合法（{detail}）。请对照 tools/list 中的 inputSchema 修正字段名或范围后重试。",
        "E1002" => "参数摘要或图纸指纹不匹配。请重新调用 read_overview 刷新指纹或直接使用 write.autoApprove=true 模式。",
        "E1003" => "CAD 当前处于交互命令中（CMDACTIVE!=0 或非静默态）。稍候 1 秒待用户结束当前命令后重试。",
        "E1004" => "CAD 主线程未响应 Idle（可能弹出模态对话框）。请关闭 CAD 弹窗后重试。",
        "E1005" => "当前无活动图纸或图纸为只读。请在 CAD 中打开可写图纸后重试。",
        "E2003" => "写通道未开启（write.enabled=false）。请在 %LOCALAPPDATA%\\EngineeringPlatform\\config.json 中设置 write.enabled=true 与 write.autoApprove=true 并重启 CAD。",
        _ => $"执行 {tool} 遇到 {code}（{detail}），可调用 sys_health 排查宿主状态或用 write_undo 回滚。"
    };

    private static JsonObject WrapToolResult(JsonObject payload, bool isError)
    {
        string summary = payload["summary"]?.GetValue<string>()
            ?? (isError ? $"Error: {payload["code"]} - {payload["detail"]}" : "OK");
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = summary },
            new JsonObject { ["type"] = "text", ["text"] = CanonicalJson.SerializeCompact(payload) }
        };
        return new JsonObject
        {
            ["content"] = content,
            ["structuredContent"] = payload,
            ["isError"] = isError
        };
    }

    private static JsonObject MakeRpcResult(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    private static JsonObject MakeRpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static (List<JsonObject>, string) LoadEmbeddedTools()
    {
        var asm = typeof(McpServer).Assembly;
        using var stream = asm.GetManifestResourceStream("protocol/tools.schema.json")
            ?? throw new InvalidOperationException("Embedded resource protocol/tools.schema.json missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] raw = ms.ToArray();
        string hash = "sha256:" + CanonicalJson.Sha256Hex(raw).Substring(0, 16);

        var doc = JsonNode.Parse(Encoding.UTF8.GetString(raw))?.AsObject()
            ?? throw new InvalidOperationException("Invalid tools.schema.json");
        var arr = doc["tools"]?.AsArray() ?? new JsonArray();
        var list = new List<JsonObject>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var entry = new JsonObject
            {
                ["name"] = obj["name"]?.GetValue<string>(),
                ["title"] = obj["title"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>(),
                ["description"] = obj["description"]?.GetValue<string>() ?? "",
                ["inputSchema"] = obj["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
            };
            if (obj["annotations"] is JsonObject ann) entry["annotations"] = ann.DeepClone();
            list.Add(entry);
        }

        // 追加 3 个进程外 MFC / Win32 模态对话框控制工具（专为南方 CASS / 飞时达 / 天正等插件弹窗设计）
        list.Add(new JsonObject
        {
            ["name"] = "ui_list_dialogs",
            ["title"] = "探测 CAD 模态弹窗与控件树",
            ["description"] = "进程外扫描指定 CAD（targetPid）当前可见的 MFC (#32770) 模态对话框，返回标题与每个输入框/按钮/复选框/下拉框的 ctrlId、自动配对中文标签与当前值。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["targetPid"] = new JsonObject { ["type"] = "integer", ["description"] = "目标 CAD 进程 PID（省略则扫描全部运行中 CAD）" },
                    ["titleContains"] = new JsonObject { ["type"] = "string", ["description"] = "可选：按弹窗标题关键字筛选" },
                    ["includeStatics"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否包含纯文本 Static/GroupBox 控件（默认 false）" }
                }
            }
        });
        list.Add(new JsonObject
        {
            ["name"] = "ui_wait_dialog",
            ["title"] = "等待 CAD 弹出模态对话框",
            ["description"] = "在投递会弹窗的命令（如南方 CASS FGW / 飞时达命令）后，轮询等待模态框出现并返回其完整控件清单。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["targetPid"] = new JsonObject { ["type"] = "integer" },
                    ["titleContains"] = new JsonObject { ["type"] = "string" },
                    ["timeoutMs"] = new JsonObject { ["type"] = "integer", ["default"] = 5000 }
                }
            }
        });
        list.Add(new JsonObject
        {
            ["name"] = "ui_interact_dialog",
            ["title"] = "自动填写与点击 CAD 模态弹窗",
            ["description"] = "对目标 CAD 的 MFC 模态对话框批量执行 set_text / set_check / select_combo / select_tab / browse_file / send_key / click / close 操作，自动触发 MFC 联动、支持一键点 [...] 走二级文件选择框与异步点击防死锁。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("actions"),
                ["properties"] = new JsonObject
                {
                    ["targetPid"] = new JsonObject { ["type"] = "integer" },
                    ["dialogTitle"] = new JsonObject { ["type"] = "string", ["description"] = "可选：按弹窗标题关键字匹配（默认最顶层弹窗）" },
                    ["dialogHwnd"] = new JsonObject { ["type"] = "string", ["description"] = "可选：按 ui_list_dialogs 返回的 hwnd 精确匹配" },
                    ["actions"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["required"] = new JsonArray("action"),
                            ["properties"] = new JsonObject
                            {
                                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("set_text", "set_check", "select_combo", "select_tab", "browse_file", "send_key", "click", "close", "cancel") },
                                ["target"] = new JsonObject { ["type"] = "string", ["description"] = "按配对标签或按钮文字匹配（如 '确定'、'方格宽度'、'...'）" },
                                ["ctrlId"] = new JsonObject { ["type"] = "integer", ["description"] = "按 MFC 控件 ID 精确匹配（如 1=确定, 2=取消, 447=CASS浏览按钮）" },
                                ["value"] = new JsonObject { ["type"] = "string", ["description"] = "文本值 / 文件完整路径（browse_file）/ 按键名（ENTER/ESC/TAB/SPACE）" },
                                ["checked"] = new JsonObject { ["type"] = "boolean" },
                                ["index"] = new JsonObject { ["type"] = "integer", ["description"] = "下拉框或选项卡 0-based 序号" },
                                ["delayMs"] = new JsonObject { ["type"] = "integer", ["default"] = 120 }
                            }
                        }
                    }
                }
            }
        });
        list.Add(new JsonObject
        {
            ["name"] = "ui_read_cmdline",
            ["title"] = "读取 CAD 命令行状态与当前提示",
            ["description"] = "即使 CAD 处于交互命令中（CMDACTIVE!=0），也可读取当前命令名(CMDNAMES)、命令行提示(LASTPROMPT)、捕捉(OSMODE)与当前层(CLAYER)，用于精准驱动 CASS/飞时达多步交互。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["targetPid"] = new JsonObject { ["type"] = "integer" }
                }
            }
        });
        list.Add(new JsonObject
        {
            ["name"] = "ui_cass_commands",
            ["title"] = "检索 CASS / 飞时达 / Civil 3D 全功能命令与自动化规范",
            ["description"] = "按中文功能名或领域检索南方 CASS 10/11、飞时达 FastTFT V15.x 与 Autodesk Civil 3D 2019–2026 全部 12 大模块（曲面/路线/纵断面/装配/道路/断面/放坡/管网/桥梁/出图/BIM与IFC）的命令名、.NET API 与避坑要点，并可补扫本机 CASS .cuix 菜单。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "中文关键词或命令名，如 '方格网'、'等高线'、'三角网'、'两期'、'shp'" },
                    ["domain"] = new JsonObject { ["type"] = "string", ["description"] = "可选领域过滤" },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["default"] = 30 }
                }
            }
        });

        return (list, hash);
    }

    // ---------------- Streamable HTTP 传输层 ----------------

    public async Task RunHttpAsync(int port, string? bearerToken, bool requireAuth, CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        Console.Error.WriteLine($"[ep-mcp] Streamable HTTP listening on http://127.0.0.1:{port}/mcp (tools={_tools.Count}, auth={requireAuth}, catalog={_catalogHash})");
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleHttpContextAsync(ctx, port, bearerToken, requireAuth, ct), ct);
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext ctx, int port, string? bearerToken, bool requireAuth, CancellationToken ct)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            // v3.0 防跨站窃图防线 #6-1：严禁带 Origin 头的浏览器跨域请求
            if (!string.IsNullOrEmpty(req.Headers["Origin"]))
            {
                await WriteHttpJsonAsync(resp, 403, new JsonObject { ["error"] = "origin_header_forbidden" });
                return;
            }

            // v3.0 防跨站窃图防线 #6-2：强制校验 Host 头防 DNS Rebinding
            string host = (req.Headers["Host"] ?? "").Trim();
            if (!string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpJsonAsync(resp, 403, new JsonObject { ["error"] = "invalid_host_header", ["host"] = host });
                return;
            }

            string path = req.Url?.AbsolutePath ?? "/";

            // GET /health（轻量活性探针）
            if (req.HttpMethod == "GET" && path == "/health")
            {
                var health = new JsonObject
                {
                    ["ok"] = true,
                    ["server"] = ServerName,
                    ["version"] = ServerVersion,
                    ["tools"] = _tools.Count,
                    ["catalogHash"] = _catalogHash,
                    ["hostInstanceId"] = _host.HostInstanceId
                };
                await WriteHttpJsonAsync(resp, 200, health);
                return;
            }

            // v3.0 防跨站窃图防线 #6-3：强制要求 Authorization: Bearer <EP_MCP_TOKEN>
            if (requireAuth)
            {
                string auth = req.Headers["Authorization"] ?? "";
                if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(bearerToken) ||
                    !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(auth.Substring(7)),
                        Encoding.UTF8.GetBytes(bearerToken)))
                {
                    await WriteHttpJsonAsync(resp, 401, new JsonObject { ["error"] = "unauthorized_bearer_required" });
                    return;
                }
            }

            if (path != "/mcp")
            {
                await WriteHttpJsonAsync(resp, 404, new JsonObject { ["error"] = "not_found", ["endpoint"] = "/mcp" });
                return;
            }

            if (req.HttpMethod == "DELETE")
            {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            if (req.HttpMethod != "POST")
            {
                await WriteHttpJsonAsync(resp, 405, new JsonObject { ["error"] = "method_not_allowed" });
                return;
            }

            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(ct);
            JsonObject? rpc;
            try
            {
                rpc = JsonNode.Parse(body) as JsonObject;
            }
            catch
            {
                await WriteHttpJsonAsync(resp, 400, MakeRpcError(null, -32700, "parse_error"));
                return;
            }
            if (rpc == null)
            {
                await WriteHttpJsonAsync(resp, 400, MakeRpcError(null, -32600, "invalid_request"));
                return;
            }

            var rpcResp = await HandleJsonRpcAsync(rpc, ct);
            if (rpcResp == null)
            {
                resp.StatusCode = 202;
                resp.Close();
                return;
            }

            await WriteHttpJsonAsync(resp, 200, rpcResp);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteHttpJsonAsync(resp, 500, MakeRpcError(null, -32603, ex.Message));
            }
            catch { }
        }
    }

    private static async Task WriteHttpJsonAsync(HttpListenerResponse resp, int statusCode, JsonObject body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(CanonicalJson.SerializeCompact(body));
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    // ---------------- Stdio 兼容传输层 ----------------

    public async Task RunStdioAsync(CancellationToken ct)
    {
        Console.Error.WriteLine($"[ep-mcp] Stdio mode ready (tools={_tools.Count}, catalog={_catalogHash})");
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var stdoutLock = new SemaphoreSlim(1, 1);

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? rpc;
            try { rpc = JsonNode.Parse(line) as JsonObject; }
            catch
            {
                await WriteStdioLineAsync(MakeRpcError(null, -32700, "parse_error"), stdoutLock, ct);
                continue;
            }
            if (rpc == null) continue;

            var resp = await HandleJsonRpcAsync(rpc, ct);
            if (resp != null)
            {
                await WriteStdioLineAsync(resp, stdoutLock, ct);
            }
        }
    }

    private static async Task WriteStdioLineAsync(JsonObject obj, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Console.Out.WriteLine(CanonicalJson.SerializeCompact(obj));
            await Console.Out.FlushAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        _router.Dispose();
        _store.Dispose();
    }
}
