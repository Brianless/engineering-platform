using System;
using System.Collections.Generic;
using System.Globalization;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 语义识别（设计 §3.5 / 方案 15.3）。
    /// 依据优先级与基础置信度：
    /// 1) Civil 3D 原生对象 1.00（第二轮：Civil 3D 已安装，按原生对象最高可信读取）
    /// 2) 企业图层映射 0.92（用映射表声明的 confidencePct）
    /// 3) 企业块名映射 0.90
    /// 4) 几何 + 文字特征规则 0.70
    /// 5) 模型推断 0.45（仅线索）
    /// 每个结果都必须带 confidence / confidenceBand / basis；没有 basis 视为缺陷。
    /// </summary>
    internal static class SemanticRecognizer
    {
        private static readonly string[] AnnotationTypes =
        {
            "TEXT", "MTEXT", "DIMENSION", "MULTILEADER", "LEADER", "ATTRIB", "ATTDEF", "MLEADER"
        };

        public static void Recognize(SemanticModel model, LayerMapping mapping)
        {
            if (model == null)
            {
                return;
            }

            for (int i = 0; i < model.Entities.Count; i++)
            {
                ObjectRef obj = model.Entities[i];
                if (obj == null)
                {
                    continue;
                }
                if (obj.IsProxy)
                {
                    // 代理对象：含义未知，置信度恒为 0，必须让模型/用户看到「不知道」
                    obj.Meaning = null;
                    obj.Confidence = 0D;
                    obj.Band = ConfidenceBand.Hint;
                    if (obj.BasisList.Count == 0)
                    {
                        obj.BasisList.Add(new Basis("inference", "proxy_object:unrecognized"));
                    }
                    continue;
                }
                RecognizeEntity(obj, mapping);
            }

            BuildLayerSummaries(model, mapping);
            BuildDisciplines(model, mapping);
        }

        private static void RecognizeEntity(ObjectRef obj, LayerMapping mapping)
        {
            // 1) Civil 3D 原生对象：最高可信（1.0），不允许被图层/块名映射降级
            if (obj.IsCivilNative)
            {
                obj.Meaning = obj.CivilKind;
                obj.Confidence = 1.0D;
                obj.Band = ConfidenceBand.Auto;
                obj.BasisList.Clear();
                obj.BasisList.Add(new Basis("civil_native", "civil3d:handle=" + obj.Handle));
                return;
            }

            // 2) 图层映射
            MappingMatch layerMatch = mapping == null ? null : mapping.MatchLayer(obj.Layer);
            if (layerMatch != null)
            {
                obj.Meaning = layerMatch.Meaning;
                obj.Confidence = layerMatch.Confidence;
                obj.Band = ConfidenceBand.Of(obj.Confidence);
                obj.BasisList.Add(new Basis("layer_map", layerMatch.Reference, layerMatch.Pattern));
                return;
            }

            // 3) 块名映射
            if (obj.IsBlockReference && !string.IsNullOrEmpty(obj.BlockName))
            {
                MappingMatch blockMatch = mapping.MatchBlock(obj.BlockName);
                if (blockMatch != null)
                {
                    obj.Meaning = blockMatch.Meaning;
                    obj.Confidence = blockMatch.Confidence;
                    obj.Band = ConfidenceBand.Of(obj.Confidence);
                    obj.BasisList.Add(new Basis("block_map", blockMatch.Reference, blockMatch.Pattern));
                    return;
                }
            }

            // 4) 几何规则
            if (TryGeometryRule(obj, out string geometryMeaning, out string geometryRef))
            {
                obj.Meaning = geometryMeaning;
                obj.Confidence = 0.70D;
                obj.Band = ConfidenceBand.Confirm;
                obj.BasisList.Add(new Basis("geometry_rule", geometryRef));
                return;
            }

            // 4) 文字规则
            if (TryTextRule(obj, out string textMeaning, out string textRef))
            {
                obj.Meaning = textMeaning;
                obj.Confidence = 0.70D;
                obj.Band = ConfidenceBand.Confirm;
                obj.BasisList.Add(new Basis("text_rule", textRef));
                return;
            }

            // 5) 推断（仅线索）
            obj.Meaning = null;
            obj.Confidence = 0.45D;
            obj.Band = ConfidenceBand.Hint;
            obj.BasisList.Add(new Basis("inference", "unclassified:" + obj.DxfName));
        }

        private static bool TryGeometryRule(ObjectRef obj, out string meaning, out string reference)
        {
            meaning = null;
            reference = null;
            string dxf = obj.DxfName ?? string.Empty;

            if (string.Equals(dxf, "HATCH", StringComparison.OrdinalIgnoreCase))
            {
                meaning = "填充区域";
                reference = "geometry_rule:hatch";
                return true;
            }

            bool closed = false;
            object closedValue;
            if (obj.Geometry.TryGetValue("closed", out closedValue) && closedValue is bool)
            {
                closed = (bool)closedValue;
            }
            if (closed && (string.Equals(dxf, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dxf, "POLYLINE", StringComparison.OrdinalIgnoreCase)))
            {
                meaning = "闭合轮廓（候选边界）";
                reference = "geometry_rule:closed_polyline";
                return true;
            }

            if ((string.Equals(dxf, "CIRCLE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dxf, "ARC", StringComparison.OrdinalIgnoreCase)) && obj.HasExtents)
            {
                meaning = "圆弧图元";
                reference = "geometry_rule:curve";
                return true;
            }

            return false;
        }

        private static bool TryTextRule(ObjectRef obj, out string meaning, out string reference)
        {
            meaning = null;
            reference = null;
            if (obj.Texts == null || obj.Texts.Count == 0)
            {
                return false;
            }

            string text = obj.Texts[0] ?? string.Empty;
            if (text.Length == 0)
            {
                return false;
            }

            string[,] rules =
            {
                { "标高", "标高标注" },
                { "桩号", "桩号标注" },
                { "里程", "里程标注" },
                { "坡度", "坡度标注" },
                { "半径", "半径标注" },
                { "宽度", "宽度标注" },
                { "说明", "说明文字" },
                { "图号", "图号标注" },
                { "比例", "比例标注" }
            };

            for (int i = 0; i < rules.GetLength(0); i++)
            {
                if (text.IndexOf(rules[i, 0], StringComparison.Ordinal) >= 0)
                {
                    meaning = rules[i, 1];
                    reference = "text_rule:keyword=" + rules[i, 0];
                    return true;
                }
            }
            return false;
        }

        private static void BuildLayerSummaries(SemanticModel model, LayerMapping mapping)
        {
            Dictionary<string, LayerSummary> byName = new Dictionary<string, LayerSummary>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, int>> meaningVotes =
                new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> meaningConfidence =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < model.Entities.Count; i++)
            {
                ObjectRef obj = model.Entities[i];
                string layerName = string.IsNullOrEmpty(obj.Layer) ? "0" : obj.Layer;

                LayerSummary summary;
                if (!byName.TryGetValue(layerName, out summary))
                {
                    summary = new LayerSummary();
                    summary.Name = layerName;
                    byName[layerName] = summary;
                    meaningVotes[layerName] = new Dictionary<string, int>(StringComparer.Ordinal);
                }
                summary.EntityCount++;

                int typeCount;
                summary.Types.TryGetValue(obj.DxfName, out typeCount);
                summary.Types[obj.DxfName] = typeCount + 1;

                if (!string.IsNullOrEmpty(obj.Meaning))
                {
                    Dictionary<string, int> votes = meaningVotes[layerName];
                    int vote;
                    votes.TryGetValue(obj.Meaning, out vote);
                    votes[obj.Meaning] = vote + 1;

                    double existing;
                    if (!meaningConfidence.TryGetValue(layerName + "\u0001" + obj.Meaning, out existing) || obj.Confidence > existing)
                    {
                        meaningConfidence[layerName + "\u0001" + obj.Meaning] = obj.Confidence;
                    }
                }
            }

            List<string> names = new List<string>(byName.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            model.Layers = new List<LayerSummary>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                LayerSummary summary = byName[names[i]];

                MappingMatch layerMatch = mapping == null ? null : mapping.MatchLayer(summary.Name);
                if (layerMatch != null)
                {
                    summary.Meaning = layerMatch.Meaning;
                    summary.Confidence = layerMatch.Confidence;
                    summary.Band = ConfidenceBand.Of(summary.Confidence);
                    summary.BasisList.Add(new Basis("layer_map", layerMatch.Reference, layerMatch.Pattern));
                }
                else
                {
                    // 用该图层上出现最多的对象含义做推断（置信度取较低档，标 confirm/hint）
                    string bestMeaning = null;
                    int bestCount = 0;
                    Dictionary<string, int> votes = meaningVotes[summary.Name];
                    foreach (KeyValuePair<string, int> pair in votes)
                    {
                        if (pair.Value > bestCount)
                        {
                            bestCount = pair.Value;
                            bestMeaning = pair.Key;
                        }
                    }

                    if (bestMeaning != null && summary.EntityCount > 0 && bestCount * 2 >= summary.EntityCount)
                    {
                        double confidence;
                        meaningConfidence.TryGetValue(summary.Name + "\u0001" + bestMeaning, out confidence);
                        summary.Meaning = bestMeaning;
                        summary.Confidence = Math.Min(confidence <= 0D ? 0.45D : confidence, 0.85D);
                        summary.Band = ConfidenceBand.Of(summary.Confidence);
                        summary.BasisList.Add(new Basis("inference", "layer_majority:" + summary.Name));
                    }
                    else
                    {
                        summary.Meaning = null;
                        summary.Confidence = 0.30D;
                        summary.Band = ConfidenceBand.Hint;
                        summary.BasisList.Add(new Basis("inference", "layer_unmapped:" + summary.Name));
                    }
                }

                model.Layers.Add(summary);
            }
        }

        private static void BuildDisciplines(SemanticModel model, LayerMapping mapping)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, double> confidenceSum = new Dictionary<string, double>(StringComparer.Ordinal);

            for (int i = 0; i < model.Layers.Count; i++)
            {
                LayerSummary layer = model.Layers[i];
                string category = "unknown";
                MappingMatch match = mapping == null ? null : mapping.MatchLayer(layer.Name);
                if (match != null)
                {
                    category = match.Category;
                }
                else if (!string.IsNullOrEmpty(layer.Meaning))
                {
                    category = CategoryFromMeaning(layer.Meaning);
                }

                int count;
                counts.TryGetValue(category, out count);
                counts[category] = count + layer.EntityCount;

                double sum;
                confidenceSum.TryGetValue(category, out sum);
                confidenceSum[category] = sum + layer.Confidence * layer.EntityCount;
            }

            model.Disciplines = new List<DisciplineSummary>();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                DisciplineSummary summary = new DisciplineSummary();
                summary.Name = DisplayNameOfCategory(pair.Key);
                summary.EntityCount = pair.Value;
                double sum;
                confidenceSum.TryGetValue(pair.Key, out sum);
                summary.Confidence = pair.Value > 0 ? Math.Round(sum / pair.Value, 3) : 0D;
                model.Disciplines.Add(summary);
            }

            model.Disciplines.Sort(delegate(DisciplineSummary a, DisciplineSummary b)
            {
                return b.EntityCount.CompareTo(a.EntityCount);
            });
        }

        private static string CategoryFromMeaning(string meaning)
        {
            if (string.IsNullOrEmpty(meaning))
            {
                return "unknown";
            }
            if (meaning.IndexOf("标高", StringComparison.Ordinal) >= 0
                || meaning.IndexOf("桩号", StringComparison.Ordinal) >= 0
                || meaning.IndexOf("标注", StringComparison.Ordinal) >= 0
                || meaning.IndexOf("说明", StringComparison.Ordinal) >= 0)
            {
                return "annotation";
            }
            if (meaning.IndexOf("等高线", StringComparison.Ordinal) >= 0
                || meaning.IndexOf("地形", StringComparison.Ordinal) >= 0)
            {
                return "terrain";
            }
            if (meaning.IndexOf("边界", StringComparison.Ordinal) >= 0)
            {
                return "site";
            }
            return "unknown";
        }

        private static string DisplayNameOfCategory(string category)
        {
            switch ((category ?? "unknown").ToLowerInvariant())
            {
                case "roadway":
                    return "道路";
                case "site":
                    return "场地";
                case "terrain":
                case "contour":
                    return "场地/地形";
                case "drainage":
                    return "排水";
                case "structure":
                    return "结构";
                case "annotation":
                    return "标注";
                case "frame":
                    return "图框";
                case "hatch":
                    return "填充";
                case "unknown":
                    return "未识别";
                default:
                    return category;
            }
        }

        /// <summary>是否为注记类对象（用于异常判定时容忍缺失包围盒）。</summary>
        public static bool IsAnnotation(ObjectRef obj)
        {
            if (obj == null || string.IsNullOrEmpty(obj.DxfName))
            {
                return false;
            }
            for (int i = 0; i < AnnotationTypes.Length; i++)
            {
                if (string.Equals(AnnotationTypes[i], obj.DxfName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>格式化置信度为百分比字符串（摘要里给人看）。</summary>
        public static string Percent(double confidence)
        {
            return (confidence * 100D).ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }
}
