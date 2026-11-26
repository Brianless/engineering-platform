using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Commands
{
    /// <summary>
    /// Civil 3D 托管 API 探测命令（第三轮，**仅用于开发期取证**，不是业务能力）。
    ///
    /// ENG_DEVPROBE &lt;输出目录&gt;：
    /// 把 AeccDbMgd 里与本项目相关的类型（曲面/路线/纵断面/管网/点编组）的
    /// 静态 Create 重载、实例方法、属性签名写到 &lt;输出目录&gt;\civil-api-probe.json。
    ///
    /// 为什么需要它：2019 与 2024 的属性/方法暴露差异很大（上一轮已踩过），
    /// 盲写反射代码只能靠编译期猜。这一条命令把「真实签名」落成文件，
    /// 后续 ENG_DEVFIXTURE 与 Civil3dReader 都按实测签名写，不再靠猜。
    ///
    /// 安全性：只读反射（Type.GetMethods/GetProperties），不实例化 CAD 对象、
    /// 不打开数据库、不写图纸。写盘只有输出目录里一个 JSON。
    /// </summary>
    public sealed class DevProbeCommand
    {
        /// <summary>AutoRun 用的环境变量名（无人值守跑探测时设置它）。</summary>
        public const string EnvOut = "EP_DEVPROBE_OUT";

        /// <summary>要 dump 的类型名（只留与读路径相关的，避免几百 KB 噪音）。</summary>
        private static readonly string[] TypeWhitelist =
        {
            "CivilDocument", "TinSurface", "Surface", "SurfaceCreationOptions",
            "Alignment", "Profile", "ProfileView", "PipeNetwork", "PressureNetwork",
            "PointGroup", "PointGroupCollection", "SurfaceCollection", "AlignmentCollection",
            "ProfileCollection", "PipeNetworkCollection", "PointGroupCollection"
        };

        /// <summary>方法名里带这些关键字才记录（其余方法对读取路径无用）。</summary>
        private static readonly string[] MethodKeywords =
        {
            "Create", "Add", "Insert", "Import", "Set", "Get", "Point", "Vertex",
            "Station", "Elevation", "Triangle", "Name", "Count", "Handle"
        };

        [CommandMethod("ENG_DEVPROBE")]
        public void RunProbe()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            string outputDir = Environment.GetEnvironmentVariable(EnvOut);
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = AskOutputDirectory(doc);
            }
            if (string.IsNullOrEmpty(outputDir))
            {
                WriteLine("DEVPROBE FAIL E1001");
                return;
            }
            string path = Probe(outputDir);
            WriteLine(path == null ? "DEVPROBE FAIL E3002" : "DEVPROBE OK " + path);
        }

        /// <summary>执行探测并落盘；返回文件路径，失败返回 null。</summary>
        internal static string Probe(string outputDir)
        {
            try
            {
                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
                string path = Path.Combine(outputDir, "civil-api-probe.json");
                Dictionary<string, object> payload = Collect();
                File.WriteAllText(path, JsonCodec.SerializeIndented(payload), new UTF8Encoding(false));
                HostLog.Info("devprobe", "probe_written", "path", HostLog.SafePath(path));
                return path;
            }
            catch (Exception ex)
            {
                HostLog.Exception("devprobe", "probe_failed", ex);
                return null;
            }
        }

        private static Dictionary<string, object> Collect()
        {
            Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.Ordinal);
            payload["generatedAtUtc"] = TimeUtil.NowIso();
            payload["acadVersion"] = SafeAcadVersion();
            payload["types"] = new List<object>();

            List<object> assemblies = new List<object>();
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                string name;
                try
                {
                    name = all[i].GetName().Name;
                }
                catch (Exception)
                {
                    continue;
                }
                if (name != null && name.IndexOf("Aecc", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    assemblies.Add(name);
                }
            }
            payload["civilAssemblies"] = assemblies;

            Assembly aecc = FindAeccDbMgd();
            if (aecc == null)
            {
                payload["note"] = "AeccDbMgd 未加载，无法探测（说明当前宿主不是 Civil 3D 或插件尚未加载 C3D 程序集）";
                return payload;
            }
            payload["aeccDbMgd"] = aecc.FullName;

            List<object> types = (List<object>)payload["types"];
            Type[] allTypes;
            try
            {
                allTypes = aecc.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                List<Type> loaded = new List<Type>();
                for (int i = 0; i < ex.Types.Length; i++)
                {
                    if (ex.Types[i] != null)
                    {
                        loaded.Add(ex.Types[i]);
                    }
                }
                allTypes = loaded.ToArray();
                payload["typeLoadWarning"] = "ReflectionTypeLoadException（部分类型未加载）";
            }

            HashSet<string> wanted = new HashSet<string>(TypeWhitelist, StringComparer.Ordinal);
            for (int i = 0; i < allTypes.Length; i++)
            {
                Type type = allTypes[i];
                if (type == null || !type.IsPublic)
                {
                    continue;
                }
                string simple = type.Name;
                if (simple != null && simple.IndexOf('`') >= 0)
                {
                    continue;
                }
                if (!wanted.Contains(simple))
                {
                    continue;
                }
                types.Add(DescribeType(type));
            }
            return payload;
        }

        private static Assembly FindAeccDbMgd()
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                try
                {
                    if (string.Equals(all[i].GetName().Name, "AeccDbMgd", StringComparison.OrdinalIgnoreCase))
                    {
                        return all[i];
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }
            try
            {
                return Assembly.Load("AeccDbMgd");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, object> DescribeType(Type type)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item["fullName"] = type.FullName;
            item["isAbstract"] = type.IsAbstract;

            List<object> statics = new List<object>();
            List<object> instanceMethods = new List<object>();
            List<object> properties = new List<object>();

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                methods = new MethodInfo[0];
            }
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.IsSpecialName)
                {
                    continue;
                }
                string signature = method.Name + "(" + DescribeParams(method.GetParameters()) + ")";
                if (method.IsStatic)
                {
                    statics.Add(signature);
                }
                else if (ContainsKeyword(method.Name))
                {
                    instanceMethods.Add(signature);
                }
            }

            PropertyInfo[] props;
            try
            {
                props = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                props = new PropertyInfo[0];
            }
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo prop = props[i];
                string entry = prop.Name + " : " + SafeTypeName(prop.PropertyType);
                if (prop.CanWrite)
                {
                    entry += " (可写)";
                }
                properties.Add(entry);
            }

            item["staticMethods"] = statics;
            item["instanceMethods"] = instanceMethods;
            item["properties"] = properties;
            return item;
        }

        private static bool ContainsKeyword(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            for (int i = 0; i < MethodKeywords.Length; i++)
            {
                if (name.IndexOf(MethodKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string DescribeParams(ParameterInfo[] parameters)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(SafeTypeName(parameters[i].ParameterType));
                builder.Append(' ');
                builder.Append(parameters[i].Name);
            }
            return builder.ToString();
        }

        private static string SafeTypeName(Type type)
        {
            try
            {
                if (type == null)
                {
                    return "?";
                }
                return type.Name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string SafeAcadVersion()
        {
            try
            {
                object value = Application.GetSystemVariable("ACADVER");
                return value == null ? "unknown" : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static string AskOutputDirectory(Document doc)
        {
            try
            {
                if (doc == null)
                {
                    return null;
                }
                PromptResult prompt = doc.Editor.GetString("\n探测输出目录: ");
                if (prompt.Status != PromptStatus.OK || string.IsNullOrEmpty(prompt.StringResult))
                {
                    return null;
                }
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(prompt.StringResult.Trim().Trim('"')));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteLine(string message)
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage("\n" + message);
                }
            }
            catch (Exception)
            {
                // 批处理阶段无编辑器上下文，忽略
            }
        }
    }
}
