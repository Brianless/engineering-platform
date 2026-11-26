using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 全局瞬态图形注册表（v3.0 物理防崩防线 #4）。
    /// 所有通过 TransientManager 挂载的瞬态图形都由此类统一追踪；
    /// 在 DocumentToBeDestroyed / DocumentActivated / HostRuntime.Stop 时强制 EraseTransient + Dispose，
    /// 避免野指针或图形驱动崩溃。
    /// </summary>
    internal sealed class TransientRegistry
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, List<Drawable>> _groups =
            new Dictionary<string, List<Drawable>>(StringComparer.OrdinalIgnoreCase);
        private bool _subscribed;

        public void SubscribeLifecycle()
        {
            if (_subscribed)
            {
                return;
            }
            try
            {
                Application.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                HostLog.Exception("transient", "subscribe_failed", ex);
            }
        }

        public void UnsubscribeLifecycle()
        {
            ClearAll();
            if (!_subscribed)
            {
                return;
            }
            try
            {
                Application.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            }
            catch (Exception)
            {
                // 退出阶段忽略
            }
            _subscribed = false;
        }

        public int AddGroup(string group, List<Drawable> drawables)
        {
            if (drawables == null || drawables.Count == 0)
            {
                return 0;
            }
            string key = string.IsNullOrEmpty(group) ? "default" : group;
            TransientManager tm = TransientManager.CurrentTransientManager;
            if (tm == null)
            {
                DisposeList(drawables);
                return 0;
            }

            List<Drawable> added = new List<Drawable>(drawables.Count);
            IntegerCollection vpNumbers = new IntegerCollection();
            for (int i = 0; i < drawables.Count; i++)
            {
                Drawable d = drawables[i];
                if (d == null)
                {
                    continue;
                }
                try
                {
                    tm.AddTransient(d, TransientDrawingMode.DirectTopmost, 128, vpNumbers);
                    added.Add(d);
                }
                catch (Exception ex)
                {
                    HostLog.Exception("transient", "add_failed", ex);
                    SafeDispose(d);
                }
            }

            lock (_sync)
            {
                List<Drawable> bucket;
                if (!_groups.TryGetValue(key, out bucket))
                {
                    bucket = new List<Drawable>();
                    _groups[key] = bucket;
                }
                bucket.AddRange(added);
            }
            return added.Count;
        }

        public int ClearGroup(string group)
        {
            if (string.IsNullOrEmpty(group))
            {
                return ClearAll();
            }
            List<Drawable> toRemove = null;
            lock (_sync)
            {
                if (_groups.TryGetValue(group, out toRemove))
                {
                    _groups.Remove(group);
                }
            }
            return EraseAndDispose(toRemove);
        }

        public int ClearAll()
        {
            List<Drawable> all = new List<Drawable>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, List<Drawable>> pair in _groups)
                {
                    if (pair.Value != null)
                    {
                        all.AddRange(pair.Value);
                    }
                }
                _groups.Clear();
            }
            return EraseAndDispose(all);
        }

        public Dictionary<string, int> GroupCounts()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                foreach (KeyValuePair<string, List<Drawable>> pair in _groups)
                {
                    counts[pair.Key] = pair.Value == null ? 0 : pair.Value.Count;
                }
            }
            return counts;
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                int cleared = ClearAll();
                if (cleared > 0)
                {
                    HostLog.Info("transient", "auto_cleared_on_doc_destroy", "count", cleared);
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("transient", "doc_destroy_clear_failed", ex);
            }
        }

        private static int EraseAndDispose(List<Drawable> items)
        {
            if (items == null || items.Count == 0)
            {
                return 0;
            }
            TransientManager tm = null;
            try
            {
                tm = TransientManager.CurrentTransientManager;
            }
            catch (Exception)
            {
                tm = null;
            }
            IntegerCollection vpNumbers = new IntegerCollection();
            int removed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Drawable d = items[i];
                if (d == null)
                {
                    continue;
                }
                if (tm != null)
                {
                    try
                    {
                        tm.EraseTransient(d, vpNumbers);
                    }
                    catch (Exception)
                    {
                        // 图形驱动已回收时忽略
                    }
                }
                SafeDispose(d);
                removed++;
            }
            return removed;
        }

        private static void DisposeList(List<Drawable> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                SafeDispose(items[i]);
            }
        }

        private static void SafeDispose(Drawable d)
        {
            if (d == null)
            {
                return;
            }
            try
            {
                d.Dispose();
            }
            catch (Exception)
            {
                // 忽略重复释放
            }
        }
    }
}
