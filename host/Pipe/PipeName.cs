using System;
using System.Globalization;
using System.IO;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Pipe
{
    /// <summary>
    /// 管道名 / 实例发现文件路径生成。必须与 Python 侧（mcp/pipeclient.py）逐字节同规则，
    /// 否则两边对不上（设计 §7.3 —— 这是双端最容易不一致的地方）。
    /// 本文件禁止引用任何 AutoCAD 类型。
    /// </summary>
    internal static class PipeName
    {
        /// <summary>协议主版本（管道名里带 v1）。</summary>
        public const string PipePrefix = @"\\.\pipe\engineering-platform.v1";

        /// <summary>应用标识：acad / civil / revit / p3d / mock。</summary>
        public const string AppAcad = "acad";
        public const string AppMock = "mock";

        private static string _cachedUserHash8;

        /// <summary>
        /// 用户键：USERDOMAIN\USERNAME 大写；USERDOMAIN 为空时退化为 USERNAME。
        /// 只用于管道名去重，不承担鉴权（鉴权靠管道 ACL）。
        /// </summary>
        public static string UserKeyFromEnvironment()
        {
            string domain = Environment.GetEnvironmentVariable("USERDOMAIN");
            string user = Environment.GetEnvironmentVariable("USERNAME");
            if (string.IsNullOrEmpty(user))
            {
                user = Environment.UserName;
            }
            string key = string.IsNullOrEmpty(domain) ? (user ?? string.Empty) : domain + "\\" + user;
            return key.ToUpperInvariant();
        }

        /// <summary>userHash8 = sha256(utf8(userKey)).hex[:8]，进程内缓存一次。</summary>
        public static string UserHash8FromEnvironment()
        {
            string cached = _cachedUserHash8;
            if (!string.IsNullOrEmpty(cached))
            {
                return cached;
            }
            string hash = ParamsDigest.Sha256Hex(UserKeyFromEnvironment());
            string eight = hash.Substring(0, 8);
            _cachedUserHash8 = eight;
            return eight;
        }

        /// <summary>构造完整管道名：\\.\pipe\engineering-platform.v1.{userHash8}.{app}.{instanceId}</summary>
        public static string Build(string app, string instanceId, string userHash8)
        {
            return PipePrefix + "." + (userHash8 ?? UserHash8FromEnvironment()) + "." + app + "." + instanceId;
        }

        /// <summary>
        /// 供 NamedPipeServerStream 使用的短名（去掉 \\.\pipe\ 前缀）。
        /// </summary>
        public static string ToServerStreamName(string fullPipeName)
        {
            if (string.IsNullOrEmpty(fullPipeName))
            {
                throw new ArgumentException("fullPipeName 不能为空", "fullPipeName");
            }
            const string prefix = @"\\.\pipe\";
            if (fullPipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return fullPipeName.Substring(prefix.Length);
            }
            return fullPipeName;
        }

        /// <summary>实例发现目录：%LOCALAPPDATA%\EngineeringPlatform\instances</summary>
        public static string DefaultInstanceDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(local, "EngineeringPlatform"), "instances");
        }

        /// <summary>
        /// 实例发现文件：%LOCALAPPDATA%\EngineeringPlatform\instances\{userHash8}.{app}.{instanceId}.json
        /// </summary>
        public static string DiscoveryFilePath(string app, string instanceId, string userHash8, string instanceDir = null)
        {
            string dir = string.IsNullOrEmpty(instanceDir) ? DefaultInstanceDir() : instanceDir;
            string name = (userHash8 ?? UserHash8FromEnvironment()) + "." + app + "." + instanceId + ".json";
            return Path.Combine(dir, name);
        }

        /// <summary>从完整管道名反解 (app, instanceId)，供日志与面板显示。</summary>
        public static bool TryParse(string fullPipeName, out string app, out string instanceId)
        {
            app = null;
            instanceId = null;
            if (string.IsNullOrEmpty(fullPipeName))
            {
                return false;
            }
            int idx = fullPipeName.LastIndexOf(".", StringComparison.Ordinal);
            if (idx <= 0 || idx >= fullPipeName.Length - 1)
            {
                return false;
            }
            instanceId = fullPipeName.Substring(idx + 1);
            string rest = fullPipeName.Substring(0, idx);
            int idx2 = rest.LastIndexOf(".", StringComparison.Ordinal);
            if (idx2 <= 0 || idx2 >= rest.Length - 1)
            {
                return false;
            }
            app = rest.Substring(idx2 + 1);
            // 只接受已知 app 标识，避免把任意管道名误判为宿主
            if (app != AppAcad && app != AppMock && app != "civil" && app != "revit" && app != "p3d")
            {
                app = null;
                instanceId = null;
                return false;
            }
            return instanceId.Length == 8;
        }

        /// <summary>
        /// 生成 hostInstanceId：{app}-{8 hex}。
        /// </summary>
        public static string BuildHostInstanceId(string app, string instanceId)
        {
            return app + "-" + instanceId;
        }

        /// <summary>校验 hostInstanceId 形如 acad-3f7c1a92。</summary>
        public static bool IsValidHostInstanceId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 13)
            {
                return false;
            }
            int dash = value.IndexOf('-');
            if (dash <= 0 || dash != value.Length - 9)
            {
                return false;
            }
            for (int i = dash + 1; i < value.Length; i++)
            {
                char c = value[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>把 long 句柄值格式化为大写 hex（不带 0x）。</summary>
        public static string FormatHandle(long value)
        {
            return value.ToString("X", CultureInfo.InvariantCulture);
        }
    }
}
