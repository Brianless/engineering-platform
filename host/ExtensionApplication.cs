using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;
using EngineeringPlatform.Host.Diagnostics;

[assembly: ExtensionApplication(typeof(EngineeringPlatform.Host.ExtensionApplication))]

namespace EngineeringPlatform.Host
{
    /// <summary>
    /// AutoCAD 插件入口（设计 §3.1）。
    /// 硬约束：任何异常都在 Initialize 内 catch 并写日志，绝不让异常冒泡——
    /// 否则会卸载整个插件的其它命令。
    /// </summary>
    public sealed class ExtensionApplication : IExtensionApplication
    {
        private static bool _alertShown;
        private static bool _subscribed;

        public void Initialize()
        {
            try
            {
                HostRuntime runtime = HostRuntime.Instance;

                // Application.Idle：主线程泵送入口（模态对话框期间不触发，这正是 E1004 的信号）
                Application.Idle += OnApplicationIdle;

                // 文档切换：语义缓存全失效
                Application.DocumentManager.DocumentActivated += OnDocumentActivated;

                // 退出前清理（部分版本无此事件，失败不阻断）
                try
                {
                    Application.QuitWillStart += OnQuitWillStart;
                }
                catch (Exception ex)
                {
                    HostLog.Exception("runtime", "quitwillstart_subscribe_failed", ex);
                }

                runtime.Start();
                _subscribed = true;

                // 第三轮：无人值守自动执行（D2 的根因是 `/b` 脚本比插件 Initialize 早 ~70 秒）。
                // 没有声明 EP_* 环境变量时这里是空操作，正式环境零影响。
                EngineeringPlatform.Host.Commands.AutoRun.TryArm();

                HostLog.Info("runtime", "initialize_ok", "assembly", typeof(ExtensionApplication).Assembly.GetName().Version.ToString());
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "initialize_failed", ex);
                ShowAlertOnce("工程平台宿主启动失败：" + ex.Message + "\n详细信息见 %LOCALAPPDATA%\\EngineeringPlatform\\logs");
            }
        }

        public void Terminate()
        {
            try
            {
                if (_subscribed)
                {
                    try
                    {
                        Application.Idle -= OnApplicationIdle;
                        Application.DocumentManager.DocumentActivated -= OnDocumentActivated;
                        Application.QuitWillStart -= OnQuitWillStart;
                    }
                    catch (Exception ex)
                    {
                        HostLog.Exception("runtime", "unsubscribe_failed", ex);
                    }
                    _subscribed = false;
                }
                HostRuntime.Instance.Stop();
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "terminate_failed", ex);
            }
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            try
            {
                HostRuntime.Instance.Dispatcher.OnIdle(sender, e);
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "idle_pump_failed", ex);
            }
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                HostRuntime.Instance.OnDocumentActivated();
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "document_activated_failed", ex);
            }
        }

        private static void OnQuitWillStart(object sender, EventArgs e)
        {
            try
            {
                HostRuntime.Instance.Stop();
            }
            catch (Exception ex)
            {
                HostLog.Exception("runtime", "quit_stop_failed", ex);
            }
        }

        private static void ShowAlertOnce(string message)
        {
            if (_alertShown)
            {
                return;
            }
            _alertShown = true;
            try
            {
                Application.ShowAlertDialog(message);
            }
            catch (Exception)
            {
                // 无桌面会话时弹窗会失败，忽略
            }
        }
    }
}
