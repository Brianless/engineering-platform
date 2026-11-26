using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Scheduling;
using EngineeringPlatform.Host.Semantic;
using EngineeringPlatform.Host.Tools;

namespace EngineeringPlatform.Host.Protocol
{
    /// <summary>
    /// 单个请求在主线程执行时的上下文。handler 只能通过它访问 CAD 对象，
    /// 保证「所有 CAD API 调用都在主线程」这一硬约束可被静态检查（grep Pipe/** 无 CAD 引用）。
    /// </summary>
    internal sealed class ToolContext
    {
        public HostRuntime Runtime;
        public WorkItem Item;
        public Document Doc;
        public Database Db;
        public string Fingerprint = string.Empty;
        public List<WireWarning> Warnings = new List<WireWarning>();
        public SemanticModel Model;

        /// <summary>取（并缓存）语义模型；refresh=true 时绕过 5 s 缓存。</summary>
        public SemanticModel EnsureModel(bool refresh)
        {
            if (Model == null)
            {
                Model = Runtime.GetSemanticModel(Doc, refresh);
            }
            return Model;
        }

        /// <summary>取消/超时判定：handler 在对象批边界调用（默认每 500 个对象）。</summary>
        public bool ShouldAbort()
        {
            if (Item == null)
            {
                return false;
            }
            if (Item.CancelRequested)
            {
                return true;
            }
            if (Item.ExecDeadlineUtc != DateTime.MinValue && DateTime.UtcNow > Item.ExecDeadlineUtc)
            {
                return true;
            }
            return false;
        }

