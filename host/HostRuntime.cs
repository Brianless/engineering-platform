using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Fingerprint;
using EngineeringPlatform.Host.Pipe;
using EngineeringPlatform.Host.Preview;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Scheduling;
using EngineeringPlatform.Host.Semantic;
using EngineeringPlatform.Host.Session;

namespace EngineeringPlatform.Host
{
    /// <summary>宿主配置（内置默认值 + %LOCALAPPDATA%\EngineeringPlatform\config.json 覆盖）。</summary>
    internal sealed class HostConfig
    {
        public string App = PipeName.AppAcad;
        public string HostVersion = "0.1.0";
        public string LogLevel = "INFO";
        public long LogMaxBytes = 10L * 1024 * 1024;
        public int LogKeepDays = 7;
        public int QueueWaitMs = 2000;
        public int EntityLimit = 50000;
        public int MaxResultBytes = 350 * 1024;
        /// <summary>
        /// 语义缓存 TTL（毫秒），默认 30 分钟。配置键：`semanticCache.ttlMs`（优先）/
        /// `host.semanticCacheTtlMs`（别名）。缓存键是内容指纹，TTL 只影响内存回收节奏，
        /// 不影响正确性 —— 所以可以放长，让一个会话里同一张图只冷抽一次。
        /// </summary>
        public int SemanticCacheTtlMs = SemanticCache.DefaultTtlMs;
        public string InstanceDir;
        public string LogDir;
        public string PreviewCacheDir;
        public string LayerMapPath;
        public string ConfigPath;

        /// <summary>
        /// 写通道总开关（M1，默认 false）。关着的时候写工具一律 E2003，宿主行为与 M0 逐字节一致。
        /// 配置键：`write.enabled`。这是**运维开关**，打开需要重启 CAD。
        /// </summary>
        public bool WriteEnabled;

        /// <summary>
        /// 全自主静默写开关（v3.0）。开启后写工具免人工模态弹窗，自动记录审计并连续执行；
        /// 关闭时退回 M1 三步审批（需人在 CAD 点确认）。配置键：`write.autoApprove`。
        /// </summary>
        public bool WriteAutoApprove;

        /// <summary>审批令牌有效期（毫秒），默认 120 秒。配置键：`write.tokenTtlMs`。</summary>
        public int WriteTokenTtlMs = 120000;

        /// <summary>HMAC 密钥文件路径（首次使用自动生成，仅当前用户可读）。</summary>
        public string WriteSecretPath;

