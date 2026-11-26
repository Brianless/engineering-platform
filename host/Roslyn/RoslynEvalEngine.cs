using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using EngineeringPlatform.Host.Diagnostics;

#if NET8_0_OR_GREATER
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
#else
using System.CodeDom.Compiler;
using Microsoft.CSharp;
#endif

namespace EngineeringPlatform.Host.Roslyn
{
    internal sealed class EvalResult
    {
        public bool Ok;
        public object ReturnValue;
        public List<string> Logs = new List<string>();
        public List<string> Diagnostics = new List<string>();
        public string ErrorMessage;
        public long CompileMs;
        public long ExecuteMs;
        public string Engine;
    }

    /// <summary>
    /// 100% 底层 API 自由执行引擎（v3.0 Phase 3）：
    /// - net8.0-windows（Civil 3D 2025）：内嵌 Roslyn (Microsoft.CodeAnalysis.CSharp) 在独立可回收
    ///   AssemblyLoadContext 中内存编译并执行，完全隔离避免 FileLoadException 与内存泄漏；
    /// - net48（Civil 3D 2024）：使用 CSharpCodeProvider 内存编译，实现双目标全覆盖。
    /// 注入对象：Document doc, Database db, Transaction tr, Editor ed,
    ///           Dictionary&lt;string, object&gt; args, Action&lt;string&gt; log。
    /// </summary>
    internal static class RoslynEvalEngine
    {
#if NET8_0_OR_GREATER
        private sealed class EvalScriptLoadContext : AssemblyLoadContext
        {
            public EvalScriptLoadContext() : base("EpEvalScript", isCollectible: true)
            {
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                // 优先复用宿主进程已加载的 AutoCAD / Civil 3D / .NET 运行时程序集
                return null;
            }
        }
#endif

        /// <summary>在给定 Document / Database / Transaction 上下文中编译并执行 C# 代码。</summary>
        public static EvalResult Execute(
            string code,
            Document doc,
            Database db,
            Transaction tr,
            Editor ed,
            Dictionary<string, object> args,
            List<string> extraUsings)
        {
            EvalResult res = new EvalResult();
#if NET8_0_OR_GREATER
            res.Engine = "roslyn-net8";
#else
            res.Engine = "codedom-net48";
#endif
            if (string.IsNullOrWhiteSpace(code))
            {
                res.Ok = false;
                res.ErrorMessage = "code_required";
                return res;
            }

            string wrappedSource = WrapCode(code, extraUsings);
            List<string> logs = new List<string>();
            Action<string> logFn = delegate(string msg)
            {
                if (logs.Count < 500)
                {
                    logs.Add(msg ?? string.Empty);
                }
            };

            Stopwatch swCompile = Stopwatch.StartNew();
#if NET8_0_OR_GREATER
            ExecuteNet8(wrappedSource, doc, db, tr, ed, args ?? new Dictionary<string, object>(StringComparer.Ordinal), logFn, swCompile, res);
#else
            ExecuteNet48(wrappedSource, doc, db, tr, ed, args ?? new Dictionary<string, object>(StringComparer.Ordinal), logFn, swCompile, res);
#endif
            res.Logs = logs;
            return res;
        }

