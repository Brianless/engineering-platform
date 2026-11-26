using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Write
{
    /// <summary>
    /// 写通道审批令牌（M1）。设计见 docs/10-M1-写通道设计.md §五。
    ///
    /// ## 它解决什么
    ///
    /// 「批准的和实际执行的是不是同一件事」：token 把
    /// `paramsDigest + targetFingerprint + docIdentity + 过期 + 单次 nonce` 绑在一起，
    /// 执行时任一不符就拒。没有它，写工具就只是"参数一给就改图"。
    ///
    /// ## 谁签发的
    ///
    /// 只有 <c>write_approve</c> 会调 <see cref="Issue"/>，而它必须先从**人在 CAD 里的点击**拿到确认
    /// （见 WriteToolHandlers）。本类不判断人也，只负责密码学上的不可伪造与不可重放。
    ///
    /// ## 为什么 nonce 表在内存
    ///
    /// 重启宿主即全部失效 —— 这是**有意**的：进程重开后"上一个人点过的那次确认"不应该还活着。
    ///
    /// ## 失败时不消耗 nonce
    ///
    /// 校验顺序是 签名 → 过期 → 未用过 → digest → fingerprint → 文档身份。
    /// digest/指纹不符属于"调用方把 token 用错了地方"，此时把 nonce 烧掉会让用户莫名其妙要点两次确认。
    /// 只有全部通过才消费。
    /// </summary>
    internal sealed class ApprovalTokens
    {
        private const string Prefix = "v1";
        private const int SecretBytes = 32;
        private const int NonceBytes = 8;

        private readonly byte[] _secret;
        private readonly object _sync = new object();
        /// <summary>已消费的 nonce → 过期时刻（UTC）。只增不减，过期项在每次 Issue 时清理。</summary>
        private readonly Dictionary<string, DateTime> _consumed = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public ApprovalTokens(string secretPath)
        {
            _secret = LoadOrCreateSecret(secretPath);
        }

        /// <summary>签发。调用方必须已完成「人确认」。</summary>
        public string Issue(string paramsDigest, string targetFingerprint, string docIdentity, int ttlMs)
        {
            byte[] nonceBytes = new byte[NonceBytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(nonceBytes);
            }
            string nonce = Base64Url(nonceBytes);
            long expiry = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(1000, ttlMs);

            string payload = string.Join("|", new[]
            {
                Prefix, paramsDigest ?? string.Empty, targetFingerprint ?? string.Empty,
                docIdentity ?? string.Empty, expiry.ToString(CultureInfo.InvariantCulture), nonce
            });

            PruneExpired();
            HostLog.Info("write", "approval_issued",
                "digest", ShortDigest(paramsDigest), "ttlMs", ttlMs, "expiry", expiry);
            return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url(Sign(payload));
        }

        /// <summary>
        /// 校验并（全部通过时）消费。失败返回 false 并给出 detail（用于 E1006/E1002 的 detail 字段）。
        /// </summary>
        public bool TryConsume(string token, string paramsDigest, string targetFingerprint,
            string docIdentity, out string detail)
        {
            detail = null;
            if (string.IsNullOrEmpty(token))
            {
                detail = "approval_missing";
                return false;
            }

            int dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1)
            {
                detail = "approval_malformed";
                return false;
            }

            string payload;
            byte[] providedSignature;
            try
            {
                payload = Encoding.UTF8.GetString(FromBase64Url(token.Substring(0, dot)));
                providedSignature = FromBase64Url(token.Substring(dot + 1));
            }
            catch (Exception)
            {
                detail = "approval_malformed";
                return false;
            }

            // 1) 签名（时间恒定比较，避免把"签名对了几位"当侧信道）
            byte[] expected = Sign(payload);
            if (!FixedTimeEquals(expected, providedSignature))
            {
                detail = "approval_invalid_signature";
                return false;
            }

            string[] parts = payload.Split('|');
            if (parts.Length != 6 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            {
                detail = "approval_malformed";
                return false;
            }

            long expiry;
            if (!long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out expiry))
            {
                detail = "approval_malformed";
                return false;
            }

            // 2) 过期
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > expiry)
            {
                detail = "approval_expired";
                return false;
            }

            // 3) 未用过（先查，不消费）
            string nonce = parts[5];
            lock (_sync)
            {
                if (_consumed.ContainsKey(nonce))
                {
                    detail = "approval_consumed";
                    return false;
                }
            }

            // 4) 绑定的三样东西
            if (!string.Equals(parts[1], paramsDigest ?? string.Empty, StringComparison.Ordinal))
            {
                detail = "approval_params_mismatch";
                return false;
            }
            if (!string.Equals(parts[2], targetFingerprint ?? string.Empty, StringComparison.Ordinal))
            {
                detail = "approval_target_mismatch";
                return false;
            }
            if (!string.Equals(parts[3], docIdentity ?? string.Empty, StringComparison.Ordinal))
            {
                detail = "approval_document_mismatch";
                return false;
            }

            // 5) 全部通过才消费
            lock (_sync)
            {
                if (_consumed.ContainsKey(nonce))
                {
                    detail = "approval_consumed";   // 并发双花
                    return false;
                }
                _consumed[nonce] = DateTimeOffset.FromUnixTimeMilliseconds(expiry).UtcDateTime;
            }

            HostLog.Info("write", "approval_consumed", "digest", ShortDigest(paramsDigest));
            return true;
        }

        /// <summary>文档身份：`doc.Name` + `db.Filename`。与 DocumentRevision 的口径保持一致。</summary>
        public static string DocumentIdentity(string docName, string dbFilename)
        {
            return (docName ?? string.Empty) + "\u0001" + (dbFilename ?? string.Empty);
        }

        private byte[] Sign(string payload)
        {
            using (HMACSHA256 hmac = new HMACSHA256(_secret))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            }
        }

        private void PruneExpired()
        {
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                List<string> dead = null;
                foreach (KeyValuePair<string, DateTime> pair in _consumed)
                {
                    if (pair.Value < now)
                    {
                        if (dead == null)
                        {
                            dead = new List<string>();
                        }
                        dead.Add(pair.Key);
                    }
                }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++)
                    {
                        _consumed.Remove(dead[i]);
                    }
                }
            }
        }

        private static byte[] LoadOrCreateSecret(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    byte[] existing = File.ReadAllBytes(path);
                    if (existing.Length >= SecretBytes)
                    {
                        byte[] trimmed = new byte[SecretBytes];
                        Array.Copy(existing, trimmed, SecretBytes);
                        return trimmed;
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("write", "secret_read_failed", ex);
            }

            byte[] fresh = new byte[SecretBytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(fresh);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, fresh);
                HostLog.Info("write", "secret_created", "path", HostLog.SafePath(path));
            }
            catch (Exception ex)
            {
                // 写不了盘就用内存里的临时密钥：本次进程内有效，重启后旧 token 全失效（安全侧可接受）。
                HostLog.Exception("write", "secret_write_failed", ex);
            }
            return fresh;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] FromBase64Url(string text)
        {
            string s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        private static string ShortDigest(string digest)
        {
            if (string.IsNullOrEmpty(digest))
            {
                return "-";
            }
            int colon = digest.IndexOf(':');
            string body = colon >= 0 ? digest.Substring(colon + 1) : digest;
            return body.Length <= 8 ? body : body.Substring(0, 8);
        }
    }
}
