using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace EngineeringPlatform.Setup;

internal enum LogLevel
{
    Info,
    Success,
    Warn,
    Error
}

internal sealed class InstallOptions
{
    public bool DeployGateway { get; set; } = true;
    public bool EnableAutoWrite { get; set; } = true;
    public bool TrustCodeSignCert { get; set; } = true;
    public bool TrustRootCert { get; set; } = true;
    public bool EnableGatewayAutostart { get; set; } = false;
    public bool GenerateClientConfig { get; set; } = true;
    public bool CleanGlobalBundleOnSelective { get; set; } = true;
    public bool DryRun { get; set; }
    public string? CustomPayloadDir { get; set; }
    public int GatewayPort { get; set; } = 18080;
}

internal sealed record InstallSummary(
    int ConfiguredPluginCount,
    int ConfiguredComCount,
    int RemovedCount,
    int Warnings,
    string GatewayExePath,
    string ClientConfigPath);

internal static class CadInstallerEngine
{
    public static string EpRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EngineeringPlatform");

    public static string HostNet48Dir => Path.Combine(EpRoot, "host", "net48");
    public static string HostNet8Dir => Path.Combine(EpRoot, "host", "net8");
    public static string BinDir => Path.Combine(EpRoot, "bin");
    public static string RouteSolverDir => Path.Combine(EpRoot, "route_solver");
    public static string InstallerDir => Path.Combine(EpRoot, "installer");
    public static string GatewayExePath => Path.Combine(BinDir, "ep-mcp.exe");
    public static string ConfigJsonPath => Path.Combine(EpRoot, "config.json");
    public static string TargetsJsonPath => Path.Combine(EpRoot, "cad-targets.json");
    public static string ClientConfigPath => Path.Combine(EpRoot, "mcp-client-config.json");
    public static string McpTokenPath => Path.Combine(EpRoot, "mcp-token.txt");
    public static string GlobalBundleDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "ApplicationPlugins", "EngineeringPlatform.bundle");

    public static InstallSummary ApplyConfiguration(
        IReadOnlyList<CadTargetInfo> allDiscovered,
        IReadOnlyList<CadTargetInfo> selected,
        InstallOptions opts,
        Action<string, LogLevel> log)
    {
        int warnings = 0;
        var selectedIds = new HashSet<string>(selected.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        bool needsNet48 = selected.Any(x => x.DriverMode == CadDriverMode.PluginNet48);
        bool needsNet8 = selected.Any(x => x.DriverMode == CadDriverMode.PluginNet8);

        log(opts.DryRun
            ? "=== [演练模式 Dry-Run] 仅展示配置变更，不写入磁盘与注册表 ==="
            : "=== 开始执行 Engineering Platform 多版本 CAD 一键配置 ===", LogLevel.Info);

        // 1) 部署载荷（宿主 DLL + 网关 ep-mcp.exe + 代码签名证书）
        if (needsNet48 || needsNet8 || opts.DeployGateway || opts.TrustCodeSignCert)
        {
            warnings += DeployPayload(needsNet48, needsNet8, opts, log);
        }

        // 2) 导入代码签名证书到当前用户「受信任的发布者」（免管理员 UAC，消除 CAD 安全确认弹窗）
        if (opts.TrustCodeSignCert)
        {
            warnings += InstallCodeSignCert(opts, log);
        }

        // 3) 清理旧全局 ApplicationPlugins .bundle（统一由每 CAD 精准 DemandLoad 接管，避免双加载或未选 CAD 误加载）
        if (opts.CleanGlobalBundleOnSelective && Directory.Exists(GlobalBundleDir))
        {
            warnings += TryRemoveGlobalBundle(opts.DryRun, log);
        }

        // 4) 逐个 CAD 精准写入或移除 DemandLoad + TRUSTEDPATHS
        int configuredPlugin = 0;
        int configuredCom = 0;
        int removed = 0;

        foreach (var cad in allDiscovered)
        {
            bool isChecked = selectedIds.Contains(cad.Id);
            if (cad.DriverMode == CadDriverMode.ComGeneric)
            {
                if (isChecked)
                {
                    configuredCom++;
                    log($"[COM 免插件] 启用 {cad.ProductName} ({cad.Release}) → 由 ep-mcp.exe 通用 COM 驱动接管（零 DLL 注入）", LogLevel.Success);
                }
                continue;
            }

            if (isChecked)
            {
                string dllPath = cad.DriverMode == CadDriverMode.PluginNet8
                    ? Path.Combine(HostNet8Dir, "EngineeringHost.dll")
                    : Path.Combine(HostNet48Dir, "EngineeringHost.dll");
                string trustDir = Path.GetDirectoryName(dllPath)!;

                ConfigureAutodeskDemandLoad(cad, dllPath, trustDir, opts.DryRun, log);
                configuredPlugin++;
            }
            else
            {
                if (RemoveAutodeskDemandLoad(cad, opts.DryRun, log))
                {
                    removed++;
                }
            }
        }

        // 处理用户手动添加的自定义 CAD
        foreach (var cad in selected.Where(x => x.IsManual))
        {
            configuredCom++;
            log($"[自定义 COM] 启用 {cad.ProductName} → {cad.InstallPath}", LogLevel.Success);
        }

        // 5) 写入 config.json / cad-targets.json / mcp-client-config.json / 网关开机自启
        WritePlatformConfig(opts, log);
        WriteCadTargetsManifest(selected, opts, log);
        ConfigureGatewayAutostart(opts, log);
        if (opts.GenerateClientConfig)
        {
            WriteMcpClientConfig(opts, log);
        }

        log($"=== 配置完成：满血插件 {configuredPlugin} 个｜通用 COM {configuredCom} 个｜已取消移除 {removed} 个｜告警 {warnings} 条 ===",
            warnings == 0 ? LogLevel.Success : LogLevel.Warn);

        return new InstallSummary(configuredPlugin, configuredCom, removed, warnings, GatewayExePath, ClientConfigPath);
    }

    public static int UninstallAll(IReadOnlyList<CadTargetInfo> allDiscovered, bool dryRun, Action<string, LogLevel> log)
    {
        int warnings = 0;
        log(dryRun ? "=== [演练模式] 全部卸载预览 ===" : "=== 开始卸载全部 CAD 配置 ===", LogLevel.Info);

        int removed = 0;
        foreach (var cad in allDiscovered.Where(x => x.Vendor == "Autodesk"))
        {
            if (RemoveAutodeskDemandLoad(cad, dryRun, log)) removed++;
        }

        if (Directory.Exists(GlobalBundleDir))
        {
            warnings += TryRemoveGlobalBundle(dryRun, log);
        }

        if (File.Exists(TargetsJsonPath))
        {
            if (dryRun) log($"[Dry-Run] 将删除 {TargetsJsonPath}", LogLevel.Info);
            else { File.Delete(TargetsJsonPath); log($"已清理目标清单：{TargetsJsonPath}", LogLevel.Info); }
        }

        RemoveGatewayAutostart(dryRun, log);

        log($"=== 卸载完成：已移除 {removed} 个 CAD 的自动加载项（告警 {warnings} 条） ===",
            warnings == 0 ? LogLevel.Success : LogLevel.Warn);
        return warnings;
    }

    private static int DeployPayload(bool needsNet48, bool needsNet8, InstallOptions opts, Action<string, LogLevel> log)
    {
        int warnings = 0;
        if (opts.DryRun)
        {
            log($"[Dry-Run] 将部署文件到 {EpRoot}（net48={needsNet48}, net8={needsNet8}, gateway={opts.DeployGateway}）", LogLevel.Info);
            return 0;
        }

        Directory.CreateDirectory(EpRoot);
        Directory.CreateDirectory(BinDir);
        Directory.CreateDirectory(InstallerDir);

        // 优先从内嵌 payload.zip 释放
        var asm = Assembly.GetExecutingAssembly();
        using var zipStream = asm.GetManifestResourceStream("payload.zip");
        if (zipStream != null && string.IsNullOrWhiteSpace(opts.CustomPayloadDir))
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string norm = entry.FullName.Replace('\\', '/');
                string? dest = ResolveZipEntryDestination(norm, needsNet48, needsNet8, opts.DeployGateway, opts.TrustCodeSignCert);
                if (dest == null) continue;

                warnings += ExtractEntrySafe(entry, dest, log);
            }
            log($"已从内置安装包释放最新签名组件（网关 + 宿主插件 + 选线引擎）→ {EpRoot}", LogLevel.Success);
            // 若旧版本在根目录遗留了未统一路径的 ep-mcp.exe，同步覆盖以防旧客户端命中过期网关
            string legacyRootGw = Path.Combine(EpRoot, "ep-mcp.exe");
            if (opts.DeployGateway && File.Exists(legacyRootGw) && File.Exists(GatewayExePath))
            {
                warnings += CopyFileSafe(GatewayExePath, legacyRootGw, log);
            }
            return warnings;
        }

        // 回退：从 CustomPayloadDir 或当前工程目录复制（开发调试模式）
        string? sourceRoot = !string.IsNullOrWhiteSpace(opts.CustomPayloadDir)
            ? opts.CustomPayloadDir
            : FindRepoRoot();
        if (sourceRoot == null)
        {
            log("未找到内置 payload.zip 或本地源码产物目录，请通过 build_setup.ps1 构建完整安装包。", LogLevel.Error);
            return 1;
        }

        if (needsNet48)
        {
            string src48 = Path.Combine(sourceRoot, "host", "bin", "Release", "net48", "EngineeringHost.dll");
            if (File.Exists(src48))
            {
                Directory.CreateDirectory(HostNet48Dir);
                warnings += CopyFileSafe(src48, Path.Combine(HostNet48Dir, "EngineeringHost.dll"), log);
            }
            else { log($"未找到 net48 产物：{src48}", LogLevel.Warn); warnings++; }
        }

        if (needsNet8)
        {
            string src8Dir = Path.Combine(sourceRoot, "host", "bin", "Release", "net8.0-windows");
            if (Directory.Exists(src8Dir))
            {
                Directory.CreateDirectory(HostNet8Dir);
                string[] forbidden = ["acmgd.dll", "acdbmgd.dll", "accoremgd.dll", "AecBaseMgd.dll", "AeccDbMgd.dll"];
                foreach (string dll in Directory.GetFiles(src8Dir, "*.dll"))
                {
                    string name = Path.GetFileName(dll);
                    if (forbidden.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    warnings += CopyFileSafe(dll, Path.Combine(HostNet8Dir, name), log);
                }
            }
            else { log($"未找到 net8 产物目录：{src8Dir}", LogLevel.Warn); warnings++; }
        }

        if (opts.DeployGateway)
        {
            string srcGw = Path.Combine(sourceRoot, "gateway", "publish", "ep-mcp.exe");
            if (File.Exists(srcGw))
            {
                warnings += CopyFileSafe(srcGw, GatewayExePath, log);
            }
            else { log($"未找到网关产物：{srcGw}", LogLevel.Warn); warnings++; }
        }

        if (opts.TrustCodeSignCert)
        {
            string srcCer = Path.Combine(sourceRoot, "tools", "codesign", "EngineeringPlatformDev.cer");
            if (File.Exists(srcCer))
            {
                warnings += CopyFileSafe(srcCer, Path.Combine(InstallerDir, "EngineeringPlatformDev.cer"), log);
            }
        }

        log($"已从开发目录同步组件 → {EpRoot}", LogLevel.Success);
        return warnings;
    }

    private static string? ResolveZipEntryDestination(
        string normPath,
        bool needsNet48,
        bool needsNet8,
        bool deployGateway,
        bool deployCert)
    {
        if (normPath.StartsWith("host/net48/", StringComparison.OrdinalIgnoreCase))
        {
            if (!needsNet48) return null;
            return Path.Combine(HostNet48Dir, normPath["host/net48/".Length..].Replace('/', Path.DirectorySeparatorChar));
        }
        if (normPath.StartsWith("host/net8/", StringComparison.OrdinalIgnoreCase))
        {
            if (!needsNet8) return null;
            return Path.Combine(HostNet8Dir, normPath["host/net8/".Length..].Replace('/', Path.DirectorySeparatorChar));
        }
        if (normPath.Equals("bin/ep-mcp.exe", StringComparison.OrdinalIgnoreCase))
        {
            return deployGateway ? GatewayExePath : null;
        }
        if (normPath.StartsWith("route_solver/", StringComparison.OrdinalIgnoreCase))
        {
            return deployGateway
                ? Path.Combine(RouteSolverDir, normPath["route_solver/".Length..].Replace('/', Path.DirectorySeparatorChar))
                : null;
        }
        if (normPath.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(EpRoot, Path.GetFileName(normPath));
        }
        if (normPath.StartsWith("installer/", StringComparison.OrdinalIgnoreCase))
        {
            return deployCert ? Path.Combine(InstallerDir, Path.GetFileName(normPath)) : null;
        }
        return null;
    }

    private static int ExtractEntrySafe(ZipArchiveEntry entry, string dest, Action<string, LogLevel> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        TryCleanOldBackup(dest);
        try
        {
            entry.ExtractToFile(dest, overwrite: true);
            return 0;
        }
        catch (IOException)
        {
            // Windows NTFS 允许对运行中的 .exe/.dll 做同卷重命名，从而热替换最新版文件
            if (TryStageReplaceLocked(dest, () => entry.ExtractToFile(dest, overwrite: true)))
            {
                log($"目标文件正被旧进程占用，已通过热重命名替换新版：{dest}（重启相关 CAD/MCP 客户端即生效）", LogLevel.Success);
                return 0;
            }
            log($"文件被运行中的进程占用，跳过覆盖：{dest}（关闭相关 CAD/网关后重新点击「一键应用配置」即可更新）", LogLevel.Warn);
            return 1;
        }
    }

    private static int CopyFileSafe(string src, string dest, Action<string, LogLevel> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        TryCleanOldBackup(dest);
        try
        {
            File.Copy(src, dest, overwrite: true);
            return 0;
        }
        catch (IOException)
        {
            if (TryStageReplaceLocked(dest, () => File.Copy(src, dest, overwrite: true)))
            {
                log($"目标文件正被旧进程占用，已通过热重命名替换新版：{dest}（重启相关 CAD/MCP 客户端即生效）", LogLevel.Success);
                return 0;
            }
            log($"文件被运行中的进程占用，跳过覆盖：{dest}", LogLevel.Warn);
            return 1;
        }
    }

    private static void TryCleanOldBackup(string dest)
    {
        try
        {
            string? dir = Path.GetDirectoryName(dest);
            string file = Path.GetFileName(dest);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (string old in Directory.GetFiles(dir, file + ".old*"))
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch { }
    }

    private static bool TryStageReplaceLocked(string dest, Action writeFresh)
    {
        try
        {
            string backup = dest + ".old";
            if (File.Exists(backup))
            {
                try { File.Delete(backup); }
                catch { backup = dest + ".old." + DateTime.UtcNow.Ticks; }
            }
            File.Move(dest, backup);
            writeFresh();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int InstallCodeSignCert(InstallOptions opts, Action<string, LogLevel> log)
    {
        string cerPath = Path.Combine(InstallerDir, "EngineeringPlatformDev.cer");
        if (opts.DryRun)
        {
            log($"[Dry-Run] 将导入代码签名证书到 CurrentUser\\TrustedPublisher：{cerPath}", LogLevel.Info);
            return 0;
        }
        if (!File.Exists(cerPath))
        {
            log("未发现代码签名证书文件，跳过证书信任导入。", LogLevel.Warn);
            return 1;
        }
        try
        {
            using var cert = new X509Certificate2(cerPath);
            using (var pubStore = new X509Store(StoreName.TrustedPublisher, StoreLocation.CurrentUser))
            {
                pubStore.Open(OpenFlags.ReadWrite);
                if (!pubStore.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).OfType<X509Certificate2>().Any())
                {
                    pubStore.Add(cert);
                }
            }
            if (opts.TrustRootCert)
            {
                try
                {
                    using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    rootStore.Open(OpenFlags.ReadWrite);
                    if (!rootStore.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).OfType<X509Certificate2>().Any())
                    {
                        rootStore.Add(cert);
                    }
                    log($"已同步导入根信任证书：{cert.Subject}（CurrentUser\\Root）", LogLevel.Success);
                }
                catch (Exception rex)
                {
                    log($"根证书导入未完成（可能取消了系统确认框，不影响 TRUSTEDPATHS 静默加载）：{rex.Message}", LogLevel.Warn);
                }
            }
            log($"已信任代码签名证书：{cert.Subject}（CurrentUser\\TrustedPublisher）", LogLevel.Success);
            return 0;
        }
        catch (Exception ex)
        {
            log($"导入证书失败（不影响 TRUSTEDPATHS 静默加载）：{ex.Message}", LogLevel.Warn);
            return 1;
        }
    }

    private static int TryRemoveGlobalBundle(bool dryRun, Action<string, LogLevel> log)
    {
        if (dryRun)
        {
            log($"[Dry-Run] 将清理全局 ApplicationPlugins 包以启用精准按 CAD 隔离：{GlobalBundleDir}", LogLevel.Info);
            return 0;
        }
        try
        {
            Directory.Delete(GlobalBundleDir, recursive: true);
            log($"已清理全局 ApplicationPlugins 包（改由每 CAD 精准 DemandLoad 独立控制）", LogLevel.Info);
            return 0;
        }
        catch (Exception ex)
        {
            // 若旧 DLL 被运行中的 acad.exe 锁定，至少尝试删掉 PackageContents.xml 让未选 CAD 不再加载
            try
            {
                string pkgXml = Path.Combine(GlobalBundleDir, "PackageContents.xml");
                if (File.Exists(pkgXml)) File.Delete(pkgXml);
                log($"旧全局包 DLL 正被运行中 CAD 占用，已先禁用 PackageContents.xml（关闭旧 CAD 后可彻底清理目录）。", LogLevel.Warn);
            }
            catch
            {
                log($"无法清理旧全局包（可能被运行中的 CAD 锁定）：{ex.Message}", LogLevel.Warn);
            }
            return 1;
        }
    }

    private static void ConfigureAutodeskDemandLoad(
        CadTargetInfo cad,
        string dllPath,
        string trustDir,
        bool dryRun,
        Action<string, LogLevel> log)
    {
        string appKeyPath = $@"{cad.RegistryApplicationsPath}\EngineeringPlatform";
        string legacyKeyPath = $@"{cad.RegistryApplicationsPath}\EngineeringHost";

        if (dryRun)
        {
            log($"[Dry-Run] 写入 HKCU\\{appKeyPath} → LOADER={dllPath}，并追加 TRUSTEDPATHS={trustDir}", LogLevel.Info);
            return;
        }

        // 1) 写入精准 DemandLoad
        using (var key = Registry.CurrentUser.CreateSubKey(appKeyPath))
        {
            key.SetValue("DESCRIPTION", "Engineering Platform Autonomous Host v1.0.0", RegistryValueKind.String);
            key.SetValue("LOADCTRLS", 3, RegistryValueKind.DWord);
            key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
            key.SetValue("LOADER", dllPath, RegistryValueKind.String);
        }

        // 2) 清理旧重复键 EngineeringHost，防止双加载
        try { Registry.CurrentUser.DeleteSubKeyTree(legacyKeyPath, throwOnMissingSubKey: false); } catch { }

        // 3) 追加 TRUSTEDPATHS 到该 CAD 的全部用户配置 Profile，确保零弹窗静默加载
        int profilesUpdated = AddTrustedPathToProfiles(cad.RegistryProductRootPath, trustDir);

        log($"[满血插件] 已配置 {cad.ProductName} ({cad.Release}) → {Path.GetFileName(Path.GetDirectoryName(dllPath))}\\EngineeringHost.dll（TrustedPaths 更新 {profilesUpdated} 个配置）",
            LogLevel.Success);
    }

    private static bool RemoveAutodeskDemandLoad(CadTargetInfo cad, bool dryRun, Action<string, LogLevel> log)
    {
        if (string.IsNullOrEmpty(cad.RegistryApplicationsPath)) return false;
        string appKeyPath = $@"{cad.RegistryApplicationsPath}\EngineeringPlatform";
        string legacyKeyPath = $@"{cad.RegistryApplicationsPath}\EngineeringHost";

        bool exists = false;
        using (var k1 = Registry.CurrentUser.OpenSubKey(appKeyPath)) { if (k1 != null) exists = true; }
        using (var k2 = Registry.CurrentUser.OpenSubKey(legacyKeyPath)) { if (k2 != null) exists = true; }
        if (!exists) return false;

        if (dryRun)
        {
            log($"[Dry-Run] 移除未勾选 CAD 的加载项：HKCU\\{appKeyPath}", LogLevel.Info);
            return true;
        }

        Registry.CurrentUser.DeleteSubKeyTree(appKeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(legacyKeyPath, throwOnMissingSubKey: false);
        log($"[已移除] 取消 {cad.ProductName} ({cad.Release}) 的自动加载项", LogLevel.Info);
        return true;
    }

    private static int AddTrustedPathToProfiles(string productRootPath, string trustDir)
    {
        int updated = 0;
        try
        {
            using var profilesKey = Registry.CurrentUser.OpenSubKey($@"{productRootPath}\Profiles");
            if (profilesKey == null) return 0;

            string normTrust = trustDir.TrimEnd('\\');
            // AutoCAD 2017–2026 实际从 Profiles\<name>\Variables\TRUSTEDPATHS 读取信任路径（旧版或部分配置也会看 General）
            foreach (string profileName in profilesKey.GetSubKeyNames())
            {
                foreach (string subNode in new[] { "Variables", "General" })
                {
                    try
                    {
                        using var targetKey = Registry.CurrentUser.CreateSubKey($@"{productRootPath}\Profiles\{profileName}\{subNode}");
                        if (targetKey == null) continue;

                        string current = (targetKey.GetValue("TRUSTEDPATHS") as string) ?? "";
                        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0)
                            .ToList();

                        bool already = parts.Any(p =>
                            p.TrimEnd('\\').Equals(normTrust, StringComparison.OrdinalIgnoreCase) ||
                            p.TrimEnd('\\').Equals(normTrust + @"\...", StringComparison.OrdinalIgnoreCase));
                        if (!already)
                        {
                            parts.Add(normTrust);
                            targetKey.SetValue("TRUSTEDPATHS", string.Join(";", parts), RegistryValueKind.String);
                            updated++;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return updated;
    }

    private static void WritePlatformConfig(InstallOptions opts, Action<string, LogLevel> log)
    {
        if (opts.DryRun)
        {
            log($"[Dry-Run] 更新 {ConfigJsonPath}（write.enabled={opts.EnableAutoWrite}, autoApprove={opts.EnableAutoWrite}）", LogLevel.Info);
            return;
        }

        Directory.CreateDirectory(EpRoot);
        JsonObject root;
        if (File.Exists(ConfigJsonPath))
        {
            try { root = JsonNode.Parse(File.ReadAllText(ConfigJsonPath))?.AsObject() ?? new JsonObject(); }
            catch { root = new JsonObject(); }
        }
        else
        {
            root = new JsonObject();
        }

        var writeObj = (root["write"] as JsonObject) ?? new JsonObject();
        writeObj["enabled"] = opts.EnableAutoWrite;
        writeObj["autoApprove"] = opts.EnableAutoWrite;
        writeObj["tokenTtlMs"] ??= 120000;
        root["write"] = writeObj;

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ConfigJsonPath, root.ToJsonString(jsonOpts), new UTF8Encoding(false));
        log($"已写入宿主配置：{ConfigJsonPath}（全自主写模式={opts.EnableAutoWrite}）", LogLevel.Info);
    }

    private static void WriteCadTargetsManifest(
        IReadOnlyList<CadTargetInfo> selected,
        InstallOptions opts,
        Action<string, LogLevel> log)
    {
        if (opts.DryRun)
        {
            log($"[Dry-Run] 写入已选 CAD 清单：{TargetsJsonPath}（共 {selected.Count} 项）", LogLevel.Info);
            return;
        }

        var arr = new JsonArray();
        foreach (var cad in selected)
        {
            arr.Add(new JsonObject
            {
                ["id"] = cad.Id,
                ["vendor"] = cad.Vendor,
                ["productName"] = cad.ProductName,
                ["release"] = cad.Release,
                ["subKey"] = cad.SubKey,
                ["installPath"] = cad.InstallPath,
                ["isCivil3D"] = cad.IsCivil3D,
                ["driverMode"] = cad.DriverModeCode
            });
        }

        var doc = new JsonObject
        {
            ["version"] = "1.0.0",
            ["schemaVersion"] = "1.0",
            ["updatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["gatewayExe"] = GatewayExePath,
            ["routeSolverDir"] = RouteSolverDir,
            ["gatewayPort"] = opts.GatewayPort,
            ["targets"] = arr
        };

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(TargetsJsonPath, doc.ToJsonString(jsonOpts), new UTF8Encoding(false));
        log($"已保存多 CAD 目标清单：{TargetsJsonPath}（{selected.Count} 个 CAD）", LogLevel.Info);
    }

    private static void WriteMcpClientConfig(InstallOptions opts, Action<string, LogLevel> log)
    {
        if (opts.DryRun)
        {
            log($"[Dry-Run] 生成 MCP 客户端接入配置：{ClientConfigPath}", LogLevel.Info);
            return;
        }

        string token = EnsureMcpToken();
        string endpoint = $"http://127.0.0.1:{opts.GatewayPort}/mcp";

        var cfg = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["engineering-platform-http"] = new JsonObject
                {
                    ["url"] = endpoint,
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = $"Bearer {token}"
                    }
                },
                ["engineering-platform-stdio"] = new JsonObject
                {
                    ["command"] = GatewayExePath,
                    ["args"] = new JsonArray("--stdio", "--backend", "auto")
                }
            }
        };

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ClientConfigPath, cfg.ToJsonString(jsonOpts), new UTF8Encoding(false));
        log($"已生成 MCP 客户端配置（HTTP + stdio 双模式）：{ClientConfigPath}", LogLevel.Success);
    }

    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "EngineeringPlatformGateway";

    private static void ConfigureGatewayAutostart(InstallOptions opts, Action<string, LogLevel> log)
    {
        if (!opts.EnableGatewayAutostart)
        {
            RemoveGatewayAutostart(opts.DryRun, log);
            return;
        }
        string cmd = $"\"{GatewayExePath}\" --port {opts.GatewayPort} --backend auto";
        if (opts.DryRun)
        {
            log($"[Dry-Run] 写入开机自启 HKCU\\{RunRegistryPath}\\{RunValueName} = {cmd}", LogLevel.Info);
            return;
        }
        using var runKey = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        runKey.SetValue(RunValueName, cmd, RegistryValueKind.String);
        log($"已启用网关开机自启：{cmd}", LogLevel.Success);
    }

    private static void RemoveGatewayAutostart(bool dryRun, Action<string, LogLevel> log)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: !dryRun);
            if (runKey?.GetValue(RunValueName) == null) return;
            if (dryRun)
            {
                log($"[Dry-Run] 将移除网关开机自启项 HKCU\\{RunRegistryPath}\\{RunValueName}", LogLevel.Info);
                return;
            }
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            log("已取消网关开机自启项。", LogLevel.Info);
        }
        catch { }
    }

    public static bool IsGatewayAutostartEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath);
            return runKey?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    public static string EnsureMcpToken()
    {
        Directory.CreateDirectory(EpRoot);
        if (File.Exists(McpTokenPath))
        {
            string existing = File.ReadAllText(McpTokenPath).Trim();
            if (existing.Length >= 32) return existing;
        }
        string fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(McpTokenPath, fresh, new UTF8Encoding(false));
        return fresh;
    }

    private static string? FindRepoRoot()
    {
        string? cur = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
        {
            if (File.Exists(Path.Combine(cur, "host", "EngineeringHost.csproj"))) return cur;
            cur = Path.GetDirectoryName(cur);
        }
        return null;
    }
}
