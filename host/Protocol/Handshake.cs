using System;
using System.Collections.Generic;
using System.Globalization;
using EngineeringPlatform.Host.Semantic;
using EngineeringPlatform.Host.Session;

namespace EngineeringPlatform.Host.Protocol
{
    /// <summary>hello 帧的解析结果。</summary>
    internal sealed class HelloRequest
    {
        public string ProtocolVersion = "1.0";
        public string ClientName = "unknown";
        public string ClientVersion = "0.0.0";
        public int? ClientPid;
        public string ClientNonce = string.Empty;
        public List<string> RequestedFeatures = new List<string>();

        /// <summary>解析 hello 帧；失败时返回 null 并给出错误对象。</summary>
        public static HelloRequest Parse(Dictionary<string, object> frame, out WireError error)
        {
            error = null;
            if (frame == null)
            {
                error = WireError.Of(ErrorCodes.E1001, "hello_frame_missing");
                return null;
            }

            string kind = JsonCodec.GetStr(frame, "kind");
            if (!string.Equals(kind, "hello", StringComparison.Ordinal))
            {
                error = WireError.Of(ErrorCodes.E1001, "expected_hello", "kind");
                return null;
            }

            HelloRequest request = new HelloRequest();
            request.ProtocolVersion = JsonCodec.GetStr(frame, "protocolVersion");
            if (!Handshake.TryParseVersion(request.ProtocolVersion, out int major, out int minor))
            {
                error = WireError.Of(ErrorCodes.E4001, "invalid_protocol_version", "protocolVersion");
                return null;
            }
            if (major != Handshake.MajorVersion)
            {
                error = WireError.Of(ErrorCodes.E4001, "major_mismatch", "protocolVersion");
                return null;
            }

            Dictionary<string, object> clientInfo = JsonCodec.GetDict(frame, "clientInfo");
            if (clientInfo == null)
            {
                error = WireError.Of(ErrorCodes.E1001, "clientInfo_missing", "clientInfo");
                return null;
            }
            // D1：required 是 [name, version]（handshake.schema.json#/$defs/Hello），
            // 以前给缺省值 "unknown"/"0.0.0" 蒙混过去，等于把「畸形帧」放行。
            request.ClientName = JsonCodec.GetStr(clientInfo, "name");
            if (string.IsNullOrEmpty(request.ClientName))
            {
                error = WireError.Of(ErrorCodes.E1001, "clientInfo.name_missing", "clientInfo.name");
                return null;
            }
            request.ClientVersion = JsonCodec.GetStr(clientInfo, "version");
            if (string.IsNullOrEmpty(request.ClientVersion))
            {
                error = WireError.Of(ErrorCodes.E1001, "clientInfo.version_missing", "clientInfo.version");
                return null;
            }
            int pid;
            if (JsonCodec.TryGetInt(clientInfo, "pid", out pid))
            {
                request.ClientPid = pid;
            }

            request.ClientNonce = JsonCodec.GetStr(frame, "clientNonce");
            if (string.IsNullOrEmpty(request.ClientNonce))
            {
                error = WireError.Of(ErrorCodes.E1001, "clientNonce_missing", "clientNonce");
                return null;
            }
            // 契约：16–64 hex（handshake.schema.json 的 pattern）
            if (request.ClientNonce.Length < 16 || request.ClientNonce.Length > 64)
            {
                error = WireError.Of(ErrorCodes.E1001, "clientNonce_invalid", "clientNonce");
                return null;
            }
            for (int i = 0; i < request.ClientNonce.Length; i++)
            {
                char c = request.ClientNonce[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex)
                {
                    error = WireError.Of(ErrorCodes.E1001, "clientNonce_invalid", "clientNonce");
                    return null;
                }
            }

            List<string> features = JsonCodec.GetStringList(frame, "requestedFeatures");
            if (features != null)
            {
                request.RequestedFeatures = features;
            }
            return request;
        }
    }

    /// <summary>
    /// 握手（设计 §2.3）。版本规则：主版本必须相等；客户端次版本超前允许连接并告警 W1002。
    /// </summary>
    internal static class Handshake
    {
        public const string ProtocolVersion = "1.0";
        public const int MajorVersion = 1;
        public const int MinorVersion = 0;

        /// <summary>客户端能力名白名单（handshake.schema.json#/$defs/Hello.requestedFeatures）。</summary>
        public static readonly string[] SupportedFeatures =
        {
            "preview", "cancel", "heartbeat", "taskReconcile", "layers", "query", "describe", "anomalies"
        };

