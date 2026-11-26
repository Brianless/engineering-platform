using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EngineeringPlatform.Setup;

internal enum CadDriverMode
{
    PluginNet8,
    PluginNet48,
    ComGeneric
}

internal sealed class CadTargetInfo
{
    public required string Id { get; init; }
    public required string Vendor { get; init; }        // Autodesk | ZWSOFT | Gstarsoft | Custom
    public required string ProductName { get; init; }
    public required string Release { get; init; }       // e.g. R25.0, R24.3, R19.1, 2024
    public required string SubKey { get; init; }        // e.g. ACAD-8100:804
    public required string InstallPath { get; init; }
    public required bool IsCivil3D { get; init; }
    public required CadDriverMode DriverMode { get; init; }
    public bool IsConfigured { get; set; }
    public string? ConfiguredLoader { get; set; }
    public int? RunningPid { get; set; }
    public bool IsManual { get; init; }

    public string DriverModeLabel => DriverMode switch
    {
        CadDriverMode.PluginNet8 => "满血插件 (.NET 8)",
        CadDriverMode.PluginNet48 => "满血插件 (.NET 4.8)",
        _ => "通用 COM (免插件)"
    };

    public string DriverModeCode => DriverMode switch
    {
        CadDriverMode.PluginNet8 => "plugin_net8",
        CadDriverMode.PluginNet48 => "plugin_net48",
        _ => "com_generic"
    };

    public string StatusLabel
    {
        get
        {
            string baseState = IsConfigured ? "已配置" : "未配置";
            if (RunningPid.HasValue) return $"{baseState} · 运行中(PID:{RunningPid.Value})";
            return baseState;
        }
    }

    public string RegistryApplicationsPath =>
        Vendor == "Autodesk" && !string.IsNullOrEmpty(Release) && !string.IsNullOrEmpty(SubKey)
            ? $@"Software\Autodesk\AutoCAD\{Release}\{SubKey}\Applications"
            : "";

    public string RegistryProductRootPath =>
        Vendor == "Autodesk" && !string.IsNullOrEmpty(Release) && !string.IsNullOrEmpty(SubKey)
            ? $@"Software\Autodesk\AutoCAD\{Release}\{SubKey}"
            : "";
}

internal static class CadDiscovery
{
    public static List<CadTargetInfo> DiscoverAll()
    {
        var list = new List<CadTargetInfo>();
        var runningByDir = SnapshotRunningCadProcesses();
        var savedComIds = LoadSavedTargetIds();

        DiscoverAutodesk(list, runningByDir);
        DiscoverZwCad(list, runningByDir, savedComIds);
        DiscoverGstarCad(list, runningByDir, savedComIds);

        // 对 COM 类型标注是否已保存在 cad-targets.json
        foreach (var item in list)
        {
            if (item.DriverMode == CadDriverMode.ComGeneric && savedComIds.Contains(item.Id))
            {
                item.IsConfigured = true;
            }
        }

        // 排序：Civil 3D 优先 + 新版本优先
        list.Sort((a, b) =>
        {
            int c = b.IsCivil3D.CompareTo(a.IsCivil3D);
            if (c != 0) return c;
            int m = a.DriverMode.CompareTo(b.DriverMode);
            if (m != 0) return m;
            return string.Compare(b.Release, a.Release, StringComparison.OrdinalIgnoreCase);
        });

        return list;
    }

    public static CadTargetInfo CreateManualTarget(string exeOrDir)
    {
        string dir = Directory.Exists(exeOrDir) ? exeOrDir : (Path.GetDirectoryName(exeOrDir) ?? exeOrDir);
        string exeName = File.Exists(exeOrDir) ? Path.GetFileNameWithoutExtension(exeOrDir) : "CAD";
        string vendor = exeName.Contains("zwcad", StringComparison.OrdinalIgnoreCase) ? "ZWSOFT"
            : exeName.Contains("gcad", StringComparison.OrdinalIgnoreCase) ? "Gstarsoft"
            : "Custom";
        return new CadTargetInfo
        {
            Id = $"custom:{dir.TrimEnd('\\').ToLowerInvariant()}",
            Vendor = vendor,
            ProductName = $"自定义 CAD ({Path.GetFileName(dir.TrimEnd('\\'))})",
            Release = "Custom",
            SubKey = "",
            InstallPath = dir,
            IsCivil3D = dir.Contains("civil", StringComparison.OrdinalIgnoreCase),
            DriverMode = CadDriverMode.ComGeneric,
            IsConfigured = false,
            IsManual = true
        };
    }

