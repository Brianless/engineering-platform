using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Roslyn;

namespace EngineeringPlatform.Host.Commands
{
    /// <summary>
    /// accoreconsole.exe 影子进程试跑入口命令（ENG_SHADOW_EVAL）。
    /// 由 ShadowDryRunner 在子进程中调用，把动态 C# 代码在副本图纸的事务中试跑一遍并写回 JSON 结果。
    /// </summary>
    public sealed class ShadowEvalCommand
    {
        [CommandMethod("ENG_SHADOW_EVAL", CommandFlags.Modal)]
        public void Execute()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }
            Editor ed = doc.Editor;
            PromptResult pr = ed.GetString("\nPayload path: ");
            if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult))
            {
                return;
            }

            string payloadPath = pr.StringResult.Trim().Trim('"');
            if (!File.Exists(payloadPath))
            {
                return;
            }

            string resultPath = Path.Combine(Path.GetDirectoryName(payloadPath), "result.json");
            Dictionary<string, object> output = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                string raw = File.ReadAllText(payloadPath, Encoding.UTF8);
                Dictionary<string, object> payload;
                if (!JsonCodec.TryParseObject(raw, out payload))
                {
                    output["ok"] = false;
                    output["error"] = "invalid_payload_json";
                    File.WriteAllText(resultPath, JsonCodec.Serialize(output), Encoding.UTF8);
                    return;
                }

                string customResult = JsonCodec.GetStr(payload, "resultPath");
                if (!string.IsNullOrEmpty(customResult))
                {
                    resultPath = customResult;
                }

                string code = JsonCodec.GetStr(payload, "code");
                Dictionary<string, object> args = JsonCodec.GetDict(payload, "args");
                List<string> usings = JsonCodec.GetStringList(payload, "usings");

                EvalResult eval;
                Database db = doc.Database;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    eval = RoslynEvalEngine.Execute(code, doc, db, tr, ed, args, usings);
                    if (eval.Ok)
                    {
                        tr.Commit();
                    }
                }

                output["ok"] = eval.Ok;
                output["engine"] = eval.Engine;
                output["compileMs"] = eval.CompileMs;
                output["executeMs"] = eval.ExecuteMs;
                output["error"] = eval.ErrorMessage;
                output["returnValue"] = eval.ReturnValue == null
                    ? null
                    : Convert.ToString(eval.ReturnValue, CultureInfo.InvariantCulture);

                List<object> logs = new List<object>(eval.Logs.Count);
                for (int i = 0; i < eval.Logs.Count; i++) logs.Add(eval.Logs[i]);
                output["logs"] = logs;

                List<object> diags = new List<object>(eval.Diagnostics.Count);
                for (int i = 0; i < eval.Diagnostics.Count; i++) diags.Add(eval.Diagnostics[i]);
                output["diagnostics"] = diags;
            }
            catch (Exception ex)
            {
                output["ok"] = false;
                output["error"] = ex.GetType().Name + ": " + ex.Message;
                HostLog.Exception("roslyn", "shadow_command_failed", ex);
            }

            try
            {
                File.WriteAllText(resultPath, JsonCodec.Serialize(output), Encoding.UTF8);
            }
            catch (Exception)
            {
                // 写结果失败时让父进程通过缺失结果文件捕获
            }
        }
    }
}
