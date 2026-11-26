using System;
using System.Globalization;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 当前文档的**修订号**：缓存键的附加项（2026-11-22 真机「未保存修改读到旧模型」的修复件）。
    ///
    /// ## 为什么需要它（指纹本身不够）
    ///
    /// `DrawingFingerprint.Compute` 的六项组成是
    /// `路径哈希 + Handseed + 模型空间实体数 + 前 200 个句柄 + **文件 mtime**`。
    /// 对一个**改了但没保存**的图：
    ///
    /// | 修改 | Handseed | 实体数 | 关键句柄 | 文件 mtime | 指纹 |
    /// |---|---|---|---|---|---|
    /// | 移动图元 / 改文字 / 改属性值 / 改图层颜色 | 不变 | 不变 | 不变 | 不变 | **不变** |
    /// | 新增图元 | 变 | 变 | 可能变 | 不变 | 变 |
    /// | 删除图元 | 不变 | 变 | 可能变 | 不变 | 变 |
    ///
    /// 也就是说指纹只识别「身份 + 结构性变化」，**「结构没变但内容变了」一律漏**。
    /// 因此缓存键必须 = `指纹 + "#" + 修订号`：指纹兜「身份/结构变化」与「事件漏挂但结构变了」，
    /// 修订号补「结构没变但内容变了」。
    ///
    /// ## 为什么不用 `DBMOD` / `Handseed` 当修订号
    ///
    /// * **`DBMOD`**：保存后归零 →「改完保存（0）」与「从未修改（0）」撞键，直接把旧模型返回。
    /// * **`Handseed`**：只在新建句柄时增长，改已有实体完全不变。
    ///
    /// ## 订阅的事件
    ///
    /// `ObjectModified` / `ObjectAppended` / `ObjectErased` / `SaveComplete` —— 覆盖「改/增/删」；
    /// 外加 `Document.CommandEnded` —— 粗粒度兜底，兜住「事件漏挂但确实执行过命令」的情形；
    /// 外加 `Application.SystemVariableChanged` —— 系统变量（D-03，2026-11-22 真机确认漏检）。
    /// 处理器**只做 `Interlocked.Increment`**（`ObjectModified` 在大编辑里会高频触发，
    /// 里面绝不做日志/IO/加锁）。
    ///
    /// ## D-03：为什么必须单独订阅系统变量
    ///
    /// 2026-11-22 真机实测（docs/05 §1.22.5–1.22.6）：`(setvar "PDMODE" 35)` 之后
    /// `ObjectModified` 不触发、`CommandEnded` 对 LISP 路径也不触发 → 修订号纹丝不动 →
    /// 缓存命中返回**旧模型**，陈旧窗口等于 TTL（30 min）。
    /// 危害不在 `PDMODE` 这种显示变量，而在 `INSUNITS` / `MEASUREMENT` 这类**单位语义**变量：
    /// 改完单位仍按旧单位解释，是静默算错数。
    ///
    /// **有意不做白名单过滤**（只跟单位类）：本类里 `OnCommandEnded` 的注释已经定了口径
    /// 「宁可多一次 miss，不可漏一次修改」。系统变量名清单随版本漂移，白名单漏一个就是静默错数据；
    /// 多一次 miss 的代价只是重抽一次（热读 ~2.4 s）。
    /// 代价是命令行密集操作时可能多几次失效 —— 若将来实测到「连续两次读都不命中」，再改成
    /// 「单位类变量白名单 + 未知名保守自增」，不要反过来做。
    ///
    /// ## 剩余风险（如实记录）
    ///
    /// 若某个修改既不触发上述任何事件、也不来自命令执行、也不是系统变量（例如另一个插件的
    /// 纯 API 写操作），修订号不会变；此时靠指纹，两者都不变则最多陈旧到 TTL（30 分钟）为止。
    /// 这是**有意取舍**：TTL 封顶，而不是永久旧。
    /// </summary>
    internal sealed class DocumentRevision
    {
        private readonly object _sync = new object();
        private Database _db;
        private Document _doc;
        private long _revision;
        private long _bumps;

        /// <summary>当前修订号（单调递增快照）。</summary>
        public long Current
        {
            get { return Interlocked.Read(ref _revision); }
        }

        /// <summary>累计自增次数（诊断用；只在需要时读，不参与判定）。</summary>
        public long Bumps
        {
            get { return Interlocked.Read(ref _bumps); }
        }

        /// <summary>
        /// 组合缓存键：`指纹 + "#" + 修订号`。**纯函数**，不碰 CAD 对象，便于自检与复用。
        /// </summary>
        public static string ComposeKey(string fingerprint, long revision)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return string.Empty;
            }
            return fingerprint + "#" + revision.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 当前可用的缓存键；判定不了「是不是同一张图」时返回 null（调用方必须**绕过缓存**）。
        ///
        /// `mode` 告诉调用方走的是哪条路，便于真机一眼定位：
        ///   * `ref`     —— 引用相等（快路径，正常情况）
        ///   * `logical` —— 引用不相等但逻辑身份相同（`doc.Name` + `db.Filename` 都一致）
        ///   * `miss`    —— 判定不了：**不许退回「仅指纹」**，见 `GetSemanticModel` 的旁路逻辑
        ///
        /// 为什么不只看引用相等：真机上 `Application.DocumentManager.MdiActiveDocument` 与
        /// `doc.Database` 可能每次返回**不同的托管包装实例**，`ReferenceEquals` 恒为 false，
        /// 于是「修订号永远用不上」—— D-02 第一版就是这样在真机上静默失效的
        /// （日志里 `revisionKeyed=False`，但 `revision_attached` 与 `rev=2` 都正常）。
        /// </summary>
        public string TryComposeKey(Document doc, Database db, string fingerprint, out string mode)
        {
            mode = "miss";
            if (string.IsNullOrEmpty(fingerprint))
            {
                return null;
            }
            lock (_sync)
            {
                if (_db == null || _doc == null)
                {
                    return null;
                }
                bool same;
                if (ReferenceEquals(_db, db) && ReferenceEquals(_doc, doc))
                {
                    same = true;
                    mode = "ref";
                }
                else
                {
                    same = SameDocumentIdentity(doc, db);
                    mode = same ? "logical" : "miss";
                }
                if (!same)
                {
                    return null;
                }
                return ComposeKey(fingerprint, _revision);
            }
        }

        /// <summary>
        /// 逻辑身份判定：**引用不相等**时用来判断「是不是同一张图」。
        ///
        /// 判据：`doc.Name` 相同 **且** `Database.Filename` 相同（两者都忽略大小写；
        /// 未保存的新图两者都是空串，空串等空串 → 视为同一张图）。
        ///
        /// **刻意不用 `Handseed`**：新增图元会让 `Handseed` 变化，那会把「同一张图」误判成
        /// 不同图，导致每次新增实体都换键（缓存永不命中）—— 那是把正确性问题换成性能问题。
        ///
        /// 命名注意：本方法**刻意不叫** `SameDocumentLocked` —— 那个名字含 `DocumentLock` 子串，
        /// 会撞上「宿主不得使用 `DocumentLock` 写操作 API」的静态规则（误报）。它不是写操作。
        /// </summary>
        private bool SameDocumentIdentity(Document doc, Database db)
        {
            if (doc == null || db == null)
            {
                return false;
            }
            return string.Equals(SafeDocName(doc), SafeDocName(_doc), StringComparison.OrdinalIgnoreCase)
                && string.Equals(SafeFileName(db), SafeFileName(_db), StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeDocName(Document doc)
        {
            if (doc == null)
            {
                return string.Empty;
            }
            try
            {
                return doc.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeFileName(Database db)
        {
            if (db == null)
            {
                return string.Empty;
            }
            try
            {
                return db.Filename ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>挂到文档上（幂等：同一文档重复调用不会重复订阅；换文档自动解钩旧的）。</summary>
        public void Attach(Document doc)
        {
            Database db = null;
            if (doc != null)
            {
                try
                {
                    db = doc.Database;
                }
                catch (Exception)
                {
                    db = null;
                }
            }
            if (db == null)
            {
                Detach();
                return;
            }
            lock (_sync)
            {
                if (ReferenceEquals(_db, db) && ReferenceEquals(_doc, doc))
                {
                    return;
                }
                UnsubscribeLocked();
                _db = db;
                _doc = doc;
                Interlocked.Increment(ref _revision);
                try
                {
                    db.ObjectModified += OnObjectChanged;
                    db.ObjectAppended += OnObjectChanged;
                    db.ObjectErased += OnObjectErased;
                    db.SaveComplete += OnSaveComplete;
                    doc.CommandEnded += OnCommandEnded;
                    // D-03：系统变量变更既不走 ObjectModified，也不走 CommandEnded（真机实测）。
                    // 静态事件，Detach 必须退订，否则换文档后仍在自增。
                    Application.SystemVariableChanged += OnSystemVariableChanged;
                }
                catch (Exception ex)
                {
                    // 订阅失败不能让宿主不可用：退化成「仅指纹做键」（行为与上一版一致）。
                    HostLog.Exception("semantic", "revision_subscribe_failed", ex);
                    UnsubscribeLocked();
                }
            }
            HostLog.Info("semantic", "revision_attached", "revision", Current);
        }

        /// <summary>解钩（切文档 / 停止运行时调用）。</summary>
        public void Detach()
        {
            lock (_sync)
            {
                UnsubscribeLocked();
            }
        }

        private void UnsubscribeLocked()
        {
            Database db = _db;
            Document doc = _doc;
            _db = null;
            _doc = null;
            try
            {
                if (db != null)
                {
                    db.ObjectModified -= OnObjectChanged;
                    db.ObjectAppended -= OnObjectChanged;
                    db.ObjectErased -= OnObjectErased;
                    db.SaveComplete -= OnSaveComplete;
                }
                if (doc != null)
                {
                    doc.CommandEnded -= OnCommandEnded;
                }
                // 静态事件：不在这里退订就是全局泄漏（进程内所有文档实例都会收到）。
                Application.SystemVariableChanged -= OnSystemVariableChanged;
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "revision_unsubscribe_failed", ex);
            }
        }

        private void Bump(string source)
        {
            Interlocked.Increment(ref _revision);
            long total = Interlocked.Increment(ref _bumps);
            // DEBUG 级别 + 抽样：高频事件全打会淹掉日志，只在每 50 次留一条痕迹。
            if (total % 50L == 0L)
            {
                HostLog.Debug("semantic", "revision_bumped", "source", source, "revision", Current);
            }
        }

        private void OnObjectChanged(object sender, ObjectEventArgs e)
        {
            Bump("object");
        }

        private void OnObjectErased(object sender, ObjectErasedEventArgs e)
        {
            Bump("erased");
        }

        private void OnSaveComplete(object sender, EventArgs e)
        {
            Bump("save");
        }

        private void OnCommandEnded(object sender, CommandEventArgs e)
        {
            // 粗粒度兜底：命令执行完就认为可能有改动（宁可多一次 miss，不可漏一次修改）。
            Bump("command");
        }

        /// <summary>
        /// 系统变量变更（D-03）。**有意不区分变量名**：见类注释里的取舍说明。
        ///
        /// 签名用 `EventArgs` 而不写具体 `XxxEventArgs`：委托逆变允许基类形参匹配
        /// `EventHandler&lt;TArgs&gt;`，这样 acmgd 换版本改 args 类型名也不会编译不过 —— 
        /// 本处理器不看 args 的内容，只当作"有东西变了"。
        /// </summary>
        private void OnSystemVariableChanged(object sender, EventArgs e)
        {
            Bump("sysvar");
        }
    }
}
