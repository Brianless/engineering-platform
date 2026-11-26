using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Scheduling;

namespace EngineeringPlatform.Host.Pipe
{
    /// <summary>
    /// 单连接（设计 §3.3）。
    /// 线程约定：
    /// - 读线程：只做 ReadFrame → JSON 解析 → 封套校验 → 建 WorkItem → 入队，绝不触碰 CAD API，也绝不阻塞等结果；
    /// - 主线程：出队执行，完成后通过 <see cref="WorkItem.OnCompleted"/> 回调本类的 Send；
    /// - 写线程：本类用 _writeSync 独占写，读线程/主线程/心跳线程都可调用。
    /// 这样 task_cancel 才能在被取消的任务还在执行时被读到（读线程始终在收帧）。
    /// </summary>
    internal sealed class PipeConnection
    {
        private readonly Stream _stream;
        private readonly HostRuntime _runtime;
        private readonly object _writeSync = new object();

        private Thread _readThread;
        private Thread _heartbeatThread;
        private volatile bool _closed;
        private string _sessionToken;
        private DateTime _lastInboundUtc = DateTime.UtcNow;
        private DateTime _lastOutboundUtc = DateTime.UtcNow;
        private bool _handshaken;

        public PipeConnection(Stream stream, HostRuntime runtime)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            if (runtime == null)
            {
                throw new ArgumentNullException("runtime");
            }
            _stream = stream;
            _runtime = runtime;
        }

        public string SessionToken
        {
            get { return _sessionToken; }
        }

        public bool IsClosed
        {
            get { return _closed; }
        }

        public bool Handshaken
        {
            get { return _handshaken; }
        }

        public DateTime LastInboundUtc
        {
            get { return _lastInboundUtc; }
        }

        /// <summary>启动读线程与心跳线程。</summary>
        public void Start()
        {
            _readThread = new Thread(ReadLoop);
            _readThread.IsBackground = true;
            _readThread.Name = "ep-pipe-read";
            _readThread.Start();

            _heartbeatThread = new Thread(HeartbeatLoop);
            _heartbeatThread.IsBackground = true;
            _heartbeatThread.Name = "ep-pipe-heartbeat";
            _heartbeatThread.Start();
        }

        // ---------------- 读 ----------------

        private void ReadLoop()
        {
            try
            {
                while (!_closed)
                {
                    FrameStatus status;
                    byte[] payload = Framing.ReadFrame(_stream, out status);
                    if (status == FrameStatus.Eof)
                    {
                        HostLog.Info("pipe", "peer_eof", "token", HostLog.Mask(_sessionToken));
                        break;
                    }
                    if (status == FrameStatus.TooLarge)
                    {
                        // 已把 len 字节读完丢弃，连接继续可用
                        HostLog.Error("pipe", "frame_too_large", "limit", Framing.MaxPayloadBytes);
                        SendError(null, "unknown", null, WireError.Of(ErrorCodes.E1001, "message_too_large"));
                        continue;
                    }
                    if (status == FrameStatus.Empty)
                    {
                        HostLog.Error("pipe", "empty_frame");
                        SendBye("protocol_violation", "empty_frame");
                        break;
                    }
                    if (status == FrameStatus.ProtocolViolation)
                    {
                        HostLog.Error("pipe", "frame_protocol_violation");
                        SendBye("protocol_violation", "frame_truncated");
                        break;
                    }

                    _lastInboundUtc = DateTime.UtcNow;
                    HandlePayload(payload);
                }
            }
            catch (IOException ex)
            {
                HostLog.Info("pipe", "read_io_closed", "detail", HostLog.Trim(ex.Message, 120));
            }
            catch (ObjectDisposedException)
            {
                HostLog.Info("pipe", "read_disposed");
            }
            catch (Exception ex)
            {
                HostLog.Exception("pipe", "read_loop_failed", ex);
            }
            finally
            {
                Close("read_loop_end");
            }
        }

        private void HandlePayload(byte[] payload)
        {
            string text = JsonCodec.FromUtf8(payload);
            Dictionary<string, object> frame;
            if (!JsonCodec.TryParseObject(text, out frame))
            {
                HostLog.Error("pipe", "invalid_json", "bytes", payload.Length);
                SendError(null, "unknown", null, WireError.Of(ErrorCodes.E1001, "invalid_json"));
                return;
            }

            string kind = JsonCodec.GetStr(frame, "kind");
            if (!string.IsNullOrEmpty(kind))
            {
                if (string.Equals(kind, "hello", StringComparison.Ordinal))
                {
                    HandleHello(frame);
                    return;
                }
                if (string.Equals(kind, "bye", StringComparison.Ordinal))
                {
                    HostLog.Info("pipe", "client_bye", "reason", JsonCodec.GetStr(frame, "reason", "unspecified"));
                    Close("client_bye");
                    return;
                }
                SendError(null, "unknown", null, WireError.Of(ErrorCodes.E1001, "unknown_frame_kind:" + kind, "kind"));
                return;
            }

            HandleRequest(frame);
        }

