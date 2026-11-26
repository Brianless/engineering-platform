using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Scheduling;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 语义采集（设计 §3.5）。
    /// 只在主线程调用；单次最多 50,000 个图元（超限按句柄升序截断，counts 仍为真实总数）。
    /// 每个图元都必须走 try/catch：个别自定义对象取 Bounds 会抛 eInvalidExtents，
    /// 绝不能因为一个对象让整次读取失败。
    /// </summary>
    internal static class SemanticExtractor
    {
        private const int CancelCheckBatch = 500;

        /// <summary>采集 + 归并基础信息（识别与异常扫描由 Recognizer / AnomalyScanner 完成）。</summary>
        public static SemanticModel Extract(Document doc, LayerMapping mapping, int entityLimit,
            string fingerprint, Func<bool> shouldAbort = null, SemanticTimings timings = null)
        {
            SemanticModel model = new SemanticModel();
            model.Fingerprint = fingerprint ?? string.Empty;
            model.ExtractedUtc = DateTime.UtcNow;

            Database db;
            try
            {
                db = doc.Database;
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "database_unavailable", ex);
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "database_unavailable"));
            }

            string path = string.Empty;
            try
            {
                path = doc.Name ?? string.Empty;
                model.ReadOnly = doc.IsReadOnly;
            }
            catch (Exception)
            {
                model.ReadOnly = false;
            }

            model.DrawingBaseName = Redactor.BaseName(path);
            model.NameHash = Redactor.NameHash(path);
            model.ModifiedUtcIso = LastWriteIso(path);
            model.SoftwareVersion = HostStateProbe.TryGetProductVersion();
            model.SoftwareName = HostStateProbe.TryGetProductName();
            model.CivilApiAvailable = CivilApiProbe.Available;

            try
            {
                model.InsUnits = (int)db.Insunits;
            }
            catch (Exception)
            {
                model.InsUnits = 0;
            }
            model.UnitCode = SemanticModel.UnitCodeOf(model.InsUnits);
            model.ScaleToMeter = SemanticModel.ScaleToMeterOf(model.InsUnits);

            try
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    model.LayerCount = SafeLayerCount(tr, db);
                    model.BlockCount = SafeBlockCount(tr, db);

                    // 模型空间（timings 只做累计，不改变任何判定）
                    long tSpace = SemanticTimings.Now();
                    ObjectId modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                    // 块定义包围盒缓存：**只活在本方法这一层**（单次抽取期间块定义不可能变，
                    // 所以它没有失效问题；跨调用缓存则要处理 BLOCKEDIT，那是另一类正确性风险）。
                    Dictionary<ObjectId, BlockExtentsEntry> blockExtents =
                        new Dictionary<ObjectId, BlockExtentsEntry>();
                    HashSet<ObjectId> blockExtentsInProgress = new HashSet<ObjectId>();
                    // 块定义「是否声明了属性」缓存：决定要不要碰 BlockReference.AttributeCollection。
                    Dictionary<ObjectId, bool> blockAttrDefs = new Dictionary<ObjectId, bool>();
                    ExtractSpace(tr, modelSpaceId, model, entityLimit, shouldAbort, timings,
                        blockExtents, blockExtentsInProgress, blockAttrDefs);
                    if (timings != null)
                    {
                        timings.TraverseTicks += SemanticTimings.Elapsed(tSpace);
                    }

                    // 图纸空间（所有布局）
                    long tPaper = SemanticTimings.Now();
                    ExtractPaperSpaces(tr, db, model, entityLimit, shouldAbort, timings,
                        blockExtents, blockExtentsInProgress, blockAttrDefs);
                    if (timings != null)
                    {
                        timings.PaperTicks += SemanticTimings.Elapsed(tPaper);
                    }

                    tr.Commit();
                }
            }
            catch (ToolFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "extract_failed", ex);
                throw new ToolFailureException(WireError.Of(ErrorCodes.E3002, "extract_failed"));
            }

            // Civil 3D 原生对象（第二轮新增）：C3D 可用时以 confidence=1.0 收录，
            // 不可用时 Detected=false，图元侧继续按代理对象 + gaps[] 降级处理。
            // 必须放在实体采集之后：要靠句柄把原生对象的语义回填到已采集的图元上。
            long tCivil = SemanticTimings.Now();
            try
            {
                model.Civil = Civil3dReader.Read(doc);
                ApplyCivilHandles(model);
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "civil_read_failed", ex);
            }
            if (timings != null)
            {
                timings.CivilTicks += SemanticTimings.Elapsed(tCivil);
            }

            // Q-12：图纸范围——未触发实体截断上限时优先实时累加所有实体的包围盒并集，
            // 彻底规避 AutoCAD db.Extmin/Extmax 在擦除图元后不自动收缩（必须 ZOOM _E 才刷新）的滞后问题；
            // 仅在超限截断或并集为空时回退数据库头 Extmin/Extmax。
            long tExtents = SemanticTimings.Now();
            if (!model.EntityLimitReached)
            {
                ComputeUnionExtents(model);
            }
            if (!model.HasExtents)
            {
                try
                {
                    Point3d extMin = db.Extmin;
                    Point3d extMax = db.Extmax;
                    model.Min[0] = extMin.X;
                    model.Min[1] = extMin.Y;
                    model.Min[2] = extMin.Z;
                    model.Max[0] = extMax.X;
                    model.Max[1] = extMax.Y;
                    model.Max[2] = extMax.Z;
                    model.HasExtents = IsValidRange(model.Min, model.Max);
                }
                catch (Exception)
                {
                    model.HasExtents = false;
                }
            }
            if (!model.HasExtents)
            {
                ComputeUnionExtents(model);
            }
            if (timings != null)
            {
                timings.ExtentsTicks += SemanticTimings.Elapsed(tExtents);
            }

            // 按句柄升序稳定排序（游标分页与「按句柄截断」都依赖它）
            model.Entities.Sort(delegate(ObjectRef a, ObjectRef b) { return a.SortKey.CompareTo(b.SortKey); });
            return model;
        }

        /// <summary>
        /// 把 Civil 3D 原生对象的语义按句柄回填到已采集的图元上。
        /// 命中即：CivilKind 置位、confidence=1.0、band=auto、basis=civil_native，
        /// 并撤销「代理对象」标记（Civil API 可用时 AECC* 是原生对象，不是代理）。
        /// </summary>
        private static void ApplyCivilHandles(SemanticModel model)
        {
            if (model == null || model.Civil == null || !model.Civil.Detected)
            {
                return;
            }
            Dictionary<string, string> meanings = model.Civil.HandleMeanings;
            if (meanings == null || meanings.Count == 0)
            {
                return;
            }

            for (int i = 0; i < model.Entities.Count; i++)
            {
                ObjectRef obj = model.Entities[i];
                if (obj == null || string.IsNullOrEmpty(obj.Handle))
                {
                    continue;
                }
                string kindDisplay;
                if (!meanings.TryGetValue(obj.Handle, out kindDisplay))
                {
                    continue;
                }

                if (obj.IsProxy)
                {
                    obj.IsProxy = false;
                    if (model.ProxyCount > 0)
                    {
                        model.ProxyCount--;
                    }
                }
                obj.CivilKind = kindDisplay;
                obj.Meaning = kindDisplay;
                obj.Confidence = 1.0D;
                obj.Band = ConfidenceBand.Auto;
                obj.BasisList.Clear();
                obj.BasisList.Add(new Basis("civil_native", "civil3d:handle=" + obj.Handle));
            }
        }

        private static int SafeLayerCount(Transaction tr, Database db)
        {
            try
            {
                LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                return CountSymbolTable(table);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int SafeBlockCount(Transaction tr, Database db)
        {
            try
            {
                BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                return CountSymbolTable(table);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>符号表条目数（SymbolTable 没有 Count 属性，只能枚举）。</summary>
        private static int CountSymbolTable(IEnumerable table)
        {
            int count = 0;
            foreach (object unused in table)
            {
                count++;
            }
            return count;
        }

        private static void ExtractPaperSpaces(Transaction tr, Database db, SemanticModel model,
            int entityLimit, Func<bool> shouldAbort, SemanticTimings timings,
            Dictionary<ObjectId, BlockExtentsEntry> blockExtents, HashSet<ObjectId> blockExtentsInProgress,
            Dictionary<ObjectId, bool> blockAttrDefs)
        {
            try
            {
                DBDictionary layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout;
                    try
                    {
                        layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (layout == null || layout.ModelType)
                    {
                        continue;
                    }
                    model.PaperSpaceCount++;
                    ExtractSpace(tr, layout.BlockTableRecordId, model, entityLimit, shouldAbort, timings,
                        blockExtents, blockExtentsInProgress, blockAttrDefs);
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "paper_space_scan_failed", ex);
            }
        }

        private static void ExtractSpace(Transaction tr, ObjectId spaceId, SemanticModel model,
            int entityLimit, Func<bool> shouldAbort, SemanticTimings timings,
            Dictionary<ObjectId, BlockExtentsEntry> blockExtents, HashSet<ObjectId> blockExtentsInProgress,
            Dictionary<ObjectId, bool> blockAttrDefs)
        {
            BlockTableRecord record;
            try
            {
                record = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
            }
            catch (Exception)
            {
                return;
            }

            int processed = 0;
            foreach (ObjectId id in record)
            {
                if (id.IsErased)
                {
                    continue;
                }

                processed++;
                model.EntityCount++;

                if (shouldAbort != null && processed % CancelCheckBatch == 0 && shouldAbort())
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E3002, "cancelled_during_extract"));
                }

                if (model.Entities.Count >= entityLimit)
                {
                    // 上限命中：标记截断（read_overview.truncated=true + W1009）
                    model.EntityLimitReached = true;
                    model.Truncated = true;
                    // 继续计数（counts 必须是真实总数），但不再打开对象
                    continue;
                }

                ObjectRef obj = ReadEntity(tr, id, timings, blockExtents, blockExtentsInProgress,
                    blockAttrDefs);
                if (obj == null)
                {
                    continue;
                }
                model.Entities.Add(obj);
                if (obj.IsProxy)
                {
                    model.ProxyCount++;
                }
                if (obj.IsBlockReference)
                {
                    model.BlockReferenceCount++;
                }
            }
        }

        /// <summary>读取单个图元；任何异常都降级为「有句柄但无几何」，不让整次采集失败。</summary>
        private static ObjectRef ReadEntity(Transaction tr, ObjectId id, SemanticTimings timings,
            Dictionary<ObjectId, BlockExtentsEntry> blockExtents, HashSet<ObjectId> blockExtentsInProgress,
            Dictionary<ObjectId, bool> blockAttrDefs)
        {
            long tEntity = SemanticTimings.Now();
            Entity entity = null;
            long tOpen = SemanticTimings.Now();
            try
            {
                entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch (Exception)
            {
                entity = null;
            }
            if (timings != null)
            {
                timings.OpenEntityTicks += SemanticTimings.Elapsed(tOpen);
            }
            if (entity == null)
            {
                if (timings != null)
                {
                    timings.ReadEntityTicks += SemanticTimings.Elapsed(tEntity);
                }
                return null;
            }
            if (timings != null)
            {
                timings.EntitiesOpened++;
            }

            ObjectRef obj = new ObjectRef();
            try
            {
                obj.Handle = id.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                obj.SortKey = id.Handle.Value;
            }
            catch (Exception)
            {
                obj.Handle = "0";
                obj.SortKey = 0;
            }

            try
            {
                obj.DxfName = id.ObjectClass.DxfName ?? "UNKNOWN";
            }
            catch (Exception)
            {
                obj.DxfName = "UNKNOWN";
            }
            obj.DotNetType = entity.GetType().Name;

            try
            {
                obj.Layer = entity.Layer ?? "0";
                obj.Linetype = entity.Linetype;
                obj.ColorIndex = entity.ColorIndex;
                obj.Visible = entity.Visible;
            }
            catch (Exception)
            {
                // 属性取不到时保留默认值
            }

            // 包围盒。
            // 块引用走「块定义包围盒单次抽取内缓存」：6614 个块引用若只有 N 种块定义，
            // 块定义内部的几何量算从 6614 次降到 N 次；每个引用只做一次仿射变换 + 属性外框并集。
            // 非块引用（或缓存/变换取不到）仍走 `entity.GeometricExtents`，行为与旧版完全一致。
            long tBounds = SemanticTimings.Now();
            try
            {
                Extents3d extents;
                if (!TryGetPlacedExtents(tr, entity, blockExtents, blockExtentsInProgress,
                        blockAttrDefs, timings, out extents))
                {
                    extents = entity.GeometricExtents;
                    if (timings != null)
                    {
                        timings.BoundsCalls++;
                    }
                }
                obj.Min[0] = extents.MinPoint.X;
                obj.Min[1] = extents.MinPoint.Y;
                obj.Min[2] = extents.MinPoint.Z;
                obj.Max[0] = extents.MaxPoint.X;
                obj.Max[1] = extents.MaxPoint.Y;
                obj.Max[2] = extents.MaxPoint.Z;
                obj.HasExtents = IsValidRange(obj.Min, obj.Max);
                obj.HasCenter = true;
                obj.CenterX = (obj.Min[0] + obj.Max[0]) / 2D;
                obj.CenterY = (obj.Min[1] + obj.Max[1]) / 2D;
            }
            catch (Exception)
            {
                obj.HasExtents = false;
            }
            if (timings != null)
            {
                timings.BoundsTicks += SemanticTimings.Elapsed(tBounds);
            }

            // 曲线长度（zero_length 判定）
            obj.Length = SafeLength(entity);

            long tGeometry = SemanticTimings.Now();
            ExtractGeometry(entity, obj, tr, timings, blockAttrDefs);
            if (timings != null)
            {
                timings.GeometryTicks += SemanticTimings.Elapsed(tGeometry);
                timings.ReadEntityTicks += SemanticTimings.Elapsed(tEntity);
            }
            return obj;
        }

        /// <summary>
        /// 块定义包围盒缓存项（**只在单次 `Extract` 内有效**）。
        /// 用 `Has` 而不是 `Extents3d.IsNull` 表示「算不出来」，避免依赖 `Extents3d` 的版本差异。
        /// </summary>
        private struct BlockExtentsEntry
        {
            public bool Has;
            public Extents3d Value;
        }

        /// <summary>
        /// 取「已放置到世界坐标」的包围盒。**只有块引用**走这条路：
        /// 块定义包围盒（按定义 ObjectId 缓存、单次抽取内共享）× `BlockTransform`，
        /// 再并上该引用**独有**的属性外框（属性不在块定义里，必须按引用算）。
        ///
        /// 返回 false 时调用方回退到 `entity.GeometricExtents`（与旧版逐字节一致的行为）。
        /// </summary>
        private static bool TryGetPlacedExtents(Transaction tr, Entity entity,
            Dictionary<ObjectId, BlockExtentsEntry> cache, HashSet<ObjectId> inProgress,
            Dictionary<ObjectId, bool> attrDefCache, SemanticTimings timings, out Extents3d extents)
        {
            extents = default(Extents3d);
            BlockReference blockRef = entity as BlockReference;
            if (blockRef == null || cache == null || inProgress == null)
            {
                return false;
            }
            try
            {
                Extents3d local;
                if (!TryGetBlockDefinitionExtents(tr, blockRef.BlockTableRecord, cache, inProgress,
                        attrDefCache, timings, out local))
                {
                    return false;
                }
                Extents3d placed = local;
                placed.TransformBy(blockRef.BlockTransform);
                // 只有「块定义里声明过 ATTDEF」的引用才可能带属性。
                // 真机数据：6614 个无属性块引用上，**仅仅取一次 `AttributeCollection`** 就花了
                // 1.6 s（attrMs=1637 / attrReads=0 就是这么来的）—— 所以这一句门禁是实打实的收益，
                // 不是防御性编程。
                if (!DefinitionHasAttributes(tr, blockRef.BlockTableRecord, attrDefCache, timings))
                {
                    extents = placed;
                    return true;
                }
                AttributeCollection attributes = null;
                try
                {
                    attributes = blockRef.AttributeCollection;
                }
                catch (Exception)
                {
                    attributes = null;
                }
                if (attributes != null)
                {
                    foreach (ObjectId attributeId in attributes)
                    {
                        AttributeReference attribute =
                            tr.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                        if (attribute == null)
                        {
                            continue;
                        }
                        try
                        {
                            if (attribute.Visible)
                            {
                                placed.AddExtents(attribute.GeometricExtents);
                            }
                        }
                        catch (Exception)
                        {
                            // 单条属性取不到外框不影响块引用本身
                        }
                    }
                }
                extents = placed;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 该块定义是否声明了属性定义（ATTDEF）。按定义 ObjectId 缓存（单次抽取内有效）。
        ///
        /// 取不到时**保守返回 true**（宁可多读一次属性，也不能漏掉属性）—— 与「取不到包围盒就回退
        /// `GeometricExtents`」同一条原则：性能优化只允许在「确定不影响结果」的地方生效。
        /// </summary>
        private static bool DefinitionHasAttributes(Transaction tr, ObjectId blockDefinitionId,
            Dictionary<ObjectId, bool> cache, SemanticTimings timings)
        {
            if (cache == null)
            {
                return true;
            }
            bool cached;
            if (cache.TryGetValue(blockDefinitionId, out cached))
            {
                return cached;
            }
            bool has = true;
            try
            {
                BlockTableRecord definition =
                    tr.GetObject(blockDefinitionId, OpenMode.ForRead, false) as BlockTableRecord;
                if (definition != null)
                {
                    has = definition.HasAttributeDefinitions;
                }
            }
            catch (Exception)
            {
                has = true;
            }
            cache[blockDefinitionId] = has;
            if (has && timings != null)
            {
                timings.AttrDefBlocks++;
            }
            return has;
        }

        /// <summary>
        /// 块定义自身坐标系下的包围盒（各内部实体 `GeometricExtents` 并集），按定义 ObjectId 缓存。
        /// 嵌套块通过 `TryGetPlacedExtents` 递归；`inProgress` 防自引用/环导致无限递归。
        /// </summary>
        private static bool TryGetBlockDefinitionExtents(Transaction tr, ObjectId blockDefinitionId,
            Dictionary<ObjectId, BlockExtentsEntry> cache, HashSet<ObjectId> inProgress,
            Dictionary<ObjectId, bool> attrDefCache, SemanticTimings timings, out Extents3d extents)
        {
            extents = default(Extents3d);
            BlockExtentsEntry cached;
            if (cache.TryGetValue(blockDefinitionId, out cached))
            {
                if (timings != null)
                {
                    timings.BlockDefCacheHits++;
                }
                extents = cached.Value;
                return cached.Has;
            }
            if (!inProgress.Add(blockDefinitionId))
            {
                return false;
            }
            if (timings != null)
            {
                timings.BlockDefComputed++;
            }
            bool has = false;
            Extents3d union = default(Extents3d);
            try
            {
                BlockTableRecord definition =
                    tr.GetObject(blockDefinitionId, OpenMode.ForRead, false) as BlockTableRecord;
                if (definition != null)
                {
                    foreach (ObjectId childId in definition)
                    {
                        if (childId.IsErased)
                        {
                            continue;
                        }
                        Entity child = tr.GetObject(childId, OpenMode.ForRead, false) as Entity;
                        if (child == null)
                        {
                            continue;
                        }
                        Extents3d childExtents;
                        bool childHas;
                        if (child is BlockReference)
                        {
                            childHas = TryGetPlacedExtents(tr, child, cache, inProgress,
                                attrDefCache, timings, out childExtents);
                        }
                        else
                        {
                            childHas = false;
                            childExtents = default(Extents3d);
                            try
                            {
                                childExtents = child.GeometricExtents;
                                childHas = true;
                                if (timings != null)
                                {
                                    timings.BoundsCalls++;
                                }
                            }
                            catch (Exception)
                            {
                                childHas = false;
                            }
                        }
                        if (!childHas)
                        {
                            continue;
                        }
                        if (!has)
                        {
                            union = childExtents;
                            has = true;
                        }
                        else
                        {
                            union.AddExtents(childExtents);
                        }
                    }
                }
            }
            catch (Exception)
            {
                has = false;
            }
            finally
            {
                inProgress.Remove(blockDefinitionId);
            }
            BlockExtentsEntry entry = new BlockExtentsEntry();
            entry.Has = has;
            entry.Value = union;
            cache[blockDefinitionId] = entry;
            extents = union;
            return has;
        }

        private static void ExtractGeometry(Entity entity, ObjectRef obj, Transaction tr,
            SemanticTimings timings, Dictionary<ObjectId, bool> attrDefCache)
        {
            try
            {
                Line line = entity as Line;
                if (line != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["start"] = PointArray(line.StartPoint);
                    geometry["end"] = PointArray(line.EndPoint);
                    geometry["length"] = line.Length;
                    obj.Geometry = geometry;
                    return;
                }

                Arc arc = entity as Arc;
                if (arc != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["center"] = PointArray(arc.Center);
                    geometry["radius"] = arc.Radius;
                    geometry["startAngle_deg"] = RadiansToDegrees(arc.StartAngle);
                    geometry["endAngle_deg"] = RadiansToDegrees(arc.EndAngle);
                    obj.Geometry = geometry;
                    return;
                }

                Circle circle = entity as Circle;
                if (circle != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["center"] = PointArray(circle.Center);
                    geometry["radius"] = circle.Radius;
                    obj.Geometry = geometry;
                    return;
                }

                Ellipse ellipse = entity as Ellipse;
                if (ellipse != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["center"] = PointArray(ellipse.Center);
                    geometry["majorAxis"] = new List<object> { ellipse.MajorAxis.X, ellipse.MajorAxis.Y, ellipse.MajorAxis.Z };
                    geometry["minorAxis"] = new List<object> { ellipse.MinorAxis.X, ellipse.MinorAxis.Y, ellipse.MinorAxis.Z };
                    geometry["radiusRatio"] = ellipse.RadiusRatio;
                    geometry["startParam"] = ellipse.StartParam;
                    geometry["endParam"] = ellipse.EndParam;
                    geometry["closed"] = ellipse.Closed;
                    obj.Geometry = geometry;
                    return;
                }

                Polyline polyline = entity as Polyline;
                if (polyline != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["vertexCount"] = polyline.NumberOfVertices;
                    geometry["closed"] = polyline.Closed;
                    geometry["length"] = SafeLength(entity);
                    obj.Geometry = geometry;
                    return;
                }

                Polyline2d polyline2d = entity as Polyline2d;
                if (polyline2d != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["vertexCount"] = 0;
                    geometry["closed"] = polyline2d.Closed;
                    geometry["length"] = SafeLength(entity);
                    geometry["kind"] = "polyline2d";
                    obj.Geometry = geometry;
                    return;
                }

                Polyline3d polyline3d = entity as Polyline3d;
                if (polyline3d != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["vertexCount"] = 0;
                    geometry["closed"] = polyline3d.Closed;
                    geometry["length"] = SafeLength(entity);
                    geometry["kind"] = "polyline3d";
                    obj.Geometry = geometry;
                    return;
                }

                DBPoint point = entity as DBPoint;
                if (point != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["position"] = PointArray(point.Position);
                    obj.Geometry = geometry;
                    return;
                }

                DBText text = entity as DBText;
                if (text != null)
                {
                    string value = text.TextString ?? string.Empty;
                    string redacted = Redactor.Text(value, 80);
                    obj.RawTextLength = value.Length;
                    if (obj.Texts.Count < 5)
                    {
                        obj.Texts.Add(redacted);
                    }
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["text"] = redacted;
                    geometry["height"] = text.Height;
                    geometry["rotation_deg"] = RadiansToDegrees(text.Rotation);
                    geometry["position"] = PointArray(text.Position);
                    obj.Geometry = geometry;
                    return;
                }

                MText mtext = entity as MText;
                if (mtext != null)
                {
                    string raw = mtext.Contents ?? string.Empty;
                    string cleaned = Redactor.StripMTextCodes(raw);
                    string redacted = Redactor.Text(cleaned, 80);
                    obj.RawTextLength = cleaned.Length;
                    if (obj.Texts.Count < 5)
                    {
                        obj.Texts.Add(redacted);
                    }
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["text"] = redacted;
                    geometry["height"] = mtext.TextHeight;
                    geometry["rotation_deg"] = RadiansToDegrees(mtext.Rotation);
                    geometry["position"] = PointArray(mtext.Location);
                    obj.Geometry = geometry;
                    return;
                }

                BlockReference blockRef = entity as BlockReference;
                if (blockRef != null)
                {
                    obj.IsBlockReference = true;
                    try
                    {
                        obj.BlockName = blockRef.Name;
                    }
                    catch (Exception)
                    {
                        obj.BlockName = null;
                    }
                    // BlockReference 没有 IsFromExternalReference，需查块定义
                    try
                    {
                        BlockTableRecord blockDef = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
                        obj.IsFromExternalReference = blockDef.IsFromExternalReference || blockDef.IsFromOverlayReference;
                    }
                    catch (Exception)
                    {
                        obj.IsFromExternalReference = false;
                    }
                    if (DefinitionHasAttributes(tr, blockRef.BlockTableRecord, attrDefCache, timings))
                    {
                        ReadAttributes(tr, blockRef, obj, timings);
                    }
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    geometry["blockName"] = obj.BlockName;
                    geometry["isXref"] = obj.IsFromExternalReference;
                    obj.Geometry = geometry;
                    return;
                }

                Dimension dimension = entity as Dimension;
                if (dimension != null)
                {
                    Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    try
                    {
                        geometry["dimensionText"] = Redactor.Text(dimension.DimensionText, 80);
                    }
                    catch (Exception)
                    {
                        geometry["dimensionText"] = null;
                    }
                    try
                    {
                        geometry["measurement"] = dimension.Measurement;
                    }
                    catch (Exception)
                    {
                        geometry["measurement"] = 0D;
                    }
                    obj.Geometry = geometry;
                    return;
                }

                MLeader mleader = entity as MLeader;
                if (mleader != null)
                {
                    try
                    {
                        MText content = mleader.MText;
                        if (content != null)
                        {
                            string cleaned = Redactor.StripMTextCodes(content.Contents);
                            obj.RawTextLength = cleaned.Length;
                            if (obj.Texts.Count < 5)
                            {
                                obj.Texts.Add(Redactor.Text(cleaned, 80));
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // MLeader 文本取不到时忽略
                    }
                    return;
                }

                ProxyEntity proxy = entity as ProxyEntity;
                if (proxy != null)
                {
                    obj.IsProxy = true;
                    obj.Meaning = null;
                    obj.Confidence = 0D;
                    obj.Band = ConfidenceBand.Hint;
                    return;
                }

                // DXF 名兜底判定：Civil 3D 代理对象在部分环境里 DXF 名不是 ACAD_PROXY_ENTITY
                if (IsProxyByDxfName(obj.DxfName))
                {
                    obj.IsProxy = true;
                    obj.Meaning = null;
                    obj.Confidence = 0D;
                    obj.Band = ConfidenceBand.Hint;
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("semantic", "geometry_extract_failed", ex);
            }
        }

        private static void ReadAttributes(Transaction tr, BlockReference blockRef, ObjectRef obj,
            SemanticTimings timings)
        {
            long tAttr = SemanticTimings.Now();
            try
            {
                AttributeCollection attributes = blockRef.AttributeCollection;
                if (attributes == null)
                {
                    return;
                }
                foreach (ObjectId attributeId in attributes)
                {
                    AttributeReference attribute;
                    try
                    {
                        // 走**当前事务**：旧写法 `attributeId.GetObject(...)` 是脱离事务的逐条打开，
                        // 属性上万条时每条都要单独起一次对象打开（真机 6614 块引用 × 每条属性）。
                        attribute = tr.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (attribute == null)
                    {
                        continue;
                    }
                    string tag = attribute.Tag ?? string.Empty;
                    string value = attribute.TextString ?? string.Empty;
                    if (tag.Length == 0)
                    {
                        continue;
                    }
                    obj.Attributes[tag] = Redactor.Text(value, 200);
                    if (obj.Texts.Count < 5 && value.Length > 0)
                    {
                        obj.Texts.Add(Redactor.Text(value, 80));
                    }
                    if (timings != null)
                    {
                        timings.AttrReads++;
                    }
                }
            }
            catch (Exception)
            {
                // 属性读取失败不影响对象本身
            }
            finally
            {
                if (timings != null)
                {
                    timings.AttrTicks += SemanticTimings.Elapsed(tAttr);
                }
            }
        }

        /// <summary>
        /// 代理对象判定：类型名 / DXF 名双条件（Civil 3D 代理在不同 OE 环境下 DXF 名不同）。
        /// 第二轮修正：Civil 3D API 可用时，AECC* 是原生对象（不是代理），必须不判为代理，
        /// 否则原生对象的语义会被压成 confidence=0。
        /// </summary>
        public static bool IsProxyByDxfName(string dxfName)
        {
            if (string.IsNullOrEmpty(dxfName))
            {
                return false;
            }
            if (string.Equals(dxfName, "ACAD_PROXY_ENTITY", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (dxfName.StartsWith("AECC", StringComparison.OrdinalIgnoreCase))
            {
                // Civil 3D 在场 → 原生对象；不在场 → 代理对象
                return !CivilApiProbe.Available;
            }
            return false;
        }

        /// <summary>
        /// 取长度：Curve 基类没有 Length 属性，按具体类型取（Line/Arc/Polyline/Circle）。
        /// 取不到返回 0，由 AnomalyScanner 判定 zero_length。
        /// </summary>
        private static double SafeLength(Entity entity)
        {
            try
            {
                Line line = entity as Line;
                if (line != null)
                {
                    return line.Length;
                }
                Arc arc = entity as Arc;
                if (arc != null)
                {
                    return arc.Length;
                }
                Polyline polyline = entity as Polyline;
                if (polyline != null)
                {
                    return polyline.Length;
                }
                Circle circle = entity as Circle;
                if (circle != null)
                {
                    return circle.Circumference;
                }
                return 0D;
            }
            catch (Exception)
            {
                return 0D;
            }
        }

        private static List<object> PointArray(Point3d point)
        {
            return new List<object> { point.X, point.Y, point.Z };
        }

        private static double RadiansToDegrees(double radians)
        {
            return Math.Round(radians * 180D / Math.PI, 6);
        }

        private static bool IsValidRange(double[] min, double[] max)
        {
            if (min == null || max == null)
            {
                return false;
            }
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(min[i]) || double.IsNaN(max[i]) || double.IsInfinity(min[i]) || double.IsInfinity(max[i]))
                {
                    return false;
                }
            }
            return max[0] >= min[0] && max[1] >= min[1];
        }

        private static void ComputeUnionExtents(SemanticModel model)
        {
            double[] min = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] max = { double.MinValue, double.MinValue, double.MinValue };
            bool any = false;
            for (int i = 0; i < model.Entities.Count; i++)
            {
                ObjectRef obj = model.Entities[i];
                if (!obj.HasExtents)
                {
                    continue;
                }
                any = true;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (obj.Min[axis] < min[axis])
                    {
                        min[axis] = obj.Min[axis];
                    }
                    if (obj.Max[axis] > max[axis])
                    {
                        max[axis] = obj.Max[axis];
                    }
                }
            }
            if (!any)
            {
                min[0] = 0;
                min[1] = 0;
                min[2] = 0;
                max[0] = 100;
                max[1] = 100;
                max[2] = 0;
            }
            model.Min = min;
            model.Max = max;
            model.HasExtents = any;
        }

        private static string LastWriteIso(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    return null;
                }
                return TimeUtil.ToIso(System.IO.File.GetLastWriteTimeUtc(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
