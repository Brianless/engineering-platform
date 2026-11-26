using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Pipe;
using EngineeringPlatform.Host.Preview;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Scheduling;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 六个 sys_* 工具：心跳、运行状态、版本、健康检查、能力与缺口、实例列表。
    /// 这些工具不读图纸，只反映宿主自身状态。
    /// </summary>
    internal static class SysToolHandlers
    {
        public static Dictionary<string, object> Handle(ToolContext context)
        {
            string tool = context.Item.Tool;
            switch (tool)
            {
                case "sys_ping":
                    return Ping(context);
                case "sys_status":
                    return Status(context);
                case "sys_version":
                    return Version(context);
                case "sys_health":
                    return Health(context);
                case "sys_capabilities":
                    return Capabilities(context);
                case "sys_list_instances":
                    return ListInstances(context);
                default:
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_sys_tool:" + tool, "tool"));
            }
        }

        private static Dictionary<string, object> Ping(ToolContext context)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["pong"] = true;
            result["serverTimeUtc"] = TimeUtil.NowIso();
            result["hostInstanceId"] = context.Runtime.Sessions.HostInstanceId;
            return result;
        }

        private static Dictionary<string, object> Status(ToolContext context)
        {
            HostRuntime runtime = context.Runtime;
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            bool pumping = runtime.Dispatcher.IsPumping(3000);

            result["summary"] = ToolResultHelper.Summary("【宿主状态】" + runtime.Sessions.HostInstanceId
                + "｜运行 " + FormatUptime(runtime.Sessions.UptimeMs)
                + "｜近 60 s 请求 " + runtime.Stats.RequestsLast60s + " 次（错误 " + runtime.Stats.ErrorsLast60s + "）"
                + "｜队列 " + runtime.Dispatcher.QueueLength
                + "｜主线程泵送 " + (pumping ? "正常" : "异常（可能有模态对话框）")
                + "｜缓存命中率 " + (runtime.CacheHitRate * 100D).ToString("0", CultureInfo.InvariantCulture) + "%");
            result["uptimeMs"] = runtime.Sessions.UptimeMs;
            result["requestsLast60s"] = runtime.Stats.RequestsLast60s;
            result["errorsLast60s"] = runtime.Stats.ErrorsLast60s;
            result["queueLength"] = runtime.Dispatcher.QueueLength;
            result["idlePumping"] = pumping;
            result["cacheHitRate"] = Math.Round(runtime.CacheHitRate, 4);
            result["sessions"] = runtime.Sessions.ActiveSessionCount;
            return result;
        }

        private static string FormatUptime(long uptimeMs)
        {
            TimeSpan span = TimeSpan.FromMilliseconds(Math.Max(uptimeMs, 0));
            if (span.TotalHours >= 1)
            {
                return ((int)span.TotalHours) + " h " + span.Minutes + " min";
            }
            if (span.TotalMinutes >= 1)
            {
                return ((int)span.TotalMinutes) + " min " + span.Seconds + " s";
            }
            return (int)span.TotalSeconds + " s";
        }

        private static Dictionary<string, object> Version(ToolContext context)
        {
            Assembly assembly = typeof(SysToolHandlers).Assembly;
            AssemblyName name = assembly.GetName();

            Dictionary<string, object> host = new Dictionary<string, object>(StringComparer.Ordinal);
            host["assembly"] = name.Name;
            host["version"] = name.Version == null ? "0.0.0" : name.Version.ToString();
            host["buildUtc"] = BuildUtc(assembly);

            Dictionary<string, object> autocad = new Dictionary<string, object>(StringComparer.Ordinal);
            autocad["product"] = HostStateProbe.TryGetProductName();
            autocad["release"] = SafeSystemVariable("ACADVER", "unknown");
            autocad["version"] = HostStateProbe.TryGetProductVersion();

            Dictionary<string, object> protocol = new Dictionary<string, object>(StringComparer.Ordinal);
            protocol["version"] = Handshake.ProtocolVersion;
            protocol["toolCatalogHash"] = ToolCatalog.CatalogHash;
            protocol["messageLimitBytes"] = Framing.MaxPayloadBytes;

            Dictionary<string, object> dotnet = new Dictionary<string, object>(StringComparer.Ordinal);
            dotnet["runtime"] = Environment.Version.ToString();
            dotnet["framework"] = ".NET Framework " + Environment.Version;
            dotnet["os"] = Environment.OSVersion.VersionString;
            dotnet["processBitness"] = Environment.Is64BitProcess ? "x64" : "x86";

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【版本信息】宿主 " + host["version"] + "（net48）"
                + "｜" + autocad["product"] + " " + autocad["version"]
                + "｜协议 " + protocol["version"]
                + "｜目录哈希 " + protocol["toolCatalogHash"]);
            result["host"] = host;
            result["autocad"] = autocad;
            result["protocol"] = protocol;
            result["dotnet"] = dotnet;
            return result;
        }

        private static string BuildUtc(Assembly assembly)
        {
            try
            {
                if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                {
                    return TimeUtil.ToIso(File.GetLastWriteTimeUtc(assembly.Location));
                }
            }
            catch (Exception)
            {
                // 取不到构建时间不影响版本响应
            }
            return TimeUtil.NowIso();
        }

        private static string SafeSystemVariable(string name, string fallback)
        {
            try
            {
                object value = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable(name);
                return value == null ? fallback : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static Dictionary<string, object> Health(ToolContext context)
        {
            HostRuntime runtime = context.Runtime;
            List<object> checks = new List<object>();

            bool aclOk = PipeServer.AclConfigured;
            checks.Add(Check("pipeAcl", aclOk, aclOk ? "仅当前用户 + SYSTEM" : "无法读取当前用户标识，ACL 可能未生效"));

            bool discoveryOk = runtime.Sessions.DiscoveryFileExists;
            checks.Add(Check("discoveryFile", discoveryOk,
                discoveryOk ? HostLog.SafePath(runtime.Sessions.DiscoveryPath) : "实例发现文件不存在（MCP 将报 C1001）"));

            bool logOk = HostLog.IsWritable;
            checks.Add(Check("logWritable", logOk, logOk ? "日志目录可写" : "日志目录不可写"));

            PreviewCache cache = new PreviewCache(runtime.Config.PreviewCacheDir);
            string cacheError;
            bool cacheOk = cache.EnsureDirectory(out cacheError);
            checks.Add(Check("cacheWritable", cacheOk, cacheOk ? "预览缓存目录可写" : cacheError));

            bool civil = CivilApiProbe.Available;
            checks.Add(Check("civilProbe", civil,
                civil ? "Civil 3D 托管类型可用" : CivilApiProbe.Reason + "（已作为 gaps 上报，不影响只读）"));

            bool mapOk = runtime.Mapping != null && runtime.Mapping.Loaded;
            checks.Add(Check("layerMapLoaded", mapOk,
                runtime.Mapping == null ? "映射表未加载" : runtime.Mapping.Describe()));

            // 项目库由 MCP 侧维护，宿主只做占位报告（不谎报成功/失败）
            checks.Add(Check("projectDb", true, "由 MCP 侧检查（宿主不落库）"));

            int failed = 0;
            List<string> failedIds = new List<string>();
            for (int i = 0; i < checks.Count; i++)
            {
                Dictionary<string, object> item = checks[i] as Dictionary<string, object>;
                if (item != null && item.ContainsKey("ok") && !(bool)item["ok"])
                {
                    failed++;
                    failedIds.Add(Convert.ToString(item["id"], CultureInfo.InvariantCulture));
                }
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【健康检查】" + checks.Count + " 项，通过 " + (checks.Count - failed)
                + " 项" + (failed > 0 ? "，未通过：" + string.Join("、", failedIds.ToArray()) : string.Empty)
                + (failed > 0 && failedIds.Contains("civilProbe") ? "（Civil 3D 未安装，已按缺口上报）" : string.Empty));
            result["checks"] = checks;
            return result;
        }

        private static Dictionary<string, object> Check(string id, bool ok, string detail)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item["id"] = id;
            item["ok"] = ok;
            item["detail"] = detail;
            return item;
        }

        private static Dictionary<string, object> Capabilities(ToolContext context)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);

            List<object> domains = new List<object>();
            for (int i = 0; i < HostCapabilities.Domains.Length; i++)
            {
                domains.Add(HostCapabilities.Domains[i]);
            }
            List<object> disabled = new List<object>();
            for (int i = 0; i < HostCapabilities.DisabledDomains.Length; i++)
            {
                disabled.Add(HostCapabilities.DisabledDomains[i]);
            }

            result["summary"] = ToolResultHelper.Summary("【能力与缺口】可用域 " + string.Join("/", HostCapabilities.Domains)
                + (HostCapabilities.WriteEnabled ? "｜写通道已开启（write_*）" : "｜写入通道关闭（M0）")
                + (CivilApiProbe.Available ? "｜Civil 3D 可用" : "｜Civil 3D 未安装：路线/曲面/纵断面/廊道/管网能力不可用")
                + "｜工具 " + ToolCatalog.All.Count + " 个");
            result["domains"] = domains;
            result["writeEnabled"] = HostCapabilities.WriteEnabled;
            result["disabledDomains"] = disabled;
            result["gaps"] = HostCapabilities.BuildGaps();
            result["tools"] = HostCapabilities.BuildToolNames();
            return result;
        }

        private static Dictionary<string, object> ListInstances(ToolContext context)
        {
            List<object> instances = new List<object>();
            string dir = context.Runtime.Sessions.InstanceDir;
            int alive = 0;

            try
            {
                if (Directory.Exists(dir))
                {
                    string[] files = Directory.GetFiles(dir, "*.json");
                    for (int i = 0; i < files.Length; i++)
                    {
                        Dictionary<string, object> parsed;
                        try
                        {
                            if (!JsonCodec.TryParseObject(File.ReadAllText(files[i]), out parsed))
                            {
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                        string hostInstanceId = JsonCodec.GetStr(parsed, "hostInstanceId",
                            JsonCodec.GetStr(parsed, "instanceId", "unknown"));
                        item["hostInstanceId"] = hostInstanceId;
                        item["app"] = JsonCodec.GetStr(parsed, "app", "unknown");
                        int pid;
                        bool hasPid = JsonCodec.TryGetInt(parsed, "pid", out pid);
                        item["pid"] = hasPid ? (object)pid : null;
                        item["hostVersion"] = JsonCodec.GetStr(parsed, "hostVersion", "unknown");
                        item["startedAtUtc"] = JsonCodec.GetStr(parsed, "startedAtUtc", TimeUtil.NowIso());
                        item["documentNameHash"] = JsonCodec.GetStr(parsed, "documentNameHash");
                        bool isAlive = hasPid && IsProcessAlive(pid);
                        item["alive"] = isAlive;
                        if (isAlive)
                        {
                            alive++;
                        }
                        instances.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("sys", "list_instances_failed", ex);
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【宿主实例】发现 " + instances.Count + " 个，存活 " + alive
                + " 个｜当前实例 " + context.Runtime.Sessions.HostInstanceId);
            result["instances"] = instances;
            return result;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
