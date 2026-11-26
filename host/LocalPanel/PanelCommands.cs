using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.LocalPanel
{
    /// <summary>
    /// AutoCAD 命令：EPPANEL（打开降级面板）/ EPSTATUS（在命令行打印宿主状态）。
    /// 所有异常都在命令内捕获，绝不让异常冒泡影响其它插件命令。
    /// </summary>
    public sealed class PanelCommands
    {
        [CommandMethod("EPPANEL")]
        public void ShowPanel()
        {
            try
            {
                LocalPanelForm form = new LocalPanelForm(HostRuntime.Instance);
                // 无模式对话框：不阻塞 CAD 命令，便于一边操作一边观察任务
                Application.ShowModelessDialog(form);
            }
            catch (Exception ex)
            {
                HostLog.Exception("panel", "open_failed", ex);
                WriteLine("工程平台：面板打开失败（" + ex.Message + "），详情见日志。");
            }
        }

        [CommandMethod("EPSTATUS")]
        public void ShowStatus()
        {
            try
            {
                HostRuntime runtime = HostRuntime.Instance;
                WriteLine("=== 工程平台宿主（M0 只读） ===");
                WriteLine("实例：" + runtime.Sessions.HostInstanceId);
                WriteLine("管道：" + runtime.Sessions.PipeName);
                WriteLine("会话数：" + runtime.Sessions.ActiveSessionCount
                    + "    队列：" + runtime.Dispatcher.QueueLength
                    + "    连接数：" + runtime.ConnectionCount);
                WriteLine("主线程泵送：" + (runtime.Dispatcher.IsPumping(3000) ? "正常" : "异常（可能有模态对话框）"));
                WriteLine("指纹：" + (string.IsNullOrEmpty(runtime.LastFingerprint) ? "（尚未采集）" : runtime.LastFingerprint));
                WriteLine("Civil 3D：" + (CivilApiProbe.Available ? "可用" : "未安装（代理对象按缺口上报）"));
                WriteLine("写入通道：关闭（M0）；只读工具 " + ToolCatalog.All.Count + " 个；目录哈希 " + ToolCatalog.CatalogHash);
                WriteLine("日志目录：" + HostLog.DirectoryPath);
            }
            catch (Exception ex)
            {
                HostLog.Exception("panel", "status_failed", ex);
                WriteLine("工程平台：状态读取失败（" + ex.Message + "）。");
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
                Editor editor = doc.Editor;
                editor.WriteMessage("\n" + message);
            }
            catch (Exception)
            {
                // 无编辑器上下文（批处理/无文档）时静默
            }
        }

        /// <summary>格式化耗时（面板/命令行共用）。</summary>
        public static string FormatMs(long ms)
        {
            return ms.ToString(CultureInfo.InvariantCulture) + " ms";
        }
    }
}
