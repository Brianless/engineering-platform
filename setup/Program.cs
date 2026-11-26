using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EngineeringPlatform.Setup;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        // 无参数双击：启动原生 Windows 图形安装向导
        if (args.Length == 0)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, ex) =>
            {
                MessageBox.Show(ex.Exception.Message, "Engineering Platform — 界面提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }

        // 有命令行参数：挂载父控制台执行静默 CLI（支持脚本/批量部署/自动化验证）
        if (!Console.IsOutputRedirected)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }

        return RunCli(args);
    }

    private static int RunCli(string[] args)
    {
        bool listOnly = false;
        bool jsonOut = false;
        bool selectAll = false;
        bool civilOnly = false;
        bool pluginOnly = false;
        bool uninstall = false;
        bool dryRun = false;
        bool noAutoWrite = false;
        bool noCert = false;
        bool trustRoot = false;
        bool autostart = false;
        bool noGateway = false;
        string? selectPattern = null;
        string? payloadDir = null;
        int port = 18080;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    PrintUsage();
                    return 0;
                case "--list":
                    listOnly = true;
                    break;
                case "--json":
                    jsonOut = true;
                    break;
                case "--all":
                    selectAll = true;
                    break;
                case "--civil-only":
                    civilOnly = true;
                    break;
                case "--plugin-only":
                    pluginOnly = true;
                    break;
                case "--select":
                    if (i + 1 < args.Length) selectPattern = args[++i];
                    break;
                case "--uninstall":
                    uninstall = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-auto-write":
                    noAutoWrite = true;
                    break;
                case "--no-cert":
                    noCert = true;
                    break;
                case "--trust-root":
                    trustRoot = true;
                    break;
                case "--autostart":
                    autostart = true;
                    break;
                case "--no-gateway":
                    noGateway = true;
                    break;
                case "--payload-dir":
                    if (i + 1 < args.Length) payloadDir = args[++i];
                    break;
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int p)) port = p;
                    break;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}（使用 --help 查看说明）");
                    return 2;
            }
        }

        var allCads = CadDiscovery.DiscoverAll();

        if (listOnly)
        {
            if (jsonOut)
            {
                var payload = allCads.Select(c => new
                {
                    id = c.Id,
                    vendor = c.Vendor,
                    productName = c.ProductName,
                    release = c.Release,
                    subKey = c.SubKey,
                    installPath = c.InstallPath,
                    isCivil3D = c.IsCivil3D,
                    driverMode = c.DriverModeCode,
                    isConfigured = c.IsConfigured,
                    configuredLoader = c.ConfiguredLoader,
                    runningPid = c.RunningPid
                });
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine($"发现本机 CAD 共 {allCads.Count} 个：");
                for (int idx = 0; idx < allCads.Count; idx++)
                {
                    var c = allCads[idx];
                    Console.WriteLine($"  [{idx + 1}] {c.ProductName} | {c.Release} | {c.DriverModeLabel} | {c.StatusLabel}");
                    Console.WriteLine($"      ID={c.Id} | Path={c.InstallPath}");
                }
            }
            return 0;
        }

        Action<string, LogLevel> logger = (msg, lvl) =>
        {
            string tag = lvl switch
            {
                LogLevel.Success => "[OK]  ",
                LogLevel.Warn => "[WARN]",
                LogLevel.Error => "[ERR] ",
                _ => "[INFO]"
            };
            Console.WriteLine($"{tag} {msg}");
        };

        if (uninstall)
        {
            CadInstallerEngine.UninstallAll(allCads, dryRun, logger);
            return 0;
        }

        List<CadTargetInfo> selected;
        if (selectAll)
        {
            selected = allCads.ToList();
        }
        else if (civilOnly)
        {
            selected = allCads.Where(c => c.IsCivil3D).ToList();
        }
        else if (pluginOnly)
        {
            selected = allCads.Where(c => c.DriverMode != CadDriverMode.ComGeneric).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(selectPattern))
        {
            var tokens = selectPattern.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            selected = allCads.Where(c => tokens.Any(t =>
                c.Id.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                c.ProductName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                c.Release.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                c.InstallPath.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (selected.Count == 0)
            {
                Console.Error.WriteLine($"未匹配到任何 CAD（筛选条件：{selectPattern}）。可先执行 --list 查看本机 CAD。");
                return 1;
            }
        }
        else
        {
            PrintUsage();
            return 1;
        }

        var opts = new InstallOptions
        {
            DeployGateway = !noGateway,
            EnableAutoWrite = !noAutoWrite,
            TrustCodeSignCert = !noCert,
            TrustRootCert = trustRoot,
            EnableGatewayAutostart = autostart,
            GenerateClientConfig = true,
            CleanGlobalBundleOnSelective = true,
            DryRun = dryRun,
            CustomPayloadDir = payloadDir,
            GatewayPort = port
        };

        CadInstallerEngine.ApplyConfiguration(allCads, selected, opts, logger);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
Engineering Platform v1.0.0 — 全版本 CAD 一体化安装配置程序

用法：
  EngineeringPlatform-Setup.exe                       打开图形化勾选安装向导（推荐）
  EngineeringPlatform-Setup.exe --list [--json]       扫描并列出本机全部 AutoCAD / Civil 3D / 中望 / 浩辰
  EngineeringPlatform-Setup.exe --all                 一键配置到本机全部 CAD（自动分派插件/COM）
  EngineeringPlatform-Setup.exe --select "条件1,条件2" 精准配置指定一个或多个 CAD（按名称/版本号/ID 匹配）
  EngineeringPlatform-Setup.exe --civil-only          仅配置所有 Civil 3D 实例
  EngineeringPlatform-Setup.exe --plugin-only         仅配置所有满血插件兼容版（2017–2026）
  EngineeringPlatform-Setup.exe --uninstall           一键卸载全部 CAD 自动加载配置

附加选项：
  --dry-run            演练模式（只打印变更计划，不写入磁盘和注册表）
  --port <端口号>      指定 ep-mcp.exe 网关端口（默认 18080）
  --no-auto-write      关闭全自主静默写模式
  --no-cert            跳过证书信任导入
  --trust-root         同步导入签名证书到 CurrentUser\Root（新电脑零提示）
  --autostart          启用 ep-mcp.exe 开机后台自启
  --no-gateway         跳过 ep-mcp.exe 网关部署
""");
    }
}
