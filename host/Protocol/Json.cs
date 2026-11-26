using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace EngineeringPlatform.Host.Protocol
{
    /// <summary>
    /// JSON 唯一出入口。
    /// 解析用 System.Web.Extensions 的 <see cref="JavaScriptSerializer"/>（GAC 自带，零额外 DLL）；
    /// 序列化用内部 <see cref="CompactWriter"/>（原生 UTF-8，不做 \uXXXX 转义），原因：
    /// JavaScriptSerializer 会把所有非 ASCII 字符转义成 \uXXXX，中文摘要体积膨胀约 6 倍，
    /// 会把 350 KB 的结果推到 1 MiB 帧上限之外；同时 paramsDigest 要求与 Python 侧
    /// ensure_ascii=False 的字节完全一致。两处都必须由本文件统一实现，其它文件禁止直接引用
    /// JavaScriptSerializer / 自写 JSON。
    /// </summary>
    internal static class JsonCodec
    {
        /// <summary>解析/序列化的最大 JSON 长度（远大于 1 MiB 帧上限，留出封装余量）。</summary>
        public const int MaxJsonLength = 8 * 1024 * 1024;

#if !NET8_0_OR_GREATER
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = MaxJsonLength,
            RecursionLimit = 200
        };
#endif

        /// <summary>解析为 CLR 对象：Dictionary&lt;string,object&gt; / object[] / string / int / double / bool / null。</summary>
        public static object Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text");
            }
#if NET8_0_OR_GREATER
            // .NET 8 没有 System.Web.Extensions；用 System.Text.Json 解析成同一组 CLR 类型：
            //   object → Dictionary<string,object>、array → List<object>、
            //   整数 → int（超 int 范围 → long）、小数 → double、true/false → bool、null → null。
            // **数字语义必须与 JavaScriptSerializer 对齐**：它是 int/decimal/double 三档，
            // 我们的 GetInt/GetDouble 对三者都接受，但 paramsDigest 依赖规范化后的字节一致，
            // 所以这里刻意保持「整数值 → 整型，否则 double」。
            using (JsonDocument document = JsonDocument.Parse(text,
                new JsonDocumentOptions { MaxDepth = 200, AllowTrailingCommas = false }))
            {
                return FromJsonElement(document.RootElement);
            }
#else
            return Serializer.DeserializeObject(text);
#endif
        }

#if NET8_0_OR_GREATER
        /// <summary>.NET 8 下的 JsonElement → CLR 对象（与 JavaScriptSerializer 同型）。</summary>
        private static object FromJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        dict[property.Name] = FromJsonElement(property.Value);
                    }
                    return dict;
                case JsonValueKind.Array:
                    List<object> list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        list.Add(FromJsonElement(item));
                    }
                    return list;
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Number:
                    int intValue;
                    if (element.TryGetInt32(out intValue))
                    {
                        return intValue;
                    }
                    long longValue;
                    if (element.TryGetInt64(out longValue))
                    {
                        return longValue;
                    }
                    return element.GetDouble();
                default:
                    return null;
            }
        }
