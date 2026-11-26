using System;
using System.Collections.Generic;
using System.Globalization;

namespace EngineeringPlatform.Host.Protocol
{
    /// <summary>
    /// 响应封套构造器（envelope.schema.json#/$defs/ResponseEnvelope）。
    /// 所有响应（成功/失败/协议级错误）都必须经由此处生成，保证字段与命名一致。
    /// </summary>
    internal sealed class ResponseBuilder
    {
        public string SessionToken = string.Empty;
        public string HostInstanceId = string.Empty;
        public string OpId = string.Empty;
        public string Tool = string.Empty;
        public string ParamsDigestValue;
        public string TargetFingerprint;
        public DateTime StartedUtc = DateTime.UtcNow;

        private readonly List<WireWarning> _warnings = new List<WireWarning>();

        public void AddWarning(WireWarning warning)
        {
            if (warning == null)
            {
                return;
            }
            // 最多 32 条（schema maxItems），且同码只留一条
            for (int i = 0; i < _warnings.Count; i++)
            {
                if (string.Equals(_warnings[i].Code, warning.Code, StringComparison.Ordinal))
                {
                    return;
                }
            }
            if (_warnings.Count < 32)
            {
                _warnings.Add(warning);
            }
        }

        public void AddWarnings(IEnumerable<WireWarning> warnings)
        {
            if (warnings == null)
            {
                return;
            }
            foreach (WireWarning w in warnings)
            {
                AddWarning(w);
            }
        }

        /// <summary>耗时（毫秒，单调递增不依赖系统时钟回拨）。</summary>
        public long ElapsedMs
        {
            get
            {
                double ms = (DateTime.UtcNow - StartedUtc).TotalMilliseconds;
                if (ms < 0)
                {
                    return 0;
                }
                return (long)Math.Round(ms);
            }
        }

        /// <summary>构造成功封套（ok=true，status 默认 verified）。</summary>
        public Dictionary<string, object> BuildOk(object result, string status = "verified")
        {
            return Build(result, true, error: null, status: status);
        }

        /// <summary>构造失败封套（ok=false，result=null）。</summary>
        public Dictionary<string, object> BuildFail(WireError error, string status = "failed")
        {
            WireError actual = error ?? WireError.Of(ErrorCodes.E1001, "unspecified_failure");
            return Build(null, false, actual, status);
        }

        private Dictionary<string, object> Build(object result, bool ok, WireError error, string status)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["protocolVersion"] = Handshake.ProtocolVersion;
            dict["sessionToken"] = SessionToken ?? string.Empty;
            dict["hostInstanceId"] = HostInstanceId ?? string.Empty;
            dict["opId"] = OpId;
            dict["tool"] = Tool ?? string.Empty;
            dict["status"] = status;
            dict["ok"] = ok;
            dict["startedAt"] = TimeUtil.ToIso(StartedUtc);
            dict["elapsedMs"] = ElapsedMs;
            dict["paramsDigest"] = ParamsDigestValue;
            dict["targetFingerprint"] = TargetFingerprint;

            List<object> warnings = new List<object>(_warnings.Count);
            for (int i = 0; i < _warnings.Count; i++)
            {
                warnings.Add(_warnings[i].ToDict());
            }
            dict["warnings"] = warnings;
            dict["result"] = result;
            dict["error"] = error == null ? null : error.ToDict();
            return dict;
        }
    }

    /// <summary>协议级错误帧（无业务封套上下文时使用，例如握手前收到坏帧）。</summary>
    internal static class EnvelopeFrames
    {
        /// <summary>
        /// 分帧/协议级错误帧。opId 为 null（设计 §2.1 明确允许），
        /// sessionToken 用全 0 占位以满足 schema 的 64 hex 约束（该帧不参与业务校验）。
        /// </summary>
        public static Dictionary<string, object> ProtocolError(string code, string detail, string tool = null, string field = null)
        {
            ResponseBuilder builder = new ResponseBuilder();
            builder.SessionToken = new string('0', 64);
            builder.HostInstanceId = new string('0', 0);
            builder.OpId = null;
            builder.Tool = tool ?? "unknown";
            builder.StartedUtc = DateTime.UtcNow;
            return builder.BuildFail(WireError.Of(code, detail, field));
        }

        /// <summary>bye 帧（协议违规 / 超时 / 正常关闭时由宿主动发出）。</summary>
        public static Dictionary<string, object> Bye(string reason, string detail = null)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["kind"] = "bye";
            dict["reason"] = reason;
            if (!string.IsNullOrEmpty(detail))
            {
                dict["detail"] = detail;
            }
            return dict;
        }

        /// <summary>格式化 ms 为耗时字符串（日志用，统一不变式区域）。</summary>
        public static string FormatMs(long ms)
        {
            return ms.ToString(CultureInfo.InvariantCulture);
        }
    }
}
