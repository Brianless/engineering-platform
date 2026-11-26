using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.Scheduling
{
    /// <summary>门禁结果：通过则带出 Document/Database，不通过则带出错误码。</summary>
    internal sealed class GateResult
    {
        public bool Ok;
        public WireError Error;
        public Document Doc;
        public Database Db;

        public static GateResult Pass(Document doc, Database db)
        {
            GateResult result = new GateResult();
            result.Ok = true;
            result.Doc = doc;
            result.Db = db;
            return result;
        }

        public static GateResult Fail(WireError error)
        {
            GateResult result = new GateResult();
            result.Ok = false;
            result.Error = error;
            return result;
        }
    }

    /// <summary>
    /// 主线程门禁检查（设计 §3.4）。只在 MainThreadDispatcher / 宿主内部命令内调用。
    /// 判据：
    /// 1) 有活动文档，否则 E1005 no_open_document；
    /// 2) CMDACTIVE == 0，否则 E1003 cmdactive=0x{值}；
    ///    —— 例外：宿主内部命令（ENG_SELFTEST）自身就在命令上下文里，
    ///    由调用方显式传 allowCommandContext=true 跳过这一条（其余判据照查）。
    /// 3) 文档非只读，否则 E1005 document_read_only。
    /// </summary>
    internal static class HostStateProbe
    {
        public static GateResult Probe(bool allowCommandContext = false)
        {
            Document doc;
            try
            {
                doc = Application.DocumentManager.MdiActiveDocument;
            }
            catch (Exception ex)
            {
                HostLog.Exception("gate", "document_manager_failed", ex);
                return GateResult.Fail(WireError.Of(ErrorCodes.E1005, "document_manager_unavailable"));
            }

            if (doc == null)
            {
                return GateResult.Fail(WireError.Of(ErrorCodes.E1005, "no_open_document"));
            }

            short cmdActive = 0;
            try
            {
                object raw = Application.GetSystemVariable("CMDACTIVE");
                if (raw != null)
                {
                    cmdActive = Convert.ToInt16(raw);
                }
            }
            catch (Exception ex)
            {
                // 取不到 CMDACTIVE 不阻断只读（个别环境在 Idle 早期取不到）
                HostLog.Exception("gate", "cmdactive_unavailable", ex);
            }

            if (cmdActive != 0)
            {
                if (!allowCommandContext)
                {
                    return GateResult.Fail(WireError.Of(ErrorCodes.E1003,
                        "cmdactive=0x" + cmdActive.ToString("X")));
                }
                HostLog.Info("gate", "cmdactive_bypassed",
                    "cmdactive=0x" + cmdActive.ToString("X"), "reason", "selftest_command");
            }

            // v3.0 物理防崩防线 #2：静默态门禁（Editor.IsQuiescent）
            if (!allowCommandContext)
            {
                try
                {
                    if (doc.Editor != null && !doc.Editor.IsQuiescent)
                    {
                        return GateResult.Fail(WireError.Of(ErrorCodes.E1003, "editor_not_quiescent"));
                    }
                }
                catch (Exception)
                {
                    // 无交互编辑器（如 accoreconsole 早期阶段）取不到 IsQuiescent 时不阻断
                }
            }

            bool readOnly;
            try
            {
                readOnly = doc.IsReadOnly;
            }
            catch (Exception ex)
            {
                HostLog.Exception("gate", "is_readonly_unavailable", ex);
                readOnly = false;
            }
            if (readOnly)
            {
                return GateResult.Fail(WireError.Of(ErrorCodes.E1005, "document_read_only"));
            }

            Database db;
            try
            {
                db = doc.Database;
            }
            catch (Exception ex)
            {
                HostLog.Exception("gate", "database_unavailable", ex);
                return GateResult.Fail(WireError.Of(ErrorCodes.E1005, "database_unavailable"));
            }
            if (db == null)
            {
                return GateResult.Fail(WireError.Of(ErrorCodes.E1005, "database_unavailable"));
            }

            return GateResult.Pass(doc, db);
        }

        /// <summary>主线程静默态探测：用户正在交互画图或有活跃命令时返回 true，供调度器自动排队等待。</summary>
        public static bool IsEditorBusy()
        {
            try
            {
                object raw = Application.GetSystemVariable("CMDACTIVE");
                if (raw != null && Convert.ToInt16(raw) != 0)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null && doc.Editor != null && !doc.Editor.IsQuiescent)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            return false;
        }

        /// <summary>当前活动文档（供面板与实例发现文件使用；无文档返回 null）。</summary>
        public static Document TryGetActiveDocument()
        {
            try
            {
                return Application.DocumentManager.MdiActiveDocument;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 产物软件版本。优先 ACADVER 系统变量（"24.3s"，与用户在 CAD 里看到的一致），
        /// 取不到再回退 Application.Version。
        /// </summary>
        public static string TryGetProductVersion()
        {
            try
            {
                object raw = Application.GetSystemVariable("ACADVER");
                string text = raw == null ? string.Empty : Convert.ToString(raw);
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            catch (Exception)
            {
                // 无桌面会话时取不到，走版本号回退
            }

            try
            {
                return Application.Version.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>产物名称（AutoCAD / Civil 3D）；Civil 3D 通过反射探测版本。</summary>
        public static string TryGetProductName()
        {
            if (CivilApiProbe.Available)
            {
                return "Civil 3D";
            }
            return "AutoCAD";
        }
    }
}
