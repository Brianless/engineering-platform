using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Base
{
    /// <summary>
    /// 基座（new-acad / civil3d-mcp 插件）的 TCP 客户端。
    ///
    /// 为什么由宿主来调，而不是网关直接连：
    /// - 宿主已经在 CAD 进程内、已经在主线程分派链上，错误码/审计/预算一条线；
    /// - 基座的 430 个方法**永不进 tools/list**（docs/10 §十五 的 B 路线），
    ///   模型只看得到我们薄工具的名字，转调发生在这一层内部。
    ///
    /// 协议（third_party/new-acad/.../RpcTcpServer.cs）：
    ///   一条连接一帧 JSON `{"auth":"&lt;token&gt;","jsonrpc":"2.0","id":1,"method":"...","params":{...}}`，
    ///   服务端读一帧、回一帧、关连接。令牌来自环境变量 `EP_BRIDGE_TOKEN`（fail-closed：无令牌时服务端拒绝一切）。
    /// </summary>
    internal static class BaseClient
    {
        public const int DefaultPort = 8080;
        public const string TokenVariable = "EP_BRIDGE_TOKEN";

        /// <summary>基座是否可达：优先进程内托管（零端口冲突），否则探测 TCP :8080。</summary>
        public static bool IsReachable(int timeoutMs)
        {
            if (InProcessBaseBridge.IsAvailable)
            {
                return true;
            }
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", DefaultPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        return false;
                    }
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 调一个基座方法。优先进程内直调（InProcessBaseBridge，零端口/零令牌），
        /// 未就绪时回退 TCP :8080。绝不把基座原始报错直接透传给模型。
        /// </summary>
        public static bool TryCall(string method, Dictionary<string, object> parameters, int timeoutMs,
            out Dictionary<string, object> result, out WireError error)
        {
            result = null;
            error = null;

            if (InProcessBaseBridge.IsAvailable)
            {
                return InProcessBaseBridge.TryCall(method, parameters, timeoutMs, out result, out error);
            }

            string token = Environment.GetEnvironmentVariable(TokenVariable);
            if (string.IsNullOrEmpty(token))
            {
                error = WireError.Of(ErrorCodes.E3002, "base_token_missing:" + TokenVariable);
                return false;
            }

            string request;
            try
            {
                Dictionary<string, object> frame = new Dictionary<string, object>(StringComparer.Ordinal);
                frame["auth"] = token;
                frame["jsonrpc"] = "2.0";
                frame["id"] = 1;
                frame["method"] = method;
                frame["params"] = parameters ?? new Dictionary<string, object>(StringComparer.Ordinal);
                request = JsonCodec.Serialize(frame);
            }
            catch (Exception ex)
            {
                HostLog.Exception("base", "request_build_failed", ex);
                error = WireError.Of(ErrorCodes.E3002, "base_request_build_failed");
                return false;
            }

            string raw;
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", DefaultPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        error = WireError.Of(ErrorCodes.E3002, "base_connect_timeout");
                        return false;
                    }
                    client.EndConnect(ar);
                    client.ReceiveTimeout = timeoutMs;

                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] payload = Encoding.UTF8.GetBytes(request);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                        raw = ReadOneFrame(stream, timeoutMs);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("base", "call_failed", ex);
                error = WireError.Of(ErrorCodes.E3002, "base_unreachable");
                return false;
            }

            Dictionary<string, object> response;
            if (!JsonCodec.TryParseObject(raw, out response))
            {
                error = WireError.Of(ErrorCodes.E3002, "base_bad_response");
                return false;
            }

            Dictionary<string, object> errorNode = JsonCodec.GetDict(response, "error");
            if (errorNode != null)
            {
                // 基座的 code 是 JSON-RPC 数值码或 CIVIL3D.* 标记；只保留我方 detail，原文进日志
                HostLog.Warn("base", "method_error", "method", method,
                    "code", JsonCodec.GetStr(errorNode, "message", "-"));
                error = WireError.Of(ErrorCodes.E2001, "base_method_failed:" + method);
                return false;
            }

            result = JsonCodec.GetDict(response, "result");
            if (result == null)
            {
                error = WireError.Of(ErrorCodes.E3002, "base_empty_result");
                return false;
            }
            return true;
        }

        /// <summary>读一帧：基座读完一帧就回一帧并关连接，所以读到「能解析的完整 JSON」即止。</summary>
        private static string ReadOneFrame(NetworkStream stream, int timeoutMs)
        {
            byte[] buffer = new byte[64 * 1024];
            MemoryStream accumulated = new MemoryStream();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                accumulated.Write(buffer, 0, read);

                string text = Encoding.UTF8.GetString(accumulated.ToArray());
                Dictionary<string, object> probe;
                if (JsonCodec.TryParseObject(text, out probe))
                {
                    return text;
                }
            }
            return Encoding.UTF8.GetString(accumulated.ToArray());
        }
    }
}