    private static void DiscoverAutodesk(List<CadTargetInfo> list, Dictionary<string, int> runningByDir)
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasGlobalBundle = File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "ApplicationPlugins", "EngineeringPlatform.bundle", "PackageContents.xml"));

        // 1) 扫描 HKLM\SOFTWARE\Autodesk\AutoCAD（权威安装清单）
        ScanAutodeskRoot(Registry.LocalMachine, @"SOFTWARE\Autodesk\AutoCAD", list, seenKeys, runningByDir, hasGlobalBundle);
        // 2) 补扫 HKCU\Software\Autodesk\AutoCAD（防止绿色/仅用户态注册漏检）
        ScanAutodeskRoot(Registry.CurrentUser, @"Software\Autodesk\AutoCAD", list, seenKeys, runningByDir, hasGlobalBundle);
    }

    private static void ScanAutodeskRoot(
        RegistryKey hive,
        string rootPath,
        List<CadTargetInfo> list,
        HashSet<string> seenKeys,
        Dictionary<string, int> runningByDir,
        bool hasGlobalBundle)
    {
        using var root = hive.OpenSubKey(rootPath);
        if (root == null) return;

        foreach (string release in root.GetSubKeyNames())
        {
            if (!release.StartsWith("R", StringComparison.OrdinalIgnoreCase)) continue;
            using var relKey = root.OpenSubKey(release);
            if (relKey == null) continue;

            foreach (string subKey in relKey.GetSubKeyNames())
            {
                if (!subKey.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase)) continue;
                // 跳过不带语言后缀的占位键（如 ACAD-D001）若已有 ACAD-D001:804
                if (!subKey.Contains(':')) continue;

                string uniq = $"{release}\\{subKey}";
                if (!seenKeys.Add(uniq)) continue;

                using var prodKey = relKey.OpenSubKey(subKey);
                if (prodKey == null) continue;

                string? productName = prodKey.GetValue("ProductName") as string;
                string? acadLoc = prodKey.GetValue("AcadLocation") as string;

                // 如果是从 HKCU 补扫，尝试从 HKLM 补全安装路径
                if (string.IsNullOrWhiteSpace(acadLoc) && hive == Registry.CurrentUser)
                {
                    using var lmProd = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Autodesk\AutoCAD\{release}\{subKey}");
                    productName ??= lmProd?.GetValue("ProductName") as string;
                    acadLoc = lmProd?.GetValue("AcadLocation") as string;
                }

                // 过滤已卸载的幽灵注册表项（若路径存在则校验 acad.exe）
                if (string.IsNullOrWhiteSpace(acadLoc) || !Directory.Exists(acadLoc)) continue;

                bool isCivil3D =
                    (productName?.Contains("Civil 3D", StringComparison.OrdinalIgnoreCase) == true) ||
                    acadLoc.Contains("Civil", StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(Path.Combine(acadLoc, "C3D", "AeccDbMgd.dll")) ||
                    File.Exists(Path.Combine(acadLoc, "AeccDbMgd.dll"));

                CadDriverMode mode = ClassifyAutodeskMode(release);
                string displayName = !string.IsNullOrWhiteSpace(productName)
                    ? productName.Trim()
                    : $"{(isCivil3D ? "Autodesk Civil 3D" : "AutoCAD")} ({release})";

                // 检测当前是否已配置
                string? configuredLoader = null;
                bool isConfigured = false;
                if (mode != CadDriverMode.ComGeneric)
                {
                    using var appKey = Registry.CurrentUser.OpenSubKey(
                        $@"Software\Autodesk\AutoCAD\{release}\{subKey}\Applications\EngineeringPlatform");
                    configuredLoader = appKey?.GetValue("LOADER") as string;
                    if (!string.IsNullOrEmpty(configuredLoader) && File.Exists(configuredLoader))
                    {
                        isConfigured = true;
                    }
                    else if (hasGlobalBundle)
                    {
                        isConfigured = true;
                        configuredLoader = "(ApplicationPlugins 全局包)";
                    }
                }

                int? runningPid = MatchRunningPid(acadLoc, runningByDir);

                list.Add(new CadTargetInfo
                {
                    Id = $"autodesk:{release}:{subKey}",
                    Vendor = "Autodesk",
                    ProductName = displayName,
                    Release = release,
                    SubKey = subKey,
                    InstallPath = acadLoc.TrimEnd('\\'),
                    IsCivil3D = isCivil3D,
                    DriverMode = mode,
                    IsConfigured = isConfigured,
                    ConfiguredLoader = configuredLoader,
                    RunningPid = runningPid
                });
            }
        }
    }

    private static void DiscoverZwCad(
        List<CadTargetInfo> list,
        Dictionary<string, int> runningByDir,
        HashSet<string> savedIds)
    {
        foreach (string regPath in new[] { @"SOFTWARE\ZWSOFT\ZWCAD", @"SOFTWARE\WOW6432Node\ZWSOFT\ZWCAD" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(regPath);
            if (root == null) continue;
            foreach (string ver in root.GetSubKeyNames())
            {
                using var verKey = root.OpenSubKey(ver);
                if (verKey == null) continue;
                foreach (string lang in verKey.GetSubKeyNames())
                {
                    using var lk = verKey.OpenSubKey(lang);
                    string? loc = (lk?.GetValue("Location") ?? lk?.GetValue("InstallDir") ?? verKey.GetValue("Location")) as string;
                    if (string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc)) continue;
                    string id = $"zwcad:{ver}:{lang}";
                    if (list.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new CadTargetInfo
                    {
                        Id = id,
                        Vendor = "ZWSOFT",
                        ProductName = $"中望 CAD (ZWCAD {ver})",
                        Release = ver,
                        SubKey = lang,
                        InstallPath = loc.TrimEnd('\\'),
                        IsCivil3D = false,
                        DriverMode = CadDriverMode.ComGeneric,
                        IsConfigured = savedIds.Contains(id),
                        RunningPid = MatchRunningPid(loc, runningByDir)
                    });
                }
            }
        }
    }

    private static void DiscoverGstarCad(
        List<CadTargetInfo> list,
        Dictionary<string, int> runningByDir,
        HashSet<string> savedIds)
    {
        foreach (string regPath in new[] { @"SOFTWARE\Gstarsoft\GstarCAD", @"SOFTWARE\WOW6432Node\Gstarsoft\GstarCAD" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(regPath);
            if (root == null) continue;
            foreach (string ver in root.GetSubKeyNames())
            {
                using var verKey = root.OpenSubKey(ver);
                if (verKey == null) continue;
                string? loc = (verKey.GetValue("Location") ?? verKey.GetValue("InstallPath")) as string;
                if (string.IsNullOrWhiteSpace(loc))
                {
                    foreach (string sub in verKey.GetSubKeyNames())
                    {
                        using var sk = verKey.OpenSubKey(sub);
                        loc = (sk?.GetValue("Location") ?? sk?.GetValue("InstallPath")) as string;
                        if (!string.IsNullOrWhiteSpace(loc)) break;
                    }
                }
                if (string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc)) continue;
                string id = $"gstarcad:{ver}";
                if (list.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new CadTargetInfo
                {
                    Id = id,
                    Vendor = "Gstarsoft",
                    ProductName = $"浩辰 CAD (GstarCAD {ver})",
                    Release = ver,
                    SubKey = ver,
                    InstallPath = loc.TrimEnd('\\'),
                    IsCivil3D = false,
                    DriverMode = CadDriverMode.ComGeneric,
                    IsConfigured = savedIds.Contains(id),
                    RunningPid = MatchRunningPid(loc, runningByDir)
                });
            }
        }
    }

    internal static CadDriverMode ClassifyAutodeskMode(string release)
    {
        var m = Regex.Match(release ?? "", @"R(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);
        if (!m.Success) return CadDriverMode.ComGeneric;
        int major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (major >= 25) return CadDriverMode.PluginNet8;   // AutoCAD / Civil 3D 2025+ (.NET 8)
        if (major >= 21) return CadDriverMode.PluginNet48;  // AutoCAD / Civil 3D 2017–2024 (.NET 4.8)
        return CadDriverMode.ComGeneric;                    // AutoCAD 2010–2016 (免插件通用 COM)
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static string? TryGetProcessExePath(Process proc)
    {
        try
        {
            string? exe = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe)) return exe;
        }
        catch { }

        // 跨提权边界回退：PROCESS_QUERY_LIMITED_INFORMATION + QueryFullProcessImageName 支持标准权限读取管理员 CAD 路径
        IntPtr hProc = IntPtr.Zero;
        try
        {
            hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, proc.Id);
            if (hProc != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                int cap = sb.Capacity;
                if (QueryFullProcessImageName(hProc, 0, sb, ref cap) && cap > 0)
                {
                    return sb.ToString();
                }
            }
        }
        catch { }
        finally
        {
            if (hProc != IntPtr.Zero) CloseHandle(hProc);
        }
        return null;
    }

    private static Dictionary<string, int> SnapshotRunningCadProcesses()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "acad", "zwcad", "gcad", "gstarcad" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string? exe = TryGetProcessExePath(proc);
                        if (!string.IsNullOrEmpty(exe))
                        {
                            string? dir = Path.GetDirectoryName(exe);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                map[dir.TrimEnd('\\')] = proc.Id;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return map;
    }

    private static int? MatchRunningPid(string installPath, Dictionary<string, int> runningByDir)
    {
        string norm = installPath.TrimEnd('\\');
        if (runningByDir.TryGetValue(norm, out int pid)) return pid;
        return null;
    }

    private static HashSet<string> LoadSavedTargetIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EngineeringPlatform", "cad-targets.json");
            if (!File.Exists(path)) return set;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("targets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    {
                        string? id = idEl.GetString();
                        if (!string.IsNullOrEmpty(id)) set.Add(id);
                    }
                }
            }
        }
        catch { }
        return set;
    }
}