        // ---------------- 握手 ----------------

        private void HandleHello(Dictionary<string, object> frame)
        {
            if (_handshaken)
            {
                SendError(null, "hello", null, WireError.Of(ErrorCodes.E1003, "session_busy"), "failed", false);
                return;
            }

            WireError parseError;
            HelloRequest request = HelloRequest.Parse(frame, out parseError);
            if (request == null)
            {
                WireError error = parseError ?? WireError.Of(ErrorCodes.E1001, "hello_invalid");
                SendError(null, "hello", null, error);
                if (string.Equals(error.Code, ErrorCodes.E4001, StringComparison.Ordinal))
                {
                    // 主版本不匹配：按设计直接关连接
                    SendBye("protocol_violation", "major_mismatch");
                    Close("major_mismatch");
                }
                return;
            }

            List<WireWarning> warnings;
            List<string> missingFeatures;
            string hostVersion = _runtime.Sessions.HostVersion;
            Dictionary<string, object> ack = Handshake.BuildHelloAck(request, _runtime.Sessions, hostVersion,
                out warnings, out missingFeatures);
            if (ack == null)
            {
                SendError(null, "hello", null, WireError.Of(ErrorCodes.E4001, "major_mismatch", "protocolVersion"));
                SendBye("protocol_violation", "major_mismatch");
                Close("major_mismatch");
                return;
            }

            _sessionToken = JsonCodec.GetStr(ack, "sessionToken");
            string busyReason;
            if (!_runtime.Sessions.TryBeginSession(_sessionToken, out busyReason))
            {
                HostLog.Warn("session", "session_busy", "token", HostLog.Mask(_sessionToken));
                SendError(null, "hello", null, WireError.Of(ErrorCodes.E1003, "session_busy"), "failed", false);
                Close("session_busy");
                return;
            }

            _handshaken = true;
            Send(ack);
            HostLog.Info("session", "handshake_ok",
                "token", HostLog.Mask(_sessionToken),
                "client", request.ClientName + "/" + request.ClientVersion,
                "warnings", warnings.Count);
        }

        // ---------------- 业务请求 ----------------

