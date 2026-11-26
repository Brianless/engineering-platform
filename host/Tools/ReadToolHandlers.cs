using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Preview;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>结果对象构造小工具：统一 summary / resultRef / drilldown 的形状。</summary>
    internal static class ToolResultHelper
    {
        public static Dictionary<string, object> ResultRef(string tool, string opId, string fingerprint, string detailTool)
        {
            Dictionary<string, object> reference = new Dictionary<string, object>(StringComparer.Ordinal);
            reference["tool"] = tool;
            reference["opId"] = opId ?? string.Empty;
            reference["fingerprint"] = string.IsNullOrEmpty(fingerprint) ? "fp1:00000000000000000000000000000000" : fingerprint;
            reference["storedAt"] = null;
            reference["detailTool"] = detailTool;
            return reference;
        }

        public static Dictionary<string, object> Drilldown(string tool, Dictionary<string, object> parameters, string why)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item["tool"] = tool;
            item["params"] = parameters ?? new Dictionary<string, object>(StringComparer.Ordinal);
            item["why"] = why ?? string.Empty;
            return item;
        }

        /// <summary>
        /// D5 契约：`truncated=true` 时 drilldown 不得为空。
        /// 有 nextCursor 就让调用方继续翻页；没有游标可翻（例如整体降级）时必须给出
        /// 「按条件收窄」与「增大 limit 到上限 100」两条可执行建议——
        /// 否则模型看到「被截断 + 无建议」只能重复调同一个工具或直接放弃。
        /// </summary>
        public static List<object> EnsureDrilldown(List<object> drilldown, bool truncated, bool hasCursor)
        {
            if (!truncated || (drilldown != null && drilldown.Count > 0) || hasCursor)
            {
                return drilldown;
            }
            List<object> fallback = new List<object>(2);
            fallback.Add(Drilldown("read_query",
                new Dictionary<string, object> { { "layer", "*" }, { "limit", 20 } },
                "按图层/范围收窄查询（结果被预算截断，不是数据缺失）"));
            fallback.Add(Drilldown("read_query",
                new Dictionary<string, object> { { "limit", 100 } },
                "增大 limit 到上限 100（默认 20）单次取更多明细"));
            return fallback;
        }

        public static Dictionary<string, object> Extents(double[] min, double[] max)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["min"] = new List<object> { min[0], min[1], min[2] };
            dict["max"] = new List<object> { max[0], max[1], max[2] };
            return dict;
        }

        public static Dictionary<string, object> Units(int insUnits, string code, double scaleToMeter)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["insUnits"] = insUnits;
            dict["code"] = code;
            dict["scaleToMeter"] = scaleToMeter;
            return dict;
        }

        /// <summary>摘要硬截断 400 字符（schema maxLength），超长加省略号。</summary>
        public static string Summary(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string single = text.Replace("\n", " ").Replace("\r", " ");
            if (single.Length <= 398)
            {
                return single;
            }
            return single.Substring(0, 397) + "…";
        }

        public static string Number(double value)
        {
            return Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>读取定长数值数组（入参已在封套层校验，这里只做安全取值）。</summary>
        public static double[] NumberArray(Dictionary<string, object> source, string key, int exactLength)
        {
            List<object> raw = JsonCodec.GetList(source, key);
            if (raw == null || raw.Count != exactLength)
            {
                return null;
            }
            double[] values = new double[exactLength];
            for (int i = 0; i < exactLength; i++)
            {
                Dictionary<string, object> wrapper = new Dictionary<string, object>(StringComparer.Ordinal);
                wrapper["v"] = raw[i];
                double number;
                if (!JsonCodec.TryGetDouble(wrapper, "v", out number))
                {
                    return null;
                }
                values[i] = number;
            }
            return values;
        }

        public static List<object> ToObjectList(IEnumerable<string> values)
        {
            List<object> list = new List<object>();
            if (values == null)
            {
                return list;
            }
            foreach (string value in values)
            {
                list.Add(value);
            }
            return list;
        }
    }

    /// <summary>
    /// 八个只读读图工具（read_overview / read_layers / read_query / read_describe / read_anomalies /
    /// read_preview / read_civil_list / read_civil_object）。
    /// 所有结果都必须含 summary（≤400 字符）+ resultRef（方案 13.6 的「摘要 + 结果引用」约定）。
    /// </summary>
    internal static class ReadToolHandlers
    {
        public static Dictionary<string, object> Handle(ToolContext context)
        {
            string tool = context.Item.Tool;
            switch (tool)
            {
                case "read_overview":
                    return Overview(context);
                case "read_layers":
                    return Layers(context);
                case "read_query":
                    return Query(context);
                case "read_describe":
                    return Describe(context);
                case "read_anomalies":
                    return Anomalies(context);
                case "read_preview":
                    return Preview(context);
                case "read_civil_list":
                    return CivilList(context);
                case "read_civil_object":
                    return CivilObject(context);
                case "read_civil_surfaces":
                    return CivilSurfaces(context);
                case "read_civil_alignments":
                    return CivilAlignments(context);
                case "read_active_selection":
                    return ActiveSelection(context);
                case "read_viewport_snapshot":
                    return ViewportSnapshot(context);
                case "read_object_xdata":
                    return ObjectXData(context);
                default:
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_read_tool:" + tool, "tool"));
            }
        }

        // ---------------- read_civil_list / read_civil_object（N04-B 新增，docs/08 §4.3 #2/#3） ----------------

        /// <summary>
        /// 按类型/名称枚举 Civil 3D 原生对象并给句柄。
        ///
        /// 数据源是 <see cref="SemanticModel.Civil"/> 快照（由 <see cref="Civil3dReader.Read"/> 一次性采集），
        /// **不新开一套反射读取**：B 侧只保留一份 Civil 反射路径，重复实现必然两边漂移。
        /// 取不到 CivilDocument 时按降级档上报 W1001 并返回空集 —— 显式缺口，不当作「这张图没有 Civil 对象」。
        /// </summary>
        private static Dictionary<string, object> CivilList(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;
            Civil3dSnapshot snapshot = model.Civil;

            // type 非必填：**不传**（null）等价于 any；但「传了空串」不等于没传，
            // 空串不在 case 里，会落到 CivilRecordsOf 的 default 分支被拒（E1001）。
            string wanted = JsonCodec.GetStr(context.Item.Params, "type");
            if (wanted == null)
            {
                wanted = "any";
            }
            string nameContains = JsonCodec.GetStr(context.Item.Params, "nameContains");
            int limit = JsonCodec.GetInt(context.Item.Params, "limit", 100);
            if (limit < 1)
            {
                limit = 1;
            }
            if (limit > 500)
            {
                limit = 500;
            }
            int offset = ParseCursor(JsonCodec.GetStr(context.Item.Params, "cursor"));

            if (!snapshot.Detected)
            {
                context.AddWarning(ErrorCodes.W1001,
                    "civil_not_available:" + (string.IsNullOrEmpty(snapshot.Note) ? "no_civil_document" : snapshot.Note));
            }

            List<CivilObjectRecord> all = CivilRecordsOf(snapshot, wanted);
            List<CivilObjectRecord> matched = new List<CivilObjectRecord>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                CivilObjectRecord record = all[i];
                if (!string.IsNullOrEmpty(nameContains) && !Contains(record.Name, nameContains))
                {
                    continue;
                }
                matched.Add(record);
            }

            List<object> items = new List<object>();
            int index = offset;
            while (index < matched.Count && items.Count < limit)
            {
                CivilObjectRecord record = matched[index];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["handle"] = record.Handle;
                item["name"] = Redactor.Text(record.Name, 200);
                item["type"] = record.Kind;
                item["basis"] = record.Basis.ToDict();
                items.Add(item);
                index++;
            }

            int returned = items.Count;
            bool truncated = (offset + returned) < matched.Count;

            Dictionary<string, object> counts = new Dictionary<string, object>(StringComparer.Ordinal);
            counts["total"] = matched.Count;
            counts["returned"] = returned;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【Civil 对象】类型 " + wanted
                + (string.IsNullOrEmpty(nameContains) ? string.Empty : "，名称含「" + nameContains + "」")
                + "｜命中 " + matched.Count + "，本页返回 " + returned
                + (truncated ? "（还有下一页）" : string.Empty)
                + "｜Civil 三态 " + (snapshot.Detected ? "可用" : "不可用")
                + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_civil_list", context.Item.OpId,
                model.Fingerprint, "read_civil_object");
            result["counts"] = counts;
            result["items"] = items;
            result["truncated"] = truncated;
            result["nextCursor"] = truncated ? "o:" + (offset + returned).ToString(CultureInfo.InvariantCulture) : null;
            result["units"] = ToolResultHelper.Units(model.InsUnits, model.UnitCode, model.ScaleToMeter);
            return result;
        }

        // ---------------- read_civil_surfaces / read_civil_alignments ----------------

        /// <summary>
        /// 列举 Civil 3D 曲面（直接走 Civil3dReader 内存快照，毫秒级返回、无跨线程死锁）。
        /// 包含 TIN/Grid/体积曲面（含挖填净方量）与点/三角面/高程范围属性。
        /// </summary>
        private static Dictionary<string, object> CivilAlignments(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;
            Civil3dSnapshot snapshot = model.Civil;

            int limit = JsonCodec.GetInt(context.Item.Params, "limit", 100);
            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            if (!snapshot.Detected)
            {
                context.AddWarning(ErrorCodes.W1001,
                    "civil_not_available:" + (string.IsNullOrEmpty(snapshot.Note) ? "no_civil_document" : snapshot.Note));
            }

            List<CivilObjectRecord> all = snapshot.Alignments;
            List<object> items = new List<object>();
            for (int i = 0; i < all.Count && items.Count < limit; i++)
            {
                CivilObjectRecord record = all[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["handle"] = record.Handle;
                item["name"] = Redactor.Text(record.Name, 200);
                item["type"] = record.Kind;
                item["typeName"] = record.TypeName;
                item["props"] = record.Props;
                item["basis"] = record.Basis.ToDict();
                items.Add(item);
            }

            int returned = items.Count;
            bool truncated = returned < snapshot.AlignmentTotal;
            Dictionary<string, object> counts = new Dictionary<string, object>(StringComparer.Ordinal);
            counts["total"] = snapshot.AlignmentTotal;
            counts["returned"] = returned;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            string civilState = snapshot.Detected
                ? "Civil 3D 可用"
                : "当前宿主为普通 AutoCAD（未安装 Civil 3D，无原生路线对象）";
            result["summary"] = ToolResultHelper.Summary("【Civil 路线】共 " + snapshot.AlignmentTotal
                + " 条，返回 " + returned + " 条"
                + (truncated ? "（已截断）" : string.Empty)
                + "｜" + civilState
                + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_civil_alignments", context.Item.OpId,
                model.Fingerprint, "read_civil_object");
            result["counts"] = counts;
            result["items"] = items;
            result["truncated"] = truncated;
            result["units"] = ToolResultHelper.Units(model.InsUnits, model.UnitCode, model.ScaleToMeter);
            return result;
        }

        private static Dictionary<string, object> CivilSurfaces(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;
            Civil3dSnapshot snapshot = model.Civil;

            int limit = JsonCodec.GetInt(context.Item.Params, "limit", 100);
            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            if (!snapshot.Detected)
            {
                context.AddWarning(ErrorCodes.W1001,
                    "civil_not_available:" + (string.IsNullOrEmpty(snapshot.Note) ? "no_civil_document" : snapshot.Note));
            }

            List<CivilObjectRecord> all = snapshot.Surfaces;
            List<object> items = new List<object>();
            for (int i = 0; i < all.Count && items.Count < limit; i++)
            {
                CivilObjectRecord record = all[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["handle"] = record.Handle;
                item["name"] = Redactor.Text(record.Name, 200);
                item["type"] = record.Kind;
                item["typeName"] = record.TypeName;
                item["props"] = record.Props;
                item["basis"] = record.Basis.ToDict();
                items.Add(item);
            }

            int returned = items.Count;
            bool truncated = returned < snapshot.SurfaceTotal;
            Dictionary<string, object> counts = new Dictionary<string, object>(StringComparer.Ordinal);
            counts["total"] = snapshot.SurfaceTotal;
            counts["returned"] = returned;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            string civilState = snapshot.Detected
                ? "Civil 3D 可用"
                : "当前宿主为普通 AutoCAD（未安装 Civil 3D，无原生曲面对象）";
            result["summary"] = ToolResultHelper.Summary("【Civil 曲面】共 " + snapshot.SurfaceTotal
                + " 个，返回 " + returned + " 个"
                + (truncated ? "（已截断）" : string.Empty)
                + "｜" + civilState
                + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_civil_surfaces", context.Item.OpId,
                model.Fingerprint, "read_civil_object");
            result["counts"] = counts;
            result["items"] = items;
            result["truncated"] = truncated;
            result["units"] = ToolResultHelper.Units(model.InsUnits, model.UnitCode, model.ScaleToMeter);
            return result;
        }

        /// <summary>
        /// 按句柄返回单个 Civil 3D 对象的属性表。
        /// 强制带 confidence 与 basis（方案 15.3：没有依据视为缺陷）；取不到的属性不写进 attributes（宁缺勿造假）。
        /// </summary>
        private static Dictionary<string, object> CivilObject(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;
            Civil3dSnapshot snapshot = model.Civil;

            string handle = (JsonCodec.GetStr(context.Item.Params, "handle") ?? string.Empty).ToUpperInvariant();
            string requestedFingerprint = JsonCodec.GetStr(context.Item.Params, "fingerprint");
            if (!string.IsNullOrEmpty(requestedFingerprint) &&
                !string.Equals(requestedFingerprint, model.Fingerprint, StringComparison.Ordinal))
            {
                context.AddWarning(ErrorCodes.W1004,
                    "expected=" + requestedFingerprint + ";actual=" + model.Fingerprint);
            }
            if (!snapshot.Detected)
            {
                context.AddWarning(ErrorCodes.W1001,
                    "civil_not_available:" + (string.IsNullOrEmpty(snapshot.Note) ? "no_civil_document" : snapshot.Note));
            }

            CivilObjectRecord record = FindCivilRecord(snapshot, handle);
            if (record == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                    "civil_object_not_found:handle=" + handle, "params.handle"));
            }

            List<string> wantedProps = JsonCodec.GetStringList(context.Item.Params, "properties");

            List<object> attributes = new List<object>();
            foreach (KeyValuePair<string, object> pair in record.Props)
            {
                if (attributes.Count >= 200)
                {
                    break;
                }
                if (wantedProps != null && wantedProps.Count > 0 && !wantedProps.Contains(pair.Key))
                {
                    continue;
                }
                Dictionary<string, object> attribute = new Dictionary<string, object>(StringComparer.Ordinal);
                attribute["name"] = pair.Key;
                attribute["value"] = ScalarOrText(pair.Value);
                attribute["unit"] = null;
                attributes.Add(attribute);
            }

            Dictionary<string, object> objectDict = new Dictionary<string, object>(StringComparer.Ordinal);
            objectDict["handle"] = record.Handle;
            objectDict["type"] = record.Kind;
            objectDict["name"] = Redactor.Text(record.Name, 200);
            objectDict["confidence"] = record.Confidence;
            objectDict["basis"] = record.Basis.ToDict();
            objectDict["attributes"] = attributes;
            objectDict["units"] = ToolResultHelper.Units(model.InsUnits, model.UnitCode, model.ScaleToMeter);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【Civil 对象属性】" + record.KindDisplay
                + (string.IsNullOrEmpty(record.Name) ? string.Empty : "「" + Redactor.Text(record.Name, 60) + "」")
                + "｜句柄 " + record.Handle
                + "｜" + (record.TypeName ?? "类型未知")
                + "｜置信度 " + SemanticRecognizer.Percent(record.Confidence)
                + "｜属性 " + attributes.Count + " 项"
                + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_civil_object", context.Item.OpId,
                model.Fingerprint, "read_preview");
            result["object"] = objectDict;
            return result;
        }

        /// <summary>
        /// 按类型取快照里的对象列表。
        ///
        /// **`any` 必须显式成一支**：早先这里没有 `case "any"`，让合法值 `any` 和非法值（如 `corridor`）
        /// 共用 `default` 分支 —— 虽然 `ParamValidator` 的 enum 校验会先拦住非法值，但这个写法把
        /// 「不可能发生」建立在校验顺序上，任何内部调用/新解析层/校验位置调整都会让非法值直接落到
        /// 「返回全部五类」。那不是「漏答」而是**错答**，比返回空集危险。所以现在：
        ///   * `any` → 五类聚合（明确写在 case 里）；
        ///   * 其余（含 null/空/未支持）→ `E1001`，detail 带允许集。
        /// </summary>
        private static List<CivilObjectRecord> CivilRecordsOf(Civil3dSnapshot snapshot, string kind)
        {
            List<CivilObjectRecord> records = new List<CivilObjectRecord>();
            switch (kind)
            {
                case "surface":
                    records.AddRange(snapshot.Surfaces);
                    break;
                case "alignment":
                    records.AddRange(snapshot.Alignments);
                    break;
                case "profile":
                    records.AddRange(snapshot.Profiles);
                    break;
                case "pipeNetwork":
                    records.AddRange(snapshot.PipeNetworks);
                    break;
                case "pointGroup":
                    records.AddRange(snapshot.PointGroups);
                    break;
                case "any":
                    records.AddRange(snapshot.Surfaces);
                    records.AddRange(snapshot.Alignments);
                    records.AddRange(snapshot.Profiles);
                    records.AddRange(snapshot.PipeNetworks);
                    records.AddRange(snapshot.PointGroups);
                    break;
                default:
                    // null / 空 / 未支持类型都到这里。**绝不**在这里兜底返回任何一类。
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                        "not in enum: " + (string.IsNullOrEmpty(kind) ? "(none)" : kind)
                        + " (allowed: any|alignment|profile|surface|pipeNetwork|pointGroup)",
                        "params.type"));
            }
            return records;
        }

        private static CivilObjectRecord FindCivilRecord(Civil3dSnapshot snapshot, string handle)
        {
            if (string.IsNullOrEmpty(handle))
            {
                return null;
            }
            List<CivilObjectRecord> all = new List<CivilObjectRecord>();
            all.AddRange(snapshot.Surfaces);
            all.AddRange(snapshot.Alignments);
            all.AddRange(snapshot.Profiles);
            all.AddRange(snapshot.PipeNetworks);
            all.AddRange(snapshot.PointGroups);
            all.AddRange(snapshot.Corridors);
            all.AddRange(snapshot.Assemblies);
            all.AddRange(snapshot.ProfileViews);
            all.AddRange(snapshot.SampleLineGroups);
            all.AddRange(snapshot.Sites);
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Handle, handle, StringComparison.OrdinalIgnoreCase))
                {
                    return all[i];
                }
            }
            return null;
        }

        /// <summary>游标格式 `o:&lt;offset&gt;`；无法解析时按 0 处理（等价第一页）。</summary>
        private static int ParseCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor) || !cursor.StartsWith("o:", StringComparison.Ordinal))
            {
                return 0;
            }
            int parsed;
            if (int.TryParse(cursor.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                && parsed > 0)
            {
                return parsed;
            }
            return 0;
        }

        private static bool Contains(string source, string needle)
        {
            return !string.IsNullOrEmpty(source)
                && source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>属性值只允许标量；复合值降级成文本（schema 的 value 是 string|number|boolean|null）。</summary>
        private static object ScalarOrText(object value)
        {
            if (value == null || value is string || value is bool)
            {
                return value;
            }
            if (value is int || value is long || value is double || value is float || value is decimal)
            {
                return value;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // ---------------- read_overview ----------------

        private static Dictionary<string, object> Overview(ToolContext context)
        {
            bool refresh = JsonCodec.GetBool(context.Item.Params, "refresh", false);
            SemanticModel model = context.EnsureModel(refresh);
            context.Fingerprint = model.Fingerprint;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);

            string unitText = model.UnitCode == "unitless" ? "单位未设置" : "单位 " + model.UnitCode;
            string rangeText = model.HasExtents
                ? "范围 " + ToolResultHelper.Number(model.Max[0] - model.Min[0]) + "×" + ToolResultHelper.Number(model.Max[1] - model.Min[1])
                : "范围不可用";

            // 第二轮：Civil 3D 原生对象单独成段（不再一律按「未安装」描述）
            Civil3dSnapshot civil = model.Civil ?? new Civil3dSnapshot();
            string civilText;
            if (civil.Detected)
            {
                civilText = civil.TotalObjects > 0
                    ? "｜Civil 3D 原生对象 " + civil.TotalObjects + "（" + DescribeCivilCounts(civil) + "）"
                    : "｜Civil 3D 可用，未发现原生对象";
            }
            else
            {
                civilText = "｜Civil 3D 未探测到";
            }

            string proxyText = model.ProxyCount > 0
                ? "｜代理对象 " + model.ProxyCount
                    + (civil.Detected ? "（无法原生识别的第三方对象）" : "（Civil 3D 不可用，无法识别为原生对象）")
                : string.Empty;
            List<AnomalyItem> top = AnomalyScanner.TopBySeverity(model.Anomalies, 5);
            string anomalyText = top.Count > 0
                ? "｜顶层异常 " + model.Anomalies.Count + " 项：" + FormatAnomalyBrief(top)
                : "｜未发现异常";

            // D7：摘要里的「图层」与 counts.layers 同口径 = 有图元的图层数；
            // 空图层单独一句说明，避免用户以为 read_layers 漏数据。
            int summaryUsedLayers = model.Layers == null ? 0 : model.Layers.Count;
            int summaryEmptyLayers = model.LayerCount > summaryUsedLayers
                ? model.LayerCount - summaryUsedLayers
                : 0;

            result["summary"] = ToolResultHelper.Summary("【图纸总览】" + context.Runtime.Sessions.HostInstanceId
                + "｜" + model.SoftwareName + " " + model.SoftwareVersion
                + "｜" + unitText
                + "｜" + rangeText
                + "｜图元 " + model.EntityCount
                + "｜图层 " + summaryUsedLayers
                + (summaryEmptyLayers > 0 ? "（另有 " + summaryEmptyLayers + " 个空图层）" : string.Empty)
                + "｜块引用 " + model.BlockReferenceCount
                + civilText
                + proxyText
                + anomalyText
                + "｜指纹 " + model.Fingerprint);

            result["resultRef"] = ToolResultHelper.ResultRef("read_overview", context.Item.OpId, model.Fingerprint, "read_layers");

            Dictionary<string, object> software = new Dictionary<string, object>(StringComparer.Ordinal);
            software["name"] = model.SoftwareName;
            software["version"] = model.SoftwareVersion;
            software["civilApiAvailable"] = model.CivilApiAvailable;
            result["software"] = software;

            Dictionary<string, object> drawing = new Dictionary<string, object>(StringComparer.Ordinal);
            drawing["nameHash"] = model.NameHash;
            drawing["modifiedUtc"] = model.ModifiedUtcIso;
            drawing["readOnly"] = model.ReadOnly;
            result["drawing"] = drawing;

            result["units"] = ToolResultHelper.Units(model.InsUnits, model.UnitCode, model.ScaleToMeter);
            result["coordinateSystem"] = null;
            result["extents"] = ToolResultHelper.Extents(model.Min, model.Max);

            Dictionary<string, object> counts = new Dictionary<string, object>(StringComparer.Ordinal);
            counts["entities"] = model.EntityCount;
            // D7：layers 统一为「有图元的图层数」，恒等于 read_layers.total；
            // 图层表记录数（含建了没用的空图层）单列 layerTableRecords，空图层数单列 emptyLayers。
            int usedLayers = model.Layers == null ? 0 : model.Layers.Count;
            int layerTableRecords = model.LayerCount;
            int emptyLayers = layerTableRecords > usedLayers ? layerTableRecords - usedLayers : 0;
            counts["layers"] = usedLayers;
            counts["layerTableRecords"] = layerTableRecords;
            counts["emptyLayers"] = emptyLayers;
            counts["blocks"] = model.BlockCount;
            counts["blockReferences"] = model.BlockReferenceCount;
            counts["paperSpaces"] = model.PaperSpaceCount;
            counts["proxyEntities"] = model.ProxyCount;
            counts["entityLimitReached"] = model.EntityLimitReached;
            result["counts"] = counts;

            List<object> disciplines = new List<object>(model.Disciplines.Count);
            for (int i = 0; i < model.Disciplines.Count; i++)
            {
                DisciplineSummary summary = model.Disciplines[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["name"] = summary.Name;
                item["entityCount"] = summary.EntityCount;
                item["confidence"] = summary.Confidence;
                disciplines.Add(item);
            }
            result["disciplines"] = disciplines;

            List<object> topAnomalies = new List<object>(top.Count);
            for (int i = 0; i < top.Count; i++)
            {
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["code"] = top[i].Code;
                item["severity"] = top[i].Severity;
                item["message"] = Redactor.Text(top[i].Message, 200);
                topAnomalies.Add(item);
            }
            result["topAnomalies"] = topAnomalies;

            result["fingerprint"] = model.Fingerprint;
            // 采集被上限截断时如实标注（W1009 已由调用方加入 warnings）
            result["truncated"] = model.Truncated;

            List<object> drilldown = new List<object>();
            drilldown.Add(ToolResultHelper.Drilldown("read_layers",
                new Dictionary<string, object> { { "limit", 50 } }, "按图层看识别结果与置信度"));
            drilldown.Add(ToolResultHelper.Drilldown("read_anomalies",
                new Dictionary<string, object> { { "limit", 30 } }, "确认异常项是否需要人工复核"));
            drilldown.Add(ToolResultHelper.Drilldown("read_preview",
                new Dictionary<string, object> { { "width", 1200 }, { "height", 800 } }, "生成结构示意图确认读到的是哪张图"));
            result["drilldown"] = drilldown;

            Dictionary<string, object> civilApi = new Dictionary<string, object>(StringComparer.Ordinal);
            civilApi["available"] = CivilApiProbe.Available;
            civilApi["product"] = CivilApiProbe.ProductName;
            civilApi["release"] = CivilApiProbe.ReleaseTag;
            civilApi["probe"] = CivilApiProbe.ProbeText;
            civilApi["reason"] = CivilApiProbe.Reason;
            result["civilApi"] = civilApi;

            // Civil 3D 原生对象清单（第二轮新增；契约见 protocol/tools.schema.json）
            result["civil3d"] = civil.ToDict();

            // 只有「Civil 3D 不可用」时，代理对象才是缺口；可用时的代理是第三方对象，不算缺口
            if (!civil.Detected && model.ProxyCount > 0)
            {
                context.AddWarning(ErrorCodes.W1001, "civil_api_absent:proxy_entities=" + model.ProxyCount);
            }
            return result;
        }

        /// <summary>Civil 3D 各类对象计数的一句话摘要（只列非零项）。</summary>
        private static string DescribeCivilCounts(Civil3dSnapshot civil)
        {
            List<string> parts = new List<string>(5);
            if (civil.SurfaceTotal > 0)
            {
                parts.Add("曲面 " + civil.SurfaceTotal);
            }
            if (civil.AlignmentTotal > 0)
            {
                parts.Add("路线 " + civil.AlignmentTotal);
            }
            if (civil.ProfileTotal > 0)
            {
                parts.Add("纵断面 " + civil.ProfileTotal);
            }
            if (civil.PipeNetworkTotal > 0)
            {
                parts.Add("管网 " + civil.PipeNetworkTotal);
            }
            if (civil.PointGroupTotal > 0)
            {
                parts.Add("点编组 " + civil.PointGroupTotal);
            }
            if (civil.CorridorTotal > 0)
            {
                parts.Add("三维道路 " + civil.CorridorTotal);
            }
            if (civil.AssemblyTotal > 0)
            {
                parts.Add("装配 " + civil.AssemblyTotal);
            }
            if (civil.ProfileViewTotal > 0)
            {
                parts.Add("纵断面图 " + civil.ProfileViewTotal);
            }
            if (civil.SampleLineGroupTotal > 0)
            {
                parts.Add("采样线组 " + civil.SampleLineGroupTotal);
            }
            return parts.Count == 0 ? "无" : string.Join("/", parts.ToArray());
        }

        private static string FormatAnomalyBrief(List<AnomalyItem> items)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                int value;
                counts.TryGetValue(items[i].Code, out value);
                counts[items[i].Code] = value + 1;
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                parts.Add(AnomalyDisplayName(pair.Key) + " " + pair.Value);
            }
            return string.Join("、", parts.ToArray());
        }

        internal static string AnomalyDisplayName(string code)
        {
            switch (code)
            {
                case "duplicate_point":
                    return "重复点";
                case "zero_length":
                    return "零长度";
                case "extreme_coord":
                    return "极值坐标";
                case "degenerate_extents":
                    return "退化包围盒";
                case "proxy_object":
                    return "代理对象";
                case "missing_unit":
                    return "单位缺失";
                case "empty_attr":
                    return "空属性";
                case "extremely_long_text":
                    return "超长文字";
                default:
                    return code;
            }
        }

        // ---------------- read_layers ----------------

        private static Dictionary<string, object> Layers(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;

            Dictionary<string, object> parameters = context.Item.Params;
            List<string> disciplines = JsonCodec.GetStringList(parameters, "disciplines");
            string namePattern = JsonCodec.GetStr(parameters, "namePattern");
            int minEntities = JsonCodec.GetInt(parameters, "minEntities", 0);
            int limit = JsonCodec.GetInt(parameters, "limit", 50);
            if (limit < 1)
            {
                limit = 1;
            }
            if (limit > 200)
            {
                limit = 200;
            }
            string cursor = JsonCodec.GetStr(parameters, "cursor");

            int skip = 0;
            string cursorValue = CursorCodec.Decode(cursor);
            if (cursorValue != null)
            {
                int parsed;
                if (int.TryParse(cursorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    skip = parsed;
                }
            }

            LayerMapping mapping = context.Runtime.Mapping;
            List<LayerSummary> filtered = new List<LayerSummary>();
            for (int i = 0; i < model.Layers.Count; i++)
            {
                LayerSummary layer = model.Layers[i];
                if (layer.EntityCount < minEntities)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(namePattern) && !LayerMapping.WildcardMatch(namePattern, layer.Name))
                {
                    continue;
                }
                if (disciplines != null && disciplines.Count > 0 && !MatchesDiscipline(layer, disciplines, mapping))
                {
                    continue;
                }
                filtered.Add(layer);
            }

            List<object> items = new List<object>();
            int end = Math.Min(filtered.Count, skip + limit);
            for (int i = skip; i < end; i++)
            {
                LayerSummary layer = filtered[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["name"] = layer.Name;
                item["entityCount"] = layer.EntityCount;

                Dictionary<string, object> types = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, int> pair in layer.Types)
                {
                    types[pair.Key] = pair.Value;
                }
                item["types"] = types;
                item["recognizedAs"] = layer.Meaning;
                item["confidence"] = layer.Confidence;
                item["confidenceBand"] = layer.Band;

                List<object> basis = new List<object>(layer.BasisList.Count);
                for (int b = 0; b < layer.BasisList.Count; b++)
                {
                    basis.Add(layer.BasisList[b].ToDict());
                }
                item["basis"] = basis;
                items.Add(item);
            }

            string nextCursor = end < filtered.Count ? CursorCodec.Encode(end.ToString(CultureInfo.InvariantCulture)) : null;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【图层摘要】共 " + filtered.Count + " 个图层，返回 "
                + items.Count + " 个（游标 " + (skip) + "）｜" + DescribeTopLayers(model)
                + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_layers", context.Item.OpId, model.Fingerprint, "read_query");
            result["layers"] = items;
            result["total"] = filtered.Count;
            result["nextCursor"] = nextCursor;
            result["truncated"] = nextCursor != null;
            List<object> layerDrilldown = new List<object>
            {
                ToolResultHelper.Drilldown("read_query", new Dictionary<string, object> { { "limit", 20 } }, "按图层下钻对象引用")
            };
            // D5：truncated 且无游标时必须仍有可执行建议
            result["drilldown"] = ToolResultHelper.EnsureDrilldown(layerDrilldown,
                nextCursor != null, nextCursor != null);
            return result;
        }

        private static bool MatchesDiscipline(LayerSummary layer, List<string> disciplines, LayerMapping mapping)
        {
            MappingMatch match = mapping == null ? null : mapping.MatchLayer(layer.Name);
            string category = match == null ? "unknown" : match.Category;
            for (int i = 0; i < disciplines.Count; i++)
            {
                string wanted = disciplines[i];
                if (string.IsNullOrEmpty(wanted))
                {
                    continue;
                }
                if (string.Equals(wanted, category, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!string.IsNullOrEmpty(layer.Meaning) &&
                    layer.Meaning.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string DescribeTopLayers(SemanticModel model)
        {
            List<KeyValuePair<string, int>> items = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < model.Layers.Count; i++)
            {
                items.Add(new KeyValuePair<string, int>(model.Layers[i].Name, model.Layers[i].EntityCount));
            }
            items.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            List<string> parts = new List<string>();
            for (int i = 0; i < items.Count && i < 3; i++)
            {
                parts.Add(items[i].Key + "(" + items[i].Value + ")");
            }
            return parts.Count == 0 ? "无图层" : "最大图层 " + string.Join("、", parts.ToArray());
        }

        // ---------------- read_query ----------------

        private static Dictionary<string, object> Query(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;

            Dictionary<string, object> parameters = context.Item.Params;
            string layerPattern = JsonCodec.GetStr(parameters, "layer");
            string dxfName = JsonCodec.GetStr(parameters, "dxfName");
            double[] extent = ToolResultHelper.NumberArray(parameters, "extent", 4);
            string textContains = JsonCodec.GetStr(parameters, "textContains");
            Dictionary<string, string> attrEquals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> attrRaw = JsonCodec.GetDict(parameters, "attrEquals");
            if (attrRaw != null)
            {
                foreach (KeyValuePair<string, object> pair in attrRaw)
                {
                    string value = pair.Value as string;
                    if (value != null)
                    {
                        attrEquals[pair.Key] = value;
                    }
                }
            }
            string meaning = JsonCodec.GetStr(parameters, "meaning");
            double minConfidence = JsonCodec.GetDouble(parameters, "minConfidence", 0D);
            int limit = JsonCodec.GetInt(parameters, "limit", 20);
            if (limit < 1)
            {
                limit = 1;
            }
            if (limit > 100)
            {
                limit = 100;
            }
            string cursor = JsonCodec.GetStr(parameters, "cursor");
            long afterHandle = -1;
            string cursorValue = CursorCodec.Decode(cursor);
            if (cursorValue != null)
            {
                long parsed;
                if (long.TryParse(cursorValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                {
                    afterHandle = parsed;
                }
            }

            List<ObjectRef> matched = new List<ObjectRef>();
            for (int i = 0; i < model.Entities.Count; i++)
            {
                if (matched.Count > 4000)
                {
                    // 保护：匹配集合本身也不无上限增长
                    break;
                }
                ObjectRef obj = model.Entities[i];
                if (obj == null || obj.SortKey <= afterHandle)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(layerPattern) && !LayerMapping.WildcardMatch(layerPattern, obj.Layer))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(dxfName) && !string.Equals(dxfName, obj.DxfName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (extent != null && !Intersects(obj, extent))
                {
                    continue;
                }
                if (obj.Confidence < minConfidence)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(meaning) &&
                    (obj.Meaning == null || obj.Meaning.IndexOf(meaning, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                bool textHit = false;
                if (!string.IsNullOrEmpty(textContains))
                {
                    textHit = MatchesText(obj, textContains);
                    if (!textHit)
                    {
                        continue;
                    }
                }
                if (attrEquals.Count > 0 && !MatchesAttributes(obj, attrEquals))
                {
                    continue;
                }
                matched.Add(obj);
            }

            List<ObjectRef> page = new List<ObjectRef>();
            for (int i = 0; i < matched.Count && page.Count < limit; i++)
            {
                page.Add(matched[i]);
            }

            List<object> items = new List<object>(page.Count);
            for (int i = 0; i < page.Count; i++)
            {
                ObjectRef obj = page[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["handle"] = obj.Handle;
                item["dxfName"] = obj.DxfName;
                item["type"] = obj.DotNetType;
                item["layer"] = obj.Layer;
                item["extents"] = obj.ExtentsDict();
                item["meaning"] = obj.Meaning;
                item["confidence"] = obj.Confidence;
                if (!string.IsNullOrEmpty(textContains))
                {
                    item["textHit"] = true;
                }
                items.Add(item);
            }

            string nextCursor = null;
            if (page.Count > 0 && matched.Count > page.Count)
            {
                nextCursor = CursorCodec.Encode(page[page.Count - 1].Handle);
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【对象查询】命中 " + matched.Count + " 个对象，返回 " + items.Count
                + " 个" + DescribeQueryFilter(layerPattern, dxfName, meaning, textContains)
                + "｜图元总数 " + model.EntityCount + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_query", context.Item.OpId, model.Fingerprint, "read_describe");
            result["items"] = items;
            result["total"] = matched.Count;
            result["nextCursor"] = nextCursor;
            result["truncated"] = nextCursor != null;
            List<object> queryDrilldown = new List<object>
            {
                ToolResultHelper.Drilldown("read_describe", new Dictionary<string, object> { { "handle", items.Count > 0 ? JsonCodec.GetStr((Dictionary<string, object>)items[0], "handle", "0") : "0" } }, "看第一个命中对象的详情"),
                ToolResultHelper.Drilldown("read_anomalies", new Dictionary<string, object> { { "limit", 30 } }, "核对命中范围是否存在异常对象")
            };
            // D5：truncated 且无游标时必须仍有可执行建议
            result["drilldown"] = ToolResultHelper.EnsureDrilldown(queryDrilldown,
                nextCursor != null, nextCursor != null);
            return result;
        }

        private static string DescribeQueryFilter(string layer, string dxfName, string meaning, string text)
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(layer))
            {
                parts.Add("图层≈" + layer);
            }
            if (!string.IsNullOrEmpty(dxfName))
            {
                parts.Add("类型=" + dxfName);
            }
            if (!string.IsNullOrEmpty(meaning))
            {
                parts.Add("含义≈" + meaning);
            }
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add("文字包含（不回显）");
            }
            return parts.Count == 0 ? "（无条件）" : "（" + string.Join("，", parts.ToArray()) + "）";
        }

        private static bool Intersects(ObjectRef obj, double[] extent)
        {
            if (!obj.HasExtents)
            {
                return false;
            }
            return !(obj.Max[0] < extent[0] || obj.Min[0] > extent[2] || obj.Max[1] < extent[1] || obj.Min[1] > extent[3]);
        }

        private static bool MatchesText(ObjectRef obj, string needle)
        {
            if (obj.Texts != null)
            {
                for (int i = 0; i < obj.Texts.Count; i++)
                {
                    if (obj.Texts[i] != null && obj.Texts[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            foreach (KeyValuePair<string, string> pair in obj.Attributes)
            {
                if (pair.Value != null && pair.Value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MatchesAttributes(ObjectRef obj, Dictionary<string, string> expected)
        {
            foreach (KeyValuePair<string, string> pair in expected)
            {
                string actual;
                if (!obj.Attributes.TryGetValue(pair.Key, out actual))
                {
                    return false;
                }
                if (!string.Equals(actual, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------- read_describe ----------------

        private static Dictionary<string, object> Describe(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;

            string handle = (JsonCodec.GetStr(context.Item.Params, "handle") ?? string.Empty).ToUpperInvariant();
            string requestedFingerprint = JsonCodec.GetStr(context.Item.Params, "fingerprint");
            if (!string.IsNullOrEmpty(requestedFingerprint) &&
                !string.Equals(requestedFingerprint, model.Fingerprint, StringComparison.Ordinal))
            {
                context.AddWarning(ErrorCodes.W1004, "expected=" + requestedFingerprint + ";actual=" + model.Fingerprint);
            }

            ObjectRef obj = model.FindByHandle(handle);
            if (obj == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                    "object_not_found:handle=" + handle, "params.handle"));
            }

            Dictionary<string, object> objectDict = new Dictionary<string, object>(StringComparer.Ordinal);
            objectDict["handle"] = obj.Handle;
            objectDict["dxfName"] = obj.DxfName;
            objectDict["type"] = obj.DotNetType;
            objectDict["layer"] = obj.Layer;
            objectDict["linetype"] = obj.Linetype;
            objectDict["colorIndex"] = obj.ColorIndex >= 0 ? (object)obj.ColorIndex : null;
            objectDict["visible"] = obj.Visible;
            objectDict["extents"] = obj.ExtentsDict();

            Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in obj.Geometry)
            {
                geometry[pair.Key] = pair.Value;
            }
            objectDict["geometry"] = geometry;

            Dictionary<string, object> props = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(obj.BlockName))
            {
                props["blockName"] = obj.BlockName;
                props["isXref"] = obj.IsFromExternalReference;
            }
            if (obj.IsProxy)
            {
                props["isProxy"] = true;
            }
            if (obj.IsCivilNative)
            {
                // Civil 3D 原生对象：类型名回给调用方（曲面/路线/纵断面/管网/点编组）
                props["civilKind"] = obj.CivilKind;
                props["civilNative"] = true;
            }
            if (obj.Attributes.Count > 0)
            {
                foreach (KeyValuePair<string, string> pair in obj.Attributes)
                {
                    props["attr." + pair.Key] = pair.Value;
                }
            }
            objectDict["props"] = props;

            List<object> texts = new List<object>(obj.Texts.Count);
            for (int i = 0; i < obj.Texts.Count && i < 5; i++)
            {
                texts.Add(Redactor.Text(obj.Texts[i], 80));
            }
            objectDict["texts"] = texts;

            List<object> basis = new List<object>(obj.BasisList.Count);
            for (int i = 0; i < obj.BasisList.Count; i++)
            {
                basis.Add(obj.BasisList[i].ToDict());
            }

            List<object> relations = new List<object>();
            if (obj.IsBlockReference && !string.IsNullOrEmpty(obj.BlockName))
            {
                Dictionary<string, object> relation = new Dictionary<string, object>(StringComparer.Ordinal);
                relation["kind"] = "references";
                relation["targetHandle"] = obj.Handle;
                relation["basis"] = "block_name=" + obj.BlockName;
                relations.Add(relation);
            }

            List<object> objectAnomalies = new List<object>();
            for (int i = 0; i < model.Anomalies.Count && objectAnomalies.Count < 10; i++)
            {
                if (string.Equals(model.Anomalies[i].Handle, obj.Handle, StringComparison.OrdinalIgnoreCase))
                {
                    objectAnomalies.Add(model.Anomalies[i].ToDict());
                }
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            string meaningText = string.IsNullOrEmpty(obj.Meaning) ? "含义未识别" : "含义「" + obj.Meaning + "」";
            string proxyText = obj.IsProxy ? "｜代理对象，Civil 3D 不可用，内容不可读" : string.Empty;
            string civilText = obj.IsCivilNative ? "｜Civil 3D 原生对象" : string.Empty;
            result["summary"] = ToolResultHelper.Summary("【对象详情】句柄 " + obj.Handle + "｜" + obj.DxfName
                + "（" + obj.DotNetType + "）｜图层 " + obj.Layer + "｜" + meaningText
                + "｜置信度 " + SemanticRecognizer.Percent(obj.Confidence)
                + civilText
                + proxyText
                + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_describe", context.Item.OpId, model.Fingerprint, "read_preview");
            result["object"] = objectDict;
            result["meaning"] = obj.Meaning;
            result["confidence"] = obj.Confidence;
            result["confidenceBand"] = obj.Band;
            result["basis"] = basis;
            result["relations"] = relations;
            result["anomalies"] = objectAnomalies;
            return result;
        }

        // ---------------- read_anomalies ----------------

        private static Dictionary<string, object> Anomalies(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(false);
            context.Fingerprint = model.Fingerprint;

            Dictionary<string, object> parameters = context.Item.Params;
            List<string> types = JsonCodec.GetStringList(parameters, "types");
            string layerPattern = JsonCodec.GetStr(parameters, "layer");
            string severity = JsonCodec.GetStr(parameters, "severity");
            int limit = JsonCodec.GetInt(parameters, "limit", 30);
            if (limit < 1)
            {
                limit = 1;
            }
            if (limit > 100)
            {
                limit = 100;
            }
            string cursor = JsonCodec.GetStr(parameters, "cursor");
            int skip = 0;
            string cursorValue = CursorCodec.Decode(cursor);
            if (cursorValue != null)
            {
                int parsed;
                if (int.TryParse(cursorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    skip = parsed;
                }
            }

            List<AnomalyItem> filtered = new List<AnomalyItem>();
            for (int i = 0; i < model.Anomalies.Count; i++)
            {
                AnomalyItem item = model.Anomalies[i];
                if (types != null && types.Count > 0 && !Contains(types, item.Code))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(severity) && !string.Equals(severity, item.Severity, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(layerPattern) && (item.Layer == null || !LayerMapping.WildcardMatch(layerPattern, item.Layer)))
                {
                    continue;
                }
                filtered.Add(item);
            }

            List<object> items = new List<object>();
            int end = Math.Min(filtered.Count, skip + limit);
            for (int i = skip; i < end; i++)
            {
                items.Add(filtered[i].ToDict());
            }
            string nextCursor = end < filtered.Count ? CursorCodec.Encode(end.ToString(CultureInfo.InvariantCulture)) : null;

            Dictionary<string, int> counts = AnomalyScanner.CountByCode(model.Anomalies);
            Dictionary<string, object> countsDict = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in counts)
            {
                countsDict[pair.Key] = pair.Value;
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【异常清单】共 " + filtered.Count + " 项，返回 " + items.Count
                + " 项｜" + FormatCounts(counts)
                + (model.ProxyCount > 0
                    ? "｜" + model.ProxyCount + " 个代理对象需人工确认（"
                        + (model.Civil != null && model.Civil.Detected ? "第三方对象" : "Civil 3D 不可用") + "）"
                    : string.Empty)
                + "｜这些是读到但不敢当真的地方，请勿自行修正");
            result["resultRef"] = ToolResultHelper.ResultRef("read_anomalies", context.Item.OpId, model.Fingerprint, "read_describe");
            result["items"] = items;
            result["counts"] = countsDict;
            result["nextCursor"] = nextCursor;
            result["truncated"] = nextCursor != null;
            // D5：以前这里根本没有 drilldown 字段；被截断时客户端拿不到任何下钻建议。
            List<object> anomalyDrilldown = new List<object>();
            anomalyDrilldown.Add(ToolResultHelper.Drilldown("read_anomalies",
                new Dictionary<string, object> { { "severity", "error" }, { "limit", 30 } },
                "先只看 error 级异常，缩小范围"));
            result["drilldown"] = ToolResultHelper.EnsureDrilldown(anomalyDrilldown,
                nextCursor != null, nextCursor != null);
            return result;
        }

        private static bool Contains(List<string> values, string target)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], target, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string FormatCounts(Dictionary<string, int> counts)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                parts.Add(AnomalyDisplayName(pair.Key) + " " + pair.Value);
            }
            return parts.Count == 0 ? "无异常" : string.Join("、", parts.ToArray());
        }

        // ---------------- read_preview ----------------

        private static Dictionary<string, object> Preview(ToolContext context)
        {
            SemanticModel model = context.EnsureModel(true);
            context.Fingerprint = model.Fingerprint;

            Dictionary<string, object> parameters = context.Item.Params;
            double[] extent = null;
            object extentRaw;
            if (parameters.TryGetValue("extent", out extentRaw) && extentRaw != null)
            {
                string asText = extentRaw as string;
                if (asText == null)
                {
                    extent = ToolResultHelper.NumberArray(parameters, "extent", 4);
                }
            }
            int width = JsonCodec.GetInt(parameters, "width", 1200);
            int height = JsonCodec.GetInt(parameters, "height", 800);
            List<string> highlights = JsonCodec.GetStringList(parameters, "highlightHandles");

            PreviewResult preview = context.Runtime.Renderer.Render(model, extent, width, height, highlights, context.Warnings);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【预览图】结构示意图 " + preview.Width + "×" + preview.Height
                + "｜" + ToolResultHelper.Number(preview.Bytes / 1024D) + " KB"
                + "｜范围 " + ToolResultHelper.Number(preview.Max[0] - preview.Min[0]) + "×"
                + ToolResultHelper.Number(preview.Max[1] - preview.Min[1])
                + " " + model.UnitCode
                + "｜这是结构示意图、非出图成果，不可用于施工或校审"
                + (preview.Oversize ? "｜图片超 1 MB，仅返回路径" : string.Empty)
                + "｜指纹 " + model.Fingerprint);
            result["resultRef"] = ToolResultHelper.ResultRef("read_preview", context.Item.OpId, model.Fingerprint, "read_query");
            result["previewPath"] = preview.PreviewPath;
            result["width"] = preview.Width;
            result["height"] = preview.Height;
            result["bytes"] = preview.Bytes;
            result["extents"] = ToolResultHelper.Extents(preview.Min, preview.Max);

            List<object> legend = new List<object>(preview.Legend.Count);
            for (int i = 0; i < preview.Legend.Count; i++)
            {
                legend.Add(preview.Legend[i].ToDict());
            }
            result["legend"] = legend;
            result["note"] = "结构示意图，非出图成果";
            result["oversize"] = preview.Oversize;
            result["truncated"] = false;

            // NIT-7：其余四个读工具都有 drilldown，唯独预览没有 → 模型拿到图却没有下一步动作。
            // 预览是链路终点，但「终点」不等于「无建议」：要么去看对象细节，要么换范围/分辨率重画。
            List<object> previewDrilldown = new List<object>(2);
            if (highlights != null && highlights.Count > 0)
            {
                previewDrilldown.Add(ToolResultHelper.Drilldown("read_describe",
                    new Dictionary<string, object>(StringComparer.Ordinal) { { "handle", highlights[0] } },
                    "看该高亮对象的完整属性（预览只表达几何位置，不含语义细节）"));
            }
            else
            {
                previewDrilldown.Add(ToolResultHelper.Drilldown("read_query",
                    new Dictionary<string, object>(StringComparer.Ordinal) { { "limit", 20 } },
                    "先取候选对象，再把 handle 放进 highlightHandles 重画，可在图上突出显示"));
            }
            Dictionary<string, object> repaint = new Dictionary<string, object>(StringComparer.Ordinal);
            repaint["width"] = width;
            repaint["height"] = height;
            if (extent != null)
            {
                repaint["extent"] = new List<object> { extent[0], extent[1], extent[2], extent[3] };
            }
            previewDrilldown.Add(ToolResultHelper.Drilldown("read_preview", repaint,
                "换范围/分辨率重画（图纸很大时先收窄 extent，细节才看得清）"));
            result["drilldown"] = previewDrilldown;

            return result;
        }

        // ---------------- read_active_selection (v3.0 Phase 1) ----------------

        private static Dictionary<string, object> ActiveSelection(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_open_document"));
            }

            int limit = JsonCodec.GetInt(context.Item.Params, "limit", 200);
            if (limit < 1) limit = 1;
            if (limit > 2000) limit = 2000;

            List<ObjectId> ids = context.Runtime.Selection.ReadLiveSelectionIds(context.Doc);
            List<object> items = new List<object>();

            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < ids.Count && items.Count < limit; i++)
                {
                    DBObject obj;
                    try
                    {
                        obj = tr.GetObject(ids[i], OpenMode.ForRead, false);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (obj == null) continue;

                    Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                    item["handle"] = obj.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                    item["dxfName"] = obj.GetRXClass().DxfName;
                    item["typeName"] = obj.GetType().Name;

                    Entity ent = obj as Entity;
                    if (ent != null)
                    {
                        item["layer"] = ent.Layer;
                        try
                        {
                            Extents3d ext = ent.GeometricExtents;
                            item["extent"] = new List<object>
                            {
                                ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z,
                                ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z
                            };
                        }
                        catch (Exception)
                        {
                            item["extent"] = null;
                        }
                    }

                    // Civil 3D 对象名称（通过反射无强依赖读取）
                    try
                    {
                        PropertyInfo nameProp = obj.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProp != null && nameProp.CanRead && nameProp.GetIndexParameters().Length == 0)
                        {
                            object nv = nameProp.GetValue(obj, null);
                            if (nv != null) item["name"] = Convert.ToString(nv, CultureInfo.InvariantCulture);
                        }
                    }
                    catch (Exception) { }

                    items.Add(item);
                }
                tr.Commit();
            }

            DateTime changed = context.Runtime.Selection.LastChangedUtc;
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["count"] = ids.Count;
            result["returned"] = items.Count;
            result["truncated"] = ids.Count > items.Count;
            result["lastChangedUtc"] = changed == DateTime.MinValue ? null : TimeUtil.ToIso(changed);
            result["items"] = items;
            result["summary"] = ToolResultHelper.Summary("【实时选区】当前选中 " + ids.Count + " 个实体，返回 " + items.Count + " 条。");
            return result;
        }

        // ---------------- read_viewport_snapshot (v3.0 Phase 1) ----------------

        private static Dictionary<string, object> ViewportSnapshot(ToolContext context)
        {
            if (context.Doc == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_open_document"));
            }

            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            int width = JsonCodec.GetInt(p, "width", 1280);
            int height = JsonCodec.GetInt(p, "height", 720);
            if (width < 200) width = 200;
            if (width > 2560) width = 2560;
            if (height < 200) height = 200;
            if (height > 1600) height = 1600;

            bool includeBase64 = JsonCodec.GetBool(p, "includeBase64", false);
            string outPath = JsonCodec.GetStr(p, "outPath");
            if (string.IsNullOrEmpty(outPath))
            {
                outPath = Path.Combine(ExportToolHandlers.DefaultWorkspaceDir(),
                    "viewport_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png");
            }
            string outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            Stopwatch sw = Stopwatch.StartNew();
            string mode = "capturePreviewImage";
            bool captured = false;

            try
            {
                using (Bitmap bmp = context.Doc.CapturePreviewImage((uint)width, (uint)height))
                {
                    if (bmp != null)
                    {
                        bmp.Save(outPath, ImageFormat.Png);
                        width = bmp.Width;
                        height = bmp.Height;
                        captured = true;
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Warn("preview", "capture_preview_image_fallback", "detail", ex.Message);
            }

            if (!captured)
            {
                // 无头控制台（accoreconsole）或无图形视口时优雅降级为语义渲染器
                mode = "schematic_fallback";
                SemanticModel model = context.EnsureModel(false);
                PreviewResult fallback = context.Runtime.Renderer.Render(model, null, width, height, null, context.Warnings);
                if (!string.IsNullOrEmpty(fallback.PreviewPath) && File.Exists(fallback.PreviewPath))
                {
                    File.Copy(fallback.PreviewPath, outPath, true);
                    width = fallback.Width;
                    height = fallback.Height;
                    captured = true;
                }
            }

            sw.Stop();
            if (!captured || !File.Exists(outPath))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E3002, "viewport_snapshot_failed"));
            }

            byte[] bytes = File.ReadAllBytes(outPath);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["outPath"] = outPath;
            result["width"] = width;
            result["height"] = height;
            result["bytes"] = bytes.Length;
            result["mode"] = mode;
            result["elapsedMs"] = sw.ElapsedMilliseconds;
            if (includeBase64 && bytes.Length <= 1048576)
            {
                result["base64Png"] = Convert.ToBase64String(bytes);
            }
            result["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【视口截图】{0}×{1}（{2:0.#} KB，模式 {3}，耗时 {4}ms）→ {5}",
                width, height, bytes.Length / 1024.0, mode, sw.ElapsedMilliseconds, outPath));
            return result;
        }

        // ---------------- read_object_xdata (v3.0 Phase 1) ----------------

        private static Dictionary<string, object> ObjectXData(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_open_document"));
            }

            string handleHex = (JsonCodec.GetStr(context.Item.Params, "handle") ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(handleHex))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "handle_required", "handle"));
            }

            long handleVal;
            if (!long.TryParse(handleHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handleVal))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "invalid_hex_handle:" + handleHex, "handle"));
            }

            ObjectId id;
            try
            {
                id = context.Db.GetObjectId(false, new Handle(handleVal), 0);
            }
            catch (Exception)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "handle_not_found:" + handleHex, "handle"));
            }

            Dictionary<string, object> xdataByApp = new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, object> xrecords = new Dictionary<string, object>(StringComparer.Ordinal);
            string dxfName = null;

            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                if (obj == null)
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "object_not_found:" + handleHex, "handle"));
                }
                dxfName = obj.GetRXClass().DxfName;

                // 1) XData (ResultBuffer)
                ResultBuffer rb = obj.XData;
                if (rb != null)
                {
                    using (rb)
                    {
                        string curApp = "_default";
                        List<object> curList = null;
                        foreach (TypedValue tv in rb)
                        {
                            if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                            {
                                curApp = Convert.ToString(tv.Value, CultureInfo.InvariantCulture) ?? "_default";
                                curList = new List<object>();
                                xdataByApp[curApp] = curList;
                                continue;
                            }
                            if (curList == null)
                            {
                                curList = new List<object>();
                                xdataByApp[curApp] = curList;
                            }
                            Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
                            entry["code"] = (int)tv.TypeCode;
                            entry["value"] = ScalarOrText(tv.Value);
                            curList.Add(entry);
                        }
                    }
                }

                // 2) ExtensionDictionary Xrecords
                if (!obj.ExtensionDictionary.IsNull)
                {
                    DBDictionary extDict = tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                    if (extDict != null)
                    {
                        foreach (DBDictionaryEntry de in extDict)
                        {
                            Xrecord xr = tr.GetObject(de.Value, OpenMode.ForRead, false) as Xrecord;
                            if (xr == null || xr.Data == null) continue;
                            List<object> vals = new List<object>();
                            using (ResultBuffer xrb = xr.Data)
                            {
                                foreach (TypedValue tv in xrb)
                                {
                                    Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
                                    entry["code"] = (int)tv.TypeCode;
                                    entry["value"] = ScalarOrText(tv.Value);
                                    vals.Add(entry);
                                }
                            }
                            xrecords[de.Key] = vals;
                        }
                    }
                }

                tr.Commit();
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["handle"] = handleHex.ToUpperInvariant();
            result["dxfName"] = dxfName;
            result["xdata"] = xdataByApp;
            result["xrecords"] = xrecords;
            result["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【对象元数据】句柄 {0}（{1}）｜XData 应用 {2} 组｜XRecord {3} 条。",
                handleHex.ToUpperInvariant(), dxfName ?? "-", xdataByApp.Count, xrecords.Count));
            return result;
        }
    }
}
