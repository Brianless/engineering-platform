using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
// AccessControlType 两档都在 System.Security.AccessControl；
// PipeSecurity/PipeAccessRule 在 net48 属 System.Security.AccessControl、在 net8 属 System.IO.Pipes
//（后者由 System.IO.Pipes.AccessControl 包提供）。两个命名空间同时引入，按 TFM 各取所需。
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Pipe
{
    /// <summary>
    /// 命名管道服务端（设计 §2.8）。
    /// - 仅当前用户 + SYSTEM 可访问（关闭 ACL 继承，不加 Everyone）；
    /// - maxInstances = 4，但应用层单会话（第二个 hello 会被 E1003 session_busy 拒绝）；
    /// - accept 循环在后台线程，绝不触碰 CAD API。
    /// </summary>
    internal sealed class PipeServer
    {
        private const int MaxInstances = 4;
        private const int BufferBytes = 64 * 1024;

        private readonly string _fullPipeName;
        private readonly string _streamName;
        private readonly HostRuntime _runtime;
        private readonly List<PipeConnection> _connections = new List<PipeConnection>();
        private readonly object _sync = new object();

        private Thread _acceptThread;
        private volatile bool _running;
        private PipeSecurity _security;

        public PipeServer(string fullPipeName, HostRuntime runtime)
        {
            if (string.IsNullOrEmpty(fullPipeName))
            {
                throw new ArgumentException("fullPipeName 不能为空", "fullPipeName");
            }
            if (runtime == null)
            {
                throw new ArgumentNullException("runtime");
            }
            _fullPipeName = fullPipeName;
            _streamName = PipeName.ToServerStreamName(fullPipeName);
            _runtime = runtime;
        }

        public string FullPipeName
        {
            get { return _fullPipeName; }
        }

        public int ConnectionCount
        {
            get
            {
                lock (_sync)
                {
                    int alive = 0;
                    for (int i = 0; i < _connections.Count; i++)
                    {
                        if (_connections[i] != null && !_connections[i].IsClosed)
                        {
                            alive++;
                        }
                    }
                    return alive;
                }
            }
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }
            _security = BuildSecurity();
            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "ep-pipe-accept";
            _acceptThread.Start();
            HostLog.Info("pipe", "server_started", "pipe", _fullPipeName);
        }

        public void Stop()
        {
            _running = false;
            List<PipeConnection> snapshot;
            lock (_sync)
            {
                snapshot = new List<PipeConnection>(_connections);
                _connections.Clear();
            }
            for (int i = 0; i < snapshot.Count; i++)
            {
                try
                {
                    snapshot[i].Close("host_shutdown");
                }
                catch (Exception)
                {
                    // 忽略单连接关闭异常
                }
            }
            HostLog.Info("pipe", "server_stopped", "pipe", _fullPipeName);
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    // PipeOptions.Asynchronous 是必须的（与 mockhost 的 FILE_FLAG_OVERLAPPED 同一约束）：
                    // 同步管道句柄会串行化同一句柄上的 I/O —— 读线程阻塞在 ReadFrame 时，
                    // 写线程（心跳/响应/bye）的 WriteFrame 会被一起阻塞，连接直接死锁。
#if NET8_0_OR_GREATER
                    // .NET 8 去掉了带 PipeSecurity 的构造函数重载，改用 NamedPipeServerStreamAcl.Create。
                    // 参数顺序与框架版一致，ACL 语义不变（仅当前用户 + SYSTEM）。
                    server = NamedPipeServerStreamAcl.Create(_streamName, PipeDirection.InOut, MaxInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        BufferBytes, BufferBytes, _security);
#else
                    server = new NamedPipeServerStream(_streamName, PipeDirection.InOut, MaxInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        BufferBytes, BufferBytes, _security);
#endif

                    server.WaitForConnection();

                    if (!_running)
                    {
                        SafeDispose(server);
                        return;
                    }

                    // 实例标识写进日志，方便多开时区分
                    PipeConnection connection = new PipeConnection(server, _runtime);
                    lock (_sync)
                    {
                        _connections.Add(connection);
                        PruneClosed();
                    }
                    connection.Start();
                    HostLog.Info("pipe", "client_connected", "connections", ConnectionCount);
                }
                catch (Exception ex)
                {
                    SafeDispose(server);
                    if (!_running)
                    {
                        return;
                    }
                    HostLog.Exception("pipe", "accept_failed", ex);
                    Thread.Sleep(500);
                }
            }
        }

        private void PruneClosed()
        {
            for (int i = _connections.Count - 1; i >= 0; i--)
            {
                if (_connections[i] == null || _connections[i].IsClosed)
                {
                    _connections.RemoveAt(i);
                }
            }
        }

        private static void SafeDispose(NamedPipeServerStream server)
        {
            if (server == null)
            {
                return;
            }
            try
            {
                server.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        /// <summary>
        /// 仅当前用户 + SYSTEM 的 ACL。关闭继承（否则会继承 Everyone 之类的规则）。
        /// </summary>
        private static PipeSecurity BuildSecurity()
        {
            PipeSecurity security = new PipeSecurity();
            try
            {
                security.SetAccessRuleProtection(true, false);

                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                if (identity != null && identity.User != null)
                {
                    security.AddAccessRule(new PipeAccessRule(identity.User,
                        PipeAccessRights.FullControl, AccessControlType.Allow));
                }

                SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                security.AddAccessRule(new PipeAccessRule(system,
                    PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            catch (Exception ex)
            {
                HostLog.Exception("pipe", "acl_build_failed", ex);
            }
            return security;
        }

        /// <summary>ACL 是否可读（sys_health.pipeAcl 用；失败只报 false，不抛）。</summary>
        public static bool AclConfigured
        {
            get
            {
                try
                {
                    WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    return identity != null && identity.User != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>管道名（面板与发现文件展示）。</summary>
        public string PipeNameText
        {
            get { return _fullPipeName; }
        }
    }
}
