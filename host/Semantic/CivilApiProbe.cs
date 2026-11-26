using System;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;

namespace EngineeringPlatform.Host.Semantic
{
    /// <summary>
    /// Civil 3D 能力探测（第二轮修订：Civil 3D 已安装，探测后按实际能力工作）。
    ///
    /// 放在 Semantic/ 而不是 Protocol/：本类编译期依赖 AutoCAD 类型（Document / Database），
    /// 而协议层（Protocol/**）必须保持零 CAD 依赖（静态审查会检查）。
    ///
    /// 判据：能解析出 Autodesk.Civil.ApplicationServices.CivilApplication 类型，
    /// 且能取到 CivilDocument。任一步失败即判定不可用，走 gaps[] / W1001 降级路径
    /// （其他机器未装 Civil 3D 时就是这个分支），绝不假设可用，
    /// 也绝不拿普通多段线冒充专业对象。
    ///
    /// 反射探测三步（任一步成功即视为可解析）：
    /// 1) 扫描已加载程序集里名为 AeccDbMgd 的类型；
    /// 2) Type.GetType 带程序集限定名；
    /// 3) Assembly.Load("AeccDbMgd") 兜底（Civil 3D 进程里该程序集必然可加载）。
    /// AutoCAD 2022 进程没有 AeccDbMgd，三步全失败 → available=false。
    /// </summary>
    internal static class CivilApiProbe
    {
        public const string AssemblyName = "AeccDbMgd";
        public const string ApplicationTypeName = "Autodesk.Civil.ApplicationServices.CivilApplication";
        private const string PrimaryTypeName = ApplicationTypeName + ", AeccDbMgd";
        private const string SecondaryTypeName = "Autodesk.Civil.DatabaseServices.Entity, AeccDbMgd";
        private const string TertiaryTypeName = "Autodesk.Civil.DatabaseServices.Alignment, AeccDbMgd";

        private static readonly object Sync = new object();
        private static bool? _available;
        private static string _reason;
        private static string _release;
        private static Type _applicationType;

        /// <summary>探测描述（写进 capabilities.civilApi.probe，让用户看到判据）。</summary>
        public static string ProbeText
        {
            get { return "ResolveType('" + PrimaryTypeName + "') && CivilApplication.ActiveDocument != null"; }
        }

        /// <summary>是否具备 Civil 3D 能力（AeccDbMgd 可解析）。</summary>
        public static bool Available
        {
            get
            {
                EnsureProbed();
                return _available.GetValueOrDefault(false);
            }
        }

        /// <summary>握手 hello_ack.hostProduct：Civil3D / AutoCAD。</summary>
        public static string ProductName
        {
            get { return Available ? "Civil3D" : "AutoCAD"; }
        }

        /// <summary>给人看的完整产品名（日志/摘要用）。</summary>
        public static string ProductDisplayName
        {
            get { return Available ? "Civil 3D" : "AutoCAD"; }
        }

        /// <summary>宿主发行版本：R24.3 / R23.0 / R24.1（取 ACADVER，失败回退 Application.Version）。</summary>
        public static string ReleaseTag
        {
            get
            {
                EnsureProbed();
                return _release ?? "unknown";
            }
        }

        public static string Reason
        {
            get
            {
                EnsureProbed();
                return _reason;
            }
        }

        /// <summary>
        /// 取当前文档对应的 CivilDocument；取不到返回 null（调用方据此判定 detected=false）。
        /// 只在主线程调用（CivilDocument 是 CAD 对象）。
        /// </summary>
        public static object GetCivilDocument(Document doc)
        {
            Type applicationType = ApplicationType;
            if (applicationType == null)
            {
                return null;
            }

            // 首选静态属性 CivilApplication.ActiveDocument
            try
            {
                PropertyInfo active = applicationType.GetProperty(
                    "ActiveDocument", BindingFlags.Public | BindingFlags.Static);
                if (active != null)
                {
                    object value = active.GetValue(null, null);
                    if (value != null)
                    {
                        return value;
                    }
                }
            }
            catch (Exception)
            {
                // 无活动 Civil 文档时 getter 会抛，继续走 Database 重载
            }

            // 兜底：CivilApplication.GetCivilDocument(Database)
            if (doc != null)
            {
                try
                {
                    MethodInfo getter = applicationType.GetMethod(
                        "GetCivilDocument",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(Database) },
                        null);
                    if (getter != null)
                    {
                        object value = getter.Invoke(null, new object[] { doc.Database });
                        if (value != null)
                        {
                            return value;
                        }
                    }
                }
                catch (Exception)
                {
                    // 取不到即返回 null，由调用方降级
                }
            }
            return null;
        }