#endif

        /// <summary>解析并要求顶层是 JSON object；失败返回 false（不抛异常）。</summary>
        public static bool TryParseObject(string text, out Dictionary<string, object> value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            try
            {
                object parsed = Parse(text);
                Dictionary<string, object> dict = parsed as Dictionary<string, object>;
                if (dict == null)
                {
                    return false;
                }
                value = dict;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>序列化为紧凑 JSON（保持键顺序，原生 UTF-8 字符不转义）。</summary>
        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value, false);
            return sb.ToString();
        }

        /// <summary>
        /// 规范化序列化：递归键排序 + 紧凑分隔符 + 原生 UTF-8。
        /// 与 Python 侧 canonical_json 对齐（见 design §2.6 / §7.2）。
        /// </summary>
        public static string SerializeCanonical(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value, true);
            return sb.ToString();
        }

        /// <summary>
        /// 缩进序列化（2 空格）：只用于写给人看的证据文件（自检 JSON、验收记录）。
        /// 线上传输一律用 <see cref="Serialize"/>（紧凑），避免字节膨胀。
        /// 实现：先按紧凑格式生成，再做字符级重排（正确跳过字符串字面量与转义）。
        /// </summary>
        public static string SerializeIndented(object value)
        {
            return Indent(Serialize(value));
        }

        /// <summary>把紧凑 JSON 重排为 2 空格缩进；输入保证由本序列化器产出，故只需处理结构字符。</summary>
        private static string Indent(string compact)
        {
            if (string.IsNullOrEmpty(compact))
            {
                return compact;
            }
            StringBuilder sb = new StringBuilder(compact.Length + compact.Length / 4);
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < compact.Length; i++)
            {
                char c = compact[i];

                if (inString)
                {
                    sb.Append(c);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < compact.Length && (compact[i + 1] == '}' || compact[i + 1] == ']'))
                        {
                            // 空对象/空数组：不换行
                            sb.Append(compact[i + 1]);
                            i++;
                        }
                        else
                        {
                            depth++;
                            sb.Append('\n');
                            sb.Append(' ', depth * 2);
                        }
                        break;
                    case '}':
                    case ']':
                        depth--;
                        sb.Append('\n');
                        sb.Append(' ', depth < 0 ? 0 : depth * 2);
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.Append('\n');
                        sb.Append(' ', depth * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>UTF-8 编码（无 BOM）。</summary>
        public static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text ?? string.Empty);
        }

        /// <summary>UTF-8 解码。</summary>
        public static string FromUtf8(byte[] bytes)
        {
            if (bytes == null)
            {
                return string.Empty;
            }
            return new UTF8Encoding(false).GetString(bytes);
        }

        // ---------------- 安全取值（避免到处做类型判断导致 InvalidCastException） ----------------

        public static Dictionary<string, object> GetDict(Dictionary<string, object> source, string key)
        {
            object value;
            if (source != null && key != null && source.TryGetValue(key, out value))
            {
                return value as Dictionary<string, object>;
            }
            return null;
        }

        public static string GetStr(Dictionary<string, object> source, string key, string fallback = null)
        {
            object value;
            if (source != null && key != null && source.TryGetValue(key, out value) && value != null)
            {
                string s = value as string;
                if (s != null)
                {
                    return s;
                }
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            return fallback;
        }

        public static bool TryGetInt(Dictionary<string, object> source, string key, out int value)
        {
            value = 0;
            object raw;
            if (source == null || key == null || !source.TryGetValue(key, out raw) || raw == null)
            {
                return false;
            }
            if (raw is int)
            {
                value = (int)raw;
                return true;
            }
            if (raw is long)
            {
                long l = (long)raw;
                if (l > int.MaxValue || l < int.MinValue)
                {
                    return false;
                }
                value = (int)l;
                return true;
            }
            if (raw is decimal)
            {
                decimal d = (decimal)raw;
                if (d != decimal.Truncate(d))
                {
                    return false;
                }
                value = (int)d;
                return true;
            }
            if (raw is double)
            {
                double dbl = (double)raw;
                if (Math.Abs(dbl - Math.Round(dbl)) > 1e-9)
                {
                    return false;
                }
                value = (int)Math.Round(dbl);
                return true;
            }
            string s = raw as string;
            int parsed;
            if (s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        public static int GetInt(Dictionary<string, object> source, string key, int fallback)
        {
            int value;
            return TryGetInt(source, key, out value) ? value : fallback;
        }

        public static bool TryGetDouble(Dictionary<string, object> source, string key, out double value)
        {
            value = 0D;
            object raw;
            if (source == null || key == null || !source.TryGetValue(key, out raw) || raw == null)
            {
                return false;
            }
            if (raw is double)
            {
                value = (double)raw;
                return true;
            }
            if (raw is int)
            {
                value = (int)raw;
                return true;
            }
            if (raw is long)
            {
                value = (long)raw;
                return true;
            }
            if (raw is decimal)
            {
                value = (double)(decimal)raw;
                return true;
            }
            string s = raw as string;
            double parsed;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        public static double GetDouble(Dictionary<string, object> source, string key, double fallback)
        {
            double value;
            return TryGetDouble(source, key, out value) ? value : fallback;
        }

        public static bool GetBool(Dictionary<string, object> source, string key, bool fallback)
        {
            object value;
            if (source == null || key == null || !source.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }
            if (value is bool)
            {
                return (bool)value;
            }
            string s = value as string;
            if (s != null)
            {
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return fallback;
        }

        public static List<object> AsList(object value)
        {
            if (value == null)
            {
                return null;
            }
            List<object> list = value as List<object>;
            if (list != null)
            {
                return list;
            }
            object[] array = value as object[];
            if (array != null)
            {
                return new List<object>(array);
            }
            IList ilist = value as IList;
            if (ilist != null && !(value is string))
            {
                List<object> copied = new List<object>(ilist.Count);
                for (int i = 0; i < ilist.Count; i++)
                {
                    copied.Add(ilist[i]);
                }
                return copied;
            }
            return null;
        }

        public static List<object> GetList(Dictionary<string, object> source, string key)
        {
            object value;
            if (source != null && key != null && source.TryGetValue(key, out value) && value != null)
            {
                return AsList(value);
            }
            return null;
        }

        public static List<string> GetStringList(Dictionary<string, object> source, string key)
        {
            List<object> raw = GetList(source, key);
            if (raw == null)
            {
                return null;
            }
            List<string> result = new List<string>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                string s = raw[i] as string;
                if (s != null)
                {
                    result.Add(s);
                }
            }
            return result;
        }

        public static bool HasKey(Dictionary<string, object> source, string key)
        {
            return source != null && key != null && source.ContainsKey(key);
        }

        // ---------------- 写入实现 ----------------

        private static void WriteValue(StringBuilder sb, object value, bool canonical)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            string text = value as string;
            if (text != null)
            {
                WriteString(sb, text);
                return;
            }

            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is DateTime)
            {
                // 设计硬约束：绝不把 DateTime 交给序列化器（会变成 /Date(...)/），一律先转 ISO-8601 字符串。
                WriteString(sb, TimeUtil.ToIso((DateTime)value));
                return;
            }

            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is long)
            {
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is uint || value is ulong)
            {
                sb.Append(Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is float || value is double)
            {
                WriteDouble(sb, Convert.ToDouble(value, CultureInfo.InvariantCulture), canonical);
                return;
            }

            if (value is decimal)
            {
                sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            IDictionary<string, object> typed = value as IDictionary<string, object>;
            if (typed != null)
            {
                WriteObject(sb, typed, canonical);
                return;
            }

            IDictionary untyped = value as IDictionary;
            if (untyped != null)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in untyped)
                {
                    string k = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                    if (k != null)
                    {
                        copy[k] = entry.Value;
                    }
                }
                WriteObject(sb, copy, canonical);
                return;
            }

            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in sequence)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteValue(sb, item, canonical);
                }
                sb.Append(']');
                return;
            }

            // 兜底：未知类型按 ToString 输出（不应出现，出现即是缺陷，但不允许抛异常打断响应）
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object> dict, bool canonical)
        {
            sb.Append('{');
            IEnumerable<string> keys;
            if (canonical)
            {
                List<string> sorted = new List<string>(dict.Keys);
                sorted.Sort(StringComparer.Ordinal);
                keys = sorted;
            }
            else
            {
                keys = dict.Keys;
            }

            bool first = true;
            foreach (string key in keys)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                WriteString(sb, key);
                sb.Append(':');
                object v;
                dict.TryGetValue(key, out v);
                WriteValue(sb, v, canonical);
            }
            sb.Append('}');
        }

        private static void WriteDouble(StringBuilder sb, double value, bool canonical)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                // JSON 不支持 NaN/Infinity；按 null 处理，避免产出非法 JSON
                sb.Append("null");
                return;
            }

            bool integral = Math.Abs(value - Math.Round(value)) < 1e-12 && Math.Abs(value) < 1e15;
            if (integral)
            {
                sb.Append(((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture));
                if (canonical)
                {
                    // Python repr(float) 对整数值浮点输出 "1200.0"，此处对齐，保证 paramsDigest 双端一致。
                    sb.Append(".0");
                }
                return;
            }

            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>时间格式工具：全项目只允许 UTC + ISO-8601 毫秒精度 + Z 结尾。</summary>
    internal static class TimeUtil
    {
        public const string IsoFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>转 UTC ISO-8601 字符串（毫秒精度，Z 结尾）。</summary>
        public static string ToIso(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return utc.ToString(IsoFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>当前 UTC 时间的 ISO-8601 字符串。</summary>
        public static string NowIso()
        {
            return ToIso(DateTime.UtcNow);
        }

        /// <summary>
        /// 解析 ISO-8601 时间；失败返回 DateTime.MinValue。
        /// 用于解析客户端下发的 sinceUtc / 发现文件时间。
        /// </summary>
        public static DateTime ParseIso(string text)
        {
            DateTime parsed;
            if (!string.IsNullOrEmpty(text) &&
                DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
            return DateTime.MinValue;
        }
    }
}
