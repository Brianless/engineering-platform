using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// NamedPipe 宿主客户端（v3.0 Phase 4）：
/// - 从 %LOCALAPPDATA%\EngineeringPlatform\instances\*.json 自动发现存活的 CAD 宿主；
/// - 实现 1.0 协议小端 uint32 分帧 + hello/hello_ack 握手；
/// - 支持多 AI Agent 并发请求串行排队，并在 CAD 重启时自动透明重连不掉线。
/// </summary>
internal sealed class PipeHostClient : IDisposable
{
    private const int MaxFrameBytes = 1024 * 1024;
    private readonly string _instanceDir;
    private readonly SemaphoreSlim _callLock = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private string? _sessionToken;
    private string? _hostInstanceId;
    private int _connectedPid;
    private JsonObject? _helloAck;

    public PipeHostClient(string? instanceDir = null)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _instanceDir = !string.IsNullOrEmpty(instanceDir)
            ? instanceDir
            : Path.Combine(local, "EngineeringPlatform", "instances");
    }

    public string? HostInstanceId => _hostInstanceId;
    public JsonObject? LastHelloAck => _helloAck;

    public List<JsonObject> ListInstances()
    {
        var list = new List<JsonObject>();
        if (!Directory.Exists(_instanceDir)) return list;

        foreach (var file in Directory.GetFiles(_instanceDir, "*.json"))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject;
                if (node == null) continue;
                int pid = node["pid"]?.GetValue<int>() ?? -1;
                string? app = node["app"]?.GetValue<string>();
                bool alive = IsProcessAlive(pid, app);
                if (!alive)
                {
                    // 自动淘汰超过 60 秒的已退出 CAD 残留发现文件，避免 instances/ 无限堆积
                    try
                    {
                        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalSeconds > 60)
                        {
                            File.Delete(file);
                            continue;
                        }
                    }
                    catch { }
                }
                node["alive"] = alive;
                node["discoveryFile"] = Path.GetFileName(file);
                list.Add(node);
            }
            catch
            {
                // 跳过损坏的发现文件
            }
        }
        return list.OrderByDescending(n => n["alive"]?.GetValue<bool>() == true)
                   .ThenByDescending(n => n["startedAtUtc"]?.GetValue<string>() ?? "")
                   .ToList();
    }

    public async Task<JsonObject> CallToolAsync(
        string tool,
        JsonObject? arguments,
        string opId,
        int timeoutMs = 60000,
        CancellationToken ct = default,
        int? targetPid = null,
        string? targetInstanceId = null)
    {
        await _callLock.WaitAsync(ct);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(ct, targetPid, targetInstanceId);
                    var req = new JsonObject
                    {
                        ["protocolVersion"] = "1.0",
                        ["opId"] = opId,
                        ["sessionToken"] = _sessionToken,
                        ["tool"] = tool,
                        ["params"] = arguments?.DeepClone() ?? new JsonObject(),
                        ["paramsDigest"] = CanonicalJson.ComputeParamsDigest(arguments),
                        ["timeoutMs"] = timeoutMs,
                        ["cancelable"] = true
                    };
                    await WriteFrameAsync(_pipe!, req, ct);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(Math.Max(timeoutMs + 3000, 10000));
                    return await ReadFrameAsync(_pipe!, timeoutCts.Token);
                }
                catch (Exception ex) when (attempt == 0 && (ex is IOException || ex is EndOfStreamException || ex is InvalidOperationException))
                {
                    // CAD 重启或管道断开：清理旧连接并在下一次循环自动重连
                    ResetConnection();
                }
            }
            throw new IOException("host_pipe_reconnect_failed");
        }
        finally
        {
            _callLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct, int? targetPid = null, string? targetInstanceId = null)
    {
        bool sameTarget =
            (targetPid == null || targetPid.Value == _connectedPid) &&
            (string.IsNullOrEmpty(targetInstanceId) || string.Equals(targetInstanceId, _hostInstanceId, StringComparison.OrdinalIgnoreCase));

        if (_pipe != null && _pipe.IsConnected && !string.IsNullOrEmpty(_sessionToken) && sameTarget)
        {
            return;
        }
        ResetConnection();

        var alive = ListInstances().Where(i => i["alive"]?.GetValue<bool>() == true).ToList();
        if (targetPid != null)
        {
            alive = alive.Where(i => (i["pid"]?.GetValue<int>() ?? 0) == targetPid.Value).ToList();
        }
        if (!string.IsNullOrEmpty(targetInstanceId))
        {
            alive = alive.Where(i =>
                string.Equals(i["hostInstanceId"]?.GetValue<string>(), targetInstanceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i["discoveryFile"]?.GetValue<string>(), targetInstanceId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (alive.Count == 0)
        {
            string hint = targetPid != null ? $"（指定 PID={targetPid}）"
                : !string.IsNullOrEmpty(targetInstanceId) ? $"（指定实例={targetInstanceId}）"
                : "";
            throw new HostUnavailableException("C1001", $"未发现匹配的运行中 AutoCAD / Civil 3D 插件宿主实例{hint}。");
        }

        Exception? lastErr = null;
        foreach (var inst in alive)
        {
            int instPid = inst["pid"]?.GetValue<int>() ?? 0;
            string? fullPipe = inst["pipeName"]?.GetValue<string>();
            if (string.IsNullOrEmpty(fullPipe)) continue;
            string pipeShort = fullPipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
                ? fullPipe.Substring(@"\\.\pipe\".Length)
                : fullPipe;

            var stream = new NamedPipeClientStream(".", pipeShort, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync(3000, ct);
                string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var hello = new JsonObject
                {
                    ["kind"] = "hello",
                    ["protocolVersion"] = "1.0",
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "ep-mcp-gateway",
                        ["version"] = McpServer.ServerVersion,
                        ["pid"] = Environment.ProcessId
                    },
                    ["clientNonce"] = nonce,
                    ["requestedFeatures"] = new JsonArray("preview", "cancel", "heartbeat", "taskReconcile")
                };

                await WriteFrameAsync(stream, hello, ct);
                var ack = await ReadFrameAsync(stream, ct);
                if (!string.Equals(ack["kind"]?.GetValue<string>(), "hello_ack", StringComparison.Ordinal))
                {
                    stream.Dispose();
                    lastErr = new InvalidOperationException("handshake_rejected:" + ack.ToJsonString());
                    continue;
                }

                _pipe = stream;
                _sessionToken = ack["sessionToken"]?.GetValue<string>();
                _hostInstanceId = ack["hostInstanceId"]?.GetValue<string>() ?? inst["hostInstanceId"]?.GetValue<string>();
                _connectedPid = instPid;
                _helloAck = ack;
                return;
            }
            catch (Exception ex)
            {
                stream.Dispose();
                lastErr = ex;
            }
        }

        throw new HostUnavailableException("C1002", "无法连接到 CAD 宿主命名管道：" + (lastErr?.Message ?? "unknown"));
    }

    private static async Task WriteFrameAsync(Stream stream, JsonObject frame, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(CanonicalJson.SerializeCompact(frame));
        if (body.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException("frame_exceeds_1mb");
        }
        byte[] header = BitConverter.GetBytes((uint)body.Length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(header);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<JsonObject> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = await ReadExactAsync(stream, 4, ct);
        if (!BitConverter.IsLittleEndian) Array.Reverse(header);
        uint len = BitConverter.ToUInt32(header, 0);
        if (len == 0 || len > MaxFrameBytes)
        {
            throw new IOException($"invalid_frame_length:{len}");
        }
        byte[] body = await ReadExactAsync(stream, (int)len, ct);
        var node = JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject;
        return node ?? throw new IOException("malformed_json_frame");
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (n <= 0) throw new EndOfStreamException("pipe_closed");
            offset += n;
        }
        return buf;
    }

    private static bool IsProcessAlive(int pid, string? expectedApp = null)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;
            if (!string.IsNullOrEmpty(expectedApp) &&
                expectedApp.Equals("acad", StringComparison.OrdinalIgnoreCase))
            {
                string pn = proc.ProcessName;
                // 防止 Windows PID 复用误判：acad 实例文件对应进程名必须是 acad/accoreconsole/python(mockhost)
                if (!pn.Contains("acad", StringComparison.OrdinalIgnoreCase) &&
                    !pn.Contains("accoreconsole", StringComparison.OrdinalIgnoreCase) &&
                    !pn.Contains("python", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ResetConnection()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _sessionToken = null;
        _connectedPid = 0;
        _hostInstanceId = null;
    }

    public void Dispose()
    {
        ResetConnection();
        _callLock.Dispose();
    }
}

internal sealed class HostUnavailableException : Exception
{
    public string Code { get; }
    public HostUnavailableException(string code, string message) : base(message) => Code = code;
}