        private void HandleRequest(Dictionary<string, object> frame)
        {
            if (!_handshaken)
            {
                SendError(JsonCodec.GetStr(frame, "opId"), JsonCodec.GetStr(frame, "tool", "unknown"), null,
                    WireError.Of(ErrorCodes.E4001, "handshake_required"));
                return;
            }

            string opId = JsonCodec.GetStr(frame, "opId");
            string tool = JsonCodec.GetStr(frame, "tool", "unknown");
            string digest = JsonCodec.GetStr(frame, "paramsDigest");
            string targetFingerprint = JsonCodec.GetStr(frame, "targetFingerprint");

            // 1) 协议版本
            string protocolVersion = JsonCodec.GetStr(frame, "protocolVersion");
            int major;
            int minor;
            if (!Handshake.TryParseVersion(protocolVersion, out major, out minor) || major != Handshake.MajorVersion)
            {
                HostLog.Error("pipe", "protocol_major_mismatch", "got", protocolVersion);
                SendError(opId, tool, digest, WireError.Of(ErrorCodes.E4001, "major_mismatch", "protocolVersion"));
                SendBye("protocol_violation", "major_mismatch");
                Close("major_mismatch");
                return;
            }

            // 2) 会话令牌
            string token = JsonCodec.GetStr(frame, "sessionToken");
            if (string.IsNullOrEmpty(token) || !string.Equals(token, _sessionToken, StringComparison.Ordinal)
                || !_runtime.Sessions.ValidateToken(token))
            {
                HostLog.Warn("session", "session_expired", "op", opId, "token", HostLog.Mask(token));
                SendError(opId, tool, digest, WireError.Of(ErrorCodes.E4001, "session_expired"));
                return;
            }

            // 3) 审批令牌：写通道关闭时一律拒（M0 行为不变）；开启时放过，交给 WriteToolHandlers 校验。
            //    令牌实际走 params.approvalToken（工具契约的一部分），信封顶层字段保留但不再使用。
            if (JsonCodec.HasKey(frame, "approvalToken") && JsonCodec.GetStr(frame, "approvalToken") != null
                && !_runtime.Config.WriteEnabled)
            {
                SendError(opId, tool, digest, WireError.Of(ErrorCodes.E1006, "approval_token_not_accepted_m0"));
                return;
            }

            // 4) opId 必填
            if (string.IsNullOrEmpty(opId))
            {
                SendError(null, tool, digest, WireError.Of(ErrorCodes.E1001, "opId_required", "opId"));
                return;
            }

            // 5) 工具名必填 + 入参校验
            if (string.IsNullOrEmpty(tool))
            {
                SendError(opId, "unknown", digest, WireError.Of(ErrorCodes.E1001, "tool_required", "tool"));
                return;
            }

            Dictionary<string, object> parameters = JsonCodec.GetDict(frame, "params");
            if (parameters == null)
            {
                if (JsonCodec.HasKey(frame, "params"))
                {
                    SendError(opId, tool, digest, WireError.Of(ErrorCodes.E1001, "params_must_be_object", "params"));
                    return;
                }
                parameters = new Dictionary<string, object>(StringComparer.Ordinal);
            }

            // 5.5) 进程内 Win32 MFC 模态弹窗桥：直接在后台管道线程执行，不经主线程 Idle，
            //      即使 AutoCAD 正卡在南方 CASS / 飞时达的模态对话框中也能毫秒级读写控件。
            if (Win32.Win32ModalBridge.IsUiDialogTool(tool))
            {
                Dictionary<string, object> uiRes;
                WireError uiErr;
                ResponseBuilder uiBuilder = new ResponseBuilder();
                uiBuilder.SessionToken = _sessionToken;
                uiBuilder.HostInstanceId = _runtime.Sessions.HostInstanceId;
                uiBuilder.OpId = opId;
                uiBuilder.Tool = tool;
                uiBuilder.ParamsDigestValue = string.IsNullOrEmpty(digest) ? ParamsDigest.Compute(parameters) : digest;
                uiBuilder.StartedUtc = DateTime.UtcNow;

                if (Win32.Win32ModalBridge.TryExecuteOffMainThread(tool, parameters, out uiRes, out uiErr))
                {
                    Send(uiBuilder.BuildOk(uiRes));
                }
                else
                {
                    Send(uiBuilder.BuildFail(uiErr, "failed"));
                }
                return;
            }

            // 写域先判（避免把 E2003 误报为 E1001 参数错误）；ui_read_cmdline 为宿主内辅助探测工具
            if (!ToolCatalog.IsWriteDomain(tool) && !string.Equals(tool, "ui_read_cmdline", StringComparison.Ordinal))
            {
                WireError validateError;
                if (!ToolCatalog.TryValidate(tool, parameters, out validateError))
                {
                    SendError(opId, tool, digest, validateError);
                    return;
                }
            }

            int timeoutMs = JsonCodec.GetInt(frame, "timeoutMs", 15000);
            if (timeoutMs < 500)
            {
                timeoutMs = 500;
            }
            if (timeoutMs > 180000)
            {
                timeoutMs = 180000;
            }

            ResponseBuilder builder = new ResponseBuilder();
            builder.SessionToken = _sessionToken;
            builder.HostInstanceId = _runtime.Sessions.HostInstanceId;
            builder.OpId = opId;
            builder.Tool = tool;
            builder.ParamsDigestValue = string.IsNullOrEmpty(digest)
                ? ParamsDigest.Compute(parameters)
                : digest;
            builder.TargetFingerprint = string.IsNullOrEmpty(targetFingerprint) ? null : targetFingerprint;
            builder.StartedUtc = DateTime.UtcNow;

            WorkItem item = new WorkItem();
            item.OpId = opId;
            item.Tool = tool;
            item.SessionToken = _sessionToken;
            item.HostInstanceId = _runtime.Sessions.HostInstanceId;
            item.ParamsDigestValue = builder.ParamsDigestValue;
            item.TargetFingerprint = builder.TargetFingerprint;
            item.Params = parameters;
            item.TimeoutMs = timeoutMs;
            item.Cancelable = JsonCodec.GetBool(frame, "cancelable", true);
            item.AllowCommandContext =
                (string.Equals(tool, "write_command", StringComparison.Ordinal) &&
                 (JsonCodec.GetBool(parameters, "allowDuringCommand", false) ||
                  JsonCodec.GetBool(parameters, "sendEnter", false) ||
                  JsonCodec.GetBool(parameters, "cancelCommand", false))) ||
                string.Equals(tool, "ui_read_cmdline", StringComparison.Ordinal);
            item.Builder = builder;
            item.EnqueuedUtc = DateTime.UtcNow;
            item.OnCompleted = OnWorkItemCompleted;

            string rejectReason;
            if (!_runtime.Dispatcher.Enqueue(item, out rejectReason))
            {
                HostLog.Error("gate", "dispatch_rejected", "op", opId, "reason", rejectReason);
                Send(item.Builder.BuildFail(WireError.Of(ErrorCodes.E1003, rejectReason), "failed"));
                return;
            }

            HostLog.Info("router", "tool_received", "op", opId, "tool", tool,
                "digest", HostLog.Trim(builder.ParamsDigestValue, 20));
        }

