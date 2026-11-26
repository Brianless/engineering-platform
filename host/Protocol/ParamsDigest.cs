using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EngineeringPlatform.Host.Protocol
{
    /// <summary>
    /// 参数摘要（paramsDigest）。双端必须字节一致，规则见设计 §2.6：
    /// canonical = 递归键排序 + 紧凑分隔符(',',':') + 原生 UTF-8；
    /// paramsDigest = "sha256:" + hex(sha256(utf8(canonical)))。
    /// M0 只用于审计与缓存键；M1 的审批令牌绑定会依赖它，所以现在就必须一致。
    /// </summary>
    internal static class ParamsDigest
    {
        /// <summary>计算参数摘要；params 为 null 时按空对象计算。</summary>
        public static string Compute(IDictionary<string, object> parameters)
        {
            IDictionary<string, object> payload = parameters ?? new Dictionary<string, object>(StringComparer.Ordinal);
            string canonical = JsonCodec.SerializeCanonical(payload);
            return "sha256:" + Sha256Hex(JsonCodec.Utf8(canonical));
        }

        /// <summary>计算字符串的 SHA-256（小写 hex）。</summary>
        public static string Sha256Hex(string text)
        {
            return Sha256Hex(JsonCodec.Utf8(text ?? string.Empty));
        }

        /// <summary>计算字节的 SHA-256（小写 hex）。</summary>
        public static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes ?? new byte[0]);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        /// <summary>生成 32 字节随机数的 64 位 hex（会话令牌）。</summary>
        public static string NewToken()
        {
            byte[] buffer = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            StringBuilder sb = new StringBuilder(64);
            for (int i = 0; i < buffer.Length; i++)
            {
                sb.Append(buffer[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>生成 8 位小写 hex（实例标识后缀）。</summary>
        public static string NewShortId()
        {
            byte[] buffer = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            StringBuilder sb = new StringBuilder(8);
            for (int i = 0; i < buffer.Length; i++)
            {
                sb.Append(buffer[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>令牌打码：只保留前 4 位（日志与面板展示用，永不落明文）。</summary>
        public static string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return "null";
            }
            if (token.Length <= 4)
            {
                return "***";
            }
            return token.Substring(0, 4) + "***";
        }
    }
}
