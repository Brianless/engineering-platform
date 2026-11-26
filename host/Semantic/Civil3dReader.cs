using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 单个 Civil 3D 原生对象的只读记录。
    /// 原生对象是本平台可得的最可信来源，因此 confidence 恒为 1.0（confidenceBand=auto）。
    /// 取不到的属性一律不写进 Props（宁缺勿造假），可得性记录在
    /// <see cref="Civil3dSnapshot.AttributeProbe"/> 里，供 docs/04 可行性清单取证。
    /// </summary>
    internal sealed class CivilObjectRecord
    {
        public string Kind = "surface";
        public string KindDisplay = "曲面";
        public string Name;
        public string Handle;
        public string TypeName;

        public Dictionary<string, object> Props = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>原生对象最高可信度（方案 15.3：Civil 原生 = 1.0）。</summary>
        public double Confidence = 1.0D;

        public string Band = ConfidenceBand.Auto;

        public Basis Basis = new Basis("civil_native", "civil3d:surface");

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["kind"] = Kind;
            dict["kindDisplay"] = KindDisplay;
            dict["name"] = Redactor.Text(Name, 120);
            dict["handle"] = Handle;
            dict["typeName"] = TypeName;
            dict["props"] = Props;
            dict["confidence"] = Confidence;
            dict["confidenceBand"] = Band;
            dict["basis"] = Basis.ToDict();
            return dict;
        }
    }

    /// <summary>
    /// 一次读图的 Civil 3D 快照。detected=false 表示「本进程/本文档取不到 CivilDocument」，
    /// 此时调用方走 gaps[] + W1001 降级路径（其他机器未装 Civil 3D 的兼容分支）。
    /// </summary>
    internal sealed class Civil3dSnapshot
    {
        /// <summary>是否取到 CivilDocument（= Civil 3D 宿主 + 文档可用）。</summary>
        public bool Detected;

        /// <summary>宿主产品名：Civil3D / AutoCAD。</summary>
        public string Product = "AutoCAD";

        /// <summary>宿主发行版本：R24.3 / R23.0 / R24.1。</summary>
        public string Release = "unknown";

        /// <summary>取不到时的原因（给人看的一句话）。</summary>
        public string Note;

        /// <summary>对象条数是否被上限截断（counts 仍为真实总数）。</summary>
        public bool Truncated;

        /// <summary>单类对象的列表上限（超出只计数不列举）。</summary>
        public int ObjectCap = 200;

        public long ElapsedMs;

        public List<CivilObjectRecord> Surfaces = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> Alignments = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> Profiles = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> PipeNetworks = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> PointGroups = new List<CivilObjectRecord>();

        /// <summary>扩展 Civil 3D 原生对象（三维道路/装配/纵断面视图/采样线/场地/桥梁/图框组）。</summary>
        public List<CivilObjectRecord> Corridors = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> Assemblies = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> ProfileViews = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> SampleLineGroups = new List<CivilObjectRecord>();
        public List<CivilObjectRecord> Sites = new List<CivilObjectRecord>();

        /// <summary>真实总数（不受列表上限影响）。</summary>
        public int SurfaceTotal;
        public int AlignmentTotal;
        public int ProfileTotal;
        public int PipeNetworkTotal;
        public int PointGroupTotal;
        public int CorridorTotal;
        public int AssemblyTotal;
        public int ProfileViewTotal;
        public int SampleLineGroupTotal;
        public int SiteTotal;
        public int BridgeTotal;
        public int ViewFrameGroupTotal;
        public int SitelessFeatureLineTotal;

        /// <summary>属性可得性探针："surface.triangleCount" → true/false。</summary>
        public Dictionary<string, bool> AttributeProbe = new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>句柄 → 中文含义（回填到图元，使原生对象 confidence=1.0）。</summary>
        public Dictionary<string, string> HandleMeanings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int TotalObjects
        {
            get { return SurfaceTotal + AlignmentTotal + ProfileTotal + PipeNetworkTotal + PointGroupTotal; }
        }

        public int ExtendedTotalObjects
        {
            get { return CorridorTotal + AssemblyTotal + ProfileViewTotal + SampleLineGroupTotal + SiteTotal + BridgeTotal + ViewFrameGroupTotal + SitelessFeatureLineTotal; }
        }

        /// <summary>写进 read_overview.civil3d 的对象（不含句柄映射）。</summary>
        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["detected"] = Detected;
            dict["hostProduct"] = Product;
            dict["hostRelease"] = Release;

            Dictionary<string, object> counts = new Dictionary<string, object>(StringComparer.Ordinal);
            counts["surfaces"] = SurfaceTotal;
            counts["alignments"] = AlignmentTotal;
            counts["profiles"] = ProfileTotal;
            counts["pipeNetworks"] = PipeNetworkTotal;
            counts["pointGroups"] = PointGroupTotal;
            counts["total"] = TotalObjects;
            dict["counts"] = counts;

            Dictionary<string, object> extCounts = new Dictionary<string, object>(StringComparer.Ordinal);
            extCounts["corridors"] = CorridorTotal;
            extCounts["assemblies"] = AssemblyTotal;
            extCounts["profileViews"] = ProfileViewTotal;
            extCounts["sampleLineGroups"] = SampleLineGroupTotal;
            extCounts["sites"] = SiteTotal;
            extCounts["bridges"] = BridgeTotal;
            extCounts["viewFrameGroups"] = ViewFrameGroupTotal;
            extCounts["sitelessFeatureLines"] = SitelessFeatureLineTotal;
            extCounts["total"] = ExtendedTotalObjects;
            dict["extendedCounts"] = extCounts;

            dict["surfaces"] = ToList(Surfaces);
            dict["alignments"] = ToList(Alignments);
            dict["profiles"] = ToList(Profiles);
            dict["pipeNetworks"] = ToList(PipeNetworks);
            dict["pointGroups"] = ToList(PointGroups);
            dict["corridors"] = ToList(Corridors);
            dict["assemblies"] = ToList(Assemblies);
            dict["profileViews"] = ToList(ProfileViews);
            dict["sampleLineGroups"] = ToList(SampleLineGroups);
            dict["sites"] = ToList(Sites);
            dict["attributeProbe"] = ToBoolDict(AttributeProbe);
            dict["objectCap"] = ObjectCap;
            dict["truncated"] = Truncated;
            dict["elapsedMs"] = ElapsedMs;
            dict["note"] = Note;
            return dict;
        }

        private static List<object> ToList(List<CivilObjectRecord> records)
        {
            List<object> list = new List<object>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                list.Add(records[i].ToDict());
            }
            return list;
        }

        private static Dictionary<string, object> ToBoolDict(Dictionary<string, bool> source)
        {
            List<string> keys = new List<string>(source.Keys);
            keys.Sort(StringComparer.Ordinal);
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                dict[keys[i]] = source[keys[i]];
            }
            return dict;
        }
    }

    /// <summary>
    /// Civil 3D 只读读取器（第二轮新增）。
    ///
    /// 设计要点：
    /// 1) 全程反射 —— 2019（R23.0）与 2024（R24.3）对同一属性的暴露程度不同，
    ///    强类型调用会在某个版本上编译或加载失败；反射 + 逐项 try/catch 才能「探测而非假设」。
    /// 2) 只读 —— 只调用 Get* / 属性读取，绝不调用任何 Add/Remove/Modify；
    ///    写通道仍由 ToolRouter 统一拒绝（E2003 write_channel_disabled_m0）。
    /// 3) 属性取不到就记录 false（AttributeProbe），不编造默认值冒充真实值。
    /// 4) 单类最多列举 200 条（ObjectCap），总数仍如实统计。
    /// </summary>
    internal static class Civil3dReader
    {
        private const int ObjectCap = 200;

        /// <summary>读取当前文档的 Civil 3D 原生对象；任何异常都降级为 detected=false + Note。</summary>
        public static Civil3dSnapshot Read(Document doc)
        {
            Civil3dSnapshot snapshot = new Civil3dSnapshot();
            snapshot.ObjectCap = ObjectCap;
            snapshot.Product = CivilApiProbe.ProductName;
            snapshot.Release = CivilApiProbe.ReleaseTag;

            if (!snapshot.Product.Equals("Civil3D", StringComparison.Ordinal))
            {
                snapshot.Detected = false;
                snapshot.Note = "当前宿主不是 Civil 3D（或 AeccDbMgd 未加载），原生对象以代理对象出现";
                return snapshot;
            }

            object civilDocument = CivilApiProbe.GetCivilDocument(doc);
            if (civilDocument == null)
            {
                snapshot.Detected = false;
                snapshot.Note = "Civil 3D 程序集可用，但当前进程取不到 CivilDocument（可能无活动文档或初始化未完成）";
                return snapshot;
            }

            Database db;
            try
            {
                db = doc.Database;
            }
            catch (Exception ex)
            {
                HostLog.Exception("civil", "database_unavailable", ex);
                snapshot.Detected = false;
                snapshot.Note = "取不到数据库";
                return snapshot;
            }

            snapshot.Detected = true;
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    CollectSurfaces(snapshot, civilDocument, tr);
                    List<ObjectId> alignmentIds = CollectAlignments(snapshot, civilDocument, tr);
                    CollectProfiles(snapshot, civilDocument, alignmentIds, tr);
                    CollectPipeNetworks(snapshot, civilDocument, tr);
                    CollectPointGroups(snapshot, civilDocument, tr);
                    CollectExtendedObjects(snapshot, civilDocument, alignmentIds, tr);
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                // Civil 对象读取失败不阻断 AutoCAD 原生图元读取；如实记录
                HostLog.Exception("civil", "read_failed", ex);
                snapshot.Note = "Civil 3D 对象读取中断：" + ex.GetType().Name;
            }
            watch.Stop();
            snapshot.ElapsedMs = watch.ElapsedMilliseconds;

            HostLog.Info("civil", "read_done",
                "product", snapshot.Product,
                "release", snapshot.Release,
                "surfaces", snapshot.SurfaceTotal,
                "alignments", snapshot.AlignmentTotal,
                "profiles", snapshot.ProfileTotal,
                "networks", snapshot.PipeNetworkTotal,
                "pointGroups", snapshot.PointGroupTotal,
                "elapsedMs", snapshot.ElapsedMs);
            return snapshot;
        }

        // ---------------- 五类对象 ----------------

        private static void CollectSurfaces(Civil3dSnapshot snapshot, object civilDocument, Transaction tr)
        {
            List<ObjectId> ids = CollectIds(civilDocument, tr,
                new[] { "GetSurfaceIds", "SurfaceIds" },
                new[] { "Surfaces" });
            snapshot.SurfaceTotal = ids.Count;

            for (int i = 0; i < ids.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, ids[i]);
                if (obj == null)
                {
                    continue;
                }
                CivilObjectRecord record = NewRecord(snapshot, "surface", "曲面", "civil3d:surface", obj, ids[i]);

                object surfaceType = GetMember(obj, "SurfaceType");
                if (surfaceType != null)
                {
                    record.TypeName = Convert.ToString(surfaceType, CultureInfo.InvariantCulture);
                    Probe(snapshot, "surface.surfaceType", true);
                }
                else
                {
                    Probe(snapshot, "surface.surfaceType", false);
                }

                // GeneralProperties 先读：里面的 NumberOfPoints 是"能不能枚举三角形"的前置条件。
                object general = InvokeZero(obj, "GetGeneralProperties");
                int? pointCount = general == null
                    ? (int?)null
                    : CountOrNumber(general, new[] { "NumberOfPoints", "PointCount" });

                // 三角形计数：**空曲面绝不枚举**。
                //
                // 2026-11-24 13:26 事故：空 TIN 曲面（0 顶点）上 foreach TinSurfaceTriangleEnumerator
                // 触发原生 AV，进程直接 ExecutionEngineException（栈：
                // Civil3dReader.CountOf → TinSurfaceTriangleEnumerator.MoveNext）。
                // 这不是可 catch 的托管异常，唯一可靠的做法是**不进入枚举**。
                int? triangles = null;
                if (pointCount.HasValue && pointCount.Value == 0)
                {
                    triangles = 0;
                    Probe(snapshot, "surface.triangleCount", true);
                }
                else
                {
                    // 只用属性式计数（ICollection.Count / Count 属性），**不 foreach**：
                    // 原生集合的枚举器不保证在任意状态下安全，宁可标未知（null）也不赌。
                    triangles = CountOfPropertyOnly(obj, new[] { "Triangles", "SurfaceTriangles" });
                    Probe(snapshot, "surface.triangleCount", triangles.HasValue);
                }
                if (triangles.HasValue)
                {
                    record.Props["triangleCount"] = triangles.Value;
                }

                // 断裂线数量：只用属性式计数（原生集合不枚举，理由同三角形）。
                // 有了这个数，write_surface_breakline 的效果才能被"数字"验证，而不是只看调用是否成功。
                // 成员名以基座实现为准：它在 SurfaceCommands.AddSurfaceBreaklineAsync 里用的是
                // tinSurface.BreaklinesDefinition.AddStandardBreaklines(...)。
                // 2026-11-24 我先按 "Breaklines"/"BreaklineCount" 猜，结果永远取不到值（属性表里没有 breaklineCount）。
                object breaklineDef = GetMember(obj, "BreaklinesDefinition");
                int? breaklines = breaklineDef == null
                    ? (int?)null
                    : CountOrNumber(breaklineDef, new[] { "Count", "NumberOfBreaklines" });
                if (breaklines.HasValue)
                {
                    record.Props["breaklineCount"] = breaklines.Value;
                    Probe(snapshot, "surface.breaklineCount", true);
                }
                else
                {
                    Probe(snapshot, "surface.breaklineCount", false);
                }
                if (general != null)
                {
                    int? points = pointCount;
                    if (points.HasValue)
                    {
                        record.Props["pointCount"] = points.Value;
                        Probe(snapshot, "surface.pointCount", true);
                    }
                    else
                    {
                        Probe(snapshot, "surface.pointCount", false);
                    }

                    object minElevation = GetMember(general, "MinimumElevation");
                    object maxElevation = GetMember(general, "MaximumElevation");
                    bool hasElevation = minElevation != null && maxElevation != null;
                    if (hasElevation)
                    {
                        record.Props["minElevation"] = ToDouble(minElevation);
                        record.Props["maxElevation"] = ToDouble(maxElevation);
                    }
                    Probe(snapshot, "surface.elevationRange", hasElevation);
                }
                else
                {
                    Probe(snapshot, "surface.pointCount", false);
                    Probe(snapshot, "surface.elevationRange", false);
                }

                // 体积曲面（TinVolumeSurface / GridVolumeSurface）挖填净方量
                object volProps = InvokeZero(obj, "GetVolumeProperties");
                if (volProps != null)
                {
                    object cutV = GetMember(volProps, "UnadjustedCutVolume");
                    object fillV = GetMember(volProps, "UnadjustedFillVolume");
                    object netV = GetMember(volProps, "UnadjustedNetVolume");
                    if (cutV != null) record.Props["cutVolume"] = ToDouble(cutV);
                    if (fillV != null) record.Props["fillVolume"] = ToDouble(fillV);
                    if (netV != null) record.Props["netVolume"] = ToDouble(netV);
                    Probe(snapshot, "surface.volumeProperties", cutV != null || fillV != null);
                }

                snapshot.Surfaces.Add(record);
                RegisterHandle(snapshot, record);
            }
            if (ids.Count > ObjectCap)
            {
                snapshot.Truncated = true;
            }
        }

        private static List<ObjectId> CollectAlignments(Civil3dSnapshot snapshot, object civilDocument, Transaction tr)
        {
            List<ObjectId> ids = CollectIds(civilDocument, tr,
                new[] { "GetAlignmentIds", "AlignmentIds" },
                new[] { "Alignments" });
            snapshot.AlignmentTotal = ids.Count;

            for (int i = 0; i < ids.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, ids[i]);
                if (obj == null)
                {
                    continue;
                }
                CivilObjectRecord record = NewRecord(snapshot, "alignment", "路线", "civil3d:alignment", obj, ids[i]);

                object start = GetMember(obj, "StartingStation");
                object end = GetMember(obj, "EndingStation");
                bool hasStation = start != null && end != null;
                if (hasStation)
                {
                    record.Props["startStation"] = ToDouble(start);
                    record.Props["endStation"] = ToDouble(end);
                }
                Probe(snapshot, "alignment.stationRange", hasStation);

                object length = GetMember(obj, "Length");
                if (length != null)
                {
                    record.Props["length"] = ToDouble(length);
                }
                Probe(snapshot, "alignment.length", length != null);

                int? profCount = CountOrMemberCount(obj, new[] { "GetProfileIds" });
                if (profCount.HasValue)
                {
                    record.Props["profileCount"] = profCount.Value;
                }
                int? pvCount = CountOrMemberCount(obj, new[] { "GetProfileViewIds" });
                if (pvCount.HasValue)
                {
                    record.Props["profileViewCount"] = pvCount.Value;
                }

                // 设计速度/起点类型等工程属性：2019 与 2024 暴露不同，能取则取
                object designSpeed = InvokeZero(obj, "GetDesignSpeeds");
                if (designSpeed != null)
                {
                    record.Props["designSpeeds"] = Redactor.Text(Convert.ToString(designSpeed, CultureInfo.InvariantCulture), 120);
                }

                snapshot.Alignments.Add(record);
                RegisterHandle(snapshot, record);
            }
            if (ids.Count > ObjectCap)
            {
                snapshot.Truncated = true;
            }
            return ids;
        }

        private static void CollectProfiles(Civil3dSnapshot snapshot, object civilDocument, List<ObjectId> alignmentIds, Transaction tr)
        {
            // Civil 3D 原生架构中纵断面挂在每条 Alignment.GetProfileIds() 下，CivilDocument 本身无 GetProfileIds()。
            List<ObjectId> ids = new List<ObjectId>();
            HashSet<long> seenHandles = new HashSet<long>();

            // 1) 主路径：遍历每条路线收集全部纵断面（地面线 EG + 设计拉坡线 FG）
            if (alignmentIds != null)
            {
                for (int a = 0; a < alignmentIds.Count; a++)
                {
                    DBObject alignObj = OpenForRead(tr, alignmentIds[a]);
                    if (alignObj == null)
                    {
                        continue;
                    }
                    List<ObjectId> subIds = CollectIds(alignObj, tr,
                        new[] { "GetProfileIds", "ProfileIds" },
                        new[] { "Profiles" });
                    for (int j = 0; j < subIds.Count; j++)
                    {
                        long hv = subIds[j].Handle.Value;
                        if (seenHandles.Add(hv))
                        {
                            ids.Add(subIds[j]);
                        }
                    }
                }
            }

            // 2) 兜底路径：兼容旧版/自定义封装在 CivilDocument 上的集合
            List<ObjectId> docIds = CollectIds(civilDocument, tr,
                new[] { "GetProfileIds", "ProfileIds" },
                new[] { "Profiles" });
            for (int j = 0; j < docIds.Count; j++)
            {
                long hv = docIds[j].Handle.Value;
                if (seenHandles.Add(hv))
                {
                    ids.Add(docIds[j]);
                }
            }

            snapshot.ProfileTotal = ids.Count;

            for (int i = 0; i < ids.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, ids[i]);
                if (obj == null)
                {
                    continue;
                }
                CivilObjectRecord record = NewRecord(snapshot, "profile", "纵断面", "civil3d:profile", obj, ids[i]);

                object start = GetMember(obj, "StartingStation");
                object end = GetMember(obj, "EndingStation");
                bool hasStation = start != null && end != null;
                if (hasStation)
                {
                    record.Props["startStation"] = ToDouble(start);
                    record.Props["endStation"] = ToDouble(end);
                }
                Probe(snapshot, "profile.stationRange", hasStation);

                object ptype = GetMember(obj, "ProfileType");
                if (ptype != null)
                {
                    record.Props["profileType"] = Convert.ToString(ptype, CultureInfo.InvariantCulture);
                }
                object minEl = GetMember(obj, "ElevationMin");
                object maxEl = GetMember(obj, "ElevationMax");
                if (minEl != null && maxEl != null)
                {
                    record.Props["minElevation"] = ToDouble(minEl);
                    record.Props["maxElevation"] = ToDouble(maxEl);
                }
                int? pviCount = CountOfPropertyOnly(obj, new[] { "PVIs" });
                if (pviCount.HasValue)
                {
                    record.Props["pviCount"] = pviCount.Value;
                }
                int? entCount = CountOfPropertyOnly(obj, new[] { "Entities" });
                if (entCount.HasValue)
                {
                    record.Props["entityCount"] = entCount.Value;
                }

                // 所属路线：不同版本暴露方式不同（AlignmentId / Alignment）
                string alignmentName = ResolveAlignmentName(obj, tr);
                if (alignmentName != null)
                {
                    record.Props["alignment"] = Redactor.Text(alignmentName, 120);
                }
                Probe(snapshot, "profile.alignmentName", alignmentName != null);

                snapshot.Profiles.Add(record);
                RegisterHandle(snapshot, record);
            }
            if (ids.Count > ObjectCap)
            {
                snapshot.Truncated = true;
            }
        }

        private static void CollectPipeNetworks(Civil3dSnapshot snapshot, object civilDocument, Transaction tr)
        {
            List<ObjectId> ids = CollectIds(civilDocument, tr,
                new[] { "GetPipeNetworkIds", "PipeNetworkIds" },
                new[] { "PipeNetworks" });
            snapshot.PipeNetworkTotal = ids.Count;

            for (int i = 0; i < ids.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, ids[i]);
                if (obj == null)
                {
                    continue;
                }
                CivilObjectRecord record = NewRecord(snapshot, "pipeNetwork", "管网", "civil3d:pipeNetwork", obj, ids[i]);

                int? pipes = CountOrMemberCount(obj, new[] { "Pipes", "GetPipeIds", "PipeIds" });
                if (pipes.HasValue)
                {
                    record.Props["pipeCount"] = pipes.Value;
                }
                Probe(snapshot, "pipeNetwork.pipeCount", pipes.HasValue);

                int? structures = CountOrMemberCount(obj, new[] { "Structures", "GetStructureIds", "StructureIds" });
                if (structures.HasValue)
                {
                    record.Props["structureCount"] = structures.Value;
                }
                Probe(snapshot, "pipeNetwork.structureCount", structures.HasValue);

                // 压力管网（PressurePipes）与重力管网在 2019/2024 都走同一接口族，取不到则不打标
                snapshot.PipeNetworks.Add(record);
                RegisterHandle(snapshot, record);
            }
            if (ids.Count > ObjectCap)
            {
                snapshot.Truncated = true;
            }
        }

        private static void CollectPointGroups(Civil3dSnapshot snapshot, object civilDocument, Transaction tr)
        {
            List<ObjectId> ids = CollectIds(civilDocument, tr,
                new[] { "GetPointGroupIds", "PointGroupIds" },
                new[] { "PointGroups" });
            snapshot.PointGroupTotal = ids.Count;

            for (int i = 0; i < ids.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, ids[i]);
                if (obj == null)
                {
                    continue;
                }
                CivilObjectRecord record = NewRecord(snapshot, "pointGroup", "点编组", "civil3d:pointGroup", obj, ids[i]);

                int? points = CountOrMemberCount(obj, new[] { "Points", "GetPointNumbers", "PointNumbers" });
                if (points.HasValue)
                {
                    record.Props["pointCount"] = points.Value;
                }
                Probe(snapshot, "pointGroup.pointCount", points.HasValue);

                snapshot.PointGroups.Add(record);
                RegisterHandle(snapshot, record);
            }
            if (ids.Count > ObjectCap)
            {
                snapshot.Truncated = true;
            }
        }

        private static void CollectExtendedObjects(Civil3dSnapshot snapshot, object civilDocument, List<ObjectId> alignmentIds, Transaction tr)
        {
            // 1) 三维道路走廊（Corridors）
            List<ObjectId> corIds = CollectIds(civilDocument, tr,
                new[] { "GetCorridorIds" },
                new[] { "CorridorCollection" });
            snapshot.CorridorTotal = corIds.Count;
            for (int i = 0; i < corIds.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, corIds[i]);
                if (obj == null) continue;
                CivilObjectRecord record = NewRecord(snapshot, "corridor", "三维道路", "civil3d:corridor", obj, corIds[i]);
                int? baselines = CountOfPropertyOnly(obj, new[] { "Baselines" });
                if (baselines.HasValue) record.Props["baselineCount"] = baselines.Value;
                int? corSurfs = CountOfPropertyOnly(obj, new[] { "CorridorSurfaces" });
                if (corSurfs.HasValue) record.Props["corridorSurfaceCount"] = corSurfs.Value;
                snapshot.Corridors.Add(record);
                RegisterHandle(snapshot, record);
            }

            // 2) 横断面装配（Assemblies）
            List<ObjectId> asmIds = CollectIds(civilDocument, tr,
                new[] { "GetAssemblyIds" },
                new[] { "AssemblyCollection" });
            snapshot.AssemblyTotal = asmIds.Count;
            for (int i = 0; i < asmIds.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, asmIds[i]);
                if (obj == null) continue;
                CivilObjectRecord record = NewRecord(snapshot, "assembly", "装配", "civil3d:assembly", obj, asmIds[i]);
                int? groups = CountOfPropertyOnly(obj, new[] { "Groups" });
                if (groups.HasValue) record.Props["groupCount"] = groups.Value;
                snapshot.Assemblies.Add(record);
                RegisterHandle(snapshot, record);
            }

            // 3) 纵断面视图（ProfileViews）与采样线编组（SampleLineGroups），挂在每条 Alignment 下
            List<ObjectId> pvIds = new List<ObjectId>();
            List<ObjectId> slgIds = new List<ObjectId>();
            if (alignmentIds != null)
            {
                for (int a = 0; a < alignmentIds.Count; a++)
                {
                    DBObject alignObj = OpenForRead(tr, alignmentIds[a]);
                    if (alignObj == null) continue;
                    pvIds.AddRange(CollectIds(alignObj, tr, new[] { "GetProfileViewIds" }, new string[0]));
                    slgIds.AddRange(CollectIds(alignObj, tr, new[] { "GetSampleLineGroupIds" }, new string[0]));
                }
            }
            snapshot.ProfileViewTotal = pvIds.Count;
            for (int i = 0; i < pvIds.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, pvIds[i]);
                if (obj == null) continue;
                CivilObjectRecord record = NewRecord(snapshot, "profileView", "纵断面图", "civil3d:profileView", obj, pvIds[i]);
                snapshot.ProfileViews.Add(record);
                RegisterHandle(snapshot, record);
            }

            snapshot.SampleLineGroupTotal = slgIds.Count;
            for (int i = 0; i < slgIds.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, slgIds[i]);
                if (obj == null) continue;
                CivilObjectRecord record = NewRecord(snapshot, "sampleLineGroup", "采样线编组", "civil3d:sampleLineGroup", obj, slgIds[i]);
                snapshot.SampleLineGroups.Add(record);
                RegisterHandle(snapshot, record);
            }

            // 4) 场地（Sites）、桥梁（Bridges）、图框组（ViewFrameGroups）、无场地要素线（SitelessFeatureLines）
            List<ObjectId> siteIds = CollectIds(civilDocument, tr, new[] { "GetSiteIds" }, new string[0]);
            snapshot.SiteTotal = siteIds.Count;
            for (int i = 0; i < siteIds.Count && i < ObjectCap; i++)
            {
                DBObject obj = OpenForRead(tr, siteIds[i]);
                if (obj == null) continue;
                CivilObjectRecord record = NewRecord(snapshot, "site", "场地", "civil3d:site", obj, siteIds[i]);
                int? flCount = CountOrMemberCount(obj, new[] { "GetFeatureLineIds" });
                if (flCount.HasValue) record.Props["featureLineCount"] = flCount.Value;
                int? parcelCount = CountOrMemberCount(obj, new[] { "GetParcelIds" });
                if (parcelCount.HasValue) record.Props["parcelCount"] = parcelCount.Value;
                snapshot.Sites.Add(record);
                RegisterHandle(snapshot, record);
            }

            snapshot.BridgeTotal = CollectIds(civilDocument, tr, new[] { "GetBridgeIds" }, new string[0]).Count;
            snapshot.ViewFrameGroupTotal = CollectIds(civilDocument, tr, new[] { "GetViewFrameGroupIds" }, new string[0]).Count;
            snapshot.SitelessFeatureLineTotal = CollectIds(civilDocument, tr, new[] { "GetSitelessFeatureLineIds" }, new string[0]).Count;
        }

        // ---------------- 通用反射工具 ----------------

        private static CivilObjectRecord NewRecord(Civil3dSnapshot snapshot, string kind, string display,
            string basisRef, DBObject obj, ObjectId id)
        {
            CivilObjectRecord record = new CivilObjectRecord();
            record.Kind = kind;
            record.KindDisplay = display;
            record.Basis = new Basis("civil_native", basisRef);
            record.TypeName = obj.GetType().Name;
            record.Handle = HandleOf(id);

            object name = GetMember(obj, "Name");
            if (name != null)
            {
                record.Name = Convert.ToString(name, CultureInfo.InvariantCulture);
            }
            if (string.IsNullOrEmpty(record.Name))
            {
                record.Name = "(未命名" + display + ")";
            }
            Probe(snapshot, kind + ".name", name != null);
            return record;
        }

        private static void RegisterHandle(Civil3dSnapshot snapshot, CivilObjectRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Handle) || record.Handle == "0")
            {
                return;
            }
            snapshot.HandleMeanings[record.Handle] = record.KindDisplay;
        }

        /// <summary>
        /// 收集 ObjectId 列表：先按「无参方法」（GetXxxIds），再按「属性集合」（Xxx）。
        /// 集合元素可能是 ObjectId，也可能是已实例化的 DBObject（取其 ObjectId）。
        /// </summary>
        private static List<ObjectId> CollectIds(object civilDocument, Transaction tr, string[] methodNames, string[] propertyNames)
        {
            List<ObjectId> ids = new List<ObjectId>();
            object collection = null;

            for (int i = 0; i < methodNames.Length && collection == null; i++)
            {
                collection = InvokeZero(civilDocument, methodNames[i]);
            }
            for (int i = 0; i < propertyNames.Length && collection == null; i++)
            {
                collection = GetMember(civilDocument, propertyNames[i]);
            }
            if (collection == null)
            {
                return ids;
            }

            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable == null)
            {
                return ids;
            }

            try
            {
                foreach (object item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }
                    ObjectId id = ObjectId.Null;
                    if (item is ObjectId)
                    {
                        id = (ObjectId)item;
                    }
                    else
                    {
                        DBObject dbObject = item as DBObject;
                        if (dbObject != null)
                        {
                            try
                            {
                                id = dbObject.ObjectId;
                            }
                            catch (Exception)
                            {
                                id = ObjectId.Null;
                            }
                        }
                    }
                    if (!id.IsNull && !id.IsErased)
                    {
                        ids.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("civil", "collect_ids_failed", ex);
            }
            return ids;
        }

        private static DBObject OpenForRead(Transaction tr, ObjectId id)
        {
            try
            {
                return tr.GetObject(id, OpenMode.ForRead, false);
            }
            catch (Exception ex)
            {
                // 个别对象（被删除 / 代理）取不到就跳过，绝不让整次读取失败
                HostLog.Exception("civil", "open_object_failed", ex);
                return null;
            }
        }

        private static string HandleOf(ObjectId id)
        {
            try
            {
                return id.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "0";
            }
        }

        /// <summary>读属性或字段（不调用方法，避免副作用）。</summary>
        private static object GetMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    return field.GetValue(target);
                }
            }
            catch (Exception)
            {
                // 属性 getter 抛异常（对象已删除、版本不支持）视为取不到
            }
            return null;
        }

        /// <summary>调用无参方法（只用于 Get* 命名的方法，只读语义）。</summary>
        private static object InvokeZero(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            try
            {
                MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method == null || method.ReturnType == typeof(void))
                {
                    return null;
                }
                return method.Invoke(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>成员的集合元素数（属性集合或 Get*Ids 方法返回值）。</summary>
        private static int? CountOfMember(object target, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                object value = GetMember(target, names[i]);
                int? count = CountOf(value);
                if (count.HasValue)
                {
                    return count;
                }
            }
            return null;
        }

        /// <summary>先按属性集合取数，再按 Get*() 方法取数。</summary>
        private static int? CountOrMemberCount(object target, string[] names)
        {
            int? count = CountOfMember(target, names);
            if (count.HasValue)
            {
                return count;
            }
            for (int i = 0; i < names.Length; i++)
            {
                object value = InvokeZero(target, names[i]);
                int? invoked = CountOf(value);
                if (invoked.HasValue)
                {
                    return invoked;
                }
            }
            return null;
        }

        /// <summary>
        /// **只读属性式**计数：ICollection.Count 或 Count 属性；绝不 foreach。
        /// 用于 Civil 3D 原生集合（如曲面三角形）——它们的枚举器在某些状态下会 AV（见 CollectSurfaces 注释）。
        /// </summary>
        private static int? CountOfPropertyOnly(object target, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                object value = GetMember(target, names[i]);
                if (value == null)
                {
                    continue;
                }
                ICollection collection = value as ICollection;
                if (collection != null)
                {
                    return collection.Count;
                }
                int? fromProperty = CountOrNumber(value, new[] { "Count" });
                if (fromProperty.HasValue)
                {
                    return fromProperty;
                }
            }
            return null;
        }

        /// <summary>集合计数：ICollection 直接取 Count，IEnumerable 逐个计数（上限 10000 防爆）。</summary>
        private static int? CountOf(object value)
        {
            if (value == null)
            {
                return null;
            }
            ICollection collection = value as ICollection;
            if (collection != null)
            {
                return collection.Count;
            }
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string)
            {
                return null;
            }
            int count = 0;
            try
            {
                foreach (object unused in enumerable)
                {
                    count++;
                    if (count > 10000)
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
            return count;
        }

        /// <summary>数值属性（也接受本身是整数/浮点的成员）。</summary>
        private static int? CountOrNumber(object target, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                object value = GetMember(target, names[i]);
                if (value == null)
                {
                    continue;
                }
                if (value is int || value is long || value is short || value is double || value is float)
                {
                    return (int)Math.Round(ToDouble(value));
                }
                int? count = CountOf(value);
                if (count.HasValue)
                {
                    return count;
                }
            }
            return null;
        }

        private static double ToDouble(object value)
        {
            try
            {
                if (value == null)
                {
                    return 0D;
                }
                if (value is double)
                {
                    return Math.Round((double)value, 3);
                }
                return Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), 3);
            }
            catch (Exception)
            {
                return 0D;
            }
        }

        /// <summary>纵断面所属路线名：AlignmentId（ObjectId）或 Alignment（对象引用）两种暴露方式都试。</summary>
        private static string ResolveAlignmentName(object profile, Transaction tr)
        {
            object alignmentId = GetMember(profile, "AlignmentId");
            if (alignmentId is ObjectId)
            {
                ObjectId id = (ObjectId)alignmentId;
                if (!id.IsNull && !id.IsErased)
                {
                    DBObject alignment = OpenForRead(tr, id);
                    if (alignment != null)
                    {
                        object name = GetMember(alignment, "Name");
                        if (name != null)
                        {
                            return Convert.ToString(name, CultureInfo.InvariantCulture);
                        }
                    }
                }
            }

            object alignmentRef = GetMember(profile, "Alignment");
            if (alignmentRef != null && !(alignmentRef is ObjectId))
            {
                object name = GetMember(alignmentRef, "Name");
                if (name != null)
                {
                    return Convert.ToString(name, CultureInfo.InvariantCulture);
                }
            }
            return null;
        }

        /// <summary>记录属性可得性：任一对象成功即为 true（OR 语义）。</summary>
        private static void Probe(Civil3dSnapshot snapshot, string key, bool ok)
        {
            bool existing;
            if (snapshot.AttributeProbe.TryGetValue(key, out existing) && existing)
            {
                return;
            }
            snapshot.AttributeProbe[key] = ok;
        }
    }
}