        /// <summary>读取配置；任何异常都退回默认值（绝不让配置问题阻止宿主启动）。</summary>
        public static HostConfig Load()
        {
            HostConfig config = new HostConfig();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Path.Combine(local, "EngineeringPlatform");
            config.InstanceDir = Path.Combine(root, "instances");
            config.LogDir = Path.Combine(root, "logs");
            config.PreviewCacheDir = Path.Combine(Path.Combine(root, "cache"), "preview");
            config.LayerMapPath = Path.Combine(root, "layer-map.json");
            config.ConfigPath = Path.Combine(root, "config.json");
            config.WriteSecretPath = Path.Combine(root, "write-secret.bin");

            try
            {
                Environment.SetEnvironmentVariable("EP_INSTANCE_DIR", config.InstanceDir);
            }
            catch (Exception)
            {
                // 环境变量设置失败不影响运行
            }

            if (!File.Exists(config.ConfigPath))
            {
                return config;
            }

            try
            {
                string text = File.ReadAllText(config.ConfigPath);
                Dictionary<string, object> dict;
                if (!JsonCodec.TryParseObject(text, out dict))
                {
                    HostLog.Warn("config", "config_parse_failed", "path", HostLog.SafePath(config.ConfigPath));
                    return config;
                }

                Dictionary<string, object> host = JsonCodec.GetDict(dict, "host");
                if (host != null)
                {
                    config.App = JsonCodec.GetStr(host, "app", config.App);
                    config.HostVersion = JsonCodec.GetStr(host, "version", config.HostVersion);
                    config.QueueWaitMs = JsonCodec.GetInt(host, "dispatchWaitMs", config.QueueWaitMs);
                    config.EntityLimit = JsonCodec.GetInt(host, "entityLimit", config.EntityLimit);
                    config.MaxResultBytes = JsonCodec.GetInt(host, "maxResultBytesHost", config.MaxResultBytes);
                    // 别名：team-lead 点名的键。真值以 `semanticCache.ttlMs` 为准（见下）。
                    config.SemanticCacheTtlMs = JsonCodec.GetInt(host, "semanticCacheTtlMs",
                        config.SemanticCacheTtlMs);
                }

                Dictionary<string, object> log = JsonCodec.GetDict(dict, "log");
                if (log != null)
                {
                    config.LogLevel = JsonCodec.GetStr(log, "level", config.LogLevel);
                    config.LogMaxBytes = (long)JsonCodec.GetInt(log, "maxBytes", (int)config.LogMaxBytes);
                    config.LogKeepDays = JsonCodec.GetInt(log, "keepDays", config.LogKeepDays);
                }

                Dictionary<string, object> instances = JsonCodec.GetDict(dict, "instances");
                if (instances != null)
                {
                    string dir = JsonCodec.GetStr(instances, "dir");
                    if (!string.IsNullOrEmpty(dir))
                    {
                        config.InstanceDir = Environment.ExpandEnvironmentVariables(dir);
                    }
                }

                Dictionary<string, object> preview = JsonCodec.GetDict(dict, "preview");
                if (preview != null)
                {
                    string cacheDir = JsonCodec.GetStr(preview, "cacheDir");
                    if (!string.IsNullOrEmpty(cacheDir))
                    {
                        config.PreviewCacheDir = Environment.ExpandEnvironmentVariables(cacheDir);
                    }
                }

                Dictionary<string, object> semanticCache = JsonCodec.GetDict(dict, "semanticCache");
                if (semanticCache != null)
                {
                    // 契约里已有的键优先（config.example.json 的 semanticCache.ttlMs）
                    config.SemanticCacheTtlMs = JsonCodec.GetInt(semanticCache, "ttlMs",
                        config.SemanticCacheTtlMs);
                }

                // 写通道（M1/v3.0）：enabled 开启写域，autoApprove 开启全自主静默写。
                Dictionary<string, object> write = JsonCodec.GetDict(dict, "write");
                if (write != null)
                {
                    config.WriteEnabled = JsonCodec.GetBool(write, "enabled", false);
                    config.WriteAutoApprove = JsonCodec.GetBool(write, "autoApprove", false);
                    config.WriteTokenTtlMs = JsonCodec.GetInt(write, "tokenTtlMs", config.WriteTokenTtlMs);
                    string secret = JsonCodec.GetStr(write, "secretPath");
                    if (!string.IsNullOrEmpty(secret))
                    {
                        config.WriteSecretPath = Environment.ExpandEnvironmentVariables(secret);
                    }
                }

                Dictionary<string, object> mapping = JsonCodec.GetDict(dict, "mapping");
                if (mapping != null)
                {
                    string layerMap = JsonCodec.GetStr(mapping, "layerMap");
                    if (!string.IsNullOrEmpty(layerMap))
                    {
                        config.LayerMapPath = Environment.ExpandEnvironmentVariables(layerMap);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("config", "config_load_failed", ex);
            }
            return config;
        }
    }

    /// <summary>任务记录（内存环形缓冲 200 条，供 task_status / task_list / 面板使用）。</summary>
    internal sealed class TaskRecord
    {
        public string OpId = string.Empty;
        public string Tool = string.Empty;
        public string Status = "unknown";
        public string ErrorCode;
        public string ParamsDigestValue;
        public string Fingerprint;
        public DateTime StartedUtc = DateTime.UtcNow;
        public long ElapsedMs;

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["opId"] = OpId;
            dict["tool"] = Tool;
            dict["status"] = Status;
            dict["startedAtUtc"] = TimeUtil.ToIso(StartedUtc);
            dict["elapsedMs"] = ElapsedMs;
            dict["errorCode"] = ErrorCode;
            return dict;
        }
    }

    /// <summary>环形任务缓冲（固定 200 条，最新的在尾部）。</summary>
    internal sealed class TaskRing
    {
        private readonly TaskRecord[] _items;
        private readonly object _sync = new object();
        private int _next;
        private int _count;

        public TaskRing(int capacity)
        {
            int size = capacity > 0 ? capacity : 200;
            _items = new TaskRecord[size];
        }

        public void Add(TaskRecord record)
        {
            if (record == null)
            {
                return;
            }
            lock (_sync)
            {
                _items[_next] = record;
                _next = (_next + 1) % _items.Length;
                if (_count < _items.Length)
                {
                    _count++;
                }
            }
        }

        /// <summary>按时间倒序返回最近任务。</summary>
        public List<TaskRecord> Recent(int limit, DateTime sinceUtc)
        {
            List<TaskRecord> result = new List<TaskRecord>();
            lock (_sync)
            {
                for (int i = 0; i < _count; i++)
                {
                    int index = (_next - 1 - i + _items.Length * 2) % _items.Length;
                    TaskRecord record = _items[index];
                    if (record == null)
                    {
                        continue;
                    }
                    if (sinceUtc != DateTime.MinValue && record.StartedUtc < sinceUtc)
                    {
                        continue;
                    }
                    result.Add(record);
                    if (result.Count >= Math.Max(limit, 1))
                    {
                        break;
                    }
                }
            }
            return result;
        }

