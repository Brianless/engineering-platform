using System;
using System.Collections.Generic;
using System.Threading;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Scheduling
{
    /// <summary>
    /// 一次请求的工作项。管道读线程只负责创建并入队；主线程执行完成后通过
    /// <see cref="TryComplete"/> 回填响应，保证响应只写一次（读线程永不阻塞等待）。
    /// </summary>
    internal sealed class WorkItem
    {
        public string OpId = string.Empty;
        public string Tool = string.Empty;
        public string SessionToken = string.Empty;
        public string HostInstanceId = string.Empty;
        public string ParamsDigestValue;
        public string TargetFingerprint;
        public Dictionary<string, object> Params = new Dictionary<string, object>(StringComparer.Ordinal);
        public int TimeoutMs = 15000;
        public bool Cancelable = true;

        /// <summary>
        /// 允许在「命令上下文」中执行（仅 ENG_SELFTEST 用）。
        /// 门禁默认要求 CMDACTIVE==0，避免在用户命令执行到一半时读图；
        /// 而自检命令本身就是一条命令，若不放行会永远读到 E1003 cmdactive=0x5。
        /// 该标记只允许宿主内部命令设置，管道请求一律为 false（PipeConnection 不设置）。
        /// </summary>
        public bool AllowCommandContext;

        public DateTime EnqueuedUtc = DateTime.UtcNow;
        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime QueueDeadlineUtc = DateTime.MinValue;
        public DateTime ExecDeadlineUtc = DateTime.MinValue;

        /// <summary>响应封套构造器（StartedUtc / opId / digest 在入队前已填好）。</summary>
        public ResponseBuilder Builder;

        /// <summary>响应发送回调（由 PipeConnection 注入：写帧 + 记录任务）。</summary>
        public Action<WorkItem, Dictionary<string, object>> OnCompleted;

        public string State = "queued";

        /// <summary>为 true 时表示该工作项已登记到延迟 Idle 回调（如 write_command 等待命令执行回执）。</summary>
        public bool Deferred;

        private int _cancelRequested;
        private int _completed;

        public bool CancelRequested
        {
            get { return Volatile.Read(ref _cancelRequested) != 0; }
        }

        public bool IsCompleted
        {
            get { return Volatile.Read(ref _completed) != 0; }
        }

        public void RequestCancel()
        {
            Interlocked.Exchange(ref _cancelRequested, 1);
            HostLog.Info("dispatch", "cancel_requested", "op", OpId, "tool", Tool);
        }

        /// <summary>回填响应；返回 false 表示已经完成过（重复完成被丢弃）。</summary>
        public bool TryComplete(Dictionary<string, object> response)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return false;
            }
            State = "done";
            Action<WorkItem, Dictionary<string, object>> callback = OnCompleted;
            if (callback != null)
            {
                try
                {
                    callback(this, response);
                }
                catch (Exception ex)
                {
                    HostLog.Exception("dispatch", "response_callback_failed", ex);
                }
            }
            return true;
        }

        public long ElapsedMs
        {
            get
            {
                double ms = (DateTime.UtcNow - StartedUtc).TotalMilliseconds;
                return ms < 0 ? 0 : (long)Math.Round(ms);
            }
        }
    }

    /// <summary>
    /// 主线程调度器（设计 §3.3）。
    /// - 队列容量 64，满则 E1003 queue_full；
    /// - Application.Idle 触发时逐个出队串行执行（M0 明确不并行）；
    /// - 看门狗每 250 ms 检查排队超时（默认 2000 ms）→ E1004 idle_not_pumping，
    ///   这是「模态对话框」的可观测信号：模态期间 Application.Idle 不再触发。
    /// </summary>
    internal sealed class MainThreadDispatcher
    {
        public const int QueueCapacity = 64;

        private sealed class DeferredCheck
        {
            public WorkItem Item;
            public DateTime DeadlineUtc;
            public Func<bool> TryFinishOnIdle;
            public Action OnTimeout;
        }

        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>(QueueCapacity);
        private readonly Dictionary<string, WorkItem> _active = new Dictionary<string, WorkItem>(StringComparer.Ordinal);
        private readonly List<DeferredCheck> _deferred = new List<DeferredCheck>();
        private readonly object _sync = new object();

        private Thread _watchdog;
        private volatile bool _running;
        private DateTime _lastIdleUtc = DateTime.UtcNow;
        private DateTime _lastPumpUtc = DateTime.UtcNow;

        /// <summary>排队等待上限（毫秒）；超过即判定主线程未泵送。</summary>
        public int QueueWaitMs = 2000;

        /// <summary>主线程执行委托（由 HostRuntime 注入）。</summary>
        public Func<WorkItem, Dictionary<string, object>> Executor;

        /// <summary>单次 Idle 最多处理多少条，避免长时间占住 UI 线程。</summary>
        public int MaxPerIdle = 4;

        public DateTime LastIdleUtc
        {
            get { return _lastIdleUtc; }
        }

        public int QueueLength
        {
            get
            {
                lock (_sync)
                {
                    return _queue.Count;
                }
            }
        }

        public int ActiveCount
        {
            get
            {
                lock (_sync)
                {
                    return _active.Count;
                }
            }
        }

        /// <summary>最近 withinMs 毫秒内是否有过 Idle（sys_status.idlePumping）。</summary>
        public bool IsPumping(int withinMs)
        {
            return (DateTime.UtcNow - _lastIdleUtc).TotalMilliseconds <= Math.Max(withinMs, 100);
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }
            _running = true;
            _lastIdleUtc = DateTime.UtcNow;
            _lastPumpUtc = DateTime.UtcNow;
            _watchdog = new Thread(WatchdogLoop);
            _watchdog.IsBackground = true;
            _watchdog.Name = "ep-dispatch-watchdog";
            _watchdog.Start();
            HostLog.Info("dispatch", "started", "queueCapacity", QueueCapacity, "queueWaitMs", QueueWaitMs);
        }

        public void Stop()
        {
            _running = false;
            lock (_sync)
            {
                while (_queue.Count > 0)
                {
                    WorkItem item = _queue.Dequeue();
                    if (item != null && item.Builder != null)
                    {
                        item.TryComplete(item.Builder.BuildFail(
                            WireError.Of(ErrorCodes.E3002, "host_shutdown"), "unknown"));
                    }
                }
                _active.Clear();
                _deferred.Clear();
            }
            HostLog.Info("dispatch", "stopped");
        }

        /// <summary>
        /// 登记延迟完成回调（供 write_command 等异步排队命令在下一轮 Application.Idle 回读执行结果与实体数量）。
        /// </summary>
        public void RegisterDeferred(WorkItem item, DateTime deadlineUtc, Func<bool> tryFinishOnIdle, Action onTimeout)
        {
            if (item == null || tryFinishOnIdle == null)
            {
                return;
            }
            item.Deferred = true;
            DeferredCheck entry = new DeferredCheck();
            entry.Item = item;
            entry.DeadlineUtc = deadlineUtc;
            entry.TryFinishOnIdle = tryFinishOnIdle;
            entry.OnTimeout = onTimeout;
            lock (_sync)
            {
                _deferred.Add(entry);
            }
        }

        /// <summary>
        /// 入队。返回 false 时 <paramref name="rejectReason"/> 给出原因（queue_full）。
        /// 排队截止时间在这里统一设置。
        /// </summary>
        public bool Enqueue(WorkItem item, out string rejectReason)
        {
            rejectReason = null;
            if (item == null)
            {
                rejectReason = "item_missing";
                return false;
            }
            item.QueueDeadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(QueueWaitMs, 200));
            lock (_sync)
            {
                if (_queue.Count >= QueueCapacity)
                {
                    rejectReason = "queue_full";
                    return false;
                }
                _queue.Enqueue(item);
                _active[item.OpId] = item;
            }
            return true;
        }

        /// <summary>Application.Idle 事件处理（AutoCAD 主线程）。</summary>
        public void OnIdle(object sender, EventArgs e)
        {
            _lastIdleUtc = DateTime.UtcNow;
            Pump();
        }

        /// <summary>由 Idle 或面板触发；主线程调用。</summary>
        public void Pump()
        {
            if (!_running)
            {
                return;
            }
            _lastPumpUtc = DateTime.UtcNow;
            PumpDeferredOnMainThread();
            int processed = 0;
            while (processed < MaxPerIdle)
            {
                WorkItem item;
                lock (_sync)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }
                    WorkItem peeked = _queue.Peek();
                    // v3.0 静默态自动排队：用户正在交互命令中且未临近超时，暂留队列等待下一次 Idle
                    if (peeked != null && !peeked.IsCompleted && !peeked.AllowCommandContext &&
                        peeked.QueueDeadlineUtc != DateTime.MinValue &&
                        DateTime.UtcNow.AddMilliseconds(350) < peeked.QueueDeadlineUtc &&
                        HostStateProbe.IsEditorBusy())
                    {
                        return;
                    }
                    item = _queue.Dequeue();
                }
                if (item == null || item.IsCompleted)
                {
                    continue;
                }
                processed++;
                Execute(item);
            }
        }

        private void Execute(WorkItem item)
        {
            item.State = "running";
            item.StartedUtc = DateTime.UtcNow;
            item.ExecDeadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(item.TimeoutMs, 500));
            if (item.Builder != null)
            {
                item.Builder.StartedUtc = item.StartedUtc;
            }

            Func<WorkItem, Dictionary<string, object>> executor = Executor;
            Dictionary<string, object> response;
            if (executor == null)
            {
                response = item.Builder != null
                    ? item.Builder.BuildFail(WireError.Of(ErrorCodes.E3002, "executor_missing"), "failed")
                    : null;
            }
            else
            {
                try
                {
                    response = executor(item);
                }
                catch (Exception ex)
                {
                    HostLog.Exception("dispatch", "executor_threw", ex);
                    response = item.Builder != null
                        ? item.Builder.BuildFail(WireError.Of(ErrorCodes.E3002, "internal_error"), "failed")
                        : null;
                }
            }

            if (item.Deferred && response == null)
            {
                // 延迟完成项由 _deferred 在后续 Idle 回调中收口并移除 _active
                return;
            }

            lock (_sync)
            {
                _active.Remove(item.OpId);
            }
            if (response != null)
            {
                item.TryComplete(response);
            }
        }

        private void PumpDeferredOnMainThread()
        {
            List<DeferredCheck> snapshot = null;
            lock (_sync)
            {
                if (_deferred.Count == 0)
                {
                    return;
                }
                snapshot = new List<DeferredCheck>(_deferred);
            }

            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < snapshot.Count; i++)
            {
                DeferredCheck entry = snapshot[i];
                bool remove = false;
                if (entry == null || entry.Item == null || entry.Item.IsCompleted)
                {
                    remove = true;
                }
                else
                {
                    try
                    {
                        if (entry.TryFinishOnIdle())
                        {
                            remove = true;
                        }
                        else if (now > entry.DeadlineUtc)
                        {
                            if (entry.OnTimeout != null)
                            {
                                entry.OnTimeout();
                            }
                            remove = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        HostLog.Exception("dispatch", "deferred_check_failed", ex);
                        remove = true;
                    }
                }

                if (remove)
                {
                    lock (_sync)
                    {
                        _deferred.Remove(entry);
                        if (entry != null && entry.Item != null)
                        {
                            _active.Remove(entry.Item.OpId);
                        }
                    }
                }
            }
        }

        /// <summary>按 opId 取消（task_cancel）。返回是否找到活动工作项。</summary>
        public bool Cancel(string opId)
        {
            if (string.IsNullOrEmpty(opId))
            {
                return false;
            }
            lock (_sync)
            {
                WorkItem item;
                if (_active.TryGetValue(opId, out item) && item != null && !item.IsCompleted)
                {
                    item.RequestCancel();
                    return true;
                }
            }
            return false;
        }

        /// <summary>按 opId 查活动工作项（task_status 用）。</summary>
        public WorkItem Find(string opId)
        {
            if (string.IsNullOrEmpty(opId))
            {
                return null;
            }
            lock (_sync)
            {
                WorkItem item;
                return _active.TryGetValue(opId, out item) ? item : null;
            }
        }

        private void WatchdogLoop()
        {
            while (_running)
            {
                try
                {
                    Thread.Sleep(250);
                    DateTime now = DateTime.UtcNow;
                    List<WorkItem> expired = null;
                    List<DeferredCheck> expiredDeferred = null;
                    lock (_sync)
                    {
                        if (_queue.Count > 0)
                        {
                            foreach (WorkItem queued in _queue)
                            {
                                if (queued != null && !queued.IsCompleted &&
                                    queued.QueueDeadlineUtc != DateTime.MinValue &&
                                    now > queued.QueueDeadlineUtc)
                                {
                                    if (expired == null)
                                    {
                                        expired = new List<WorkItem>();
                                    }
                                    expired.Add(queued);
                                }
                            }
                        }
                        if (_deferred.Count > 0)
                        {
                            for (int i = _deferred.Count - 1; i >= 0; i--)
                            {
                                DeferredCheck dc = _deferred[i];
                                if (dc != null && dc.Item != null && !dc.Item.IsCompleted && now > dc.DeadlineUtc)
                                {
                                    if (expiredDeferred == null)
                                    {
                                        expiredDeferred = new List<DeferredCheck>();
                                    }
                                    expiredDeferred.Add(dc);
                                    _deferred.RemoveAt(i);
                                    _active.Remove(dc.Item.OpId);
                                }
                            }
                        }
                    }

                    if (expiredDeferred != null)
                    {
                        for (int i = 0; i < expiredDeferred.Count; i++)
                        {
                            DeferredCheck dc = expiredDeferred[i];
                            try
                            {
                                if (dc.OnTimeout != null)
                                {
                                    dc.OnTimeout();
                                }
                            }
                            catch (Exception ex)
                            {
                                HostLog.Exception("dispatch", "deferred_timeout_failed", ex);
                            }
                        }
                    }

                    if (expired != null)
                    {
                        for (int i = 0; i < expired.Count; i++)
                        {
                            WorkItem item = expired[i];
                            HostLog.Warn("gate", "idle_not_pumping",
                                "op", item.OpId, "waitMs", QueueWaitMs, "advice", "close_modal_dialog");
                            if (item.Builder != null)
                            {
                                item.TryComplete(item.Builder.BuildFail(
                                    WireError.Of(ErrorCodes.E1004, "idle_not_pumping"), "failed"));
                            }
                            lock (_sync)
                            {
                                _active.Remove(item.OpId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    HostLog.Exception("dispatch", "watchdog_error", ex);
                }
            }
        }

        /// <summary>最近一次泵送距现在多久（毫秒）。</summary>
        public long SinceLastPumpMs
        {
            get
            {
                double ms = (DateTime.UtcNow - _lastPumpUtc).TotalMilliseconds;
                return ms < 0 ? 0 : (long)ms;
            }
        }
    }
}
