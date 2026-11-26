using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Session
{
    /// <summary>
    /// 实例标识、会话令牌与实例发现文件（设计 §2.7 / §2.8）。
    /// 本文件禁止引用任何 AutoCAD 类型。
    /// </summary>
    internal sealed class SessionRegistry
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, DateTime> _tokens = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> _activeTokens = new HashSet<string>(StringComparer.Ordinal);

        private string _app = Pipe.PipeName.AppAcad;
        private string _instanceId = string.Empty;
        private string _userHash8 = string.Empty;
        private string _instanceDir;
        private string _documentNameHash = "null";
        private string _hostVersion = "0.1.0";
        private DateTime _startedUtc = DateTime.UtcNow;
        private bool _started;

        /// <summary>acad-3f7c1a92 形态的实例标识，进程生命周期内不变。</summary>
        public string HostInstanceId
        {
            get { return Pipe.PipeName.BuildHostInstanceId(_app, _instanceId ?? "00000000"); }
        }

        public string InstanceId
        {
            get { return _instanceId; }
        }

        public string App
        {
            get { return _app; }
        }

        public string PipeName
        {
            get { return Pipe.PipeName.Build(_app, _instanceId, _userHash8); }
        }

        public string DiscoveryPath
        {
            get { return Pipe.PipeName.DiscoveryFilePath(_app, _instanceId, _userHash8, _instanceDir); }
        }

        public DateTime StartedUtc
        {
            get { return _startedUtc; }
        }

        /// <summary>当前活动会话数（M0 应用层单会话）。</summary>
        public int ActiveSessionCount
        {
            get
            {
                lock (_sync)
                {
                    return _activeTokens.Count;
                }
            }
        }

        /// <summary>初始化：生成实例标识、写发现文件。</summary>
        public void Init(string app, string hostVersion, string documentNameHash, string instanceDir = null)
        {
            lock (_sync)
            {
                _app = string.IsNullOrEmpty(app) ? Pipe.PipeName.AppAcad : app;
                _hostVersion = string.IsNullOrEmpty(hostVersion) ? "0.1.0" : hostVersion;
                _instanceId = ParamsDigest.NewShortId();
                _userHash8 = Pipe.PipeName.UserHash8FromEnvironment();
                _instanceDir = string.IsNullOrEmpty(instanceDir) ? Pipe.PipeName.DefaultInstanceDir() : instanceDir;
                _documentNameHash = string.IsNullOrEmpty(documentNameHash) ? "null" : documentNameHash;
                _startedUtc = DateTime.UtcNow;
                _started = true;
            }
            PublishDiscoveryFile(_documentNameHash);
        }

        /// <summary>签发会话令牌（32 字节随机 → 64 hex）。</summary>
        public string IssueToken()
        {
            string token = ParamsDigest.NewToken();
            lock (_sync)
            {
                _tokens[token] = DateTime.UtcNow;
                _activeTokens.Add(token);
            }
            HostLog.Info("session", "token_issued", "token", HostLog.Mask(token), "instance", HostInstanceId);
            return token;
        }

        /// <summary>校验令牌是否存在（不判活动，活动判定另用 TryBeginSession）。</summary>
        public bool ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }
            lock (_sync)
            {
                return _tokens.ContainsKey(token);
            }
        }

        /// <summary>撤销令牌（连接断开时调用）。</summary>
        public void RevokeToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return;
            }
            lock (_sync)
            {
                _tokens.Remove(token);
                _activeTokens.Remove(token);
            }
            HostLog.Info("session", "token_revoked", "token", HostLog.Mask(token));
        }

        /// <summary>
        /// 单会话判定：已有活动会话且不是本令牌时返回 false（E1003 session_busy）。
        /// </summary>
        public bool TryBeginSession(string token, out string reason)
        {
            reason = null;
            lock (_sync)
            {
                if (_activeTokens.Count > 0 && !_activeTokens.Contains(token))
                {
                    reason = "session_busy";
                    return false;
                }
                _activeTokens.Add(token);
                return true;
            }
        }

        /// <summary>更新发现文件里的文档名哈希（DocumentActivated 时刷新）。</summary>
        public void UpdateDocumentNameHash(string documentNameHash)
        {
            _documentNameHash = string.IsNullOrEmpty(documentNameHash) ? "null" : documentNameHash;
            if (_started)
            {
                PublishDiscoveryFile(_documentNameHash);
            }
        }

        /// <summary>
        /// 写实例发现文件。内容全部是可公开信息，不含令牌、不含图纸路径明文。
        /// </summary>
        public void PublishDiscoveryFile(string documentNameHash)
        {
            string path = DiscoveryPath;
            string dir = Path.GetDirectoryName(path);
            Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.Ordinal);
            payload["pipeName"] = PipeName;
            payload["app"] = _app;
            payload["instanceId"] = _instanceId;
            payload["hostInstanceId"] = HostInstanceId;
            payload["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            payload["hostVersion"] = _hostVersion;
            payload["protocolVersion"] = Handshake.ProtocolVersion;
            payload["startedAtUtc"] = TimeUtil.ToIso(_startedUtc);
            payload["documentNameHash"] = string.IsNullOrEmpty(documentNameHash) ? "null" : documentNameHash;
            payload["writeEnabled"] = Protocol.HostCapabilities.WriteEnabled;

            try
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonCodec.Serialize(payload);
                // UTF-8 无 BOM；先写临时文件再替换，避免 MCP 读到半截 JSON
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
                HostLog.Info("session", "discovery_published", "path", HostLog.SafePath(path), "instance", HostInstanceId);
            }
            catch (Exception ex)
            {
                HostLog.Exception("session", "discovery_publish_failed", ex);
            }
        }

        /// <summary>删除发现文件（Terminate 时调用）。</summary>
        public void RemoveDiscoveryFile()
        {
            string path = DiscoveryPath;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    HostLog.Info("session", "discovery_removed", "path", HostLog.SafePath(path));
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("session", "discovery_remove_failed", ex);
            }
        }

        /// <summary>停用：清空令牌并删发现文件。</summary>
        public void Shutdown()
        {
            lock (_sync)
            {
                _tokens.Clear();
                _activeTokens.Clear();
                _started = false;
            }
            RemoveDiscoveryFile();
        }

        /// <summary>发现文件是否在位（sys_health 检查项）。</summary>
        public bool DiscoveryFileExists
        {
            get
            {
                try
                {
                    return File.Exists(DiscoveryPath);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>实例目录（面板显示与健康检查用）。</summary>
        public string InstanceDir
        {
            get { return string.IsNullOrEmpty(_instanceDir) ? Pipe.PipeName.DefaultInstanceDir() : _instanceDir; }
        }

        /// <summary>宿主版本（写进发现文件与 hello_ack）。</summary>
        public string HostVersion
        {
            get { return _hostVersion; }
        }

        /// <summary>启动时间字符串（面板显示）。</summary>
        public string StartedAtIso
        {
            get { return TimeUtil.ToIso(_startedUtc); }
        }

        /// <summary>启动至今毫秒数（sys_status）。</summary>
        public long UptimeMs
        {
            get
            {
                double ms = (DateTime.UtcNow - _startedUtc).TotalMilliseconds;
                return ms < 0 ? 0 : (long)ms;
            }
        }

        /// <summary>格式化实例行（面板展示）。</summary>
        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} | {1} | 会话 {2} | 启动 {3}",
                HostInstanceId, PipeName, ActiveSessionCount, StartedAtIso);
        }
    }
}
