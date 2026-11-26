using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Roslyn
{
    internal sealed class ShadowDryRunResult
    {
        public bool Ok;
        public bool Skipped;
        public int ExitCode;
        public long ElapsedMs;
        public string Reason;
        public string ResultJson;
        public List<string> Diagnostics = new List<string>();
    }

    /// <summary>
    /// Roslyn 影子进程试跑器（v3.0 物理防崩防线 #1）：
    /// 将当前图纸副本复制到 %TEMP%\ep_shadow\，在独立 accoreconsole.exe 子进程中执行
    /// ENG_SHADOW_EVAL 试跑。即使动态代码触发不可 catch 的原生 ExecutionEngineException / AV 崩溃，
    /// 死亡的只是子进程，主界面 acad.exe 毫发无损。
    /// </summary>
    internal static class ShadowDryRunner
    {
        public static ShadowDryRunResult TryDryRun(
            Document doc,
            Database db,
            string code,
            Dictionary<string, object> args,
            List<string> extraUsings,
            int timeoutMs)
        {
            ShadowDryRunResult outcome = new ShadowDryRunResult();
            Stopwatch sw = Stopwatch.StartNew();

            string accorePath = LocateAcCoreConsole();
            if (string.IsNullOrEmpty(accorePath))
            {
                outcome.Ok = true;
                outcome.Skipped = true;
                outcome.Reason = "accoreconsole_not_found_beside_host";
                return outcome;
            }

            string runDir = Path.Combine(Path.Combine(Path.GetTempPath(), "ep_shadow"),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDir);

            try
            {
                string shadowDwg = Path.Combine(runDir, "shadow.dwg");
                if (!CreateShadowDrawing(db, shadowDwg))
                {
                    outcome.Ok = false;
                    outcome.Reason = "shadow_drawing_copy_failed";
                    return outcome;
                }

                string payloadPath = Path.Combine(runDir, "payload.json");
                string resultPath = Path.Combine(runDir, "result.json");
                string scriptPath = Path.Combine(runDir, "shadow.scr");

                Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.Ordinal);
                payload["code"] = code;
                payload["args"] = args ?? new Dictionary<string, object>(StringComparer.Ordinal);
                payload["resultPath"] = resultPath;
                if (extraUsings != null)
                {
                    List<object> uList = new List<object>(extraUsings.Count);
                    for (int i = 0; i < extraUsings.Count; i++) uList.Add(extraUsings[i]);
                    payload["usings"] = uList;
                }
                File.WriteAllText(payloadPath, JsonCodec.Serialize(payload), Encoding.UTF8);

                string hostDll = typeof(ShadowDryRunner).Assembly.Location;
                StringBuilder scr = new StringBuilder();
                if (!string.IsNullOrEmpty(hostDll) && File.Exists(hostDll))
                {
                    scr.AppendLine("_.NETLOAD");
                    scr.AppendLine("\"" + hostDll.Replace("\\", "/") + "\"");
                }
                scr.AppendLine("ENG_SHADOW_EVAL");
                scr.AppendLine("\"" + payloadPath.Replace("\\", "/") + "\"");
                scr.AppendLine("_.QUIT");
                scr.AppendLine("_N");
                File.WriteAllText(scriptPath, scr.ToString(), Encoding.ASCII);

                bool isCivilHost = IsCivilInstallDir(Path.GetDirectoryName(accorePath));
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = accorePath;
                psi.Arguments = isCivilHost
                    ? string.Format(CultureInfo.InvariantCulture,
                        "/p \"<<C3D_Metric>>\" /i \"{0}\" /s \"{1}\"", shadowDwg, scriptPath)
                    : string.Format(CultureInfo.InvariantCulture,
                        "/i \"{0}\" /s \"{1}\"", shadowDwg, scriptPath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = runDir;

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        outcome.Ok = false;
                        outcome.Reason = "accoreconsole_start_failed";
                        return outcome;
                    }
                    // 异步消费 stdout/stderr，防止 4KB 管道缓冲区满引发子进程永久死锁
                    proc.OutputDataReceived += delegate { };
                    proc.ErrorDataReceived += delegate { };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    int wait = timeoutMs > 2000 ? timeoutMs : 25000;
                    if (!proc.WaitForExit(wait))
                    {
                        try { proc.Kill(); } catch (Exception) { }
                        outcome.Ok = false;
                        outcome.ExitCode = -1;
                        outcome.Reason = "shadow_dry_run_timeout:" + wait + "ms";
                        return outcome;
                    }
                    outcome.ExitCode = proc.ExitCode;
                }

                if (!File.Exists(resultPath))
                {
                    outcome.Ok = false;
                    outcome.Reason = "shadow_process_crashed_or_no_result:exit=" + outcome.ExitCode;
                    return outcome;
                }

                string rawResult = File.ReadAllText(resultPath, Encoding.UTF8);
                outcome.ResultJson = rawResult;
                Dictionary<string, object> parsed;
                if (!JsonCodec.TryParseObject(rawResult, out parsed))
                {
                    outcome.Ok = false;
                    outcome.Reason = "shadow_result_malformed";
                    return outcome;
                }

                outcome.Ok = JsonCodec.GetBool(parsed, "ok", false);
                outcome.Reason = JsonCodec.GetStr(parsed, "error");
                List<string> diags = JsonCodec.GetStringList(parsed, "diagnostics");
                if (diags != null)
                {
                    outcome.Diagnostics = diags;
                }
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Ok = false;
                outcome.Reason = "shadow_runner_exception:" + ex.GetType().Name + ":" + ex.Message;
                HostLog.Exception("roslyn", "shadow_dry_run_failed", ex);
                return outcome;
            }
            finally
            {
                sw.Stop();
                outcome.ElapsedMs = sw.ElapsedMilliseconds;
                try
                {
                    if (Directory.Exists(runDir))
                    {
                        Directory.Delete(runDir, true);
                    }
                }
                catch (Exception)
                {
                    // 临时目录清理失败不阻断
                }
            }
        }

        private static bool CreateShadowDrawing(Database db, string targetPath)
        {
            if (db == null) return false;
            try
            {
                string src = db.Filename;
                if (!string.IsNullOrEmpty(src) && File.Exists(src))
                {
                    File.Copy(src, targetPath, true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                HostLog.Exception("roslyn", "shadow_copy_failed", ex);
            }

            try
            {
                using (Database empty = new Database(true, true))
                {
                    empty.SaveAs(targetPath, DwgVersion.Current);
                }
                return File.Exists(targetPath);
            }
            catch (Exception ex)
            {
                HostLog.Exception("roslyn", "shadow_empty_dwg_failed", ex);
                return false;
            }
        }

        private static bool IsCivilInstallDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            return File.Exists(Path.Combine(Path.Combine(dir, "C3D"), "AeccDbMgd.dll")) ||
                   File.Exists(Path.Combine(dir, "AeccDbMgd.dll"));
        }

        internal static string LocateAcCoreConsole()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string candidate = Path.Combine(dir, "accoreconsole.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略 MainModule 读取异常
            }
            return null;
        }
    }
}
