using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Exception = System.Exception;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Commands
{
    /// <summary>
    /// 无人值守自动执行器（第三轮新增，用来修 D2）。
    ///
    /// ## 为什么不能靠 `acad.exe /b selftest.scr`
    ///
    /// 实测（2026-11-21 15:06 那次）：CAD 进程 07:06:54 启动，插件 07:08:05 才 Initialize ——
    /// `/b` 脚本比插件早约 70 秒执行。脚本里写 `ENG_SELFTEST` 时命令还没注册，
    /// AutoCAD 把整行当未知命令，随后**中止脚本**（所以连后面的 `_.QUIT` 都没跑）。
    ///
    /// 让脚本里用 LISP 轮询 `(atoms-family 1)` 也不行：LISP 在主线程同步跑循环，
    /// 期间 Application.Idle 不会触发，插件永远等不到加载时机 —— 轮询会把自己等死。
    ///
    /// ## 现在这条路径
    ///
    /// 命令行只传图纸（不带 `/b`），用**环境变量**声明「加载完成后要做什么」：
    ///
    ///   EP_DEVPROBE_OUT=&lt;dir&gt;      → 落 civil-api-probe.json（Civil API 签名取证）
    ///   EP_DEVFIXTURE_OUT=&lt;dir&gt;    → 建 Civil 3D 夹具图并自检（仅限 sandbox）
    ///   EP_SELFTEST_OUT=&lt;dir&gt;      → 跑五个只读工具，落 selftest-*.json + 预览 PNG
    ///   EP_SELFTEST_DWG=&lt;path&gt;     → 只对这张图跑（防止在 Drawing1 上白跑一次）
    ///
    /// 插件 Initialize 时订阅 Application.Idle，等图纸真正打开（Idle 稳定若干拍、
    /// 且 MdiActiveDocument 与 EP_SELFTEST_DWG 对得上）后执行一次，然后自己退订。
    /// 退出由外部 PowerShell 在拿到 JSON 后 Stop-Process 完成（关闭图纸/退出程序不是
    /// 本插件的职责，也不该在这条只读路径上引入任何写操作）。
    ///
    /// 注意：这里是**测试夹具/取证**通道，不进 MCP 工具目录，也没有任何 MCP 可达路径。
    /// </summary>
    internal static class AutoRun
    {
        /// <summary>探针输出目录（Civil API 签名 dump）。</summary>
        public const string EnvProbeOut = "EP_DEVPROBE_OUT";

        /// <summary>夹具输出目录（建曲面/路线后自检）。</summary>
        public const string EnvFixtureOut = "EP_DEVFIXTURE_OUT";

        /// <summary>自检输出目录。</summary>
        public const string EnvSelfTestOut = "EP_SELFTEST_OUT";

        /// <summary>期望自动执行发生在哪张图上（全路径或文件名，大小写不敏感）。</summary>
        public const string EnvSelfTestDwg = "EP_SELFTEST_DWG";

        /// <summary>状态文件名（PowerShell 靠它判定 OK/FAIL，不用解析 CAD 输出）。</summary>
        public const string StatusFileName = "autorun-status.txt";

        private static bool _armed;
        private static bool _finished;
        private static int _idleTicks;

        /// <summary>稳定若干拍再动手：插件刚加载时 MdiActiveDocument 可能还是 Drawing1。</summary>
        private const int IdleWarmup = 3;

        /// <summary>最多等多少拍仍没等到目标图纸，就放弃（避免永远挂着）。</summary>
        private const int IdleGiveUp = 600;

        /// <summary>每隔多少拍打一行排障日志。</summary>
        private const int TraceEvery = 40;

        /// <summary>插件 Initialize 末尾调用；没有声明任何环境变量时什么都不做。</summary>
        public static void TryArm()
        {
            if (_armed)
            {
                return;
            }
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvProbeOut))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvFixtureOut))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvSelfTestOut)))
            {
                return;
            }
            try
            {
                Application.Idle += OnIdle;
                _armed = true;
                HostLog.Info("autorun", "armed",
                    "probe", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvProbeOut)),
                    "fixture", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvFixtureOut)),
                    "selftest", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvSelfTestOut)));
            }
            catch (Exception ex)
            {
                HostLog.Exception("autorun", "arm_failed", ex);
            }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (_finished)
            {
                Disarm();
                return;
            }
            try
            {
                _idleTicks++;
                if (_idleTicks < IdleWarmup)
                {
                    return;
                }

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (_idleTicks == IdleWarmup || _idleTicks % TraceEvery == 0)
                {
                    // 排障用：只打「有没有文档 / 名字是否对得上」，不打明文路径
                    HostLog.Info("autorun", "tick", "n", _idleTicks,
                        "doc", doc == null ? "null" : Redactor.NameHash(doc.Name));
                }
                if (doc == null)
                {
                    return;
                }

                string expected = Environment.GetEnvironmentVariable(EnvSelfTestDwg);
                if (!string.IsNullOrEmpty(expected) && !MatchesDrawing(doc, expected))
                {
                    if (_idleTicks == IdleWarmup)
                    {
                        HostLog.Warn("autorun", "waiting_for_drawing", "expected",
                            Redactor.NameHash(expected), "current", Redactor.NameHash(doc.Name));
                    }
                    if (_idleTicks > IdleGiveUp)
                    {
                        Finish(StatusOf(PathOf(EnvSelfTestOut), "FAIL E1005 drawing_not_opened:" + ShortName(expected)));
                    }
                    return;
                }

                // 顺序：探针 → 夹具 → 自检（同一轮只会命中一个，外部按需只设一个）
                string probeOut = Environment.GetEnvironmentVariable(EnvProbeOut);
                if (!string.IsNullOrEmpty(probeOut))
                {
                    string path = DevProbeCommand.Probe(probeOut);
                    Finish(StatusOf(probeOut, path == null ? "FAIL E3002" : "OK " + path));
                    return;
                }

                string fixtureOut = Environment.GetEnvironmentVariable(EnvFixtureOut);
                if (!string.IsNullOrEmpty(fixtureOut))
                {
                    string status = DevFixtureCommand.Run(fixtureOut);
                    Finish(StatusOf(fixtureOut, status));
                    return;
                }

                string outDir = Environment.GetEnvironmentVariable(EnvSelfTestOut);
                string jsonPath = null;
                string failCode = null;
                Dictionary<string, object> payload = SelfTestCommand.Execute(doc, outDir, "selftest", out jsonPath, out failCode);
                SelfTestCommand.WriteJsonFile(jsonPath, payload);
                Finish(StatusOf(outDir, failCode == null ? "OK " + jsonPath : "FAIL " + failCode));
            }
            catch (Exception ex)
            {
                HostLog.Exception("autorun", "autorun_failed", ex);
                try
                {
                    Finish(StatusOf(PathOf(EnvSelfTestOut), "FAIL E3002 autorun_exception"));
                }
                catch (Exception)
                {
                    _finished = true;
                    Disarm();
                }
            }
        }

        private static string PathOf(string envName)
        {
            string value = Environment.GetEnvironmentVariable(envName);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static bool MatchesDrawing(Document doc, string expected)
        {
            try
            {
                string name = doc.Name;
                if (string.IsNullOrEmpty(name))
                {
                    return false;
                }
                string a = Path.GetFullPath(name);
                string b;
                try
                {
                    b = Path.GetFullPath(expected);
                }
                catch (Exception)
                {
                    b = expected;
                }
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                return string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>把一行状态写进输出目录，PowerShell 只读这个文件（不解析 CAD 控制台）。</summary>
        private static string StatusOf(string outputDir, string status)
        {
            if (string.IsNullOrEmpty(outputDir))
            {
                return status;
            }
            try
            {
                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
                string path = Path.Combine(outputDir, StatusFileName);
                File.WriteAllText(path, status + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                HostLog.Exception("autorun", "status_write_failed", ex);
            }
            return status;
        }

        private static void Finish(string status)
        {
            _finished = true;
            Disarm();
            HostLog.Info("autorun", "finished", "status", Clip(status));
        }

        private static void Disarm()
        {
            if (!_armed)
            {
                return;
            }
            _armed = false;
            try
            {
                Application.Idle -= OnIdle;
            }
            catch (Exception ex)
            {
                HostLog.Exception("autorun", "disarm_failed", ex);
            }
        }

        private static string ShortName(string path)
        {
            try
            {
                return Path.GetFileName(path);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return text.Length <= 200 ? text : text.Substring(0, 200);
        }

        /// <summary>调试用：把关键环境变量打一行日志（不含路径明文的脱敏版本）。</summary>
        public static string DescribeEnvironment()
        {
            string probe = Environment.GetEnvironmentVariable(EnvProbeOut);
            string fixture = Environment.GetEnvironmentVariable(EnvFixtureOut);
            string selftest = Environment.GetEnvironmentVariable(EnvSelfTestOut);
            return string.Format(CultureInfo.InvariantCulture, "probe={0} fixture={1} selftest={2}",
                probe == null ? "-" : "set", fixture == null ? "-" : "set", selftest == null ? "-" : "set");
        }
    }
}
