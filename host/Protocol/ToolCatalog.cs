using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace EngineeringPlatform.Host.Protocol
{
    /// <summary>单个入参问题：字段路径 + 具体原因（转成 E1001 的 error.field / detail）。</summary>
    internal sealed class ParamIssue
    {
        public string Field;
        public string Detail;

        public ParamIssue(string field, string detail)
        {
            Field = field;
            Detail = detail;
        }
    }

    /// <summary>
    /// 入参校验器：模拟 protocol/tools.schema.json 的 inputSchema（宿主侧强制二次校验，
    /// 不只信 MCP）。只实现 M0 用到的 JSON Schema 子集：type / required / enum / minimum /
    /// maximum / maxLength / minItems / maxItems / additionalProperties=false。
    /// </summary>
    internal sealed class ParamValidator
    {
        private readonly Dictionary<string, object> _params;
        private readonly List<ParamIssue> _issues = new List<ParamIssue>();

        public ParamValidator(Dictionary<string, object> parameters)
        {
            _params = parameters ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public List<ParamIssue> Issues
        {
            get { return _issues; }
        }

        public bool HasIssues
        {
            get { return _issues.Count > 0; }
        }

        public void AddIssue(string field, string detail)
        {
            _issues.Add(new ParamIssue(field, detail));
        }

        /// <summary>拒绝未声明的字段（schema 全部是 additionalProperties=false）。</summary>
        public void RejectUnknown(params string[] allowed)
        {
            foreach (string key in _params.Keys)
            {
                bool ok = false;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (string.Equals(allowed[i], key, StringComparison.Ordinal))
                    {
                        ok = true;
                        break;
                    }
                }
                if (!ok)
                {
                    AddIssue("params." + key, "unexpected_field");
                }
            }
        }

        public bool Has(string key)
        {
            return _params.ContainsKey(key) && _params[key] != null;
        }

        public int GetInt(string key, int min, int max, int fallback)
        {
            if (!Has(key))
            {
                return fallback;
            }
            int value;
            if (!JsonCodec.TryGetInt(_params, key, out value))
            {
                AddIssue("params." + key, "must be integer");
                return fallback;
            }
            if (value < min || value > max)
            {
                AddIssue("params." + key, "must be in [" + min + "," + max + "], got " + value);
                return fallback;
            }
            return value;
        }

        public string GetString(string key, int maxLength)
        {
            if (!Has(key))
            {
                return null;
            }
            string value = JsonCodec.GetStr(_params, key);
            if (value == null)
            {
                AddIssue("params." + key, "must be string");
                return null;
            }
            if (value.Length > maxLength)
            {
                AddIssue("params." + key, "maxLength=" + maxLength + ", got " + value.Length);
                return value.Substring(0, maxLength);
            }
            return value;
        }

        public bool GetBool(string key, bool fallback)
        {
            if (!Has(key))
            {
                return fallback;
            }
            object raw;
            if (!_params.TryGetValue(key, out raw) || !(raw is bool))
            {
                AddIssue("params." + key, "must be boolean");
                return fallback;
            }
            return (bool)raw;
        }

        /// <summary>定长数值数组（如 extent=[x0,y0,x1,y1]）。</summary>
        public double[] GetNumberArray(string key, int exactLength)
        {
            if (!Has(key))
            {
                return null;
            }
            List<object> raw = JsonCodec.GetList(_params, key);
            if (raw == null)
            {
                AddIssue("params." + key, "must be array of numbers");
                return null;
            }
            if (raw.Count != exactLength)
            {
                AddIssue("params." + key, "must have exactly " + exactLength + " items, got " + raw.Count);
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
                    AddIssue("params." + key + "[" + i + "]", "must be number");
                    return null;
                }
                values[i] = number;
            }
            return values;
        }

        public List<string> GetStringArray(string key, int maxItems, string[] allowedValues)
        {
            if (!Has(key))
            {
                return null;
            }
            List<object> raw = JsonCodec.GetList(_params, key);
            if (raw == null)
            {
                AddIssue("params." + key, "must be array of strings");
                return null;
            }
            if (raw.Count > maxItems)
            {
                AddIssue("params." + key, "maxItems=" + maxItems + ", got " + raw.Count);
            }
            List<string> result = new List<string>(raw.Count);
            for (int i = 0; i < raw.Count && i < maxItems; i++)
            {
                string s = raw[i] as string;
                if (s == null)
                {
                    AddIssue("params." + key + "[" + i + "]", "must be string");
                    continue;
                }
                if (allowedValues != null && Array.IndexOf(allowedValues, s) < 0)
                {
                    AddIssue("params." + key + "[" + i + "]", "not in enum: " + s);
                    continue;
                }
                result.Add(s);
            }
            return result;
        }

        public Dictionary<string, string> GetStringMap(string key)
        {
            if (!Has(key))
            {
                return null;
            }
            Dictionary<string, object> raw = JsonCodec.GetDict(_params, key);
            if (raw == null)
            {
                AddIssue("params." + key, "must be object of string");
                return null;
            }
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in raw)
            {
                string value = pair.Value as string;
                if (value == null)
                {
                    AddIssue("params." + key + "." + pair.Key, "must be string");
                    continue;
                }
                result[pair.Key] = value;
            }
            return result;
        }

        /// <summary>带枚举的可选字符串（如 severity）。</summary>
        public string GetEnumString(string key, string[] allowedValues)
        {
            string value = GetString(key, 120);
            if (value == null)
            {
                return null;
            }
            if (Array.IndexOf(allowedValues, value) < 0)
            {
                AddIssue("params." + key, "not in enum: " + value);
                return null;
            }
            return value;
        }
    }

    /// <summary>工具描述（与 protocol/tools.schema.json 对齐的 C# 镜像）。</summary>
    internal sealed class ToolDescriptor
    {
        public string Name;
        public string Domain;
        public string Title;
        public string Description;
        public bool ReadOnly = true;
        public bool ControlOnly;
        public bool HasOutputSchema = true;
        public Func<Dictionary<string, object>, List<ParamIssue>> Validate;
    }

    /// <summary>
    /// 只读工具目录（16 个：sys 6 / read 6 / task 4）。
    /// toolCatalogHash = "sha256:" + sha256(protocol/tools.schema.json 文件字节)[:16]，
    /// 该文件以嵌入资源方式打进程序集，保证与 MCP 侧读到的是同一份字节。
    /// </summary>
    internal static class ToolCatalog
    {
        /// <summary>
        /// 写域前缀：命中即 E2003（写通道关闭时）。
        /// `write_` 是 M1 自己的写工具命名空间；`cad_/civil_/dyn_/file_/rev_/p3d_/calc_/skill_`
        /// 是底层方法名的前缀 —— 那两个来源**永远**不该被模型直接调用，保留拦截。
        /// </summary>
        public static readonly string[] WriteDomainPrefixes =
        {
            "write_", "cad_", "civil_", "dyn_", "file_", "rev_", "p3d_", "calc_", "skill_"
        };

        private static readonly List<ToolDescriptor> Descriptors = BuildDescriptors();
        private static readonly Dictionary<string, ToolDescriptor> Index = BuildIndex();
        private static string _catalogHash;

        /// <summary>全部工具（顺序与 tools.schema.json 一致）。</summary>
        public static IList<ToolDescriptor> All
        {
            get { return Descriptors; }
        }

        public static bool Exists(string name)
        {
            return !string.IsNullOrEmpty(name) && Index.ContainsKey(name);
        }

        public static ToolDescriptor Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            ToolDescriptor descriptor;
            return Index.TryGetValue(name, out descriptor) ? descriptor : null;
        }

        /// <summary>工具名是否属于写域（大小写不敏感，仅用于给出 E2003 而不是 E2001）。</summary>
        public static bool IsWriteDomain(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < WriteDomainPrefixes.Length; i++)
            {
                if (lower.StartsWith(WriteDomainPrefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>校验入参；失败返回 E1001（field 指向具体字段）。</summary>
        public static bool TryValidate(string tool, Dictionary<string, object> parameters, out WireError error)
        {
            error = null;
            ToolDescriptor descriptor = Find(tool);
            if (descriptor == null)
            {
                error = WireError.Of(ErrorCodes.E2001, "unknown_tool:" + tool, "tool");
                return false;
            }
            if (descriptor.Validate == null)
            {
                return true;
            }
            List<ParamIssue> issues = descriptor.Validate(parameters ?? new Dictionary<string, object>(StringComparer.Ordinal));
            if (issues == null || issues.Count == 0)
            {
                return true;
            }
            ParamIssue first = issues[0];
            error = WireError.Of(ErrorCodes.E1001, first.Detail, first.Field,
                "参数无效：" + first.Field.Replace("params.", string.Empty));
            return false;
        }

        /// <summary>工具目录哈希（握手交换用）。</summary>
        public static string CatalogHash
        {
            get
            {
                string cached = _catalogHash;
                if (!string.IsNullOrEmpty(cached))
                {
                    return cached;
                }
                string hash;
                try
                {
                    byte[] bytes = ReadEmbeddedToolsSchema();
                    if (bytes != null && bytes.Length > 0)
                    {
                        hash = ParamsDigest.Sha256Hex(bytes).Substring(0, 16);
                    }
                    else
                    {
                        hash = FallbackHash();
                    }
                }
                catch (Exception)
                {
                    hash = FallbackHash();
                }
                _catalogHash = "sha256:" + hash;
                return _catalogHash;
            }
        }

        /// <summary>读嵌入的 tools.schema.json 原始字节（哈希必须基于原始字节）。</summary>
        public static byte[] ReadEmbeddedToolsSchema()
        {
            Assembly assembly = typeof(ToolCatalog).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream("protocol/tools.schema.json"))
            {
                if (stream == null)
                {
                    return null;
                }
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        buffer.Write(chunk, 0, read);
                    }
                    return buffer.ToArray();
                }
            }
        }

        private static string FallbackHash()
        {
            // 嵌入资源缺失时的兜底（不应发生）：用工具名拼接算一个稳定值
            List<string> names = new List<string>();
            for (int i = 0; i < Descriptors.Count; i++)
            {
                names.Add(Descriptors[i].Name);
            }
            return ParamsDigest.Sha256Hex(string.Join(",", names.ToArray())).Substring(0, 16);
        }

        private static Dictionary<string, ToolDescriptor> BuildIndex()
        {
            Dictionary<string, ToolDescriptor> index = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
            for (int i = 0; i < Descriptors.Count; i++)
            {
                index[Descriptors[i].Name] = Descriptors[i];
            }
            return index;
        }

        // ---------------- 目录定义 ----------------

        private static List<ToolDescriptor> BuildDescriptors()
        {
            List<ToolDescriptor> list = new List<ToolDescriptor>();

            list.Add(Tool("sys_ping", "sys", "心跳",
                "探测宿主是否存活。无入参。返回 pong、宿主时间与实例标识；客户端每 5 秒调用一次，连续 3 次超时判定连接断开。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown();
                    return v.Issues;
                }));

            list.Add(Tool("sys_status", "sys", "宿主运行状态",
                "返回宿主运行态：启动时间、近 60 秒请求数与错误数、队列长度、主线程泵送是否正常、语义缓存命中率。无入参。用于判断「慢」是链路还是 CAD。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown();
                    return v.Issues;
                }));

            list.Add(Tool("sys_version", "sys", "版本信息",
                "返回宿主程序集版本、构建时间、AutoCAD 产品与版本、.NET 运行时、协议版本。无入参。排查兼容性问题时优先调用。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown();
                    return v.Issues;
                }));

            list.Add(Tool("sys_health", "sys", "健康检查",
                "逐项检查管道 ACL、实例发现文件、日志目录可写、缓存目录可写、Civil 3D 探测、图层映射表加载。无入参。返回 checks 数组，任一 ok=false 时摘要给出修复建议。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown();
                    return v.Issues;
                }));

            list.Add(Tool("sys_capabilities", "sys", "能力与缺口",
                "返回当前宿主真正可用的能力域、已禁用域、Civil 3D 等缺口清单与本机工具名。无入参。回答「能不能做某件专业操作」之前必须先看这里，不要臆测。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown();
                    return v.Issues;
                }));

            list.Add(Tool("sys_list_instances", "sys", "列出宿主实例",
                "列出本机所有已注册宿主实例（来自实例发现目录），含活性标记。无入参。多开 CAD 时用它确认要连哪一个。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown();
                    return v.Issues;
                }));

            list.Add(Tool("read_overview", "read", "图纸总览",
                "读取当前图纸语义总览：软件版本、单位与坐标系、范围、图元/图层/块数量、专业分布、顶层异常、图纸指纹。无必填入参。这是读图第一步，先调它再决定下钻方向；返回含 summary 与 resultRef，超预算按 drilldown 下钻。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("refresh");
                    v.GetBool("refresh", false);
                    return v.Issues;
                }));

            list.Add(Tool("read_layers", "read", "图层摘要",
                "按图层返回对象类型与数量、识别含义、置信度与依据。可选按专业过滤、按图层名通配。分页返回，单页上限 200 层。用于回答「这张图分几类专业内容、识别可信度如何」。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("disciplines", "namePattern", "minEntities", "limit", "cursor");
                    v.GetStringArray("disciplines", 10, null);
                    v.GetString("namePattern", 120);
                    v.GetInt("minEntities", 0, int.MaxValue, 0);
                    v.GetInt("limit", 1, 200, 50);
                    v.GetString("cursor", 64);
                    return v.Issues;
                }));

            list.Add(Tool("read_query", "read", "对象查询",
                "按图层、DXF 类型、范围、文字包含、块属性等条件查询对象，分页返回轻量引用（句柄/类型/图层/范围/含义/置信度），单页上限 100 条。只返回引用不返回详情，需要细节再用 read_describe。范围为图纸单位。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("layer", "dxfName", "extent", "textContains", "attrEquals", "meaning", "minConfidence", "limit", "cursor");
                    v.GetString("layer", 120);
                    v.GetString("dxfName", 40);
                    v.GetNumberArray("extent", 4);
                    v.GetString("textContains", 80);
                    v.GetStringMap("attrEquals");
                    v.GetString("meaning", 60);
                    v.GetInt("limit", 1, 100, 20);
                    v.GetString("cursor", 64);
                    if (v.Has("minConfidence"))
                    {
                        Dictionary<string, object> wrapper = new Dictionary<string, object>(StringComparer.Ordinal);
                        wrapper["v"] = p["minConfidence"];
                        double confidence;
                        if (!JsonCodec.TryGetDouble(wrapper, "v", out confidence))
                        {
                            v.AddIssue("params.minConfidence", "must be number");
                        }
                        else if (confidence < 0D || confidence > 1D)
                        {
                            v.AddIssue("params.minConfidence", "must be in [0,1], got " + confidence.ToString("R", CultureInfo.InvariantCulture));
                        }
                    }
                    return v.Issues;
                }));

            list.Add(Tool("read_describe", "read", "对象详情",
                "按句柄返回单个对象完整描述：类型、图层、几何摘要、关键属性、文字、识别含义与依据、关联关系、该对象上的异常。句柄必填且应来自 read_query 结果（不跨命令持有 ObjectId）。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("handle", "fingerprint");
                    string handle = v.GetString("handle", 16);
                    if (string.IsNullOrEmpty(handle))
                    {
                        v.AddIssue("params.handle", "required");
                    }
                    else if (!IsHex(handle))
                    {
                        v.AddIssue("params.handle", "must be hex string");
                    }
                    v.GetString("fingerprint", 40);
                    return v.Issues;
                }));

            list.Add(Tool("read_anomalies", "read", "异常清单",
                "返回图纸异常清单：重复点、零长度/退化几何、极值坐标、单位缺失、代理对象、空属性、超长文字。可按类型/图层/严重度过滤，单页上限 100 条。这些是「读到了但不敢当真」的地方，必须如实报告而非自行修正。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("types", "layer", "severity", "limit", "cursor");
                    v.GetStringArray("types", 8, new string[]
                    {
                        "duplicate_point", "zero_length", "extreme_coord", "degenerate_extents",
                        "proxy_object", "missing_unit", "empty_attr", "extremely_long_text"
                    });
                    v.GetString("layer", 120);
                    v.GetEnumString("severity", new string[] { "info", "warn", "error" });
                    v.GetInt("limit", 1, 100, 30);
                    v.GetString("cursor", 64);
                    return v.Issues;
                }));

            list.Add(Tool("read_preview", "read", "预览图",
                "按范围生成当前图纸 PNG 预览图并直接返回图片。范围可 \"all\" 或 [x0,y0,x1,y1]；宽 200-2400、高 200-1600，默认 1200x800。M0 为结构示意图（由语义模型几何绘制），非出图成果，不可用于施工或校审；超 1 MB 自动降尺寸重绘。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("extent", "width", "height", "highlightHandles", "format");
                    if (v.Has("extent"))
                    {
                        object raw = p["extent"];
                        string asString = raw as string;
                        if (asString != null)
                        {
                            if (!string.Equals(asString, "all", StringComparison.Ordinal))
                            {
                                v.AddIssue("params.extent", "must be \"all\" or [x0,y0,x1,y1]");
                            }
                        }
                        else
                        {
                            v.GetNumberArray("extent", 4);
                        }
                    }
                    v.GetInt("width", 200, 2400, 1200);
                    v.GetInt("height", 200, 1600, 800);
                    v.GetStringArray("highlightHandles", 50, null);
                    v.GetEnumString("format", new string[] { "png" });
                    return v.Issues;
                }));

            list.Add(Tool("task_status", "task", "任务状态",
                "按 opId 查询任务状态与耗时。无副作用。用于确认某个请求是否真的执行过、耗时多少、失败在哪个错误码。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("opId");
                    if (string.IsNullOrEmpty(v.GetString("opId", 64)))
                    {
                        v.AddIssue("params.opId", "required");
                    }
                    return v.Issues;
                }));

            list.Add(Tool("task_list", "task", "最近任务",
                "列出最近任务（宿主内存环形缓冲，最多 200 条）。可按时间过滤，默认 20 条。用于在 WorkBuddy 与降级面板之间核对同一批任务。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("limit", "sinceUtc");
                    v.GetInt("limit", 1, 200, 20);
                    v.GetString("sinceUtc", 40);
                    return v.Issues;
                }));

            ToolDescriptor cancel = Tool("task_cancel", "task", "取消任务",
                "请求取消一个正在执行的任务。只置取消标志，由 handler 在对象批边界安全停止；M0 无写入故无副作用。返回 accepted 表示是否受理。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("opId");
                    if (string.IsNullOrEmpty(v.GetString("opId", 64)))
                    {
                        v.AddIssue("params.opId", "required");
                    }
                    return v.Issues;
                });
            cancel.ReadOnly = false;
            cancel.ControlOnly = true;
            cancel.HasOutputSchema = false;
            list.Add(cancel);

            list.Add(Tool("task_reconcile", "task", "结果不明对账",
                "对结果不明（超时/断线）的任务做一次对账：查宿主任务记录，判定 verified/failed/unknown 并落库。只读幂等，不触发重放。unknown 时提示「可安全重试，但必须用新 opId」。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("opId");
                    if (string.IsNullOrEmpty(v.GetString("opId", 64)))
                    {
                        v.AddIssue("params.opId", "required");
                    }
                    return v.Issues;
                }));

            list.Add(Tool("read_civil_list", "read", "Civil 对象枚举",
                "按类型与名称枚举 Civil 3D 对象并返回句柄。type 必填：any（不限定，五类聚合）、alignment（路线）、profile（纵断面）、surface（曲面）、pipeNetwork（管网）、pointGroup（点编组）。句柄仅在本次调用有效，不跨命令持有 ObjectId；结果带 counts 与 truncated。limit 取 1..500，默认 100；超出时返回 nextCursor，用 cursor 取下一页。未支持的类型一律回 E1001（不是空集）。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("type", "nameContains", "limit", "cursor");
                    // type **非必填**：不传等价于 any（schema 的 default 语义）。但「传了却是空串」
                    // 与「没传」不同，必须拒 —— GetEnumString 对缺省返回 null（不报错）、
                    // 对 "" 落在 enum 之外（报 not in enum）。
                    // 显式 null 在网关侧就被 schema 的 "type":"string" 拒掉（宿主这里
                    // GetString 对 null 与缺省一样返回 null，这是既有 NIT-3 约定，不另立规矩）。
                    v.GetEnumString("type", CivilTypes);
                    v.GetString("nameContains", 120);
                    v.GetInt("limit", 1, 500, 100);
                    v.GetString("cursor", 64);
                    return v.Issues;
                }));

            list.Add(Tool("read_civil_object", "read", "Civil 对象属性",
                "按句柄返回单个 Civil 3D 对象的属性表。句柄必填且应来自 read_civil_list；每个结果强制带 confidence（0..1）与 basis（识别依据）。properties 取 0..40 个属性名，不传表示按类型给默认属性集；长度类属性在同一响应给 units。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("handle", "properties", "fingerprint");
                    string handle = v.GetString("handle", 16);
                    if (string.IsNullOrEmpty(handle))
                    {
                        v.AddIssue("params.handle", "required");
                    }
                    else if (!IsHex(handle))
                    {
                        v.AddIssue("params.handle", "must be hex string");
                    }
                    v.GetStringArray("properties", 40, null);
                    v.GetString("fingerprint", 40);
                    return v.Issues;
                }));

            // ---------------- M1 写通道（write_*） ----------------
            // 骨架就位、默认关闭：write.enabled=false 时 ToolRouter 直接 E2003，这些 descriptor 不参与分派。
            // 参数形状的最终裁决在 WriteToolHandlers（它要给出 index 级的错误定位），这里只做形状筛查。

            list.Add(ToolWrite("write_preview", "write", "写入预演（不落盘）",
                "计算一次写入的摘要与参数指纹，**不做任何修改**。返回将要新增的点位数、目标图层、是否需要新建图层，以及 paramsDigest 与 targetFingerprint —— 后两者是 write_approve 的输入。写通道关闭时恒 E2003。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("kind", "points", "layer", "block", "command", "surface");
                    v.GetEnumString("kind", new string[] { "insert_points", "command", "surface_breakline" });
                    if (!v.Has("points"))
                    {
                        v.AddIssue("params.points", "required");
                    }
                    v.GetString("layer", 255);
                    v.GetString("block", 255);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_approve", "write", "写入审批（需人在 CAD 点确认）",
                "为一次已预演的写入签发一次性审批令牌。必须带 userConfirmation=true（表示已在对话里把摘要汇报给用户并得到「执行」），随后宿主会在 CAD 中弹模态确认框，**只有人点「是」才签发**。令牌默认 120 秒有效、仅可用一次，绑定 paramsDigest + targetFingerprint + 文档身份。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("kind", "points", "layer", "block", "command", "surface", "paramsDigest",
                        "targetFingerprint", "userConfirmation", "dryRun");
                    v.GetEnumString("kind", new string[] { "insert_points", "command", "surface_breakline" });
                    if (!v.Has("points"))
                    {
                        v.AddIssue("params.points", "required");
                    }
                    v.GetString("paramsDigest", 80);
                    v.GetString("targetFingerprint", 40);
                    v.GetBool("userConfirmation", false);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_insert_points", "write", "按坐标放点（测设）",
                "按坐标表在模型空间插入点（POINT），图层不存在则新建。必须携带 write_approve 签发的 approvalToken 与同一份参数；参数任一变化令牌即失效（E1002）。**永不保存文件**，返回新增句柄清单，可用 U 撤销。单次上限 5000 点。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("kind", "points", "layer", "block", "paramsDigest", "approvalToken");
                    v.GetEnumString("kind", new string[] { "insert_points" });
                    if (!v.Has("points"))
                    {
                        v.AddIssue("params.points", "required");
                    }
                    v.GetString("layer", 255);
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_command", "write", "执行一条 CAD 命令（需审批）",
                "在当前图纸的命令上下文里执行一条非交互命令（如 (setvar \"PDMODE\" 35)）。必须携带 write_approve 签发的 approvalToken 且命令原文与批准时一致。硬拒保存/关闭/打印/发布类命令；永不落盘。用于自动化那些只能靠命令行完成的动作。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("kind", "command", "paramsDigest", "approvalToken", "dryRun", "wrapUndoGroup");
                    if (!v.Has("command"))
                    {
                        v.AddIssue("params.command", "required");
                    }
                    v.GetString("command", 500);
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    v.GetBool("wrapUndoGroup", true);
                    return v.Issues;
                }));

            // B 路线首例（docs/10 §十五）：工具是我们的名字，执行在基座（new-acad 的 listSurfaces）。
            // 基座的 430 个方法永不进 tools/list —— 模型只能看到这一层薄壳。
            list.Add(Tool("read_civil_surfaces", "read", "列举 Civil 3D 曲面",
                "列举图纸中的 Civil 3D 曲面（名称/类型等标量属性）。执行在基座（new-acad listSurfaces），本工具只做归一化。基座未加载时返回 E3002 并说明如何加载。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("limit");
                    v.GetInt("limit", 1, 500, 100);
                    return v.Issues;
                }));

            list.Add(Tool("read_civil_alignments", "read", "列举 Civil 3D 路线",
                "列举图纸中的 Civil 3D 路线（名称/类型等标量属性）。执行在基座（new-acad listAlignments），由网关转调；宿主只保留入口防误用。基座未加载时返回 E3002。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("limit");
                    v.GetInt("limit", 1, 500, 100);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_surface_breakline", "write", "向曲面添加断裂线（需审批）",
                "向指定 Civil 3D 曲面添加一条断裂线（breakline）。入参用工程坐标（e/n/z），内部换算为基座要求的 x/y/z 并转调 addSurfaceBreakline。必须携带 write_approve 签发的 approvalToken。执行在后台线程（基座需主线程，同步等会死锁），返回 queued，效果用 read_civil_surfaces 复核。永不保存文件。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("kind", "surface", "points", "paramsDigest", "approvalToken");
                    if (!v.Has("surface"))
                    {
                        v.AddIssue("params.surface", "required");
                    }
                    if (!v.Has("points"))
                    {
                        v.AddIssue("params.points", "required");
                    }
                    v.GetString("surface", 255);
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    return v.Issues;
                }));

            // ---------------- v3.0 Phase 1 & Phase 3 原子能力 ----------------

            list.Add(Tool("export_surface_grid", "export", "无损导出曲面高程栅格",
                "将 Civil 3D TinSurface 的真实大地坐标高程格网无损导出为 NumPy .npz 或 .json 文件至 %TEMP%/ep_workspace/，供外置选线引擎直接读取。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("surface", "resolution", "maxGridDim", "format", "outPath", "extent");
                    v.GetString("surface", 255);
                    v.GetInt("maxGridDim", 10, 2000, 400);
                    v.GetEnumString("format", new string[] { "npz", "json" });
                    v.GetString("outPath", 500);
                    v.GetNumberArray("extent", 4);
                    return v.Issues;
                }));

            list.Add(Tool("export_layer_geojson", "export", "无损导出图层 GeoJSON",
                "将指定图层的多段线/线/圆/弧/块参照/点/填充按真实大地坐标无损导出为 RFC 7946 GeoJSON，供外置选线引擎构建障碍物与拆迁代价场。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("layers", "layerCategoryMap", "limit", "outPath");
                    v.GetStringArray("layers", 100, null);
                    v.GetStringMap("layerCategoryMap");
                    v.GetInt("limit", 1, 100000, 20000);
                    v.GetString("outPath", 500);
                    return v.Issues;
                }));

            list.Add(Tool("read_active_selection", "read", "实时选区感知",
                "读取当前 CAD 活动视口中工程师鼠标框选的实体集合（句柄、DXF 类型、图层、真实坐标包围盒与 Civil 对象名）。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("limit");
                    v.GetInt("limit", 1, 2000, 200);
                    return v.Issues;
                }));

            list.Add(Tool("read_viewport_snapshot", "read", "低延迟视口截图",
                "通过 Document.CapturePreviewImage 毫秒级截取当前 2D/3D 视口 PNG 截图（无头模式下自动降级语义渲染），便于 AI 肉眼核验建模效果。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("width", "height", "outPath", "includeBase64");
                    v.GetInt("width", 200, 2560, 1280);
                    v.GetInt("height", 200, 1600, 720);
                    v.GetString("outPath", 500);
                    v.GetBool("includeBase64", false);
                    return v.Issues;
                }));

            list.Add(Tool("read_object_xdata", "read", "读取对象 XData 与 XRecord",
                "按句柄读取实体挂载的 XData（按 RegApp 分组）与 ExtensionDictionary 中的 XRecord 元数据。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("handle");
                    string h = v.GetString("handle", 16);
                    if (string.IsNullOrEmpty(h)) v.AddIssue("params.handle", "required");
                    else if (!IsHex(h)) v.AddIssue("params.handle", "must be hex string");
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_object_xdata", "write", "写入对象 XData 与 XRecord",
                "按句柄向实体写入 XData（自动注册 RegApp）或 ExtensionDictionary XRecord 键值对。事务级提交，可单步撤销。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("handle", "regApp", "xrecordKey", "entries", "paramsDigest", "approvalToken");
                    string h = v.GetString("handle", 16);
                    if (string.IsNullOrEmpty(h)) v.AddIssue("params.handle", "required");
                    else if (!IsHex(h)) v.AddIssue("params.handle", "must be hex string");
                    v.GetString("regApp", 120);
                    v.GetString("xrecordKey", 120);
                    if (!v.Has("entries")) v.AddIssue("params.entries", "required");
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_undo", "write", "精准事务撤销",
                "执行原子回滚（_.UNDO <steps>），支持 AI 试错后精准撤回最近 N 步图纸修改。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("steps", "paramsDigest", "approvalToken");
                    v.GetInt("steps", 1, 50, 1);
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_alignment_plan", "write", "平纵选线方案原生落盘",
                "读取 route_solver 输出的 plan_N.json 或内联平纵参数，在 Civil 3D 中原生生成 Alignment、曲面纵断面与设计拉坡纵断面（纯 AutoCAD 自动降级 3D Polyline）。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("planPath", "name", "layer", "surfaceName", "eraseSourcePolyline",
                        "points", "sampledCenterline", "alignment", "profile", "pvis", "paramsDigest", "approvalToken");
                    v.GetString("planPath", 500);
                    v.GetString("name", 200);
                    v.GetString("layer", 255);
                    v.GetString("surfaceName", 255);
                    v.GetBool("eraseSourcePolyline", false);
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_eval_csharp", "write", "Roslyn C# 内存编译执行",
                "在独立 AssemblyLoadContext 中内存编译并执行任意 ObjectARX / Civil 3D C# 代码（注入 doc/db/tr/ed），支持 accoreconsole 影子进程试跑防崩溃。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("code", "args", "usings", "commit", "shadowDryRun", "paramsDigest", "approvalToken");
                    if (!v.Has("code")) v.AddIssue("params.code", "required");
                    v.GetString("code", 65536);
                    v.GetStringArray("usings", 30, null);
                    v.GetBool("commit", true);
                    v.GetBool("shadowDryRun", false);
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    return v.Issues;
                }));

            list.Add(ToolWrite("write_entities", "write", "原生批量创建实体（线/多段线/矩形/圆/圆弧/椭圆/文字/点）",
                "在单个事务内按结构化 JSON 批量创建原生 CAD 实体（line/polyline/rectangle/circle/arc/ellipse/text/mtext/point），自动建图层与设颜色，支持插件/COM/离线三驱动，同步返回精确创建数、句柄、包围盒与失败明细，支持 write_undo 撤销。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("entities", "layer", "strict", "zoomExtents", "paramsDigest", "approvalToken");
                    if (!v.Has("entities")) v.AddIssue("params.entities", "required");
                    v.GetString("layer", 255);
                    v.GetBool("strict", false);
                    v.GetBool("zoomExtents", false);
                    v.GetString("paramsDigest", 80);
                    v.GetString("approvalToken", 900);
                    return v.Issues;
                }));

            list.Add(Tool("preview_transient", "preview", "瞬态 3D 预览渲染",
                "使用 TransientManager 在视口中绘制或清除非破坏性 3D 预览图形（多段线/线/圆/文字），不改图纸 rev，关图自动清理防驱动崩溃。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("action", "group", "primitives");
                    v.GetEnumString("action", new string[] { "add", "replace", "clear", "list" });
                    v.GetString("group", 120);
                    return v.Issues;
                }));

            list.Add(ToolWrite("batch_side_database", "batch", "侧数据库无界面跨图批处理",
                "使用 new Database(false, true) 后台无界面批量读取或修改多份 DWG 图纸（inspect / export_layers / find_replace_text），不占用 MDI 视口。",
                delegate(Dictionary<string, object> p)
                {
                    ParamValidator v = new ParamValidator(p);
                    v.RejectUnknown("files", "action", "findText", "replaceText", "saveChanges");
                    if (!v.Has("files")) v.AddIssue("params.files", "required");
                    v.GetStringArray("files", 50, null);
                    v.GetEnumString("action", new string[] { "inspect", "export_layers", "find_replace_text" });
                    v.GetString("findText", 500);
                    v.GetString("replaceText", 500);
                    v.GetBool("saveChanges", false);
                    return v.Issues;
                }));

            return list;
        }

        /// <summary>
        /// `read_civil_list.type` 的允许集 —— 与 `protocol/tools.schema.json` 的 enum、
        /// `ReadToolHandlers.CivilRecordsOf` 的 case 标签、`mockhost.CIVIL_TYPES` 四处一致。
        /// 改这里必须同步另外三处（静态审查与 `tests/test_gateway_n04.py` 会红）。
        /// </summary>
        private static readonly string[] CivilTypes =
        {
            "any", "alignment", "profile", "surface", "pipeNetwork", "pointGroup"
        };

        private static ToolDescriptor Tool(string name, string domain, string title, string description,
            Func<Dictionary<string, object>, List<ParamIssue>> validate)
        {
            ToolDescriptor descriptor = new ToolDescriptor();
            descriptor.Name = name;
            descriptor.Domain = domain;
            descriptor.Title = title;
            descriptor.Description = description;
            descriptor.Validate = validate;
            descriptor.ReadOnly = true;
            return descriptor;
        }

        /// <summary>写工具（ReadOnly=false）。签名与 <see cref="Tool"/> 一致，只差这一个标记。</summary>
        private static ToolDescriptor ToolWrite(string name, string domain, string title, string description,
            Func<Dictionary<string, object>, List<ParamIssue>> validate)
        {
            ToolDescriptor descriptor = Tool(name, domain, title, description, validate);
            descriptor.ReadOnly = false;
            return descriptor;
        }

        private static bool IsHex(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok)
                {
                    return false;
                }
            }
            return value.Length > 0;
        }

        /// <summary>工具目录的 MCP 侧快照（本机不对外提供 tools/list，但面板与 sys 工具会用到）。</summary>
        public static List<Dictionary<string, object>> Snapshot()
        {
            List<Dictionary<string, object>> snapshot = new List<Dictionary<string, object>>(Descriptors.Count);
            for (int i = 0; i < Descriptors.Count; i++)
            {
                ToolDescriptor d = Descriptors[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["name"] = d.Name;
                item["title"] = d.Title;
                item["domain"] = d.Domain;
                item["readOnly"] = d.ReadOnly;
                item["description"] = d.Description;
                Dictionary<string, object> annotations = new Dictionary<string, object>(StringComparer.Ordinal);
                annotations["readOnlyHint"] = d.ReadOnly;
                annotations["destructiveHint"] = false;
                annotations["idempotentHint"] = true;
                annotations["openWorldHint"] = false;
                annotations["title"] = d.Title;
                item["annotations"] = annotations;
                snapshot.Add(item);
            }
            return snapshot;
        }
    }
}
