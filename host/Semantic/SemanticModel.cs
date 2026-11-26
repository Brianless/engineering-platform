using System;
using System.Collections.Generic;
using System.Text;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>识别依据（方案 15.3：每个识别结果都必须带依据，没有依据视为缺陷）。</summary>
    internal sealed class Basis
    {
        public string Kind = "inference"; // civil_native | layer_map | block_map | geometry_rule | text_rule | inference
        public string Ref = string.Empty;
        public string Pattern;

        public Basis()
        {
        }

        public Basis(string kind, string reference, string pattern = null)
        {
            Kind = kind;
            Ref = reference;
            Pattern = pattern;
        }

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["kind"] = Kind;
            dict["ref"] = Ref;
            if (!string.IsNullOrEmpty(Pattern))
            {
                dict["pattern"] = Pattern;
            }
            return dict;
        }
    }

    /// <summary>置信度分档：>=0.9 auto；0.6–0.9 confirm；<0.6 hint（方案 15.3）。</summary>
    internal static class ConfidenceBand
    {
        public const string Auto = "auto";
        public const string Confirm = "confirm";
        public const string Hint = "hint";

        public static string Of(double confidence)
        {
            if (confidence >= 0.9)
            {
                return Auto;
            }
            if (confidence >= 0.6)
            {
                return Confirm;
            }
            return Hint;
        }

        public static double FromPercent(int percent)
        {
            if (percent < 0)
            {
                percent = 0;
            }
            if (percent > 100)
            {
                percent = 100;
            }
            return percent / 100D;
        }
    }

    /// <summary>对象引用（read_query / read_describe / 预览图共用的轻量结构）。</summary>
    internal sealed class ObjectRef
    {
        public string Handle = string.Empty;
        public string DxfName = "UNKNOWN";
        public string DotNetType = "Unknown";
        public string Layer = "0";
        public string Linetype;
        public int ColorIndex = -1;
        public bool Visible = true;

        public bool HasExtents;
        public double[] Min = new double[3];
        public double[] Max = new double[3];

        public string Meaning;
        public double Confidence;
        public string Band = ConfidenceBand.Hint;
        public List<Basis> BasisList = new List<Basis>();

        public List<string> Texts = new List<string>();
        public Dictionary<string, object> Geometry = new Dictionary<string, object>(StringComparer.Ordinal);
        public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool IsProxy;
        public bool IsBlockReference;
        public string BlockName;
        public bool IsFromExternalReference;

        /// <summary>
        /// Civil 3D 原生对象类型中文名（曲面/路线/纵断面/管网/点编组）。
        /// 非空即代表这是 Civil 原生对象，识别时 confidence 直接取 1.0（方案 15.3）。
        /// </summary>
        public string CivilKind;

        /// <summary>是否为 Civil 3D 原生对象。</summary>
        public bool IsCivilNative
        {
            get { return !string.IsNullOrEmpty(CivilKind); }
        }

        /// <summary>句柄数值，用于稳定排序与游标分页。</summary>
        public long SortKey;

        /// <summary>线状对象的长度（图纸单位），用于 zero_length 判定。</summary>
        public double Length;

        /// <summary>文字长度（原始字符数，用于 extremely_long_text 判定）。</summary>
        public int RawTextLength;

        /// <summary>对象中心点（快速范围过滤与预览用）。</summary>
        public bool HasCenter;
        public double CenterX;
        public double CenterY;

        public Dictionary<string, object> ExtentsDict()
        {
            if (!HasExtents)
            {
                return null;
            }
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["min"] = new List<object> { Min[0], Min[1], Min[2] };
            dict["max"] = new List<object> { Max[0], Max[1], Max[2] };
            return dict;
        }
    }

    /// <summary>图层汇总。</summary>
    internal sealed class LayerSummary
    {
        public string Name = "0";
        public int EntityCount;
        public Dictionary<string, int> Types = new Dictionary<string, int>(StringComparer.Ordinal);
        public string Meaning;
        public double Confidence;
        public string Band = ConfidenceBand.Hint;
        public List<Basis> BasisList = new List<Basis>();
    }

    /// <summary>异常项。severity 只有 info / warn / error 三档。</summary>
    internal sealed class AnomalyItem
    {
        public string Code = "duplicate_point";
        public string Severity = "warn";
        public string Handle;
        public string Layer;
        public string Message = string.Empty;
        public Dictionary<string, object> Evidence = new Dictionary<string, object>(StringComparer.Ordinal);

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["code"] = Code;
            dict["severity"] = Severity;
            dict["handle"] = Handle;
            dict["layer"] = Layer;
            dict["message"] = Message;
            dict["evidence"] = Evidence;
            return dict;
        }
    }

    /// <summary>专业分布汇总。</summary>
    internal sealed class DisciplineSummary
    {
        public string Name = "未识别";
        public int EntityCount;
        public double Confidence;
    }

    /// <summary>
    /// 语义模型：宿主内存中按指纹缓存 5 s，是本模块唯一的数据源。
    /// 所有 read_* 工具都从本结构裁剪，绝不在 handler 里二次访问 CAD API（除 read_describe 的重解析）。
    /// </summary>
    internal sealed class SemanticModel
    {
        public string Fingerprint = string.Empty;
        public string DrawingBaseName = "unknown.dwg";
        public string NameHash = "unknown";
        public string ModifiedUtcIso;
        public bool ReadOnly;

        public int InsUnits;
        public string UnitCode = "unitless";
        public double ScaleToMeter = 1D;

        public string SoftwareName = "AutoCAD";
        public string SoftwareVersion = "unknown";
        public bool CivilApiAvailable;

        public bool HasExtents;
        public double[] Min = new double[3];
        public double[] Max = new double[3];

        public int EntityCount;
        public int LayerCount;
        public int BlockCount;
        public int BlockReferenceCount;
        public int PaperSpaceCount;
        public int ProxyCount;
        public bool EntityLimitReached;
        public bool Truncated;

        public List<ObjectRef> Entities = new List<ObjectRef>();
        public List<LayerSummary> Layers = new List<LayerSummary>();
        public List<AnomalyItem> Anomalies = new List<AnomalyItem>();
        public List<DisciplineSummary> Disciplines = new List<DisciplineSummary>();

        /// <summary>
        /// Civil 3D 原生对象快照（第二轮新增）。Detected=false 表示本机/本文档取不到 CivilDocument，
        /// 此时走代理对象 + gaps[] 降级路径。
        /// </summary>
        public Civil3dSnapshot Civil = new Civil3dSnapshot();

        public DateTime ExtractedUtc = DateTime.UtcNow;
        public long ExtractElapsedMs;

        /// <summary>按图层名取对象集合（不分配新列表，遍历用）。</summary>
        public IEnumerable<ObjectRef> EntitiesOfLayer(string layer)
        {
            for (int i = 0; i < Entities.Count; i++)
            {
                if (string.Equals(Entities[i].Layer, layer, StringComparison.OrdinalIgnoreCase))
                {
                    yield return Entities[i];
                }
            }
        }

        /// <summary>按句柄查找（句柄为十六进制大写字符串）。</summary>
        public ObjectRef FindByHandle(string handle)
        {
            if (string.IsNullOrEmpty(handle))
            {
                return null;
            }
            long key;
            if (!long.TryParse(handle, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out key))
            {
                return null;
            }
            for (int i = 0; i < Entities.Count; i++)
            {
                if (Entities[i].SortKey == key)
                {
                    return Entities[i];
                }
            }
            return null;
        }

        /// <summary>单位代码映射（INSUNITS → code）。</summary>
        public static string UnitCodeOf(int insUnits)
        {
            switch (insUnits)
            {
                case 1:
                    return "in";
                case 2:
                    return "ft";
                case 4:
                    return "mm";
                case 5:
                    return "cm";
                case 6:
                    return "m";
                default:
                    return "unitless";
            }
        }

        /// <summary>图纸单位 → 米的换算系数；unitless 时为 1.0（并在响应中标注 unitless）。</summary>
        public static double ScaleToMeterOf(int insUnits)
        {
            switch (insUnits)
            {
                case 1:
                    return 0.0254D;
                case 2:
                    return 0.3048D;
                case 4:
                    return 0.001D;
                case 5:
                    return 0.01D;
                case 6:
                    return 1D;
                default:
                    return 1D;
            }
        }
    }

    /// <summary>游标编解码：M0 用 lastHandle 的 base64url（不透明字符串）。</summary>
    internal static class CursorCodec
    {
        public static string Encode(string handle)
        {
            if (string.IsNullOrEmpty(handle))
            {
                return null;
            }
            byte[] bytes = Encoding.UTF8.GetBytes("h:" + handle);
            string base64 = Convert.ToBase64String(bytes);
            return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static string Decode(string cursor)
        {
            if (string.IsNullOrEmpty(cursor))
            {
                return null;
            }
            try
            {
                string padded = cursor.Replace('-', '+').Replace('_', '/');
                switch (padded.Length % 4)
                {
                    case 2:
                        padded += "==";
                        break;
                    case 3:
                        padded += "=";
                        break;
                }
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
                if (text.StartsWith("h:", StringComparison.Ordinal))
                {
                    return text.Substring(2);
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