        /// <summary>
        /// 取**内存中**（不附着于文档）的 CivilDocument —— 夹具生成用。
        /// `new Database(true, true)` 建出来的库也能取到 CivilDocument，
        /// 这样建夹具时不必先开一张真图，避免污染用户的图纸目录。
        /// </summary>
        public static object GetCivilDocument(Database db)
        {
            Type applicationType = ApplicationType;
            if (applicationType == null || db == null)
            {
                return null;
            }
            try
            {
                MethodInfo getter = applicationType.GetMethod(
                    "GetCivilDocument",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Database) },
                    null);
                if (getter != null)
                {
                    return getter.Invoke(null, new object[] { db });
                }
            }
            catch (Exception ex)
            {
                HostLog.Warn("semantic", "get_civil_document_db_failed", "type", ex.GetType().Name);
            }
            return null;
        }

        private static Type ApplicationType
        {
            get
            {
                EnsureProbed();
                return _applicationType;
            }
        }

        private static void EnsureProbed()
        {
            if (_available.HasValue)
            {
                return;
            }
            lock (Sync)
            {
                if (_available.HasValue)
                {
                    return;
                }

                Type applicationType = ResolveApplicationType();
                bool found = applicationType != null;
                if (!found)
                {
                    try
                    {
                        found = Type.GetType(SecondaryTypeName, false) != null
                            || Type.GetType(TertiaryTypeName, false) != null;
                    }
                    catch (Exception)
                    {
                        found = false;
                    }
                }

                _applicationType = applicationType;
                _available = found;
                _release = ResolveReleaseTag();
                _reason = found
                    ? "已探测到 Civil 3D 托管 API（AeccDbMgd 可解析），原生对象按 confidence=1.0 读取"
                    : "Civil 3D 托管 API 不可用（AeccDbMgd 无法解析）：相关对象以 ACAD_PROXY_ENTITY 出现，按缺口上报";
            }
        }

        /// <summary>三步反射找到 CivilApplication 类型（找不到返回 null）。</summary>
        private static Type ResolveApplicationType()
        {
            // 1) 已加载程序集
            try
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    Assembly assembly = loaded[i];
                    string name;
                    try
                    {
                        name = assembly.GetName().Name;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (!string.Equals(name, AssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Type type = assembly.GetType(ApplicationTypeName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
            }
            catch (Exception)
            {
                // 继续下一步
            }

            // 2) 程序集限定名
            try
            {
                Type type = Type.GetType(PrimaryTypeName, false);
                if (type != null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // 继续下一步
            }

            // 3) Assembly.Load 兜底
            try
            {
                Assembly assembly = Assembly.Load(AssemblyName);
                if (assembly != null)
                {
                    return assembly.GetType(ApplicationTypeName, false);
                }
            }
            catch (Exception)
            {
                // 未安装 Civil 3D 的正常路径
            }
            return null;
        }

        /// <summary>发行版本号：ACADVER（"24.3s"）→ "R24.3"；取不到回退 Application.Version。</summary>
        private static string ResolveReleaseTag()
        {
            try
            {
                object raw = Application.GetSystemVariable("ACADVER");
                string text = raw == null ? string.Empty : Convert.ToString(raw, CultureInfo.InvariantCulture);
                string digits = string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if ((c >= '0' && c <= '9') || c == '.')
                    {
                        digits += c;
                    }
                    else if (digits.Length > 0)
                    {
                        break;
                    }
                }
                if (digits.Length >= 3)
                {
                    return "R" + digits;
                }
            }
            catch (Exception)
            {
                // 无桌面会话时取不到，走版本号回退
            }

            try
            {
                Version version = Application.Version;
                if (version != null)
                {
                    return "R" + version.Major.ToString(CultureInfo.InvariantCulture)
                        + "." + version.Minor.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            return "unknown";
        }

        /// <summary>
        /// 判定对象是否「Civil 3D 原生对象」。Civil API 可用时 AECC* 是原生对象，
        /// 不可用时才是代理对象（此时不允许当原生处理）。
        /// </summary>
        public static bool IsCivilNativeDxfName(string dxfName)
        {
            if (!Available || string.IsNullOrEmpty(dxfName))
            {
                return false;
            }
            return dxfName.StartsWith("AECC", StringComparison.OrdinalIgnoreCase)
                || dxfName.StartsWith("AeccDb", StringComparison.OrdinalIgnoreCase);
        }
    }
}
