using System;
using System.Collections.Generic;
using System.Threading;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 语义模型缓存：指纹为键、5 秒 TTL（设计 §3.5）。
    /// DocumentActivated 时全失效（防止「切了图还拿到旧摘要」）。
    /// </summary>
    internal sealed class SemanticCache
    {
        private sealed class Entry
        {
            public SemanticModel Model;
            /// <summary>写入时刻（TTL / AgeMs 用；**不参与 LRU 排序**）。</summary>
            public DateTime StoredUtc;
            /// <summary>
            /// LRU 序号（单调递增，严格有序）。
            ///
            /// 为什么不用 `DateTime.UtcNow` 记「最近使用」：Windows 上它的分辨率约 **15.6 ms**，
            /// 连续几次「Put → Put → Get → Put」会落在同一个 tick 内 → 时间戳全部相等 →
            /// 淘汰循环用「严格小于」比较时挑不出真正最久未用的那条（会丢掉刚用过的那条）。
            /// 这是自检 `cache_behavior_selftest.lruEvicts` 在真机上抓到的**真实弱点**，
            /// 不是自检写错了。计数器没有相等问题，也不依赖时钟精度。
            /// </summary>
            public long UseSeq;
        }

        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _sync = new object();
        private readonly int _ttlMs;
        private readonly int _maxEntries;
        private long _useSeq;

        /// <summary>
        /// 参数：
        ///   ttlMs: 条目存活时长（兜底回收；正确性由**内容指纹键**保证，不靠 TTL）。
        ///   maxEntries: 条目上限（默认 4）。M0 单文档场景通常只有 1~2 条（未改 / 改过一次），
        ///               给 4 是留一点余量；超限按 **LRU** 淘汰，避免「改了几版图全留在内存里」。
        /// </summary>
        public SemanticCache(int ttlMs, int maxEntries = 4)
        {
            _ttlMs = ttlMs > 0 ? ttlMs : DefaultTtlMs;
            _maxEntries = maxEntries > 0 ? maxEntries : 4;
        }

        /// <summary>默认 TTL：30 分钟。冷抽取在真机上是 ~20 s，热命中 ~1.4 s，5 s 的旧值等于没有缓存。</summary>
        public const int DefaultTtlMs = 1800000;

        /// <summary>默认条目上限。</summary>
        public const int DefaultMaxEntries = 4;

        public int TtlMs
        {
            get { return _ttlMs; }
        }

        public int MaxEntries
        {
            get { return _maxEntries; }
        }

        /// <summary>
        /// 该指纹的缓存条目已存在多少毫秒（不存在/已过期返回 -1）。
        ///
        /// 只读观测，**不刷新也不删除**条目（`Get` 会删过期项，这里刻意不删，
        /// 否则在缓存命中路径上调用它就会改变缓存状态）。
        /// 用途：真机判「命中缓存还剩多少 TTL」——把 TTL 调大之后要能看出是不是真的在复用。
        /// </summary>
        public long AgeMsOf(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return -1L;
            }
            lock (_sync)
            {
                Entry entry;
                if (!_map.TryGetValue(fingerprint, out entry) || entry == null)
                {
                    return -1L;
                }
                return (long)(DateTime.UtcNow - entry.StoredUtc).TotalMilliseconds;
            }
        }

        /// <summary>取缓存；过期或不存在返回 null。</summary>
        public SemanticModel Get(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return null;
            }
            lock (_sync)
            {
                Entry entry;
                if (!_map.TryGetValue(fingerprint, out entry) || entry == null)
                {
                    return null;
                }
                DateTime now = DateTime.UtcNow;
                if ((now - entry.StoredUtc).TotalMilliseconds > _ttlMs)
                {
                    _map.Remove(fingerprint);
                    return null;
                }
                // LRU：命中也算「用过」。用单调计数器，**不用时间戳**（见 Entry.UseSeq 注释）。
                entry.UseSeq = Interlocked.Increment(ref _useSeq);
                return entry.Model;
            }
        }

        public void Put(string fingerprint, SemanticModel model)
        {
            if (string.IsNullOrEmpty(fingerprint) || model == null)
            {
                return;
            }
            lock (_sync)
            {
                DateTime now = DateTime.UtcNow;
                Entry entry = new Entry();
                entry.Model = model;
                entry.StoredUtc = now;
                entry.UseSeq = Interlocked.Increment(ref _useSeq);
                _map[fingerprint] = entry;

                // 容量保护：按 **LRU** 淘汰（旧实现按「写入时间」淘汰，命中不刷新，
                // 会把「一直在用的那条」当成最旧丢掉 —— TTL 拉长到 30 分钟后这个差别才显出来）。
                while (_map.Count > _maxEntries)
                {
                    long oldest = long.MaxValue;
                    string oldestKey = null;
                    foreach (KeyValuePair<string, Entry> pair in _map)
                    {
                        if (pair.Value.UseSeq < oldest)
                        {
                            oldest = pair.Value.UseSeq;
                            oldestKey = pair.Key;
                        }
                    }
                    if (oldestKey == null)
                    {
                        break;
                    }
                    _map.Remove(oldestKey);
                    HostLog.Info("semantic", "cache_evicted_lru", "key", oldestKey,
                        "entries", _map.Count);
                }
            }
        }

        /// <summary>全失效（DocumentActivated 调用）。</summary>
        public void InvalidateAll()
        {
            lock (_sync)
            {
                _map.Clear();
            }
            HostLog.Info("semantic", "cache_invalidated_all");
        }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _map.Count;
                }
            }
        }
    }
}
