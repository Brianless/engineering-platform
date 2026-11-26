using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

#if NET8_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Nodes;
#endif

namespace EngineeringPlatform.Host.Base
{
    /// <summary>
    /// 进程内托管调用 new-acad（Civil3DMcpPlugin.dll，430 个底层方法直调，v3.0 Phase 4）。
    ///
    /// 核心价值：
    /// 1. 彻底消灭 TCP :8080 端口冲突与 EP_BRIDGE_TOKEN 双端同步；
    /// 2. 无需注册第二条 AutoCAD 插件（不触发 new-acad 的 TCP 服务器、relay 或面板）；
    /// 3. 必须在后台线程调用（CivilExecution 内部会通过 ExecuteInCommandContextAsync 排队回主线程），
    ///    防止主线程互锁。
    /// </summary>
    internal static class InProcessBaseBridge
    {
#if NET8_0_OR_GREATER
        private static readonly object LoadLock = new object();
        private static bool _loadAttempted;
        private static MethodInfo _dispatchMethod;
        private static string _loadedPath;
#endif

        public static bool IsAvailable
        {
            get
            {
#if NET8_0_OR_GREATER
                EnsureLoaded();
                return _dispatchMethod != null;
#else
                return false;
#endif
            }
        }

        public static string LoadedAssemblyPath
        {
            get
            {
#if NET8_0_OR_GREATER
                return _loadedPath;
#else
                return null;
#endif
            }
        }

        /// <summary>
        /// 在进程内直接调用 Civil3DMcpPlugin.CommandDispatcher.DispatchAsync。
        /// 仅限后台线程调用！
        /// </summary>
        public static bool TryCall(
            string method,
            Dictionary<string, object> parameters,
            int timeoutMs,
            out Dictionary<string, object> result,
            out WireError error)
        {
            result = null;
            error = null;

#if !NET8_0_OR_GREATER
            error = WireError.Of(ErrorCodes.E3002, "inproc_base_requires_net8");
            return false;
#else
            EnsureLoaded();
            if (_dispatchMethod == null)
            {
                error = WireError.Of(ErrorCodes.E3002, "inproc_base_assembly_not_found");
                return false;
            }

            try
            {
                string paramsJson = JsonCodec.Serialize(parameters ?? new Dictionary<string, object>(StringComparer.Ordinal));
                JsonObject jsonObj = JsonNode.Parse(paramsJson) as JsonObject;

                using (CancellationTokenSource cts = new CancellationTokenSource(timeoutMs > 500 ? timeoutMs : 30000))
                {
                    object taskObj = _dispatchMethod.Invoke(null, new object[] { method, jsonObj, cts.Token });
                    Task<object> task = taskObj as Task<object>;
                    if (task == null)
                    {
                        error = WireError.Of(ErrorCodes.E3002, "inproc_base_invalid_return_task");
                        return false;
                    }

                    if (!task.Wait(timeoutMs > 500 ? timeoutMs : 30000))
                    {
                        cts.Cancel();
                        error = WireError.Of(ErrorCodes.E3002, "inproc_base_timeout:" + method);
                        return false;
                    }

                    object rawResult = task.GetAwaiter().GetResult();
                    if (rawResult == null)
                    {
                        result = new Dictionary<string, object>(StringComparer.Ordinal);
                        return true;
                    }

                    string resultJson = JsonSerializer.Serialize(rawResult);
                    Dictionary<string, object> parsed;
                    if (JsonCodec.TryParseObject(resultJson, out parsed))
                    {
                        result = parsed;
                        return true;
                    }

                    Dictionary<string, object> wrapped = new Dictionary<string, object>(StringComparer.Ordinal);
                    wrapped["value"] = rawResult;
                    result = wrapped;
                    return true;
                }
            }
            catch (AggregateException ae)
            {
                Exception inner = ae.Flatten().InnerException ?? ae;
                HostLog.Warn("base", "inproc_method_failed", "method", method, "error", inner.Message);
                error = WireError.Of(ErrorCodes.E2001, "inproc_base_method_failed:" + method + ":" + inner.Message);
                return false;
            }
            catch (TargetInvocationException tie)
            {
                Exception inner = tie.InnerException ?? tie;
                HostLog.Warn("base", "inproc_invoke_failed", "method", method, "error", inner.Message);
                error = WireError.Of(ErrorCodes.E2001, "inproc_base_method_failed:" + method + ":" + inner.Message);
                return false;
            }
            catch (Exception ex)
            {
                HostLog.Exception("base", "inproc_call_exception", ex);
                error = WireError.Of(ErrorCodes.E3002, "inproc_base_error:" + ex.Message);
                return false;
            }
#endif
        }

#if NET8_0_OR_GREATER
        private static void EnsureLoaded()
        {
            if (_dispatchMethod != null) return;
            lock (LoadLock)
            {
                if (_loadAttempted) return;
                _loadAttempted = true;

                try
                {
                    Assembly target = FindLoadedAssembly();
                    if (target == null)
                    {
                        string path = LocatePluginDll();
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            target = Assembly.LoadFrom(path);
                            _loadedPath = path;
                        }
                    }
                    else
                    {
                        _loadedPath = target.Location;
                    }

                    if (target == null)
                    {
                        HostLog.Info("base", "inproc_base_not_found");
                        return;
                    }

                    Type dispatcher = target.GetType("Civil3DMcpPlugin.CommandDispatcher", false);
                    if (dispatcher == null)
                    {
                        HostLog.Warn("base", "inproc_dispatcher_type_missing");
                        return;
                    }

                    _dispatchMethod = dispatcher.GetMethod(
                        "DispatchAsync",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(string), typeof(JsonObject), typeof(CancellationToken) },
                        null);

                    if (_dispatchMethod != null)
                    {
                        HostLog.Info("base", "inproc_base_ready", "path", HostLog.SafePath(_loadedPath));
                    }
                }
                catch (Exception ex)
                {
                    HostLog.Exception("base", "inproc_load_failed", ex);
                }
            }
        }

        private static Assembly FindLoadedAssembly()
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                AssemblyName name = all[i].GetName();
                if (name != null && string.Equals(name.Name, "Civil3DMcpPlugin", StringComparison.OrdinalIgnoreCase))
                {
                    return all[i];
                }
            }
            return null;
        }

        private static string LocatePluginDll()
        {
            // 1. 环境变量显式指定
            string fromEnv = Environment.GetEnvironmentVariable("EP_BASE_PLUGIN_DLL");
            if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            {
                return fromEnv;
            }

            // 2. 与 EngineeringHost.dll 同目录（一体化 bundle 交付模式）
            string hostLoc = typeof(InProcessBaseBridge).Assembly.Location;
            if (!string.IsNullOrEmpty(hostLoc))
            {
                string beside = Path.Combine(Path.GetDirectoryName(hostLoc), "Civil3DMcpPlugin.dll");
                if (File.Exists(beside))
                {
                    return beside;
                }
            }

            // 3. 标准 ApplicationPlugins 目录中的 new-acad.bundle
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                string bundleDll = Path.Combine(appData, "Autodesk", "ApplicationPlugins",
                    "new-acad.bundle", "Contents", "Windows", "Civil3DMcpPlugin.dll");
                if (File.Exists(bundleDll))
                {
                    return bundleDll;
                }
            }

            return null;
        }
#endif
    }
}
