using System.Security.Cryptography;
using System.Text;

namespace EngineeringPlatform.Gateway;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        int port = ParseIntEnv("EP_MCP_PORT", 18080);
        bool stdio = false;
        bool noAuth = false;
        string? explicitToken = Environment.GetEnvironmentVariable("EP_MCP_TOKEN");
        string? instanceDir = Environment.GetEnvironmentVariable("EP_INSTANCE_DIR");
        string? dbPath = Environment.GetEnvironmentVariable("EP_PROJECT_DB");
        string? backend = Environment.GetEnvironmentVariable("EP_BACKEND");

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version":
                case "-v":
                    Console.WriteLine($"{McpServer.ServerName} {McpServer.ServerVersion}");
                    return 0;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                case "--stdio":
                    stdio = true;
                    break;
                case "--no-auth":
                    noAuth = true;
                    break;
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int p) && p > 0 && p < 65536) port = p;
                    break;
                case "--token" when i + 1 < args.Length:
                    explicitToken = args[++i];
                    break;
                case "--instance-dir" when i + 1 < args.Length:
                    instanceDir = args[++i];
                    break;
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--backend" when i + 1 < args.Length:
                    backend = args[++i];
                    break;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var host = new PipeHostClient(instanceDir);
        var offline = new OfflineDwgDriver();
        var com = new ComCadDriver(offline);
        using var router = new BackendRouter(host, com, offline, backend);
        using var store = new ResultStore(dbPath);
        using var server = new McpServer(router, host, store);

        if (stdio)
        {
            await server.RunStdioAsync(cts.Token);
            return 0;
        }

        string? token = noAuth ? null : LoadOrCreateBearerToken(explicitToken);
        await server.RunHttpAsync(port, token, !noAuth, cts.Token);
        return 0;
    }

    private static string LoadOrCreateBearerToken(string? explicitToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(local, "EngineeringPlatform");
        string file = Path.Combine(dir, "mcp-token.txt");
        Directory.CreateDirectory(dir);
        if (File.Exists(file))
        {
            string existing = File.ReadAllText(file, Encoding.UTF8).Trim();
            if (existing.Length >= 32) return existing;
        }

        string generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(file, generated, new UTF8Encoding(false));
        Console.Error.WriteLine($"[ep-mcp] Generated 256-bit Bearer token -> {file}");
        return generated;
    }

    private static int ParseIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) && v > 0 && v < 65536 ? v : fallback;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"ep-mcp v{McpServer.ServerVersion} — Engineering Platform 统一多驱动 MCP 网关 (.NET 8 单文件)");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ep-mcp.exe [--port 18080] [--token <bearer>] [--no-auth] [--backend auto|plugin|com|offline]");
        Console.WriteLine("  ep-mcp.exe --stdio [--backend auto|plugin|com|offline]");
        Console.WriteLine("  ep-mcp.exe --version | --help");
        Console.WriteLine();
        Console.WriteLine("可选参数与环境变量（零硬编码，CLI 优先于环境变量）:");
        Console.WriteLine("  --port <端口>          | EP_MCP_PORT         HTTP 监听端口（默认 18080）");
        Console.WriteLine("  --token <令牌>         | EP_MCP_TOKEN        自定义 Bearer Token（默认自动生成于 %LOCALAPPDATA%\\EngineeringPlatform\\mcp-token.txt）");
        Console.WriteLine("  --backend <模式>       | EP_BACKEND          驱动模式：auto（默认）/ plugin / com / offline");
        Console.WriteLine("  --instance-dir <目录>  | EP_INSTANCE_DIR     自定义 CAD 插件实例发现目录");
        Console.WriteLine("  --db <路径>            | EP_PROJECT_DB       自定义 SQLite 结果与审计库路径");
        Console.WriteLine("  (环境变量)             | EP_TOOL_TIMEOUT_MS  普通插件工具超时毫秒（默认 60000）");
        Console.WriteLine("  (环境变量)             | EP_HEAVY_TIMEOUT_MS 重计算工具超时毫秒（默认 180000）");
        Console.WriteLine("  (环境变量)             | EP_CASS_DIR         自定义南方 CASS 安装目录（用于 .cuix 命令补扫）");
    }
}