        public TaskRecord Find(string opId)
        {
            if (string.IsNullOrEmpty(opId))
            {
                return null;
            }
            lock (_sync)
            {
                for (int i = 0; i < _count; i++)
                {
                    int index = (_next - 1 - i + _items.Length * 2) % _items.Length;
                    TaskRecord record = _items[index];
                    if (record != null && string.Equals(record.OpId, opId, StringComparison.Ordinal))
                    {
                        return record;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>请求统计（近 60 秒窗口 + 缓存命中率）。</summary>
    internal sealed class RequestStats
    {
        private readonly Queue<KeyValuePair<DateTime, bool>> _window = new Queue<KeyValuePair<DateTime, bool>>();
        private readonly object _sync = new object();
        private long _cacheHits;
        private long _cacheMisses;

        public void RecordRequest(bool ok)
        {
            lock (_sync)
            {
                _window.Enqueue(new KeyValuePair<DateTime, bool>(DateTime.UtcNow, ok));
                Trim();
            }
        }

        public void RecordCache(bool hit)
        {
            lock (_sync)
            {
                if (hit)
                {
                    _cacheHits++;
                }
                else
                {
                    _cacheMisses++;
                }
            }
        }

        public int RequestsLast60s
        {
            get
            {
                lock (_sync)
                {
                    Trim();
                    return _window.Count;
                }
            }
        }

        public int ErrorsLast60s
        {
            get
            {
                lock (_sync)
                {
                    Trim();
                    int errors = 0;
                    foreach (KeyValuePair<DateTime, bool> entry in _window)
                    {
                        if (!entry.Value)
                        {
                            errors++;
                        }
                    }
                    return errors;
                }
            }
        }

        public double CacheHitRate
        {
            get
            {
                lock (_sync)
                {
                    long total = _cacheHits + _cacheMisses;
                    if (total <= 0)
                    {
                        return 0D;
                    }
                    return (double)_cacheHits / (double)total;
                }
            }
        }

        private void Trim()
        {
            DateTime limit = DateTime.UtcNow.AddSeconds(-60);
            while (_window.Count > 0 && _window.Peek().Key < limit)
            {
                _window.Dequeue();
            }
        }
    }

    /// <summary>
    /// 进程内单例运行时：装配 PipeServer / Dispatcher / Session / Router / Cache。
    /// CAD API 只允许在 <see cref="ExecuteItem"/> 及其下游（主线程）里使用。
    /// </summary>
    internal sealed class HostRuntime
    {
        private static readonly Lazy<HostRuntime> LazyInstance =
            new Lazy<HostRuntime>(delegate { return new HostRuntime(); });

        private volatile bool _started;

        public static HostRuntime Instance
        {
            get { return LazyInstance.Value; }
        }


        public HostConfig Config = new HostConfig();
        public SessionRegistry Sessions = new SessionRegistry();
        public MainThreadDispatcher Dispatcher = new MainThreadDispatcher();
        public SemanticCache Cache = new SemanticCache(SemanticCache.DefaultTtlMs,
            SemanticCache.DefaultMaxEntries);
        private readonly DocumentRevision _revision = new DocumentRevision();
        public readonly Tools.SelectionTracker Selection = new Tools.SelectionTracker();
        public readonly Tools.TransientRegistry Transients = new Tools.TransientRegistry();
        public TaskRing Tasks = new TaskRing(200);
        public RequestStats Stats = new RequestStats();

        /// <summary>写通道审批令牌（M1）。惰性构造：M0 跑法下不读密钥文件、不生成任何额外文件。</summary>
        private Write.ApprovalTokens _approvals;

        public Write.ApprovalTokens Approvals
        {
            get
            {
                if (_approvals == null)
                {
                    _approvals = new Write.ApprovalTokens(Config.WriteSecretPath);
                }
                return _approvals;
            }
        }

        public ToolRouter Router;
        public Semantic.LayerMapping Mapping;
        public PreviewRenderer Renderer;
        public PipeServer Server;
        public string LastFingerprint = string.Empty;

        private HostRuntime()
        {
        }

        public bool IsStarted
        {
            get { return _started; }
        }

        /// <summary>启动运行时（ExtensionApplication.Initialize 调用）。</summary>
        public void Start()
        {
            if (_started)
            {
                return;
            }

            Config = HostConfig.Load();
            HostLog.Init(Config.LogDir, Config.LogLevel, Config.LogMaxBytes, Config.LogKeepDays);

            // 写通道开关的唯一赋值点（HostCapabilities 是静态类，握手构造点拿不到 runtime 实例）。
            HostCapabilities.WriteEnabled = Config.WriteEnabled;
            HostCapabilities.WriteAutoApprove = Config.WriteAutoApprove;
            if (Config.WriteEnabled)
            {
                HostLog.Warn("write", "write_channel_enabled",
                    "autoApprove", Config.WriteAutoApprove,
                    "tokenTtlMs", Config.WriteTokenTtlMs,
                    "secret", Config.WriteSecretPath == null ? "-" : "configured");
            }
            else
            {
                HostLog.Info("write", "write_channel_disabled");
            }

            Mapping = Semantic.LayerMapping.Load(Config.LayerMapPath);
            // TTL 可配：默认 30 分钟（真机冷抽 20 s、热命中 1.4 s，短 TTL 等于没有缓存）。
            // 正确性不依赖它 —— 键是内容指纹，图纸一变就换键。
            Cache = new SemanticCache(Config.SemanticCacheTtlMs, SemanticCache.DefaultMaxEntries);
            HostLog.Info("semantic", "cache_configured",
                "ttlMs", Cache.TtlMs, "maxEntries", Cache.MaxEntries,
                "keyMode", "fingerprint+revision");
            // 缓存键自检：`指纹 + "#" + 修订号`。这一层不依赖任何 CAD 对象，
            // 所以可以在启动时真跑一次并把结果写进日志（真机上可核对，不靠嘴说）。
            bool keyOk = DocumentRevision.ComposeKey("fp1:selftest", 7L)
                             == "fp1:selftest#7"
                         && DocumentRevision.ComposeKey("fp1:selftest", 8L)
                             != DocumentRevision.ComposeKey("fp1:selftest", 7L)
                         && DocumentRevision.ComposeKey("fp1:other", 7L)
                             != DocumentRevision.ComposeKey("fp1:selftest", 7L)
                         && DocumentRevision.ComposeKey(string.Empty, 7L).Length == 0;
            if (keyOk)
            {
                HostLog.Info("semantic", "cache_key_selftest", "ok", true);
            }
            else
            {
                HostLog.Error("semantic", "cache_key_selftest", "ok", false);
            }
            // 缓存行为自检：不是测「拼装函数」，而是**真跑一遍缓存**（临时实例，不污染真缓存）：
            //   同指纹同修订号 → 命中；同指纹修订号 +1 → miss；换指纹 → miss；容量满时淘汰最久未用。
            // 这样「改了但没保存会不会读到旧模型」这条判据在宿主启动时就有一条可执行证据。
            RunCacheBehaviorSelfTest();
            Renderer = new PreviewRenderer(new PreviewCache(Config.PreviewCacheDir));
            Router = new ToolRouter(this);

            string documentHash = "null";
            Document doc = Scheduling.HostStateProbe.TryGetActiveDocument();
            if (doc != null && !string.IsNullOrEmpty(doc.Name))
            {
                documentHash = Redactor.NameHash(doc.Name);
            }
            Sessions.Init(Config.App, Config.HostVersion, documentHash, Config.InstanceDir);
            AttachRevisionTracker(doc);
            Selection.Attach(doc);
            Transients.SubscribeLifecycle();

            Dispatcher.QueueWaitMs = Config.QueueWaitMs;
            Dispatcher.Executor = ExecuteItem;
            Dispatcher.Start();

            Server = new PipeServer(Sessions.PipeName, this);
            Server.Start();

            _started = true;
            HostLog.Info("runtime", "started",
                "instance", Sessions.HostInstanceId,
                "pipe", Sessions.PipeName,
                "catalogHash", ToolCatalog.CatalogHash,
                "civilApi", CivilApiProbe.Available);
        }

        /// <summary>停止运行时（Terminate / QuitWillStart 调用）。</summary>
        public void Stop()
        {
            if (!_started)
            {
                return;
            }
            _started = false;
            try
            {
                if (Server != null)
                {
                    Server.Stop();
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "server_stop_failed", ex);
            }
            try
            {
                Dispatcher.Stop();
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "dispatcher_stop_failed", ex);
            }
            try
            {
                _revision.Detach();
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "revision_detach_failed", ex);
            }
            try
            {
                Selection.Detach();
                Transients.UnsubscribeLifecycle();
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "selection_transient_stop_failed", ex);
            }
            try
            {
                Sessions.Shutdown();
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "session_shutdown_failed", ex);
            }
            HostLog.Info("runtime", "stopped", "instance", Sessions.HostInstanceId);
        }

        public int ConnectionCount
        {
            get { return Server == null ? 0 : Server.ConnectionCount; }
        }

        // ---------------- 主线程执行 ----------------

        /// <summary>
        /// 主线程执行入口（Dispatcher.Executor）。返回值即响应封套。
        /// 顺序：写域/未知工具 → 门禁 → 指纹与漂移 → handler → 预算硬截断。
        /// </summary>
        public Dictionary<string, object> ExecuteItem(WorkItem item)
        {
            ResponseBuilder builder = item.Builder;
            if (builder == null)
            {
                builder = new ResponseBuilder();
                builder.OpId = item.OpId;
                builder.Tool = item.Tool;
                builder.StartedUtc = DateTime.UtcNow;
            }

            // 1) 写域/未知工具：不触碰 CAD，也不受文档状态影响
            //
            // 注意（2026-11-23 踩坑）：这是**第二道**门禁，与 ToolRouter.Dispatch 里那道是独立的两处。
            // 2026-11-23 00:35 实测：只改了 ToolRouter，写工具仍被这里的无条件拒绝挡住（E2003 _m0），
            // 因为本方法在进入路由前就返回了。两处必须同改，否则「开了写通道却永远 E2003」。
            if (ToolCatalog.IsWriteDomain(item.Tool))
            {
                bool writeAllowed = Config.WriteEnabled
                    && item.Tool.StartsWith("write_", StringComparison.OrdinalIgnoreCase);
                if (!writeAllowed)
                {
                    Stats.RecordRequest(false);
                    HostLog.Warn("router", "write_domain_rejected", "op", item.OpId, "tool", item.Tool,
                        "writeEnabled", Config.WriteEnabled);
                    return builder.BuildFail(ToolRouter.RejectWriteDomain(item.Tool), "failed");
                }
            }
            if (ToolCatalog.Find(item.Tool) == null)
            {
                Stats.RecordRequest(false);
                HostLog.Warn("router", "unknown_tool", "op", item.OpId, "tool", item.Tool);
                return builder.BuildFail(WireError.Of(ErrorCodes.E2001, "unknown_tool:" + item.Tool, "tool"), "failed");
            }

            // 2) 门禁（ENG_SELFTEST 自带命令上下文，需显式放行 CMDACTIVE 这一条）
            GateResult gate = HostStateProbe.Probe(item.AllowCommandContext);
            if (!gate.Ok)
            {
                Stats.RecordRequest(false);
                HostLog.Warn("gate", "gate_rejected", "op", item.OpId, "tool", item.Tool,
                    "code", gate.Error.Code, "detail", gate.Error.Detail);
                return builder.BuildFail(gate.Error, "failed");
            }

            ToolContext context = new ToolContext();
            context.Runtime = this;
            context.Item = item;
            context.Doc = gate.Doc;
            context.Db = gate.Db;

            try
            {
                Dictionary<string, object> result = Router.Dispatch(context);
                if (result == null && item.Deferred)
                {
                    return null;
                }

                // 指纹回填 + 漂移告警（只读不阻断）
                if (!string.IsNullOrEmpty(context.Fingerprint))
                {
                    builder.TargetFingerprint = context.Fingerprint;
                    LastFingerprint = context.Fingerprint;
                    if (!string.IsNullOrEmpty(item.TargetFingerprint) &&
                        !string.Equals(item.TargetFingerprint, context.Fingerprint, StringComparison.Ordinal))
                    {
                        context.AddWarning(ErrorCodes.W1004,
                            "expected=" + item.TargetFingerprint + ";actual=" + context.Fingerprint);
                    }
                }

                if (context.Model != null && context.Model.EntityLimitReached)
                {
                    context.AddWarning(ErrorCodes.W1009,
                        "entities=" + context.Model.EntityCount + ";limit=" + Config.EntityLimit);
                }
                if (context.Model != null && context.Model.ExtractElapsedMs > 3000)
                {
                    context.AddWarning(ErrorCodes.W1005, "extractMs=" + context.Model.ExtractElapsedMs);
                }

                builder.AddWarnings(context.Warnings);
                result = EnforceResultBudget(result, builder);

                Stats.RecordRequest(true);
                HostLog.Info("router", "tool_completed", "op", item.OpId, "tool", item.Tool,
                    "status", "verified", "elapsedMs", builder.ElapsedMs);
                return builder.BuildOk(result, "verified");
            }
            catch (ToolFailureException failure)
            {
                builder.AddWarnings(context.Warnings);
                Stats.RecordRequest(false);
                string status = "failed";
                if (failure.Error != null &&
                    string.Equals(failure.Error.Code, ErrorCodes.E3002, StringComparison.Ordinal))
                {
                    status = "unknown";
                    if (item.CancelRequested)
                    {
                        status = "cancelled";
                    }
                }
                HostLog.Warn("router", "tool_failed", "op", item.OpId, "tool", item.Tool,
                    "code", failure.Error == null ? "null" : failure.Error.Code,
                    "detail", failure.Error == null ? null : HostLog.Trim(failure.Error.Detail, 160));
                return builder.BuildFail(failure.Error, status);
            }
            catch (Exception ex)
            {
                builder.AddWarnings(context.Warnings);
                Stats.RecordRequest(false);
                HostLog.Exception("router", "tool_threw", ex);
                return builder.BuildFail(WireError.Of(ErrorCodes.E3002, "internal_error"), "failed");
            }
        }

        /// <summary>
        /// 宿主侧硬截断：单条结果不超过 350 KB（避免把 1 MiB 帧上限当唯一防线）。
        /// 只在数组元素边界裁剪，结构始终合法。
        /// </summary>
        public Dictionary<string, object> EnforceResultBudget(Dictionary<string, object> result, ResponseBuilder builder)
        {
            if (result == null)
            {
                return null;
            }
            if (JsonCodec.Serialize(result).Length <= Config.MaxResultBytes)
            {
                return result;
            }

            if (!ShrinkArrays(result, 2) || JsonCodec.Serialize(result).Length > Config.MaxResultBytes)
            {
                ShrinkArrays(result, 3);
            }
            if (JsonCodec.Serialize(result).Length > Config.MaxResultBytes)
            {
                ShrinkArrays(result, 1);
            }

            result["truncated"] = true;
            if (builder != null)
            {
                builder.AddWarning(WireWarning.Of(ErrorCodes.W1008, "result_truncated_host_side"));
            }
            HostLog.Warn("budget", "result_truncated", "limitBytes", Config.MaxResultBytes,
                "actualBytes", JsonCodec.Serialize(result).Length);
            return result;
        }

        private static readonly string[] ArrayKeys =
        {
            "items", "layers", "topAnomalies", "checks", "tasks", "instances",
            "legend", "basis", "relations", "anomalies", "drilldown", "disciplines"
        };

        private static bool ShrinkArrays(Dictionary<string, object> root, int keep)
        {
            bool changed = false;
            for (int i = 0; i < ArrayKeys.Length; i++)
            {
                object raw;
                if (!root.TryGetValue(ArrayKeys[i], out raw) || raw == null)
                {
                    continue;
                }
                IList list = raw as IList;
                if (list == null)
                {
                    continue;
                }
                int target = Math.Max(keep, 0);
                while (list.Count > target)
                {
                    list.RemoveAt(list.Count - 1);
                    changed = true;
                }
                if (changed)
                {
                    root["truncated"] = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// 取语义模型：**内容指纹为键**、TTL 120 s 缓存；未命中则采集 + 识别 + 异常扫描。
        /// 只在主线程调用。
        ///
        /// ## 为什么键仍然是指纹（2026-11-22 真机数据后的定论）
        ///
        /// 曾计划把键换成「文档修订号」，理由是推断「命中缓存仍要 2 s」来自指纹遍历。
        /// 真机分段计时否掉了这个推断：`fingerprintMs = 6`（六毫秒），而当时那 2 s 的真身是
        /// 客户端建连的 `pipe_not_found` 重试等待。
        /// 所以**不做**修订号 / 文档事件订阅改造 —— 为 6 ms 去承担「图纸改了却返回旧模型」的风险
        /// 不划算。指纹当键是最强的正确性保证：内容一变，键就变，不存在漏挂事件的可能。
        ///
        /// TTL 只做兜底，且已从 5 s 提到 120 s：指纹便宜（~6 ms），而冷抽取贵（~52 s），
        /// 真正要避免的是「交互中反复重抽」。
        /// </summary>
        public SemanticModel GetSemanticModel(Document doc, bool refresh)
        {
            long tFingerprint = SemanticTimings.Now();
            string fingerprint = ComputeFingerprint(doc);
            long fingerprintMs = SemanticTimings.MsSince(tFingerprint);

            // 缓存键 = 指纹 + "#" + 修订号。
            //   指纹：身份变化、结构变化（新增/删除实体，mtime）、事件漏挂但结构变了；
            //   修订号：**结构没变但内容变了**（移动图元 / 改文字 / 改属性值 / 改图层颜色 /
            //           改数据库头变量）—— 这些指纹完全看不出来。
            //
            // `revisionKeyMode` 三条路：
            //   ref / logical —— 判定为同一张图，用「指纹#修订号」做键（正常情况）
            //   miss          —— **判定不了**：这一轮**整轮绕过缓存**（不 Get 也不 Put）
            //
            // 为什么 miss 时不退回「仅指纹」：那等于悄悄失去 D-02 的全部意义
            // （未保存的修改又会命中旧模型）。宁可这一轮慢一次，也不能悄悄返回旧数据。
            string revisionKeyMode;
            Autodesk.AutoCAD.DatabaseServices.Database docDb = null;
            if (doc != null)
            {
                try
                {
                    docDb = doc.Database;
                }
                catch (Exception)
                {
                    docDb = null;
                }
            }
            string cacheKey = _revision.TryComposeKey(doc, docDb, fingerprint, out revisionKeyMode);
            bool revisionKeyed = !string.IsNullOrEmpty(cacheKey);
            bool cacheAllowed = revisionKeyed;
            long revision = _revision.Current;
            if (revisionKeyed)
            {
                long hashAt = cacheKey.LastIndexOf('#');
                if (hashAt <= 0 || hashAt == cacheKey.Length - 1)
                {
                    HostLog.Error("semantic", "cache_key_invalid", "key", cacheKey);
                    revisionKeyed = false;
                    cacheAllowed = false;
                    revisionKeyMode = "miss";
                }
            }
            if (!cacheAllowed)
            {
                HostLog.Warn("semantic", "cache_bypassed_no_revision_key",
                    "mode", revisionKeyMode, "fp", fingerprint, "rev", revision,
                    "note", "本轮不读也不写缓存（宁慢一次，不返回可能过期的模型）");
            }

            if (cacheAllowed && !refresh && !string.IsNullOrEmpty(cacheKey))
            {
                SemanticModel cached = Cache.Get(cacheKey);
                if (cached != null)
                {
                    Stats.RecordCache(true);
                    LastFingerprint = fingerprint;
                    HostLog.Info("semantic", "extract_cache_hit",
                        "fp", fingerprint,
                        "rev", revision,
                        "revisionKeyed", revisionKeyed,
                        "revisionKeyMode", revisionKeyMode,
                        "fingerprintMs", fingerprintMs,
                        "ageMs", Cache.AgeMsOf(cacheKey),
                        "entities", cached.EntityCount);
                    return cached;
                }
            }

            Stats.RecordCache(false);
            Stopwatch watch = Stopwatch.StartNew();
            // 分段计时：只累计、不参与任何判定（2026-11-22 真机 52 s 事故的定位手段）。
            SemanticTimings timings = new SemanticTimings();
            SemanticModel model = SemanticExtractor.Extract(doc, Mapping, Config.EntityLimit,
                fingerprint, null, timings);
            long tRecognize = SemanticTimings.Now();
            SemanticRecognizer.Recognize(model, Mapping);
            long recognizeMs = SemanticTimings.MsSince(tRecognize);
            long tAnomaly = SemanticTimings.Now();
            AnomalyScanner.Scan(model);
            long anomalyMs = SemanticTimings.MsSince(tAnomaly);
            watch.Stop();
            model.ExtractElapsedMs = watch.ElapsedMilliseconds;
            if (cacheAllowed && !string.IsNullOrEmpty(cacheKey))
            {
                Cache.Put(cacheKey, model);
            }
            LastFingerprint = fingerprint;
            // 指纹含「完整路径哈希」，但日志里只有指纹本身、不可分解。补记 pathHash：
            // 「同 basename、不同路径 → 指纹变」这类问题一次 grep 就能归因（2026-11-22 踩过，见 05 §1.22.2）。
            string docPathHash;
            try
            {
                docPathHash = Redactor.PathHash(doc == null ? null : doc.Name);
            }
            catch (Exception)
            {
                docPathHash = "err";
            }
            HostLog.Info("semantic", "extract_done",
                "fp", fingerprint,
                "pathHash", docPathHash,
                "entities", model.EntityCount,
                "layers", model.LayerCount,
                "proxy", model.ProxyCount,
                "elapsedMs", model.ExtractElapsedMs);
            // 分段明细：定位「时间花在哪」。修完块定义缓存后要靠它验证收益，不许移除。
            HostLog.Info("semantic", "extract_breakdown",
                "fp", fingerprint,
                "totalMs", model.ExtractElapsedMs,
                "rev", revision,
                "revisionKeyMode", revisionKeyMode,
                "fingerprintMs", fingerprintMs,
                "spaceMs", SemanticTimings.TicksToMs(timings.TraverseTicks),
                "paperMs", SemanticTimings.TicksToMs(timings.PaperTicks),
                "civilMs", SemanticTimings.TicksToMs(timings.CivilTicks),
                "extentsMs", SemanticTimings.TicksToMs(timings.ExtentsTicks),
                "recognizeMs", recognizeMs,
                "anomalyMs", anomalyMs,
                "readEntityMs", SemanticTimings.TicksToMs(timings.ReadEntityTicks),
                "openMs", SemanticTimings.TicksToMs(timings.OpenEntityTicks),
                "boundsMs", SemanticTimings.TicksToMs(timings.BoundsTicks),
                "geometryMs", SemanticTimings.TicksToMs(timings.GeometryTicks),
                "attrMs", SemanticTimings.TicksToMs(timings.AttrTicks),
                "opened", timings.EntitiesOpened,
                "boundsCalls", timings.BoundsCalls,
                "attrReads", timings.AttrReads,
                "blockDefComputed", timings.BlockDefComputed,
                "blockDefCacheHits", timings.BlockDefCacheHits,
                "attrDefBlocks", timings.AttrDefBlocks,
                "blockRefs", model.BlockReferenceCount,
                "entities", model.EntityCount);
            return model;
        }

        /// <summary>
        /// 缓存行为自检（启动时跑一次，结果进日志）。
        ///
        /// 为什么要有它：`tests/` 是 Python，**无法执行 C#**，而这条规则的正确性恰好只在 C# 里。
        /// 与其写一条「测试我的 Python 复刻」的假用例，不如让宿主自己启动时真跑一遍缓存，
        /// 把四条判据的布尔结果写进日志 —— 真机上可核对，且以后谁改动键机制会立刻在启动日志里露出来。
        ///
        /// 用**临时 `SemanticCache` 实例**，绝不碰运行时缓存（`Cache`）。
        /// </summary>
        private void RunCacheBehaviorSelfTest()
        {
            try
            {
                bool hitSame = false;
                bool missOnBump = false;
                bool missOtherFp = false;
                bool lruEvicts = false;

                SemanticCache probe = new SemanticCache(60000, 4);
                string fp = "fp1:selftest";
                string keyRev1 = DocumentRevision.ComposeKey(fp, 1L);
                string keyRev2 = DocumentRevision.ComposeKey(fp, 2L);
                string keyOther = DocumentRevision.ComposeKey("fp1:selftest2", 1L);
                SemanticModel model = new SemanticModel();
                probe.Put(keyRev1, model);
                hitSame = ReferenceEquals(probe.Get(keyRev1), model);
                missOnBump = probe.Get(keyRev2) == null;
                missOtherFp = probe.Get(keyOther) == null;

                SemanticCache lru = new SemanticCache(60000, 2);
                lru.Put("a", model);
                lru.Put("b", model);
                lru.Get("a");                 // 刷新 a → b 成为最久未使用
                lru.Put("c", model);          // 容量 2 → 淘汰 b
                lruEvicts = lru.Get("b") == null && lru.Get("a") != null && lru.Get("c") != null;

                bool ok = hitSame && missOnBump && missOtherFp && lruEvicts;
                if (ok)
                {
                    HostLog.Info("semantic", "cache_behavior_selftest", "ok", true,
                        "hitSame", hitSame, "missOnBump", missOnBump,
                        "missOtherFp", missOtherFp, "lruEvicts", lruEvicts);
                }
                else
                {
                    HostLog.Error("semantic", "cache_behavior_selftest", "ok", false,
                        "hitSame", hitSame, "missOnBump", missOnBump,
                        "missOtherFp", missOtherFp, "lruEvicts", lruEvicts);
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "cache_behavior_selftest_failed", ex);
            }
        }

        /// <summary>
        /// 把修订号跟踪器挂到当前文档上（幂等；换文档时内部会自动解钩旧的）。
        /// 任何异常都只记日志 —— 挂不上就退回「仅指纹做键」，不能让工具不可用。
        /// </summary>
        private void AttachRevisionTracker(Document doc)
        {
            try
            {
                _revision.Attach(doc);
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "revision_attach_failed", ex);
            }
        }

        /// <summary>计算当前图纸指纹；失败返回空串（不阻断只读）。</summary>
        public string ComputeFingerprint(Document doc)
        {
            if (doc == null)
            {
                return string.Empty;
            }
            try
            {
                return DrawingFingerprint.Compute(doc.Database, doc);
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "fingerprint_failed", ex);
                return string.Empty;
            }
        }

        // ---------------- 文档 / 连接 / 任务 ----------------

        /// <summary>DocumentActivated：语义缓存全失效 + 刷新发现文件里的文档哈希。</summary>
        public void OnDocumentActivated()
        {
            try
            {
                Cache.InvalidateAll();
                Document doc = HostStateProbe.TryGetActiveDocument();
                string hash = doc == null || string.IsNullOrEmpty(doc.Name) ? "null" : Redactor.NameHash(doc.Name);
                Sessions.UpdateDocumentNameHash(hash);
                // 换文档 = 换修订号作用域：旧库/旧文档解钩、新文档从头计数（Attach 内部幂等）。
                AttachRevisionTracker(doc);
                Selection.Attach(doc);
                Transients.ClearAll();
                HostLog.Info("semantic", "cache_invalidated", "document", hash);
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "document_activated_failed", ex);
            }
        }

        public void OnConnectionClosed(PipeConnection connection, string reason)
        {
            HostLog.Info("pipe", "connection_removed", "reason", reason, "connections", ConnectionCount);
        }

        /// <summary>记录任务（写内存环 + 审计日志）。</summary>
        public void RecordTask(WorkItem item, Dictionary<string, object> response, bool sent)
        {
            TaskRecord record = new TaskRecord();
            record.OpId = item.OpId;
            record.Tool = item.Tool;
            record.ParamsDigestValue = item.ParamsDigestValue;
            record.StartedUtc = item.StartedUtc == DateTime.MinValue ? item.EnqueuedUtc : item.StartedUtc;
            record.ElapsedMs = item.ElapsedMs;

            try
            {
                if (response != null)
                {
                    record.Status = JsonCodec.GetStr(response, "status", "unknown");
                    bool ok = JsonCodec.GetBool(response, "ok", false);
                    Dictionary<string, object> error = JsonCodec.GetDict(response, "error");
                    if (!ok && error != null)
                    {
                        record.ErrorCode = JsonCodec.GetStr(error, "code");
                    }
                    record.Fingerprint = JsonCodec.GetStr(response, "targetFingerprint");
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "record_task_failed", ex);
            }

            Tasks.Add(record);
            HostLog.Info("task", "recorded",
                "op", record.OpId, "tool", record.Tool, "status", record.Status,
                "elapsedMs", record.ElapsedMs, "sent", sent,
                "errorCode", record.ErrorCode);
        }

        /// <summary>取消任务（task_cancel）：置取消位，由 handler 在批边界停止。</summary>
        public bool CancelTask(string opId)
        {
            bool accepted = Dispatcher.Cancel(opId);
            TaskRecord record = new TaskRecord();
            record.OpId = opId;
            record.Tool = "task_cancel";
            record.Status = accepted ? "cancelled" : "unknown";
            record.StartedUtc = DateTime.UtcNow;
            record.ElapsedMs = 0;
            Tasks.Add(record);
            HostLog.Info("task", "cancel_requested", "op", opId, "accepted", accepted);
            return accepted;
        }

        public TaskRecord FindTask(string opId)
        {
            return Tasks.Find(opId);
        }

        public List<TaskRecord> RecentTasks(int limit, DateTime sinceUtc)
        {
            return Tasks.Recent(limit, sinceUtc);
        }

        /// <summary>语义缓存命中率（sys_status）。</summary>
        public double CacheHitRate
        {
            get { return Stats.CacheHitRate; }
        }
    }
}