        public static bool TryParseVersion(string text, out int major, out int minor)
        {
            major = 0;
            minor = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string[] parts = text.Split('.');
            if (parts.Length != 2)
            {
                return false;
            }
            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
        }

        /// <summary>主版本相等即可兼容；客户端次版本更高时 clientAhead=true（仅告警）。</summary>
        public static bool IsCompatible(string clientVersion, out bool clientAhead)
        {
            clientAhead = false;
            int major;
            int minor;
            if (!TryParseVersion(clientVersion, out major, out minor))
            {
                return false;
            }
            if (major != MajorVersion)
            {
                return false;
            }
            clientAhead = minor > MinorVersion;
            return true;
        }

        /// <summary>
        /// 构造 hello_ack；不支持的 requestedFeatures 进 missingFeatures。
        ///
        /// 第二轮新增两个宿主身份字段（契约见 handshake.schema.json）：
        /// - hostProduct：Civil3D / AutoCAD（反射探测结果，不是猜的）；
        /// - hostRelease：R24.3 / R23.0 / R24.1（ACADVER）。
        /// 注意 hostVersion 语义保持不变 = 插件版本号（实例发现文件与 mcp/instances.py 都按此解析），
        /// 不复用为 CAD 版本；CAD 版本单独走 hostRelease。
        /// </summary>
        public static Dictionary<string, object> BuildHelloAck(
            HelloRequest request,
            SessionRegistry sessions,
            string hostVersion,
            out List<WireWarning> warnings,
            out List<string> missingFeatures)
        {
            warnings = new List<WireWarning>();
            missingFeatures = new List<string>();
            if (!IsCompatible(request.ProtocolVersion, out bool clientAhead))
            {
                return null;
            }
            if (clientAhead)
            {
                warnings.Add(WireWarning.Of(ErrorCodes.W1002, "client_ahead:" + request.ProtocolVersion));
            }

            for (int i = 0; i < request.RequestedFeatures.Count; i++)
            {
                string feature = request.RequestedFeatures[i];
                if (Array.IndexOf(SupportedFeatures, feature) >= 0)
                {
                    continue;
                }
                // 已知工具名不视为 feature（客户端可能误传），只有真正未知的才回报
                if (!ToolCatalog.Exists(feature))
                {
                    missingFeatures.Add(feature);
                }
            }

            string token = sessions.IssueToken();

            Dictionary<string, object> ack = new Dictionary<string, object>(StringComparer.Ordinal);
            ack["kind"] = "hello_ack";
            ack["protocolVersion"] = ProtocolVersion;
            ack["hostVersion"] = hostVersion;
            ack["hostProduct"] = CivilApiProbe.ProductName;
            ack["hostRelease"] = CivilApiProbe.ReleaseTag;
            ack["hostInstanceId"] = sessions.HostInstanceId;
            ack["sessionToken"] = token;
            ack["serverTimeUtc"] = TimeUtil.NowIso();
            ack["clientNonceEcho"] = request.ClientNonce;
            ack["writeEnabled"] = HostCapabilities.WriteEnabled;
            ack["capabilities"] = HostCapabilities.Build();
            ack["gaps"] = HostCapabilities.BuildGaps();
            ack["missingFeatures"] = ToObjectList(missingFeatures);
            // D3：warnings 以前只构造、只写日志，客户端永远看不到（_clientAhead 只赋值不读）。
            // 现在 hello_ack 带上 warnings，W1002 client_ahead 才真正上线；
            // 空数组也照发，客户端不用判断字段是否存在。
            List<object> warningList = new List<object>(warnings.Count);
            for (int i = 0; i < warnings.Count; i++)
            {
                warningList.Add(warnings[i].ToDict());
            }
            ack["warnings"] = warningList;
            return ack;
        }

