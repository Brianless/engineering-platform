using System;
using System.Collections.Generic;
using System.Globalization;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 异常扫描（设计 §3.5）。
    /// 这些是「读到了但不敢当真」的地方，必须如实报告，绝不自行修正。
    /// 类型：duplicate_point / zero_length / extreme_coord / degenerate_extents /
    ///       proxy_object / missing_unit / empty_attr / extremely_long_text。
    /// </summary>
    internal static class AnomalyScanner
    {
        private const int MaxItems = 2000;
        private const int MaxDuplicateGroups = 100;
        private const double ZeroLengthThreshold = 1e-6;
        private const double ExtremeCoordinate = 1e9;
        private const int LongTextThreshold = 500;

        public static List<AnomalyItem> Scan(SemanticModel model)
        {
            List<AnomalyItem> anomalies = new List<AnomalyItem>();
            if (model == null)
            {
                return anomalies;
            }

            // 单位缺失（全局一条）
            if (model.InsUnits == 0)
            {
                AnomalyItem item = new AnomalyItem();
                item.Code = "missing_unit";
                item.Severity = "warn";
                item.Handle = null;
                item.Layer = null;
                item.Message = "图纸未设置 INSUNITS（=0），长度单位不可推断；所有长度按图纸单位原样返回";
                item.Evidence["insUnits"] = 0;
                anomalies.Add(item);
            }

            Dictionary<string, List<ObjectRef>> pointGroups = new Dictionary<string, List<ObjectRef>>(StringComparer.Ordinal);
            int duplicateGroups = 0;

            for (int i = 0; i < model.Entities.Count && anomalies.Count < MaxItems; i++)
            {
                ObjectRef obj = model.Entities[i];
                if (obj == null)
                {
                    continue;
                }

                // 代理对象（Civil 3D 未安装时最典型的缺口）
                if (obj.IsProxy)
                {
                    AnomalyItem item = new AnomalyItem();
                    item.Code = "proxy_object";
                    item.Severity = "warn";
                    item.Handle = obj.Handle;
                    item.Layer = obj.Layer;
                    item.Message = "代理对象（" + obj.DxfName + "）：Civil 3D 未安装，无法识别为原生专业对象，置信度 0";
                    item.Evidence["dxfName"] = obj.DxfName;
                    item.Evidence["dotNetType"] = obj.DotNetType;
                    anomalies.Add(item);
                }

                // 零长度
                if (IsLengthBearing(obj.DxfName) && obj.Length >= 0D && obj.Length < ZeroLengthThreshold)
                {
                    AnomalyItem item = new AnomalyItem();
                    item.Code = "zero_length";
                    item.Severity = "warn";
                    item.Handle = obj.Handle;
                    item.Layer = obj.Layer;
                    item.Message = "零长度几何（" + obj.DxfName + "）：长度 < 1e-6，可能是残留图元";
                    item.Evidence["length"] = obj.Length;
                    anomalies.Add(item);
                }

                // 极值坐标
                if (obj.HasExtents &&
                    (Math.Abs(obj.Min[0]) > ExtremeCoordinate || Math.Abs(obj.Min[1]) > ExtremeCoordinate
                     || Math.Abs(obj.Max[0]) > ExtremeCoordinate || Math.Abs(obj.Max[1]) > ExtremeCoordinate))
                {
                    AnomalyItem item = new AnomalyItem();
                    item.Code = "extreme_coord";
                    item.Severity = "error";
                    item.Handle = obj.Handle;
                    item.Layer = obj.Layer;
                    item.Message = "坐标绝对值超过 1e9，可能是错误基准点或误输入";
                    item.Evidence["minX"] = obj.Min[0];
                    item.Evidence["minY"] = obj.Min[1];
                    item.Evidence["maxX"] = obj.Max[0];
                    item.Evidence["maxY"] = obj.Max[1];
                    anomalies.Add(item);
                }

                // 退化包围盒（注记类允许没有包围盒）
                if (!obj.HasExtents && !SemanticRecognizer.IsAnnotation(obj) && !obj.IsProxy)
                {
                    AnomalyItem item = new AnomalyItem();
                    item.Code = "degenerate_extents";
                    item.Severity = "info";
                    item.Handle = obj.Handle;
                    item.Layer = obj.Layer;
                    item.Message = "无法取得包围盒（" + obj.DxfName + "）：可能是自定义对象或空几何";
                    item.Evidence["dxfName"] = obj.DxfName;
                    anomalies.Add(item);
                }

                // 空属性
                if (obj.Attributes != null && obj.Attributes.Count > 0)
                {
                    foreach (KeyValuePair<string, string> pair in obj.Attributes)
                    {
                        if (string.IsNullOrEmpty(pair.Value))
                        {
                            AnomalyItem item = new AnomalyItem();
                            item.Code = "empty_attr";
                            item.Severity = "info";
                            item.Handle = obj.Handle;
                            item.Layer = obj.Layer;
                            item.Message = "块属性为空：" + pair.Key;
                            item.Evidence["tag"] = pair.Key;
                            anomalies.Add(item);
                            break;
                        }
                    }
                }

                // 超长文字（只计数，不回显内容）
                if (obj.RawTextLength > LongTextThreshold)
                {
                    AnomalyItem item = new AnomalyItem();
                    item.Code = "extremely_long_text";
                    item.Severity = "warn";
                    item.Handle = obj.Handle;
                    item.Layer = obj.Layer;
                    item.Message = "文字长度 " + obj.RawTextLength + " 超过 500 字符；按涉密处理，不回显内容";
                    item.Evidence["length"] = obj.RawTextLength;
                    anomalies.Add(item);
                }

                // 重复点（同坐标点 > 1）
                CollectPoint(obj, pointGroups);
            }

            // 重复点汇总
            foreach (KeyValuePair<string, List<ObjectRef>> pair in pointGroups)
            {
                if (duplicateGroups >= MaxDuplicateGroups)
                {
                    break;
                }
                if (pair.Value.Count < 2)
                {
                    continue;
                }
                duplicateGroups++;
                ObjectRef first = pair.Value[0];
                AnomalyItem item = new AnomalyItem();
                item.Code = "duplicate_point";
                item.Severity = "info";
                item.Handle = first.Handle;
                item.Layer = first.Layer;
                item.Message = "同一坐标存在 " + pair.Value.Count + " 个点对象（重复点）";
                item.Evidence["count"] = pair.Value.Count;
                if (first.Geometry.ContainsKey("position"))
                {
                    item.Evidence["position"] = first.Geometry["position"];
                }
                item.Evidence["handles"] = HandlesOf(pair.Value, 5);
                anomalies.Add(item);
            }

            if (anomalies.Count > MaxItems)
            {
                anomalies.RemoveRange(MaxItems, anomalies.Count - MaxItems);
            }

            model.Anomalies = anomalies;
            HostLog.Info("semantic", "anomalies_scanned", "count", anomalies.Count,
                "proxy", model.ProxyCount, "insUnits", model.InsUnits);
            return anomalies;
        }

        private static bool IsLengthBearing(string dxfName)
        {
            if (string.IsNullOrEmpty(dxfName))
            {
                return false;
            }
            return string.Equals(dxfName, "LINE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dxfName, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dxfName, "POLYLINE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dxfName, "ARC", StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectPoint(ObjectRef obj, Dictionary<string, List<ObjectRef>> groups)
        {
            if (!string.Equals(obj.DxfName, "POINT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            object raw;
            if (!obj.Geometry.TryGetValue("position", out raw))
            {
                return;
            }
            List<object> position = EngineeringPlatform.Host.Protocol.JsonCodec.AsList(raw);
            if (position == null || position.Count < 2)
            {
                return;
            }
            double x = ToDouble(position[0]);
            double y = ToDouble(position[1]);

            // 按 0.001 精度归一（避免浮点尾差把同一点判成两点）
            string key = Math.Round(x, 3).ToString("0.###", CultureInfo.InvariantCulture) + ","
                + Math.Round(y, 3).ToString("0.###", CultureInfo.InvariantCulture);

            List<ObjectRef> list;
            if (!groups.TryGetValue(key, out list))
            {
                list = new List<ObjectRef>();
                groups[key] = list;
            }
            list.Add(obj);
        }

        private static List<object> HandlesOf(List<ObjectRef> objects, int limit)
        {
            List<object> handles = new List<object>();
            for (int i = 0; i < objects.Count && i < limit; i++)
            {
                handles.Add(objects[i].Handle);
            }
            return handles;
        }

        private static double ToDouble(object value)
        {
            if (value is double)
            {
                return (double)value;
            }
            if (value is int)
            {
                return (int)value;
            }
            if (value is long)
            {
                return (long)value;
            }
            if (value is decimal)
            {
                return (double)(decimal)value;
            }
            double parsed;
            if (value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return 0D;
        }

        /// <summary>按严重度排序（error → warn → info），用于 topAnomalies。</summary>
        public static List<AnomalyItem> TopBySeverity(List<AnomalyItem> anomalies, int limit)
        {
            List<AnomalyItem> sorted = new List<AnomalyItem>();
            if (anomalies == null)
            {
                return sorted;
            }
            string[] order = { "error", "warn", "info" };
            for (int i = 0; i < order.Length && sorted.Count < limit; i++)
            {
                for (int j = 0; j < anomalies.Count && sorted.Count < limit; j++)
                {
                    if (string.Equals(anomalies[j].Severity, order[i], StringComparison.Ordinal))
                    {
                        sorted.Add(anomalies[j]);
                    }
                }
            }
            return sorted;
        }

        /// <summary>按 code 汇总计数（read_anomalies.counts）。</summary>
        public static Dictionary<string, int> CountByCode(List<AnomalyItem> anomalies)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (anomalies == null)
            {
                return counts;
            }
            for (int i = 0; i < anomalies.Count; i++)
            {
                int value;
                counts.TryGetValue(anomalies[i].Code, out value);
                counts[anomalies[i].Code] = value + 1;
            }
            return counts;
        }
    }
}