        /// <summary>取消/超时统一转成 E3002（结果不明），语义与设计 §2.4 一致。</summary>
        public void ThrowIfAborted()
        {
            if (!ShouldAbort())
            {
                return;
            }
            if (Item != null && Item.CancelRequested)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E3002, "cancelled_by_client"));
            }
            throw new ToolFailureException(WireError.Of(ErrorCodes.E3002, "execution_deadline_exceeded"));
        }

        public void AddWarning(string code, string detail = null)
        {
            Warnings.Add(WireWarning.Of(code, detail));
        }
    }

    /// <summary>
    /// <summary>
    /// 工具分派：写域一律 E2003，未知工具 E2001，其余按域交给对应 handler。
    /// 注意：这里的判定发生在 MainThreadDispatcher 的主线程上，故可安全访问 CAD。
    /// </summary>
    /// <remarks>
    /// NIT-1（QA 提出「Protocol/ 下出现 Autodesk.* 引用」）的处理说明 —— 选择「加注释说明」而非「挪到 Tools/」：
    /// 1. 它**确实**是本层唯一接触 CAD 类型的文件，但接触方式是 `ToolContext.Runtime`（HostRuntime 句柄），
    ///    自身不做任何 CAD 对象操作、不开事务、不取 ObjectId —— 真正碰 CAD 的代码在 `Tools/*ToolHandlers.cs`；
    /// 2. 挪到 `Tools/` 会反过来破坏分层：`Pipe/` → `Protocol/` → `Tools/` 是单向依赖，
    ///    而 `Tools/` 的 handler 需要 `ToolContext`（定义在本文件上方），挪走会形成 Protocol ↔ Tools 双向引用；
    /// 3. 静态审查的硬约束是「`Pipe/**` 不得出现 Autodesk 引用」，本文件不在 `Pipe/` 下，不违反该约束
    ///    （`tests/static_audit.py` 与 `tests/qa/qa_static_review.py` 均已按目录断言，可复验）。
    /// 若未来本文件真的开始直接调 CAD API，则必须按 NIT-1 原建议挪到 `Tools/`。
    /// </remarks>
    internal sealed class ToolRouter
    {
        private readonly HostRuntime _runtime;

        public ToolRouter(HostRuntime runtime)
        {
            _runtime = runtime;
        }

        /// <summary>读取当前命令行提示与交互状态（允许在 CMDACTIVE!=0 期间调用，用于驱动 CASS/飞时达多步交互）。</summary>
        private static Dictionary<string, object> ReadCmdLineState(ToolContext context)
        {
            Dictionary<string, object> res = new Dictionary<string, object>(StringComparer.Ordinal);
            int cmdActive = 0;
            string cmdNames = string.Empty;
            string lastPrompt = string.Empty;
            int osMode = 0;
            string clayer = string.Empty;
            try
            {
                object v1 = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDACTIVE");
                if (v1 != null) cmdActive = Convert.ToInt32(v1, System.Globalization.CultureInfo.InvariantCulture);
                object v2 = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDNAMES");
                if (v2 != null) cmdNames = Convert.ToString(v2, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                object v3 = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("LASTPROMPT");
                if (v3 != null) lastPrompt = Convert.ToString(v3, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                object v4 = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("OSMODE");
                if (v4 != null) osMode = Convert.ToInt32(v4, System.Globalization.CultureInfo.InvariantCulture);
                object v5 = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CLAYER");
                if (v5 != null) clayer = Convert.ToString(v5, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch (Exception)
            {
            }
            res["cmdActive"] = cmdActive;
            res["cmdNames"] = cmdNames;
            res["lastPrompt"] = lastPrompt;
            res["osMode"] = osMode;
            res["clayer"] = clayer;
            res["quiescent"] = (cmdActive == 0);
            res["summary"] = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "【命令行状态】CMDACTIVE={0}｜命令='{1}'｜当前提示='{2}'｜OSMODE={3}｜CLAYER={4}",
                cmdActive, cmdNames, lastPrompt, osMode, clayer);
            return res;
        }

        /// <summary>写域工具拒绝（M0 写入通道硬关闭）。</summary>
        public static WireError RejectWriteDomain(string tool)
        {
            return WireError.Of(ErrorCodes.E2003, "write_channel_disabled_m0", "tool");
        }

        /// <summary>分派入口；成功返回工具结果对象，失败抛 <see cref="ToolFailureException"/>。</summary>
        public Dictionary<string, object> Dispatch(ToolContext context)
        {
            if (context == null || context.Item == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "context_missing"));
            }

            string tool = context.Item.Tool ?? string.Empty;
            if (string.Equals(tool, "ui_read_cmdline", StringComparison.Ordinal))
            {
                return ReadCmdLineState(context);
            }
            if (ToolCatalog.IsWriteDomain(tool))
            {
                // 第 1 层门禁：配置开关。关着的时候输出与 M0 逐字节一致（E2003 + 同一 detail）。
                if (!_runtime.Config.WriteEnabled)
                {
                    HostLog.Warn("router", "write_domain_rejected", "tool", tool, "op", context.Item.OpId);
                    throw new ToolFailureException(RejectWriteDomain(tool));
                }
                // 注意：底层方法名（cad_/civil_/…）即使开了写通道也**不允许**直接调 ——
                // 只有 write_* 才是宿主自己实现的、过了审批的写工具。
                if (!tool.StartsWith("write_", StringComparison.OrdinalIgnoreCase))
                {
                    HostLog.Warn("router", "write_domain_rejected", "tool", tool,
                        "reason", "only_write_namespace_allowed", "op", context.Item.OpId);
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E2003,
                        "only_write_namespace_allowed", "tool"));
                }
            }

            ToolDescriptor descriptor = ToolCatalog.Find(tool);
            if (descriptor == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_tool:" + tool, "tool"));
            }

            HostLog.Info("router", "tool_executing", "op", context.Item.OpId, "tool", tool);

            if (string.Equals(descriptor.Domain, "sys", StringComparison.Ordinal))
            {
                return SysToolHandlers.Handle(context);
            }
            if (string.Equals(descriptor.Domain, "read", StringComparison.Ordinal))
            {
                return ReadToolHandlers.Handle(context);
            }
            if (string.Equals(descriptor.Domain, "task", StringComparison.Ordinal))
            {
                return TaskToolHandlers.Handle(context);
            }
            if (string.Equals(descriptor.Domain, "write", StringComparison.Ordinal))
            {
                return WriteToolHandlers.Handle(context);
            }
            if (string.Equals(descriptor.Domain, "export", StringComparison.Ordinal))
            {
                return ExportToolHandlers.Handle(context);
            }
            if (string.Equals(descriptor.Domain, "preview", StringComparison.Ordinal))
            {
                return PreviewToolHandlers.Handle(context);
            }
            if (string.Equals(descriptor.Domain, "batch", StringComparison.Ordinal))
            {
                return BatchToolHandlers.Handle(context);
            }

            throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "domain_not_supported:" + descriptor.Domain, "tool"));
        }
    }
}