        private static List<object> ToObjectList(List<string> values)
        {
            List<object> list = new List<object>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                list.Add(values[i]);
            }
            return list;
        }
    }

    /// <summary>宿主能力与缺口（握手与 sys_capabilities 共用同一份构造逻辑）。</summary>
    internal static class HostCapabilities
    {
        public static readonly string[] Domains = { "sys", "read", "task", "export", "preview", "batch" };

        public static readonly string[] DisabledDomains = { "cad", "civil", "calc", "dyn", "skill", "file", "rev", "p3d" };

        /// <summary>
        /// 写通道开关，由 <c>HostRuntime</c> 启动时从配置（`write.enabled`）写入。
        /// 用静态字段是因为宿主是进程内单实例、且握手构造点拿不到 runtime 实例；
        /// 不要在别处写它 —— 唯一赋值点在 HostRuntime.Start。
        /// </summary>
        public static bool WriteEnabled;

        /// <summary>全自主静默写开关（v3.0，`write.autoApprove`）。</summary>
        public static bool WriteAutoApprove;

        /// <summary>capabilities 对象（设计 §2.3 的 M0 固定值 + 第二轮 civilApi 实测值）。</summary>
        public static Dictionary<string, object> Build()
        {
            Dictionary<string, object> caps = new Dictionary<string, object>(StringComparer.Ordinal);

            List<object> domains = new List<object>();
            for (int i = 0; i < Domains.Length; i++)
            {
                domains.Add(Domains[i]);
            }
            if (WriteEnabled)
            {
                domains.Add("write");
            }
            caps["domains"] = domains;
            caps["writeEnabled"] = WriteEnabled;
            caps["writeAutoApprove"] = WriteAutoApprove;

            Dictionary<string, object> civil = new Dictionary<string, object>(StringComparer.Ordinal);
            civil["available"] = CivilApiProbe.Available;
            civil["product"] = CivilApiProbe.ProductName;
            civil["release"] = CivilApiProbe.ReleaseTag;
            civil["probe"] = CivilApiProbe.ProbeText;
            civil["reason"] = CivilApiProbe.Reason;
            // M0 只读：Civil 3D 的创建/修改 API 一个都没实现，这里如实写明
            civil["writeSupported"] = false;
            caps["civilApi"] = civil;

            Dictionary<string, object> preview = new Dictionary<string, object>(StringComparer.Ordinal);
            preview["formats"] = new List<object> { "png" };
            preview["maxWidth"] = 2400;
            preview["maxHeight"] = 1600;
            preview["renderMode"] = "schematic";
            caps["preview"] = preview;

            caps["messageLimitBytes"] = 1048576;

            Dictionary<string, object> heartbeat = new Dictionary<string, object>(StringComparer.Ordinal);
            heartbeat["intervalMs"] = 5000;
            heartbeat["missLimit"] = 3;
            caps["heartbeat"] = heartbeat;

            caps["cancellation"] = true;
            caps["toolCatalogHash"] = ToolCatalog.CatalogHash;
            return caps;
        }

        /// <summary>
        /// 缺口清单。Civil 3D 可用时不再上报 civil 缺口（第二轮修正：
        /// 装上就是装上，不能继续按「未安装」骗调用方）；
        /// 不可用时必须如实上报，因为代理对象确实读不出语义。
        /// </summary>
        public static List<object> BuildGaps()
        {
            List<object> gaps = new List<object>();
            if (!CivilApiProbe.Available)
            {
                Dictionary<string, object> gap = new Dictionary<string, object>(StringComparer.Ordinal);
                gap["capability"] = "civil.roadway|corridor|surface|pipe_network";
                gap["status"] = "absent";
                gap["reason"] = "Civil 3D 托管 API 不可用（AeccDbMgd 无法解析）：相关对象以 ACAD_PROXY_ENTITY 出现";
                gap["nextAction"] = "换用 Civil 3D 宿主（含 C3D\\AeccDbMgd.dll），或改用 AutoCAD 原生图元并在成果中明示降级";
                gaps.Add(gap);
            }

            Dictionary<string, object> write = new Dictionary<string, object>(StringComparer.Ordinal);
            write["capability"] = "write.*";
            if (WriteEnabled)
            {
                List<string> writeNames = new List<string>();
                foreach (ToolDescriptor t in ToolCatalog.All)
                {
                    if (t != null && t.Name != null && t.Name.StartsWith("write_", StringComparison.Ordinal))
                    {
                        writeNames.Add(t.Name);
                    }
                }
                write["status"] = "available";
                write["reason"] = "写通道已开启，支持 " + writeNames.Count + " 个写工具：" + string.Join(" / ", writeNames.ToArray());
                write["nextAction"] = "批量原生建图元优先使用 write_entities；写操作不自动保存文件，支持 write_undo 撤销";
            }
            else
            {
                write["status"] = "absent";
                write["reason"] = "写入通道关闭（writeEnabled=false）";
                write["nextAction"] = "在 config.json 里设 write.enabled=true 并重启 CAD，开启写通道";
            }
            gaps.Add(write);
            return gaps;
        }

        /// <summary>工具名摘要（sys_capabilities.tools）。</summary>
        public static List<object> BuildToolNames()
        {
            List<object> names = new List<object>();
            foreach (ToolDescriptor tool in ToolCatalog.All)
            {
                names.Add(tool.Name);
            }
            return names;
        }
    }
}
