using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 实时选区感知器：挂载 Editor.SelectionAdded / Document.ImpliedSelectionChanged，
    /// 同时支持按需主动读取 Editor.SelectImplied()，让工程师鼠标框选实体即可直接给 AI 下指令。
    /// </summary>
    internal sealed class SelectionTracker
    {
        private readonly object _sync = new object();
        private Document _attachedDoc;
        private List<string> _lastHandles = new List<string>();
        private DateTime _lastChangedUtc = DateTime.MinValue;

        public void Attach(Document doc)
        {
            if (ReferenceEquals(_attachedDoc, doc))
            {
                return;
            }
            Detach();
            if (doc == null)
            {
                return;
            }
            _attachedDoc = doc;
            try
            {
                doc.ImpliedSelectionChanged += OnImpliedSelectionChanged;
            }
            catch (Exception ex)
            {
                HostLog.Exception("selection", "attach_implied_failed", ex);
            }
            try
            {
                if (doc.Editor != null)
                {
                    doc.Editor.SelectionAdded += OnSelectionAdded;
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("selection", "attach_added_failed", ex);
            }
        }

        public void Detach()
        {
            Document doc = _attachedDoc;
            _attachedDoc = null;
            if (doc == null)
            {
                return;
            }
            try
            {
                doc.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
            }
            catch (Exception)
            {
                // 文档已销毁时解钩忽略
            }
            try
            {
                if (doc.Editor != null)
                {
                    doc.Editor.SelectionAdded -= OnSelectionAdded;
                }
            }
            catch (Exception)
            {
                // 文档已销毁时解钩忽略
            }
        }

        public DateTime LastChangedUtc
        {
            get
            {
                lock (_sync)
                {
                    return _lastChangedUtc;
                }
            }
        }

        public List<string> GetCachedHandles()
        {
            lock (_sync)
            {
                return new List<string>(_lastHandles);
            }
        }

        /// <summary>读取当前活动文档的隐式选区 ObjectId 列表（主线程调用）。</summary>
        public List<ObjectId> ReadLiveSelectionIds(Document doc)
        {
            List<ObjectId> ids = new List<ObjectId>();
            if (doc == null)
            {
                return ids;
            }
            try
            {
                Editor ed = doc.Editor;
                if (ed == null)
                {
                    return ids;
                }
                PromptSelectionResult sel = ed.SelectImplied();
                if (sel != null && sel.Status == PromptStatus.OK && sel.Value != null)
                {
                    ObjectId[] arr = sel.Value.GetObjectIds();
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (!arr[i].IsNull && !arr[i].IsErased)
                            {
                                ids.Add(arr[i]);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("selection", "read_implied_failed", ex);
            }

            List<string> handles = new List<string>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                try
                {
                    handles.Add(ids[i].Handle.Value.ToString("X", CultureInfo.InvariantCulture));
                }
                catch (Exception)
                {
                    // 跳过无效句柄
                }
            }
            lock (_sync)
            {
                _lastHandles = handles;
                if (handles.Count > 0)
                {
                    _lastChangedUtc = DateTime.UtcNow;
                }
            }
            return ids;
        }

        private void OnImpliedSelectionChanged(object sender, EventArgs e)
        {
            try
            {
                Document doc = _attachedDoc;
                if (doc != null)
                {
                    ReadLiveSelectionIds(doc);
                }
            }
            catch (Exception)
            {
                // 事件回调绝不外抛
            }
        }

        private void OnSelectionAdded(object sender, SelectionAddedEventArgs e)
        {
            try
            {
                if (e == null || e.AddedObjects == null)
                {
                    return;
                }
                ObjectId[] added = e.AddedObjects.GetObjectIds();
                if (added == null || added.Length == 0)
                {
                    return;
                }
                lock (_sync)
                {
                    HashSet<string> set = new HashSet<string>(_lastHandles, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < added.Length; i++)
                    {
                        if (!added[i].IsNull && !added[i].IsErased)
                        {
                            string h = added[i].Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                            if (set.Add(h))
                            {
                                _lastHandles.Add(h);
                            }
                        }
                    }
                    _lastChangedUtc = DateTime.UtcNow;
                }
            }
            catch (Exception)
            {
                // 事件回调绝不外抛
            }
        }
    }
}