        private void OnWorkItemCompleted(WorkItem item, Dictionary<string, object> response)
        {
            bool sent = Send(response);
            _runtime.RecordTask(item, response, sent);
        }

        // ---------------- 写 ----------------

        /// <summary>发送一帧；失败即关连接（调用方无需处理异常）。</summary>
        public bool Send(Dictionary<string, object> response)
        {
            if (response == null)
            {
                return false;
            }
            try
            {
                byte[] payload = JsonCodec.Utf8(JsonCodec.Serialize(response));
                if (payload.Length > Framing.MaxPayloadBytes)
                {
                    // 理论上宿主侧硬截断（350 KB）后不会到这里；兜底发一个错误帧
                    HostLog.Error("pipe", "response_too_large", "bytes", payload.Length);
                    Dictionary<string, object> shrink = new Dictionary<string, object>(response, StringComparer.Ordinal);
                    shrink["result"] = null;
                    shrink["ok"] = false;
                    shrink["status"] = "failed";
                    shrink["error"] = WireError.Of(ErrorCodes.E1001, "response_too_large").ToDict();
                    payload = JsonCodec.Utf8(JsonCodec.Serialize(shrink));
                }
                lock (_writeSync)
                {
                    if (_closed)
                    {
                        return false;
                    }
                    Framing.WriteFrame(_stream, payload);
                }
                _lastOutboundUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Exception("pipe", "write_failed", ex);
                Close("write_failed");
                return false;
            }
        }

        private void SendError(string opId, string tool, string digest, WireError error,
            string status = "failed", bool includeSession = true)
        {
            ResponseBuilder builder = new ResponseBuilder();
            builder.SessionToken = includeSession ? (_sessionToken ?? string.Empty) : string.Empty;
            builder.HostInstanceId = _runtime.Sessions.HostInstanceId;
            builder.OpId = opId;
            builder.Tool = tool ?? "unknown";
            builder.ParamsDigestValue = digest;
            builder.StartedUtc = DateTime.UtcNow;
            Send(builder.BuildFail(error, status));
        }

        private void SendBye(string reason, string detail)
        {
            try
            {
                byte[] payload = JsonCodec.Utf8(JsonCodec.Serialize(EnvelopeFrames.Bye(reason, detail)));
                lock (_writeSync)
                {
                    if (_closed)
                    {
                        return;
                    }
                    Framing.WriteFrame(_stream, payload);
                }
            }
            catch (Exception)
            {
                // bye 失败无需处理（连接本来就要关）
            }
        }

        // ---------------- 心跳与关闭 ----------------

        private void HeartbeatLoop()
        {
            while (!_closed)
            {
                try
                {
                    Thread.Sleep(2000);
                    if (_closed || !_handshaken)
                    {
                        continue;
                    }
                    DateTime last = _lastInboundUtc > _lastOutboundUtc ? _lastInboundUtc : _lastOutboundUtc;
                    double idleMs = (DateTime.UtcNow - last).TotalMilliseconds;
                    if (idleMs > 15000)
                    {
                        HostLog.Warn("pipe", "heartbeat_timeout", "idleMs", (long)idleMs,
                            "token", HostLog.Mask(_sessionToken));
                        SendBye("heartbeat_timeout", "idle_ms=" + (long)idleMs);
                        Close("heartbeat_timeout");
                        return;
                    }
                }
                catch (ThreadAbortException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    HostLog.Exception("pipe", "heartbeat_error", ex);
                }
            }
        }

        /// <summary>关闭连接：撤销令牌、关流、通知运行时。</summary>
        public void Close(string reason)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            string token = _sessionToken;
            _sessionToken = null;
            _handshaken = false;

            try
            {
                if (token != null)
                {
                    _runtime.Sessions.RevokeToken(token);
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            _runtime.OnConnectionClosed(this, reason);
            HostLog.Info("pipe", "connection_closed", "reason", reason, "token", HostLog.Mask(token));
        }
    }
}
