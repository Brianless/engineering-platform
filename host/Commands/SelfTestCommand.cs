using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.LocalPanel;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Scheduling;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Commands
{
    /// <summary>
    /// 无人值守自检命令（第二轮新增）。
    ///
    /// ENG_SELFTEST &lt;输出目录&gt;：
    /// 对当前文档依次跑 read_overview → read_layers → read_query(limit=20) → read_preview → read_anomalies，
    /// 走的是 Host 内部同一条 ToolRouter（不是另写一套查询），把每个工具的完整响应
    /// 写成 &lt;输出目录&gt;\selftest-&lt;yyyyMMddHHmmss&gt;.json，
    /// 命令行回显一行 "SELFTEST OK &lt;路径&gt;" 或 "SELFTEST FAIL &lt;错误码&gt;"。
    ///
    /// 为什么要这个命令：NETLOAD/ApplicationPlugins 加载后无法从外部直接调 ToolRouter，
    /// 而管道链路要求客户端在 2 秒内泵送（GUI 弹窗会打断），批处理实测必须有一个内部入口。
    ///
    /// 只读：五个工具全是只读域；本命令不写图纸、不碰写通道。
    /// </summary>
    public sealed class SelfTestCommand
    {
        /// <summary>自检工具顺序（与验收记录一致；不要在这里加写域工具）。</summary>
        private static readonly string[] ToolOrder =
        {
            "read_overview", "read_layers", "read_query", "read_preview", "read_anomalies"
        };

        [CommandMethod("ENG_SELFTEST")]
        public void RunSelfTest()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                WriteLine("SELFTEST FAIL E1005");
                return;
            }

            string outputDir = AskOutputDirectory(doc);
            if (string.IsNullOrEmpty(outputDir))
            {
                WriteLine("SELFTEST FAIL E1001");
                return;
            }

            string jsonPath = null;
            string failCode = null;
            try
            {
                Dictionary<string, object> payload = Execute(doc, outputDir, "selftest", out jsonPath, out failCode);
                WriteJsonFile(jsonPath, payload);
            }
            catch (Exception ex)
            {
                HostLog.Exception("selftest", "selftest_failed", ex);
                failCode = ErrorCodes.E3002;
            }

            if (failCode == null)
            {
                WriteLine("SELFTEST OK " + jsonPath);
            }
            else
            {
                WriteLine("SELFTEST FAIL " + failCode);
                HostLog.Warn("selftest", "selftest_reported_failure", "code", failCode,
                    "path", HostLog.SafePath(jsonPath));
            }
        }

        /// <summary>面板命令别名（ApplicationPlugins 包里只注册 ENG_SELFTEST / ENG_PANEL 两条）。</summary>
        [CommandMethod("ENG_PANEL")]
        public void ShowPanel()
        {
            try
            {
                LocalPanelForm form = new LocalPanelForm(HostRuntime.Instance);
                Application.ShowModelessDialog(form);
            }
            catch (Exception ex)
            {
                HostLog.Exception("panel", "eng_panel_failed", ex);
                WriteLine("工程平台：面板打开失败（" + ex.Message + "），详情见日志。");
            }
        }

        // ---------------- 执行 ----------------

        /// <summary>
        /// 执行自检并返回可序列化载荷。filePrefix 区分来源：
        /// "selftest"（命令/无人值守）、"fixture-selftest"（ENG_DEVFIXTURE 夹具）。
        /// </summary>
        internal static Dictionary<string, object> Execute(Document doc, string outputDir, string filePrefix,
            out string jsonPath, out string failCode)
        {
            DateTime startedUtc = DateTime.UtcNow;
            string stamp = startedUtc.ToLocalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            jsonPath = Path.Combine(outputDir, filePrefix + "-" + stamp + ".json");
            failCode = null;

            HostRuntime runtime = HostRuntime.Instance;
            if (!runtime.IsStarted)
            {
                // 命令先于插件初始化执行（极少见）：手动启动，避免自检假失败
                runtime.Start();
            }

            List<object> toolResults = new List<object>(ToolOrder.Length);
            string fingerprint = null;
            Dictionary<string, object> civilSection = null;
            int okCount = 0;

            for (int i = 0; i < ToolOrder.Length; i++)
            {
                string tool = ToolOrder[i];
                Dictionary<string, object> parameters = BuildParams(tool);
                DateTime toolStart = DateTime.UtcNow;
                Dictionary<string, object> response = InvokeTool(runtime, tool, parameters);
                long elapsed = (long)Math.Round((DateTime.UtcNow - toolStart).TotalMilliseconds);

                bool ok = JsonCodec.GetBool(response, "ok", false);
                if (ok)
                {
                    okCount++;
                }
                else if (failCode == null)
                {
                    Dictionary<string, object> error = JsonCodec.GetDict(response, "error");
                    failCode = error == null ? ErrorCodes.E3002 : JsonCodec.GetStr(error, "code", ErrorCodes.E3002);
                }

                // 指纹：第一次成功响应即可（五个工具同一张图）
                if (fingerprint == null)
                {
                    string target = JsonCodec.GetStr(response, "targetFingerprint");
                    if (!string.IsNullOrEmpty(target))
                    {
                        fingerprint = target;
                    }
                }

                Dictionary<string, object> result = JsonCodec.GetDict(response, "result");
                if (result == null && ok)
                {
                    // 结果可能不是对象（理论上不会），保守处理
                    result = new Dictionary<string, object>(StringComparer.Ordinal);
                }

                if (string.Equals(tool, "read_overview", StringComparison.Ordinal) && result != null)
                {
                    civilSection = JsonCodec.GetDict(result, "civil3d");
                    string fp = JsonCodec.GetStr(result, "fingerprint");
                    if (!string.IsNullOrEmpty(fp))
                    {
                        fingerprint = fp;
                    }
                }

                if (string.Equals(tool, "read_preview", StringComparison.Ordinal) && ok && result != null)
                {
                    // 把预览 PNG 落到输出目录，作为验收证据（原文件在宿主缓存目录）
                    CopyPreview(result, outputDir, stamp);
                }

                Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
                entry["tool"] = tool;
                entry["ok"] = ok;
                entry["status"] = JsonCodec.GetStr(response, "status", "unknown");
                entry["elapsedMs"] = elapsed;
                entry["opId"] = JsonCodec.GetStr(response, "opId");
                entry["paramsDigest"] = JsonCodec.GetStr(response, "paramsDigest");
                entry["warnings"] = response.ContainsKey("warnings") ? response["warnings"] : new List<object>();
                entry["error"] = response.ContainsKey("error") ? response["error"] : null;
                entry["result"] = result;
                toolResults.Add(entry);
            }

            Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.Ordinal);
            payload["kind"] = "selftest";
            payload["generatedAtUtc"] = TimeUtil.NowIso();
            payload["outputDir"] = outputDir;
            payload["hostInstanceId"] = runtime.Sessions.HostInstanceId;
            payload["hostProduct"] = CivilApiProbe.ProductName;
            payload["hostRelease"] = CivilApiProbe.ReleaseTag;
            payload["hostVersion"] = runtime.Config == null ? "0.1.0" : runtime.Config.HostVersion;
            payload["protocolVersion"] = Handshake.ProtocolVersion;
            payload["catalogHash"] = ToolCatalog.CatalogHash;

            Dictionary<string, object> drawing = new Dictionary<string, object>(StringComparer.Ordinal);
            drawing["fingerprint"] = fingerprint;
            drawing["nameHash"] = SafeNameHash(doc);
            drawing["readOnly"] = SafeIsReadOnly(doc);
            payload["drawing"] = drawing;

            payload["civil3d"] = civilSection;
            payload["toolResults"] = toolResults;
            payload["toolCount"] = ToolOrder.Length;
            payload["okCount"] = okCount;
            payload["elapsedMs"] = (long)Math.Round((DateTime.UtcNow - startedUtc).TotalMilliseconds);
            payload["result"] = failCode == null ? "OK" : "FAIL";
            payload["failedCode"] = failCode;
            return payload;
        }

        /// <summary>逐工具参数（read_query 限 20 条，其余默认）。</summary>
        private static Dictionary<string, object> BuildParams(string tool)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>(StringComparer.Ordinal);
            if (string.Equals(tool, "read_query", StringComparison.Ordinal))
            {
                parameters["limit"] = 20;
            }
            else if (string.Equals(tool, "read_preview", StringComparison.Ordinal))
            {
                parameters["width"] = 1600;
                parameters["height"] = 1000;
            }
            else if (string.Equals(tool, "read_layers", StringComparison.Ordinal))
            {
                parameters["limit"] = 50;
            }
            else if (string.Equals(tool, "read_anomalies", StringComparison.Ordinal))
            {
                parameters["limit"] = 50;
            }
            return parameters;
        }

        /// <summary>
        /// 走同一条 ToolRouter：构造 WorkItem 后直接调用 HostRuntime.ExecuteItem（已在主线程）。
        /// 不经过管道，但门禁、指纹、预算截断、错误码映射全部与线上一致。
        /// </summary>
        private static Dictionary<string, object> InvokeTool(HostRuntime runtime, string tool, Dictionary<string, object> parameters)
        {
            ResponseBuilder builder = new ResponseBuilder();
            builder.SessionToken = new string('0', 64); // 自检没有真实会话，用占位值满足 schema
            builder.HostInstanceId = runtime.Sessions.HostInstanceId;
            builder.OpId = "selftest-" + tool + "-" + ParamsDigest.NewShortId();
            builder.Tool = tool;
            builder.ParamsDigestValue = ParamsDigest.Compute(parameters);
            builder.StartedUtc = DateTime.UtcNow;

            WorkItem item = new WorkItem();
            item.OpId = builder.OpId;
            item.Tool = tool;
            item.SessionToken = builder.SessionToken;
            item.HostInstanceId = builder.HostInstanceId;
            item.ParamsDigestValue = builder.ParamsDigestValue;
            item.Params = parameters;
            item.TimeoutMs = 60000;
            item.Cancelable = false;
            item.Builder = builder;
            item.EnqueuedUtc = DateTime.UtcNow;
            item.StartedUtc = DateTime.UtcNow;
            // 自检在命令上下文里执行：CMDACTIVE 必然非 0，必须显式放行这一条门禁，
            // 否则五个工具会一律返回 E1003 cmdactive=0x5（实测过）。
            item.AllowCommandContext = true;

            Dictionary<string, object> response = runtime.ExecuteItem(item);
            item.TryComplete(response); // 记录状态（无 OnCompleted 回调，不发帧）
            return response;
        }

        /// <summary>把预览 PNG 复制到输出目录（read_preview 只在缓存里留文件）。</summary>
        private static void CopyPreview(Dictionary<string, object> result, string outputDir, string stamp)
        {
            try
            {
                string source = JsonCodec.GetStr(result, "previewPath");
                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    return;
                }
                string target = Path.Combine(outputDir, "selftest-" + stamp + "-preview.png");
                File.Copy(source, target, true);
                result["previewEvidencePath"] = target;
            }
            catch (Exception ex)
            {
                HostLog.Exception("selftest", "preview_copy_failed", ex);
            }
        }

        internal static void WriteJsonFile(string path, Dictionary<string, object> payload)
        {
            if (string.IsNullOrEmpty(path) || payload == null)
            {
                return;
            }
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            // 自检证据必须人能直接打开：缩进 + 无 BOM UTF-8
            string text = JsonCodec.SerializeIndented(payload);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        // ---------------- 命令输入/输出 ----------------

        /// <summary>
        /// 取输出目录。脚本/批处理里由 `(command "ENG_SELFTEST" "...")` 直接喂入，
        /// 交互时由用户输入；两者都走 Editor.GetString，不需要额外分支。
        /// </summary>
        private static string AskOutputDirectory(Document doc)
        {
            try
            {
                PromptResult prompt = doc.Editor.GetString("\n自检输出目录: ");
                if (prompt.Status != PromptStatus.OK || string.IsNullOrEmpty(prompt.StringResult))
                {
                    return null;
                }
                string value = prompt.StringResult.Trim().Trim('"');
                if (value.Length == 0)
                {
                    return null;
                }
                string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
                if (!Directory.Exists(full))
                {
                    Directory.CreateDirectory(full);
                }
                return full;
            }
            catch (Exception ex)
            {
                HostLog.Exception("selftest", "output_dir_failed", ex);
                return null;
            }
        }

        private static string SafeNameHash(Document doc)
        {
            try
            {
                return Redactor.NameHash(doc.Name);
            }
            catch (Exception)
            {
                return "null";
            }
        }

        private static bool SafeIsReadOnly(Document doc)
        {
            try
            {
                return doc.IsReadOnly;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void WriteLine(string message)
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    return;
                }
                doc.Editor.WriteMessage("\n" + message);
            }
            catch (Exception)
            {
                // 无编辑器上下文时静默（批处理 /quit 阶段）
            }
        }
    }
}