        private static bool HasCivilRuntimeLoaded()
        {
            try
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    string name = loaded[i].GetName().Name;
                    if (string.Equals(name, "AeccDbMgd", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static string WrapCode(string code, List<string> extraUsings)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Globalization;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using Autodesk.AutoCAD.ApplicationServices;");
            sb.AppendLine("using Autodesk.AutoCAD.DatabaseServices;");
            sb.AppendLine("using Autodesk.AutoCAD.EditorInput;");
            sb.AppendLine("using Autodesk.AutoCAD.Geometry;");
            sb.AppendLine("using Autodesk.AutoCAD.Runtime;");
            if (HasCivilRuntimeLoaded())
            {
                sb.AppendLine("using Autodesk.Civil.ApplicationServices;");
                sb.AppendLine("using Autodesk.Civil.DatabaseServices;");
            }
            if (extraUsings != null)
            {
                for (int i = 0; i < extraUsings.Count; i++)
                {
                    string u = (extraUsings[i] ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(u))
                    {
                        if (!u.StartsWith("using ", StringComparison.Ordinal))
                        {
                            u = "using " + u;
                        }
                        if (!u.EndsWith(";", StringComparison.Ordinal))
                        {
                            u = u + ";";
                        }
                        sb.AppendLine(u);
                    }
                }
            }

            // 如果用户代码已经定义了完整 EpEvalScript 类，直接拼接；否则自动包裹成 Run 方法体
            if (code.IndexOf("class EpEvalScript", StringComparison.Ordinal) >= 0)
            {
                sb.AppendLine(code);
            }
            else
            {
                sb.AppendLine("public static class EpEvalScript");
                sb.AppendLine("{");
                sb.AppendLine("    public static object Run(Document doc, Database db, Transaction tr, Editor ed, Dictionary<string, object> args, Action<string> log)");
                sb.AppendLine("    {");
                sb.AppendLine(code);
                if (!EndsWithReturn(code))
                {
                    sb.AppendLine("        return null;");
                }
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        private static bool EndsWithReturn(string code)
        {
            string trimmed = (code ?? string.Empty).TrimEnd();
            if (trimmed.Length == 0) return false;
            int lastLine = trimmed.LastIndexOf('\n');
            string tail = lastLine >= 0 ? trimmed.Substring(lastLine + 1).Trim() : trimmed;
            return tail.StartsWith("return ", StringComparison.Ordinal) || tail == "return;";
        }

#if NET8_0_OR_GREATER
        private static void ExecuteNet8(
            string source,
            Document doc,
            Database db,
            Transaction tr,
            Editor ed,
            Dictionary<string, object> args,
            Action<string> logFn,
            Stopwatch swCompile,
            EvalResult res)
        {
            EvalScriptLoadContext alc = null;
            try
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
                List<MetadataReference> refs = CollectMetadataReferences();
                string asmName = "EpEval_" + Guid.NewGuid().ToString("N");
                CSharpCompilationOptions options = new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release);

                CSharpCompilation compilation = CSharpCompilation.Create(asmName, new[] { tree }, refs, options);

                using (MemoryStream ms = new MemoryStream())
                {
                    EmitResult emit = compilation.Emit(ms);
                    swCompile.Stop();
                    res.CompileMs = swCompile.ElapsedMilliseconds;

                    if (!emit.Success)
                    {
                        foreach (Diagnostic diag in emit.Diagnostics)
                        {
                            if (diag.Severity == DiagnosticSeverity.Error || diag.IsWarningAsError)
                            {
                                res.Diagnostics.Add(diag.ToString());
                            }
                        }
                        res.Ok = false;
                        res.ErrorMessage = "compile_failed:" + (res.Diagnostics.Count > 0 ? res.Diagnostics[0] : "unknown");
                        return;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    alc = new EvalScriptLoadContext();
                    Assembly loaded = alc.LoadFromStream(ms);
                    Type entryType = loaded.GetType("EpEvalScript");
                    if (entryType == null)
                    {
                        res.Ok = false;
                        res.ErrorMessage = "entry_type_EpEvalScript_not_found";
                        return;
                    }
                    MethodInfo runMethod = entryType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (runMethod == null)
                    {
                        res.Ok = false;
                        res.ErrorMessage = "entry_method_Run_not_found";
                        return;
                    }

                    Stopwatch swExec = Stopwatch.StartNew();
                    try
                    {
                        res.ReturnValue = runMethod.Invoke(null, new object[] { doc, db, tr, ed, args, logFn });
                        res.Ok = true;
                    }
                    catch (TargetInvocationException tie)
                    {
                        Exception inner = tie.InnerException ?? tie;
                        res.Ok = false;
                        res.ErrorMessage = inner.GetType().Name + ": " + inner.Message;
                    }
                    finally
                    {
                        swExec.Stop();
                        res.ExecuteMs = swExec.ElapsedMilliseconds;
                    }
                }
            }
            catch (Exception ex)
            {
                swCompile.Stop();
                res.CompileMs = swCompile.ElapsedMilliseconds;
                res.Ok = false;
                res.ErrorMessage = ex.GetType().Name + ": " + ex.Message;
                HostLog.Exception("roslyn", "eval_net8_failed", ex);
            }
            finally
            {
                if (alc != null)
                {
                    try
                    {
                        alc.Unload();
                    }
                    catch (Exception)
                    {
                        // 忽略卸载异常
                    }
                }
            }
        }

        private static List<MetadataReference> CollectMetadataReferences()
        {
            List<MetadataReference> refs = new List<MetadataReference>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                Assembly asm = loaded[i];
                if (asm == null || asm.IsDynamic)
                {
                    continue;
                }
                string loc;
                try
                {
                    loc = asm.Location;
                }
                catch (Exception)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(loc) || !File.Exists(loc) || !seen.Add(loc))
                {
                    continue;
                }
                try
                {
                    refs.Add(MetadataReference.CreateFromFile(loc));
                }
                catch (Exception)
                {
                    // 非托管/混合程序集无法创建元数据引用时跳过
                }
            }
            return refs;
        }
#else
        private static void ExecuteNet48(
            string source,
            Document doc,
            Database db,
            Transaction tr,
            Editor ed,
            Dictionary<string, object> args,
            Action<string> logFn,
            Stopwatch swCompile,
            EvalResult res)
        {
            try
            {
                using (CSharpCodeProvider provider = new CSharpCodeProvider())
                {
                    CompilerParameters cp = new CompilerParameters();
                    cp.GenerateInMemory = true;
                    cp.GenerateExecutable = false;
                    cp.IncludeDebugInformation = false;

                    // 按程序集简称去重（防止 GAC 与 Framework64 目录下的同名 System.dll / mscorlib.dll 触发 CS1703 重复引用）
                    HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    seenNames.Add("mscorlib"); // csc 默认隐式引用 mscorlib，绝不重复添加

                    Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < loaded.Length; i++)
                    {
                        Assembly asm = loaded[i];
                        if (asm == null || asm.IsDynamic) continue;
                        string loc = null;
                        string simpleName = null;
                        try
                        {
                            loc = asm.Location;
                            simpleName = asm.GetName().Name;
                        }
                        catch (Exception) { continue; }
                        if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;
                        if (string.IsNullOrEmpty(simpleName)) simpleName = Path.GetFileNameWithoutExtension(loc);
                        if (seenNames.Add(simpleName))
                        {
                            cp.ReferencedAssemblies.Add(loc);
                        }
                    }

                    // net48 仅在尚未加载时补齐 System.Runtime / netstandard 等门面程序集（修复 CS0012 且避免 CS1703）
                    string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
                    if (!string.IsNullOrEmpty(runtimeDir))
                    {
                        string[] coreFacades = new string[]
                        {
                            "System.Runtime",
                            "netstandard",
                            "System.Core",
                            "System",
                            "System.Collections",
                            "System.Linq",
                            "System.Globalization"
                        };
                        for (int f = 0; f < coreFacades.Length; f++)
                        {
                            string name = coreFacades[f];
                            if (seenNames.Contains(name)) continue;
                            string p1 = Path.Combine(runtimeDir, name + ".dll");
                            string p2 = Path.Combine(runtimeDir, "Facades", name + ".dll");
                            string pick = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);
                            if (pick != null && seenNames.Add(name))
                            {
                                cp.ReferencedAssemblies.Add(pick);
                            }
                        }
                    }

                    CompilerResults cr = provider.CompileAssemblyFromSource(cp, source);
                    swCompile.Stop();
                    res.CompileMs = swCompile.ElapsedMilliseconds;

                    if (cr.Errors.HasErrors)
                    {
                        foreach (CompilerError err in cr.Errors)
                        {
                            if (!err.IsWarning)
                            {
                                res.Diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                                    "L{0}: {1} {2}", err.Line, err.ErrorNumber, err.ErrorText));
                            }
                        }
                        res.Ok = false;
                        res.ErrorMessage = "compile_failed:" + (res.Diagnostics.Count > 0 ? res.Diagnostics[0] : "unknown");
                        return;
                    }

                    Type entryType = cr.CompiledAssembly.GetType("EpEvalScript");
                    MethodInfo runMethod = entryType == null ? null : entryType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (runMethod == null)
                    {
                        res.Ok = false;
                        res.ErrorMessage = "entry_method_Run_not_found";
                        return;
                    }

                    Stopwatch swExec = Stopwatch.StartNew();
                    try
                    {
                        res.ReturnValue = runMethod.Invoke(null, new object[] { doc, db, tr, ed, args, logFn });
                        res.Ok = true;
                    }
                    catch (TargetInvocationException tie)
                    {
                        Exception inner = tie.InnerException ?? tie;
                        res.Ok = false;
                        res.ErrorMessage = inner.GetType().Name + ": " + inner.Message;
                    }
                    finally
                    {
                        swExec.Stop();
                        res.ExecuteMs = swExec.ElapsedMilliseconds;
                    }
                }
            }
            catch (Exception ex)
            {
                swCompile.Stop();
                res.CompileMs = swCompile.ElapsedMilliseconds;
                res.Ok = false;
                res.ErrorMessage = ex.GetType().Name + ": " + ex.Message;
                HostLog.Exception("roslyn", "eval_net48_failed", ex);
            }
        }
#endif
    }
}
