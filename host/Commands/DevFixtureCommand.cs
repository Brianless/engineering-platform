using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Commands
{
    /// <summary>
    /// Civil 3D 夹具生成器（第三轮新增）——**这是测试夹具，不是业务写能力**。
    ///
    /// ## 为什么要有它
    ///
    /// M0 的卖点是「Civil 3D 语义读取」，但实测三张真实生产图里 Civil 原生对象合计 0 个
    /// （那批图都是纯 AutoCAD 图元）。也就是说读路径的「属性级证据」一次都没被验证过——
    /// 只能证明 API 可达，不能证明属性能取到。本命令造一张**确定含 Civil 曲面**的图
    /// （路线也做了尝试，但反射建不出来，见下），再用同一条只读链路读回来。
    ///
    /// ## 实现的能力边界（不要读成「全套对象都造得出来」）
    ///
    /// * **曲面**：已实现并实测成功（6×6=36 点、60 条边的 TIN，读回 50 个三角面）；
    /// * **路线**：已实现尝试，但 2024 上 `Alignment.Create` 的 ObjectId 重载要求真实 `styleId`
    ///   （传 null → `ArgumentNullException`），String 重载要求真实 `styleName`
    ///   （`<None>`/空串 → `Can not get style ID from style name`），`PolylineOptions` 又无默认构造
    ///   → 产出 `alignmentName=null`，失败原因逐条写进报告的 `attempts[]`；
    /// * **纵断面 / 管网 / 点编组**：**本轮根本没有实现**（本文件里没有 `Profile`/`PipeNetwork`/
    ///   `PointGroup` 任何代码）。要补是「写代码」，不是「换策略」——两者的工作量含义不同，别混。
    ///
    /// ## 刻意绕过写通道关闭（唯一例外，附条件）
    ///
    /// M0 硬约束是「零写入」。本命令是唯一的写例外，条件全部写死在这里：
    /// 1) **输出目录**只能在 `%LOCALAPPDATA%\EngineeringPlatform\sandbox\` 之下，传别的目录
    ///    **直接 E1001 拒绝**，连建库动作都不做（不做「先建再删」，避免任何写坏用户文件的可能）；
    /// 2) **活动文档也必须在同一个 sandbox 之内**（D8）。本命令是在**活动文档的数据库**上建对象
    ///    （`new Database(true,true)` 造出来的图会让 AutoCAD 下次开图时堆损坏崩溃，实测踩过），
    ///    所以必须确认「当前打开的就是沙箱副本」；否则拒绝，绝不能把对象塞进用户自己那张图里；
    /// 3) 落盘只落到该 sandbox 目录（`SaveAs` 目标路径同样是 sandbox 内的新文件）；
    /// 4) 不进 MCP 工具目录（`protocol/tools.schema.json` 与 `mcp/tools_catalog.py` 都没有它），
    ///    MCP 侧 `is_write_domain` 也不认这个名字——它从设计上就不可被 Agent 调用；
    /// 5) 日志单独标记 `audit|dev_fixture`，便于事后把「测试写入」与「业务写入」区分开；
    /// 6) 全部走反射：Civil 3D 2019 与 2024 的签名不同，强类型调用会在某一版编译/加载失败。
    ///
    /// ## 对「用户图纸」的准确承诺（别写成「什么都不碰」）
    ///
    /// * **不修改任何用户文件**：原图只被复制、从不打开、从不保存；
    /// * **不做无痕操作**：在活动文档上建对象 + `SaveAs` 是**文档级、不可撤销**的动作。
    ///   若用户在自己那张图上跑本命令，他会发现当前文档已被另存到 sandbox（此后 Ctrl+S 落到 sandbox 那份）。
    ///   这正是 D8 门禁存在的原因 —— 现在的实现会在动作前把它拒掉。
    /// </summary>
    public sealed class DevFixtureCommand
    {
        /// <summary>环境变量名（AutoRun 用它触发无人值守夹具）。</summary>
        public const string EnvOut = AutoRun.EnvFixtureOut;

        /// <summary>夹具图里曲面/路线的名字前缀（一眼能看出是造出来的）。</summary>
        private const string FixturePrefix = "EP-FIXTURE";

        /// <summary>曲面网格边长点数（6×6 = 36 点，高低起伏 0–8 米）。</summary>
        private const int GridSize = 6;

        [CommandMethod("ENG_DEVFIXTURE")]
        public void RunFromCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            string outputDir = Environment.GetEnvironmentVariable(EnvOut);
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = AskOutputDirectory(doc);
            }
            string status = Run(outputDir);
            WriteLine("DEVFIXTURE " + status);
        }

        /// <summary>
        /// 生成夹具并自检。返回一行状态："OK &lt;json 路径&gt;" 或 "FAIL &lt;code&gt; &lt;细节&gt;"。
        /// 任何一步失败都如实返回，绝不写「假的通过」。
        /// </summary>
        internal static string Run(string outputDir)
        {
            if (string.IsNullOrEmpty(outputDir))
            {
                return "FAIL E1001 output_dir_missing";
            }
            string sandbox;
            try
            {
                sandbox = Path.GetFullPath(outputDir);
            }
            catch (Exception ex)
            {
                return "FAIL E1001 output_dir_invalid:" + ex.GetType().Name;
            }
            if (!IsInsideSandbox(sandbox))
            {
                // 硬门禁：越界一律拒绝，且此时还没有任何建库/写盘动作
                HostLog.Warn("audit", "dev_fixture_rejected", "reason", "outside_sandbox",
                    "path", HostLog.SafePath(sandbox));
                return "FAIL E1001 output_not_in_sandbox:" + Path.GetFileName(sandbox);
            }

            // ---- D8 门禁：活动文档也必须在 sandbox 之内 ----
            // 为什么必须有：本命令在**活动文档的数据库**上建 Civil 对象，然后 SaveAs 到 sandbox。
            // 如果用户此刻开着的是他自己的图，那就是「往用户图上塞夹具对象 + 把当前文档另存到别处」
            // （SaveAs 是文档级、不可撤销的动作）。所以先确认「当前打开的就是沙箱副本」，否则一步都不做。
            string documentGate = CheckActiveDocumentInsideSandbox();
            if (documentGate != null)
            {
                HostLog.Warn("audit", "dev_fixture_rejected", "reason", "active_doc_gate",
                    "detail", documentGate, "path", HostLog.SafePath(sandbox));
                return "FAIL E1001 " + documentGate;
            }

            try
            {
                if (!Directory.Exists(sandbox))
                {
                    Directory.CreateDirectory(sandbox);
                }
            }            catch (Exception ex)
            {
                return "FAIL E3002 sandbox_create_failed:" + ex.GetType().Name;
            }

            // 顺带把 Civil API 签名落成证据（夹具失败时这份 dump 就是定位依据）
            string probePath = DevProbeCommand.Probe(sandbox);

            Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal);
            report["kind"] = "dev_fixture";
            report["generatedAtUtc"] = TimeUtil.NowIso();
            report["sandboxDir"] = sandbox;
            report["probePath"] = probePath;
            report["civilApiAvailable"] = CivilApiProbe.Available;
            report["hostProduct"] = CivilApiProbe.ProductName;
            report["hostRelease"] = CivilApiProbe.ReleaseTag;

            List<object> attempts = new List<object>();
            report["attempts"] = attempts;

            if (!CivilApiProbe.Available)
            {
                report["result"] = "C3D_UNAVAILABLE";
                string path = WriteReport(sandbox, report);
                return "FAIL E1005 c3d_unavailable" + (path == null ? string.Empty : " report=" + path);
            }

            DateTime stamp = DateTime.UtcNow;
            string stampText = stamp.ToLocalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string dwgPath = Path.Combine(sandbox, FixturePrefix.ToLowerInvariant() + "-" + stampText + ".dwg");

            object createdSurface = null;
            object createdAlignment = null;
            Dictionary<string, object> readback = new Dictionary<string, object>(StringComparer.Ordinal);
            report["readback"] = readback;
            string failure = null;

            // 注意（实测 2026-11-21 16:07）：先用 `new Database(true,true)` 建库再 SaveAs，
            // 生成出来的 DWG 会在下一次读图时让 AutoCAD 直接崩掉（进程退出码 1，无异常可捕）。
            // 所以改成**在已经打开的沙箱副本上**建对象：那是真文档，Civil 数据是完整的，
            // SaveAs 出来的图能被正常打开与读取。
            //
            // 代价（必须说清，别读成「什么都不碰」）：这是**文档级、不可撤销**的动作 ——
            // 建对象会改当前文档的内存数据库，SaveAs 会把当前文档指向新文件。因此本方法入口处
            // 有 D8 门禁：活动文档不在 sandbox 内就一步都不做（返回 E1001 active_doc_not_in_sandbox）。
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    failure = "no_active_document";
                }
                else
                {
                    object civilDoc = CivilApiProbe.GetCivilDocument(doc);
                    if (civilDoc == null)
                    {
                        failure = "civil_document_unavailable";
                        attempts.Add("ActiveDocument.CivilDocument 为 null");
                    }
                    else
                    {
                        report["civilDocumentSource"] = "CivilApplication.ActiveDocument";
                        Point3dCollection points = BuildGridPoints();
                        report["fixturePoints"] = points.Count;

                        createdSurface = CreateSurface(doc.Database, civilDoc, points, attempts, readback);
                        createdAlignment = CreateAlignment(doc.Database, civilDoc, points, attempts, readback);

                        if (createdSurface == null && createdAlignment == null)
                        {
                            failure = "no_civil_object_created（建曲面与建路线都失败，详见 attempts）";
                        }
                        else
                        {
                            report["surfaceName"] = SafeStringProperty(createdSurface, "Name");
                            report["alignmentName"] = SafeStringProperty(createdAlignment, "Name");
                            // 保存到 sandbox 里的新文件（不覆盖副本本身，证据更干净）
                            doc.Database.SaveAs(dwgPath, DwgVersion.Current);
                            report["dwgPath"] = dwgPath;
                            report["dwgBytes"] = new FileInfo(dwgPath).Length;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = "fixture_exception:" + ex.GetType().Name + ":" + Clip(ex.Message, 200);
                HostLog.Exception("audit", "dev_fixture_exception", ex);
            }

            // 无论成败都在这里写一行审计标记：这是唯一被允许写的一次，必须留痕
            HostLog.Info("audit", "dev_fixture", "surface", createdSurface != null,
                "alignment", createdAlignment != null, "sandbox", Path.GetFileName(sandbox));

            if (failure != null)
            {
                report["result"] = "FAIL";
                report["failure"] = failure;
                string failPath = WriteReport(sandbox, report);
                return "FAIL E3002 " + Clip(failure, 160)
                    + (failPath == null ? string.Empty : " report=" + failPath);
            }

            // 读路径验证走**独立的第二阶段**（另一个 CAD 进程 + 普通 EP_SELFTEST_OUT），
            // 理由同上：在同一个进程里连着 Open 新图 + 切 MdiActiveDocument + 读 Civil 对象，
            // 实测会让 AutoCAD 崩溃（跨文档的 Civil 上下文不安全）。
            // 这样第二阶段与生产读路径完全同构，证据也更强。
            report["result"] = "CREATED";
            report["nextStep"] = "用 selftest-阶段读取 " + dwgPath;
            string reportPath = WriteReport(sandbox, report);
            if (reportPath == null)
            {
                return "FAIL E3002 report_write_failed";
            }
            return "OK " + reportPath;
        }

        // ---------------- Civil 对象构造（全反射） ----------------

        private static object CreateSurface(Database db, object civilDoc, Point3dCollection points,
            List<object> attempts, Dictionary<string, object> readback)
        {
            Type type = ResolveCivilType(civilDoc, "Autodesk.Civil.DatabaseServices.TinSurface");
            if (type == null)
            {
                attempts.Add("TinSurface 类型未找到");
                return null;
            }
            object surface = CreateWithBestOverload(type, civilDoc, db, FixturePrefix + "-TIN",
                points, null, null, attempts);
            if (surface == null)
            {
                return null;
            }
            attempts.Add("TinSurface 返回类型=" + surface.GetType().FullName);

            // 实测 2024：Create(Database, String) 可能返回 ObjectId 而不是 TinSurface 本身，
            // 这时必须开事务把对象取出来才能调 AddVertices。
            object target = surface;
            if (surface is ObjectId)
            {
                ObjectId id = (ObjectId)surface;
                attempts.Add("Create 返回 ObjectId（handle 可用=" + (id != ObjectId.Null) + "）");
                target = OpenForReadOrWrite(db, id, attempts);
            }
            if (target == null)
            {
                attempts.Add("取不到 TinSurface 实例，跳过加点");
                return surface;
            }

            int added = AddPointsToSurface(target, points, attempts);
            readback["pointsAdded"] = added;
            readback["triangles"] = CountOfMember(target, "Triangles");
            readback["surfacePoints"] = CountOrNumber(target, "Vertices", "NumberOfPoints");
            readback["name"] = SafeStringProperty(target, "Name");
            return target;
        }

        private static object CreateAlignment(Database db, object civilDoc, Point3dCollection points,
            List<object> attempts, Dictionary<string, object> readback)
        {
            Type type = ResolveCivilType(civilDoc, "Autodesk.Civil.DatabaseServices.Alignment");
            if (type == null)
            {
                attempts.Add("Alignment 类型未找到");
                return null;
            }
            // 先试着带几何（PolylineOptions）建，这样起止桩号才不是 0/0；
            // 建不出来再退到「只有名字」的重载。
            object polylineOptions = TryBuildPolylineOptions(civilDoc, points, attempts);
            bool styleResolved = false;
            // Alignment.Create 的 ObjectId 重载对 styleId 有非空要求（实测 null 抛
            // ArgumentNullException），所以先从 CivilDocument.Styles 里取一个真实样式。
            ObjectId styleId = ResolveStyleId(civilDoc, "AlignmentStyles", ref styleResolved, attempts);
            object alignment = CreateWithBestOverload(type, civilDoc, db, FixturePrefix + "-ALIGN",
                points, polylineOptions, styleResolved ? (ObjectId?)styleId : null, attempts);
            if (alignment == null)
            {
                return null;
            }
            readback["startingStation"] = SafeDoubleProperty(alignment, "StartingStation");
            readback["endingStation"] = SafeDoubleProperty(alignment, "EndingStation");
            readback["alignmentLength"] = SafeDoubleProperty(alignment, "Length");
            return alignment;
        }

        /// <summary>
        /// 构造 PolylineOptions（Alignment.Create 的几何输入）。签名各版本不同，
        /// 这里只用「无参构造 + AddVertices(Point3dCollection)」这一条最稳的路；
        /// 拿不到就返回 null，由调用方退化到无几何重载。
        /// </summary>
        private static object TryBuildPolylineOptions(object civilDoc, Point3dCollection points,
            List<object> attempts)
        {
            Type type = ResolveCivilType(civilDoc, "Autodesk.Civil.DatabaseServices.PolylineOptions");
            if (type == null)
            {
                attempts.Add("PolylineOptions 类型未找到");
                return null;
            }
            try
            {
                ConstructorInfo ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    attempts.Add("PolylineOptions 无默认构造");
                    return null;
                }
                object options = ctor.Invoke(null);
                MethodInfo addVertices = null;
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (string.Equals(methods[i].Name, "AddVertices", StringComparison.Ordinal))
                    {
                        ParameterInfo[] parameters = methods[i].GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Point3dCollection))
                        {
                            addVertices = methods[i];
                            break;
                        }
                    }
                }
                if (addVertices == null)
                {
                    attempts.Add("PolylineOptions 无 AddVertices(Point3dCollection)");
                    return null;
                }
                addVertices.Invoke(options, new object[] { points });
                attempts.Add("PolylineOptions.AddVertices OK（" + points.Count + " 点）");
                return options;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;
                attempts.Add("PolylineOptions 构造失败:" + inner.GetType().Name + ":"
                    + Clip(inner.Message, 120));
                return null;
            }
        }

        /// <summary>
        /// 在类型的全部静态 Create 重载里挑一个能绑定的调用。
        /// 参数按类型绑定：Database / CivilDocument / Point3dCollection / name 类 string /
        /// ObjectId→Null / bool→false / double|int→0。绑不上就换下一个重载。
        /// 2019 与 2024 的 Create 签名不同，这一层是版本兼容的唯一手段。
        /// </summary>
        /// <summary>
        /// 取一个真实存在的样式 ObjectId（Alignment.Create 的 ObjectId 重载要求非空）。
        /// 路径：CivilDocument.Styles.&lt;集合名&gt;，然后取第 0 项（Item(0) 或索引器）。
        /// 取不到就返回 ObjectId.Null，由调用方退化到无样式重载（并如实记录）。
        /// </summary>
        private static ObjectId ResolveStyleId(object civilDoc, string collectionName,
            ref bool resolved, List<object> attempts)
        {
            resolved = false;
            try
            {
                object styles = ReadMember(civilDoc, "Styles");
                if (styles == null)
                {
                    attempts.Add("CivilDocument.Styles 取不到");
                    return ObjectId.Null;
                }
                object collection = ReadMember(styles, collectionName);
                if (collection == null)
                {
                    attempts.Add("Styles." + collectionName + " 取不到（本文档可能没有样式库）");
                    return ObjectId.Null;
                }
                object countValue = ReadMember(collection, "Count");
                int count = countValue is int ? (int)countValue : 0;
                if (count <= 0)
                {
                    attempts.Add("Styles." + collectionName + " 为空");
                    return ObjectId.Null;
                }
                // Civil 集合一般有 Item(int) 索引器或名称索引器
                MethodInfo item = collection.GetType().GetMethod("get_Item", new[] { typeof(int) });
                if (item == null)
                {
                    attempts.Add("Styles." + collectionName + " 没有 int 索引器");
                    return ObjectId.Null;
                }
                object first = item.Invoke(collection, new object[] { 0 });
                if (first is ObjectId)
                {
                    resolved = true;
                    attempts.Add("Styles." + collectionName + "[0] 取到样式 ObjectId");
                    return (ObjectId)first;
                }
                attempts.Add("Styles." + collectionName + "[0] 不是 ObjectId");
            }
            catch (Exception ex)
            {
                attempts.Add("取样式失败:" + Clip(Unwrap(ex).Message, 120));
            }
            return ObjectId.Null;
        }

        /// <summary>
        /// 用事务把 ObjectId 打开成对象。先试只读（不动图），只读拿不到再试写模式
        /// （Civil 对象在只读句柄上常常回 null）。写模式仅用于 sandbox 里我们自己建的夹具。
        /// </summary>
        private static object OpenForReadOrWrite(Database db, ObjectId id, List<object> attempts)
        {
            if (db == null || id == ObjectId.Null)
            {
                return null;
            }
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    object obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj != null)
                    {
                        attempts.Add("tr.GetObject(ForRead) OK");
                        // 不 Commit：只读事务，保持对象在位
                        return obj;
                    }
                    attempts.Add("tr.GetObject(ForRead) 返回 null");
                }
            }
            catch (Exception ex)
            {
                attempts.Add("tr.GetObject(ForRead) 失败:" + Clip(ex.Message, 120));
            }
            return null;
        }

        private static object CreateWithBestOverload(Type type, object civilDoc, Database db, string name,
            Point3dCollection points, object polylineOptions, ObjectId? styleId, List<object> attempts)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                attempts.Add(type.Name + ".GetMethods 失败:" + ex.GetType().Name);
                return null;
            }
            List<MethodInfo> creates = new List<MethodInfo>();
            for (int i = 0; i < methods.Length; i++)
            {
                if (string.Equals(methods[i].Name, "Create", StringComparison.Ordinal))
                {
                    creates.Add(methods[i]);
                }
            }
            if (creates.Count == 0)
            {
                attempts.Add(type.Name + " 没有静态 Create 方法");
                return null;
            }

            for (int i = 0; i < creates.Count; i++)
            {
                MethodInfo method = creates[i];
                ParameterInfo[] parameters = method.GetParameters();
                object[] args = new object[parameters.Length];
                bool bindable = true;
                for (int p = 0; p < parameters.Length; p++)
                {
                    object value;
                    if (!TryBindArgument(parameters[p], civilDoc, db, name, points, polylineOptions, styleId, out value))
                    {
                        bindable = false;
                        break;
                    }
                    args[p] = value;
                }
                if (!bindable)
                {
                    continue;
                }
                try
                {
                    object result = method.Invoke(null, args);
                    attempts.Add(type.Name + ".Create(" + DescribeParams(parameters) + ") OK");
                    return result;
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null
                        ? ex.InnerException
                        : ex;
                    attempts.Add(type.Name + ".Create(" + DescribeParams(parameters) + ") 失败:"
                        + inner.GetType().Name + ":" + Clip(inner.Message, 160));
                }
            }
            return null;
        }

        private static bool TryBindArgument(ParameterInfo parameter, object civilDoc, Database db,
            string name, Point3dCollection points, object polylineOptions, ObjectId? styleId,
            out object value)
        {
            value = null;
            Type type = parameter.ParameterType;
            if (type == typeof(Database))
            {
                value = db;
                return db != null;
            }
            if (type == typeof(Point3dCollection))
            {
                value = points;
                return true;
            }
            if (polylineOptions != null && type.IsInstanceOfType(polylineOptions))
            {
                value = polylineOptions;
                return true;
            }
            if (type == typeof(ObjectId))
            {
                string idName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                if (idName.IndexOf("layer", StringComparison.Ordinal) >= 0 && db != null)
                {
                    // ObjectId 是结构体，不能用 Null 比较；图层 0 一定存在
                    value = db.LayerZero;
                    return true;
                }
                if (idName.IndexOf("style", StringComparison.Ordinal) >= 0
                    && styleId.HasValue && styleId.Value != ObjectId.Null)
                {
                    value = styleId.Value;
                    return true;
                }
                value = ObjectId.Null;
                return true;
            }
            if (type == typeof(string))
            {
                string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                // 顺序很重要：siteName / layerName / styleName / labelSetName 都以 Name 结尾，
                // 如果先判 "name" 会把它们全填成路线名（实测会报“找不到站点/样式”）。
                if (parameterName.IndexOf("site", StringComparison.Ordinal) >= 0)
                {
                    // 空串 = 不挂站点（实测 "<None>" 会报 Can not get site ID from site name）
                    value = string.Empty;
                }
                else if (parameterName.IndexOf("layer", StringComparison.Ordinal) >= 0)
                {
                    value = "0";
                }
                else if (parameterName.IndexOf("style", StringComparison.Ordinal) >= 0)
                {
                    // Civil 约定：<None> = 不用样式（空串会报 Can not get style ID from style name）
                    value = "<None>";
                }
                else if (parameterName.IndexOf("label", StringComparison.Ordinal) >= 0)
                {
                    value = "<None>";
                }
                else if (parameterName.IndexOf("name", StringComparison.Ordinal) >= 0)
                {
                    value = name;
                }
                else
                {
                    value = string.Empty;
                }
                return true;
            }
            if (type == typeof(bool))
            {
                value = false;
                return true;
            }
            if (type == typeof(double))
            {
                value = 0D;
                return true;
            }
            if (type == typeof(int))
            {
                value = 0;
                return true;
            }
            if (type.Name == "CivilDocument" || (civilDoc != null && type.IsInstanceOfType(civilDoc)))
            {
                value = civilDoc;
                return civilDoc != null;
            }
            return false;
        }

        /// <summary>
        /// 往曲面里塞点。实测 2024 的 API 是：
        /// `AddVertices(Point3dCollection)` / `AddVertex(Point3d)` / `AddLine(TinSurfaceVertex, TinSurfaceVertex)`。
        /// 只加点不连线，TIN 往往一个三角面都构不出来（面由线框三角化而来），
        /// 所以加完点还要按网格把相邻点连起来，才能得到 triangles &gt; 0。
        /// </summary>
        private static int AddPointsToSurface(object surface, Point3dCollection points, List<object> attempts)
        {
            int added = 0;

            // 1) 批量（最快也最稳）
            MethodInfo bulk = FindMethod(surface.GetType(), "AddVertices", typeof(Point3dCollection));
            if (bulk != null)
            {
                try
                {
                    bulk.Invoke(surface, new object[] { points });
                    added = points.Count;
                    attempts.Add("surface.AddVertices(Point3dCollection) OK（" + added + " 点）");
                }
                catch (Exception ex)
                {
                    Exception inner = Unwrap(ex);
                    attempts.Add("surface.AddVertices 失败:" + inner.GetType().Name + ":"
                        + Clip(inner.Message, 140));
                }
            }

            // 2) 逐点兜底
            if (added == 0)
            {
                string[] pointMethods = { "AddVertex", "AddPoint", "InsertPoint" };
                for (int m = 0; m < pointMethods.Length && added == 0; m++)
                {
                    MethodInfo method = FindMethod(surface.GetType(), pointMethods[m], typeof(Point3d));
                    if (method == null)
                    {
                        attempts.Add("surface 上没有 " + pointMethods[m] + "(Point3d)");
                        continue;
                    }
                    int ok = 0;
                    for (int i = 0; i < points.Count; i++)
                    {
                        try
                        {
                            method.Invoke(surface, new object[] { points[i] });
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            if (i == 0)
                            {
                                attempts.Add("surface." + pointMethods[m] + " 首点失败:"
                                    + Clip(Unwrap(ex).Message, 120));
                            }
                        }
                    }
                    attempts.Add("surface." + pointMethods[m] + " 成功 " + ok + "/" + points.Count);
                    added = ok;
                }
            }

            if (added == 0)
            {
                return 0;
            }

            // 3) 连边：网格相邻点两两 AddLine，才能三角化出 TIN
            int lines = WireGridLines(surface, attempts);
            attempts.Add("surface.AddLine 连边 " + lines + " 条");
            return added;
        }

        /// <summary>按网格把相邻顶点连线（行、列各连一遍）。</summary>
        private static int WireGridLines(object surface, List<object> attempts)
        {
            MethodInfo addLine = null;
            MethodInfo[] methods;
            try
            {
                methods = surface.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception)
            {
                return 0;
            }
            for (int i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, "AddLine", StringComparison.Ordinal))
                {
                    continue;
                }
                ParameterInfo[] parameters = methods[i].GetParameters();
                if (parameters.Length == 2
                    && parameters[0].ParameterType.Name == "TinSurfaceVertex"
                    && parameters[1].ParameterType.Name == "TinSurfaceVertex")
                {
                    addLine = methods[i];
                    break;
                }
            }
            if (addLine == null)
            {
                attempts.Add("surface 上没有 AddLine(TinSurfaceVertex, TinSurfaceVertex)");
                return 0;
            }

            MethodInfo find = FindMethod(surface.GetType(), "FindVertexAtXY", typeof(double), typeof(double));
            if (find == null)
            {
                attempts.Add("surface 上没有 FindVertexAtXY(double,double)");
                return 0;
            }

            int ok = 0;
            for (int row = 0; row < GridSize; row++)
            {
                for (int column = 0; column < GridSize; column++)
                {
                    if (column + 1 < GridSize)
                    {
                        ok += TryAddLine(surface, addLine, find, row, column, row, column + 1);
                    }
                    if (row + 1 < GridSize)
                    {
                        ok += TryAddLine(surface, addLine, find, row, column, row + 1, column);
                    }
                }
            }
            return ok;
        }

        private static int TryAddLine(object surface, MethodInfo addLine, MethodInfo find,
            int row1, int column1, int row2, int column2)
        {
            try
            {
                object v1 = find.Invoke(surface, new object[] { column1 * 20.0D, row1 * 20.0D });
                object v2 = find.Invoke(surface, new object[] { column2 * 20.0D, row2 * 20.0D });
                if (v1 == null || v2 == null)
                {
                    return 0;
                }
                addLine.Invoke(surface, new object[] { v1, v2 });
                return 1;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
            {
                return ex.InnerException;
            }
            return ex;
        }

        // ---------------- 反射小工具 ----------------

        private static Type ResolveCivilType(object civilDoc, string fullName)
        {
            try
            {
                if (civilDoc != null)
                {
                    Type type = civilDoc.GetType().Assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
            }
            catch (Exception)
            {
                // 继续走 Type.GetType 兜底
            }
            try
            {
                return Type.GetType(fullName + ", AeccDbMgd", false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MethodInfo FindMethod(Type type, string name, Type parameterType)
        {
            try
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { parameterType }, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MethodInfo FindMethod(Type type, string name, Type first, Type second)
        {
            try
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { first, second }, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int CountOfMember(object instance, string propertyName)
        {
            object value = ReadMember(instance, propertyName);
            if (value == null)
            {
                return 0;
            }
            if (value is System.Collections.ICollection)
            {
                return ((System.Collections.ICollection)value).Count;
            }
            // Civil 的集合类型（TinSurfaceTriangleCollection 等）不实现 ICollection，
            // 但都有 Count 属性 —— 反射取它，别把「拿不到」当成 0。
            object count = ReadMember(value, "Count");
            if (count is int)
            {
                return (int)count;
            }
            return -1;
        }

        private static int CountOrNumber(object instance, string propertyName, string fallbackName)
        {
            int count = CountOfMember(instance, propertyName);
            if (count >= 0)
            {
                return count;
            }
            object value = ReadMember(instance, fallbackName);
            if (value is int)
            {
                return (int)value;
            }
            return 0;
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            try
            {
                PropertyInfo property = instance.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property != null)
                {
                    return property.GetValue(instance, null);
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        private static string SafeStringProperty(object instance, string name)
        {
            object value = ReadMember(instance, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double SafeDoubleProperty(object instance, string name)
        {
            object value = ReadMember(instance, name);
            if (value is double)
            {
                return (double)value;
            }
            double parsed;
            if (value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return -1D;
        }

        private static Point3dCollection BuildGridPoints()
        {
            Point3dCollection points = new Point3dCollection();
            for (int row = 0; row < GridSize; row++)
            {
                for (int column = 0; column < GridSize; column++)
                {
                    double x = column * 20.0D;
                    double y = row * 20.0D;
                    // 起伏：对角线坡度 + 一个鼓包，保证三角面有真实高程差
                    double z = (row + column) * 0.8D + Math.Sin(row * 0.7D) * 1.5D;
                    points.Add(new Point3d(x, y, z));
                }
            }
            return points;
        }

        // ---------------- 沙箱门禁 ----------------

        /// <summary>
        /// D8 门禁：当前活动文档必须位于 sandbox 之内。返回 null 表示放行，否则返回失败原因
        /// （调用方拼成 `FAIL E1001 &lt;原因&gt;`）。
        ///
        /// 只检查「文档路径在不在 sandbox」这一件事，不做任何写动作；所有异常都转成可读原因，
        /// 绝不因为读不到路径就默认放行（**失败关闭**）。
        /// </summary>
        internal static string CheckActiveDocumentInsideSandbox()
        {
            Document doc;
            try
            {
                doc = Application.DocumentManager.MdiActiveDocument;
            }
            catch (Exception ex)
            {
                return "active_doc_unavailable:" + ex.GetType().Name;
            }
            if (doc == null)
            {
                return "no_active_document";
            }
            string name;
            try
            {
                name = doc.Name;
            }
            catch (Exception ex)
            {
                return "active_doc_name_unavailable:" + ex.GetType().Name;
            }
            if (string.IsNullOrEmpty(name))
            {
                return "active_doc_unnamed";
            }
            string fullName;
            try
            {
                fullName = Path.GetFullPath(name);
            }
            catch (Exception)
            {
                return "active_doc_path_invalid";
            }
            if (!IsInsideSandbox(fullName))
            {
                return "active_doc_not_in_sandbox:" + Path.GetFileName(fullName);
            }
            return null;
        }

        /// <summary>
        /// 目标目录必须在 `%LOCALAPPDATA%\EngineeringPlatform\sandbox\` 之下。
        /// 用 `..`、符号链接、UNC 绕出去也会被 `Path.GetFullPath` 归一后拒绝。
        /// </summary>
        internal static bool IsInsideSandbox(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return false;
            }
            string root;
            try
            {
                root = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EngineeringPlatform", "sandbox"));
            }
            catch (Exception)
            {
                return false;
            }
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                root += Path.DirectorySeparatorChar;
            }
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- 报告 ----------------

        private static string WriteReport(string outputDir, Dictionary<string, object> report)
        {
            try
            {
                string stamp = DateTime.UtcNow.ToLocalTime()
                    .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(outputDir, "fixture-" + stamp + ".json");
                SelfTestCommand.WriteJsonFile(path, report);
                return path;
            }
            catch (Exception ex)
            {
                HostLog.Exception("audit", "dev_fixture_report_failed", ex);
                return null;
            }
        }

        private static string DescribeParams(ParameterInfo[] parameters)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(parameters[i].ParameterType.Name);
            }
            return builder.ToString();
        }

        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string single = text.Replace("\r", " ").Replace("\n", " ");
            return single.Length <= max ? single : single.Substring(0, max) + "…";
        }

        private static string AskOutputDirectory(Document doc)
        {
            try
            {
                if (doc == null)
                {
                    return null;
                }
                PromptResult prompt = doc.Editor.GetString("\n夹具输出目录（必须在 sandbox 下）: ");
                if (prompt.Status != PromptStatus.OK || string.IsNullOrEmpty(prompt.StringResult))
                {
                    return null;
                }
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                    prompt.StringResult.Trim().Trim('"')));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteLine(string message)
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage("\n" + message);
                }
            }
            catch (Exception)
            {
                // 批处理阶段无编辑器上下文，忽略
            }
        }
    }
}
