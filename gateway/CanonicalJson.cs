using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// 统一 Canonical JSON SHA-256 摘要与短周期幂等缓存（v3.0 Phase 4）。
/// 与 host/Protocol/ParamsDigest.cs 算法完全字节一致：递归键按 Ordinal 排序、紧凑分隔符、UTF-8 不转义中文。
/// </summary>
internal static class CanonicalJson
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(JsonNode? node)
    {
        if (node == null) return "null";
        var sb = new StringBuilder();
        WriteCanonical(node, sb);
        return sb.ToString();
    }

    public static string ComputeParamsDigest(JsonNode? parameters)
    {
        string canonical = Serialize(parameters ?? new JsonObject());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string SerializeCompact(object value)
    {
        return JsonSerializer.Serialize(value, CompactOptions);
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        if (node is null)
        {
            sb.Append("null");
            return;
        }
        if (node is JsonObject obj)
        {
            sb.Append('{');
            var keys = obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonSerializer.Serialize(keys[i], CompactOptions));
                sb.Append(':');
                WriteCanonical(obj[keys[i]], sb);
            }
            sb.Append('}');
            return;
        }
        if (node is JsonArray arr)
        {
            sb.Append('[');
            for (int i = 0; i < arr.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteCanonical(arr[i], sb);
            }
            sb.Append(']');
            return;
        }
        if (node is JsonValue val)
        {
            if (val.TryGetValue<string>(out var s))
            {
                sb.Append(JsonSerializer.Serialize(s, CompactOptions));
            }
            else if (val.TryGetValue<bool>(out var b))
            {
                sb.Append(b ? "true" : "false");
            }
            else if (val.TryGetValue<long>(out var l))
            {
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
            }
            else if (val.TryGetValue<double>(out var d))
            {
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(val.ToJsonString(CompactOptions));
            }
            return;
        }
        sb.Append(node.ToJsonString(CompactOptions));
    }
}

internal sealed class IdempotencyCache
{
    private sealed record Entry(JsonObject Response, DateTime ExpiresUtc);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public IdempotencyCache(TimeSpan ttl) => _ttl = ttl;

    public bool TryGet(string key, out JsonObject? response)
    {
        response = null;
        if (string.IsNullOrEmpty(key)) return false;
        if (_entries.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow <= entry.ExpiresUtc)
            {
                response = entry.Response.DeepClone().AsObject();
                return true;
            }
            _entries.TryRemove(key, out _);
        }
        return false;
    }

    public void Put(string key, JsonObject response)
    {
        if (string.IsNullOrEmpty(key)) return;
        _entries[key] = new Entry(response.DeepClone().AsObject(), DateTime.UtcNow.Add(_ttl));
        if (_entries.Count > 256)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _entries)
            {
                if (kv.Value.ExpiresUtc < now) _entries.TryRemove(kv.Key, out _);
            }
        }
    }
}
