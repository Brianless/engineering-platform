using System;
using System.Globalization;
using System.IO;
using System.Text;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// 脱敏工具（设计 §2.8 / §7.4）。
    /// - 完整路径只出现哈希，不出现明文；
    /// - 图元文字在 JSON 响应里最多 80 字符，日志里完全不记；
    /// - 用户名一律替换。
    /// </summary>
    internal static class Redactor
    {
        /// <summary>路径哈希：sha256(大写规范化路径) 前 8 位。</summary>
        public static string PathHash(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "00000000";
            }
            return ParamsDigest.Sha256Hex(path.ToUpperInvariant()).Substring(0, 8);
        }

        /// <summary>图纸名哈希：basename 的 sha256 前 8 位 + 扩展名（不返回明文路径）。</summary>
        public static string NameHash(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "unknown.dwg";
            }
            string baseName;
            try
            {
                baseName = Path.GetFileName(path);
            }
            catch (Exception)
            {
                baseName = path;
            }
            string extension = ".dwg";
            try
            {
                string ext = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(ext))
                {
                    extension = ext.ToLowerInvariant();
                }
            }
            catch (Exception)
            {
                // 保持默认扩展名
            }
            string hash = ParamsDigest.Sha256Hex((baseName ?? string.Empty).ToUpperInvariant()).Substring(0, 8);
            return hash + extension;
        }

        /// <summary>安全 basename（无路径，仅用于响应里给人看）。</summary>
        public static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "未命名图纸";
            }
            try
            {
                string name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? "未命名图纸" : name;
            }
            catch (Exception)
            {
                return "未命名图纸";
            }
        }

        /// <summary>文字截断（响应里单条文字最多 80 字符）。</summary>
        public static string Text(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string normalized = text.Replace("\r", " ").Replace("\n", " ");
            if (normalized.Length <= max)
            {
                return normalized;
            }
            return normalized.Substring(0, max);
        }

        /// <summary>
        /// 去除 MText 格式码：\P（换行）、\f…;（字体）、\H…;（字高）、{} 分组、\A…; 等。
        /// M0 只需「能读懂大概写了什么」，不做富文本还原。
        /// </summary>
        public static string StripMTextCodes(string contents)
        {
            if (string.IsNullOrEmpty(contents))
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(contents.Length);
            int i = 0;
            while (i < contents.Length)
            {
                char c = contents[i];
                if (c == '\\' && i + 1 < contents.Length)
                {
                    char next = contents[i + 1];
                    if (next == 'P' || next == 'p')
                    {
                        sb.Append(' ');
                        i += 2;
                        continue;
                    }
                    if (next == '~')
                    {
                        sb.Append(' ');
                        i += 2;
                        continue;
                    }
                    if (next == '\\' || next == '{' || next == '}')
                    {
                        sb.Append(next);
                        i += 2;
                        continue;
                    }
                    // \fArial|b0|i0;  \H2.5x;  \A1;  \C1;  \S1^2; 之类：跳过到分号
                    int semicolon = contents.IndexOf(';', i);
                    if (semicolon > 0 && semicolon - i <= 40)
                    {
                        i = semicolon + 1;
                        continue;
                    }
                    i += 2;
                    continue;
                }
                if (c == '{' || c == '}')
                {
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString().Trim();
        }

        /// <summary>去掉用户名（用于本机路径被意外带入时）。</summary>
        public static string StripUserName(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            string user = Environment.UserName;
            if (string.IsNullOrEmpty(user) || user.Length < 3)
            {
                return text;
            }
            return text.Replace(user, "{user}");
        }

        /// <summary>格式化数值（统一不变式，保证双端字符串一致）。</summary>
        public static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
