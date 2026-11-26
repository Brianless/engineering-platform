using System.Diagnostics;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 语义抽取的**分段计时**容器（性能诊断专用，2026-11-22 真机 40 s 事故）。
    ///
    /// 只做两件事：累计耗时、累计调用次数；**不参与任何工具输出**、不影响任何判定。
    /// 调用方（`HostRuntime.GetSemanticModel`）在抽取结束后打成一行
    /// `semantic|extract_breakdown` 日志，用于定位热点，随后即可移除或降级为 Debug。
    ///
    /// ## 为什么内部存 **ticks** 而不是毫秒
    ///
    /// 内层是热路径：`BoundsMs`/`AttrMs` 每次调用只有几十~几百微秒。
    /// 若每次调用都 `(elapsed * 1000 / frequency)` 取整，**单次会被截断成 0**，
    /// 累加 6000 多次仍然是 0 —— 那就等于没测。
    /// 所以这里一律累计 `Stopwatch` 原始 ticks，**只在写日志那一刻**换算一次成毫秒。
    ///
    /// ## 为什么单独一个类型而不是往 <see cref="SemanticModel"/> 塞字段
    ///
    /// `SemanticModel` 会被序列化成工具结果（`ToDict()`）。诊断字段一旦进去，
    /// 就会变成对外契约的一部分 —— 那正是我们要避免的「顺手改语义」。
    /// </summary>
    internal sealed class SemanticTimings
    {
        // ---- 顶层分段（与 GetSemanticModel 的调用顺序一一对应）----
        /// <summary>模型空间实体遍历（含 ReadEntity）。</summary>
        public long TraverseTicks;
        /// <summary>图纸空间（所有布局）遍历。</summary>
        public long PaperTicks;
        /// <summary>Civil 3D 原生对象快照（`Civil3dReader.Read`）。</summary>
        public long CivilTicks;
        /// <summary>图纸范围计算（`db.Extmin/Extmax`，失败时退回对象包围盒并集）。</summary>
        public long ExtentsTicks;

        // ---- ReadEntity 内部拆分（定位「为什么每个图元 5.5 ms」）----
        /// <summary>逐个图元读取的累计耗时（≈ 下面几项 + 句柄/图层/类型等零碎开销）。</summary>
        public long ReadEntityTicks;
        /// <summary>`tr.GetObject(...)` 打开图元的累计耗时。</summary>
        public long OpenEntityTicks;
        /// <summary>`entity.GeometricExtents` 累计 —— **块引用**会在这里触发块定义包围盒计算。</summary>
        public long BoundsTicks;
        /// <summary>`ExtractGeometry` 累计（各类型分支几何取值，含块引用查块定义）。</summary>
        public long GeometryTicks;
        /// <summary>
        /// 块引用属性读取累计 —— **含「取 `AttributeCollection`」这一步本身的开销**。
        ///
        /// 真机上出现过 `attrMs=1637` 而 `attrReads=0`：那不是埋点错位，而是 6614 个块引用
        /// **都没有属性**，时间全花在取 `AttributeCollection` 这个属性上（≈0.25 ms/次）。
        /// 现在 `DefinitionHasAttributes()` 先按块定义挡一道，无 ATTDEF 的块连这个属性都不取。
        /// </summary>
        public long AttrTicks;

        // ---- 计数（判断「耗时是否被放大到不成比例」，比 ms 更能说明问题）----
        /// <summary>成功打开的图元数。</summary>
        public int EntitiesOpened;
        /// <summary>`GeometricExtents` 调用次数。</summary>
        public int BoundsCalls;
        /// <summary>块引用属性读取次数（每读到一条属性 +1）。</summary>
        public int AttrReads;
        /// <summary>块定义包围盒**实际计算**次数（= 不同块定义种类的数量级）。</summary>
        public int BlockDefComputed;
        /// <summary>块定义包围盒**缓存命中**次数（= 块引用数 − 计算次数 − 取不到的次数）。</summary>
        public int BlockDefCacheHits;
        /// <summary>
        /// 带属性定义（ATTDEF）的块定义种数。
        /// 只有这些块定义下的引用才可能带属性；其余引用连 `AttributeCollection` 都不该碰
        /// （真机实测：6614 个无属性块引用上，仅「取 AttributeCollection」就花了 1.6 s）。
        /// </summary>
        public int AttrDefBlocks;

        // ---- 计时工具（单调时钟；不引入 DateTime，符合「时间一律 UTC / 不用本地时间」的既有约定）----

        /// <summary>`Stopwatch` 高精度计数器当前值（不受系统时间调整影响）。</summary>
        public static long Now()
        {
            return Stopwatch.GetTimestamp();
        }

        /// <summary>从 `startTicks` 到现在的原始 ticks 差（可为 0；用 <see cref="TicksToMs"/> 换算）。</summary>
        public static long Elapsed(long startTicks)
        {
            long delta = Stopwatch.GetTimestamp() - startTicks;
            return delta > 0L ? delta : 0L;
        }

        /// <summary>ticks → 毫秒（向下取整）。**只应在写日志/汇总时调用一次**，不要在热路径里逐次调用。</summary>
        public static long TicksToMs(long ticks)
        {
            long frequency = Stopwatch.Frequency;
            if (frequency <= 0L || ticks <= 0L)
            {
                return 0L;
            }
            return ticks * 1000L / frequency;
        }

        /// <summary>单次计时的毫秒数（用于「一次调用本身就 ≥1 ms」的场景，如指纹/识别/异常扫描）。</summary>
        public static long MsSince(long startTicks)
        {
            return TicksToMs(Elapsed(startTicks));
        }
    }
}
