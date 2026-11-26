using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>一条映射规则（图层名或块名 → 工程含义）。</summary>
    internal sealed class MappingRule
    {
        public string Pattern = string.Empty;
        public string Meaning = string.Empty;
        public string Category = "unknown";
        public int ConfidencePct = 70;
        public Regex Compiled;
    }

    /// <summary>一次映射命中结果。</summary>
    internal sealed class MappingMatch
    {
        public string Meaning;
        public string Category;
        public double Confidence;
        public string Reference;
        public string Pattern;
    }

    /// <summary>
    /// 企业映射表（设计 §3.5）。
    /// 默认映射只做「通用图元类别」（contour/terrain/structure/annotation/frame/hatch/unknown），
    /// 不含具体工程语义——工程语义必须由企业映射提供，体现「AI 不擅自推定专业规则」。
    /// </summary>
    internal sealed class LayerMapping
    {
        public const string DefaultLayerResource = "EngineeringPlatform.Host.Resources.layer-map.default.json";
        public const string DefaultBlockResource = "EngineeringPlatform.Host.Resources.block-map.default.json";

        public string LayerVersion = "1.0";
        public string BlockVersion = "1.0";
        public string Source = "embedded:layer-map.default.json";
        public string EnterprisePath;
        public bool Loaded;
        public string LoadError;

        public readonly List<MappingRule> LayerRules = new List<MappingRule>();
        public readonly List<MappingRule> BlockRules = new List<MappingRule>();

        /// <summary>
        /// 加载：先读嵌入的默认映射，再用 %LOCALAPPDATA%\EngineeringPlatform\layer-map.json 覆盖（若存在）。
        /// 加载失败只记录并保持可用（绝不因映射表问题让只读工具全盘失败）。
        /// </summary>
        public static LayerMapping Load(string enterpriseLayerMapPath)
        {
            LayerMapping mapping = new LayerMapping();
            try
            {
                string layerJson = ReadEmbedded(DefaultLayerResource);
                if (!string.IsNullOrEmpty(layerJson))
                {
                    Dictionary<string, object> dict;
                    if (JsonCodec.TryParseObject(layerJson, out dict))
                    {
                        mapping.LayerVersion = JsonCodec.GetStr(dict, "version", "1.0");
                        ReadRules(dict, mapping.LayerRules);
                    }
                }

                string blockJson = ReadEmbedded(DefaultBlockResource);
                if (!string.IsNullOrEmpty(blockJson))
                {
                    Dictionary<string, object> dict;
                    if (JsonCodec.TryParseObject(blockJson, out dict))
                    {
                        mapping.BlockVersion = JsonCodec.GetStr(dict, "version", "1.0");
                        ReadRules(dict, mapping.BlockRules);
                    }
                }
                mapping.Loaded = mapping.LayerRules.Count > 0 || mapping.BlockRules.Count > 0;
            }
            catch (Exception ex)
            {
                mapping.LoadError = ex.GetType().Name + ":" + ex.Message;
                HostLog.Exception("mapping", "default_load_failed", ex);
            }

            if (!string.IsNullOrEmpty(enterpriseLayerMapPath) && File.Exists(enterpriseLayerMapPath))
            {
                try
                {
                    string text = File.ReadAllText(enterpriseLayerMapPath);
                    Dictionary<string, object> dict;
                    if (JsonCodec.TryParseObject(text, out dict))
                    {
                        List<MappingRule> rules = new List<MappingRule>();
                        ReadRules(dict, rules);
                        if (rules.Count > 0)
                        {
                            mapping.LayerRules.Clear();
                            mapping.LayerRules.AddRange(rules);
                            mapping.LayerVersion = JsonCodec.GetStr(dict, "version", mapping.LayerVersion);
                            mapping.Source = "enterprise:" + Path.GetFileName(enterpriseLayerMapPath);
                            mapping.EnterprisePath = enterpriseLayerMapPath;
                        }
                    }
                }
                catch (Exception ex)
                {
                    mapping.LoadError = ex.GetType().Name + ":" + ex.Message;
                    HostLog.Exception("mapping", "enterprise_load_failed", ex);
                }
            }

            HostLog.Info("mapping", "loaded",
                "source", mapping.Source,
                "layerRules", mapping.LayerRules.Count,
                "blockRules", mapping.BlockRules.Count,
                "loaded", mapping.Loaded);
            return mapping;
        }

        /// <summary>图层名映射（通配、大小写不敏感）；未命中返回 null，绝不猜测。</summary>
        public MappingMatch MatchLayer(string layerName)
        {
            return Match(LayerRules, layerName, "layer_map", "layer-map@" + LayerVersion);
        }

        /// <summary>块名映射。</summary>
        public MappingMatch MatchBlock(string blockName)
        {
            return Match(BlockRules, blockName, "block_map", "block-map@" + BlockVersion);
        }

        private static MappingMatch Match(List<MappingRule> rules, string name, string basisKind, string reference)
        {
            if (string.IsNullOrEmpty(name) || rules == null || rules.Count == 0)
            {
                return null;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                MappingRule rule = rules[i];
                if (rule.Compiled != null && rule.Compiled.IsMatch(name))
                {
                    MappingMatch match = new MappingMatch();
                    match.Meaning = rule.Meaning;
                    match.Category = rule.Category;
                    match.Confidence = ConfidenceBand.FromPercent(rule.ConfidencePct);
                    match.Reference = reference + ":#" + rule.Pattern;
                    match.Pattern = rule.Pattern;
                    return match;
                }
            }
            return null;
        }

        private static void ReadRules(Dictionary<string, object> dict, List<MappingRule> target)
        {
            List<object> patterns = JsonCodec.GetList(dict, "patterns");
            if (patterns == null)
            {
                return;
            }
            for (int i = 0; i < patterns.Count; i++)
            {
                Dictionary<string, object> item = patterns[i] as Dictionary<string, object>;
                if (item == null)
                {
                    continue;
                }
                string pattern = JsonCodec.GetStr(item, "pattern");
                string meaning = JsonCodec.GetStr(item, "meaning");
                if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(meaning))
                {
                    continue;
                }
                MappingRule rule = new MappingRule();
                rule.Pattern = pattern;
                rule.Meaning = meaning;
                rule.Category = JsonCodec.GetStr(item, "category", "unknown");
                rule.ConfidencePct = JsonCodec.GetInt(item, "confidencePct", 70);
                try
                {
                    rule.Compiled = new Regex(WildcardToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (Exception)
                {
                    rule.Compiled = null;
                }
                if (rule.Compiled != null)
                {
                    target.Add(rule);
                }
            }
        }

        /// <summary>通配符转正则：* → .*，? → .，其余转义。</summary>
        public static string WildcardToRegex(string pattern)
        {
            StringBuilder sb = new StringBuilder(pattern.Length + 8);
            sb.Append('^');
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    sb.Append(".*");
                }
                else if (c == '?')
                {
                    sb.Append('.');
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            sb.Append('$');
            return sb.ToString();
        }

        /// <summary>通配匹配（供 read_layers.namePattern / read_query.layer 使用）。</summary>
        public static bool WildcardMatch(string pattern, string value)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return true;
            }
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            try
            {
                return Regex.IsMatch(value, WildcardToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (Exception)
            {
                return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string ReadEmbedded(string resourceName)
        {
            Assembly assembly = typeof(LayerMapping).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    HostLog.Warn("mapping", "embedded_missing", "resource", resourceName);
                    return null;
                }
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>映射表摘要（sys_health / sys_capabilities 展示）。</summary>
        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} | 图层规则 {1} 条 | 块规则 {2} 条 | 版本 {3}/{4}",
                Source, LayerRules.Count, BlockRules.Count, LayerVersion, BlockVersion);
        }
    }
}
