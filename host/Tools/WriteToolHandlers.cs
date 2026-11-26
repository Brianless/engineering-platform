using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Roslyn;
using EngineeringPlatform.Host.Semantic;
using EngineeringPlatform.Host.Write;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 写通道工具（M1）。设计见 docs/10-M1-写通道设计.md。
    ///
    /// 三个工具构成一条审批链，缺一环就不成立：
    ///   `write_preview`（算 digest，不写） → `write_approve`（人点确认，签发 token） → `write_insert_points`（带 token 执行）
    ///
    /// ## 双确认（2026-11-22 决策：两个都要）
    ///
    /// | 层 | 在哪 | 谁证明 | 性质 |
    /// |---|---|---|---|
    /// | 流程确认 | 聊天里把摘要念给用户、用户回「执行」 | 模型带 `userConfirmation=true` | **流程纪律**，不是密码学防线 |
    /// | 硬确认 | CAD 里弹模态框、人点「是」 | 物理点击 | **硬防线**：模型没法替人点 |
    ///
    /// 写在这里是为了让后来者别把 `userConfirmation` 当成安全控制 —— 它是防"模型自作主张跳过汇报"，
    /// 真正的门是那个弹框。
    /// </summary>
    internal static class WriteToolHandlers
    {
        /// <summary>写参数里参与摘要的键。**必须排除** approvalToken / paramsDigest / userConfirmation，
        /// 否则"执行时的摘要"永远不可能等于"批准时的摘要"。</summary>
        private static readonly string[] DigestKeys = { "kind", "points", "layer", "block", "surface" };

        public static Dictionary<string, object> Handle(ToolContext context)
        {
            string tool = context.Item.Tool;
            if (string.Equals(tool, "write_preview", StringComparison.Ordinal))
            {
                return Preview(context);
            }
            if (string.Equals(tool, "write_approve", StringComparison.Ordinal))
            {
                return Approve(context);
            }
            if (string.Equals(tool, "write_insert_points", StringComparison.Ordinal))
            {
                return InsertPoints(context);
            }
            if (string.Equals(tool, "write_command", StringComparison.Ordinal))
            {
                return RunCommand(context);
            }
            if (string.Equals(tool, "write_surface_breakline", StringComparison.Ordinal))
            {
                return SurfaceBreakline(context);
            }
            if (string.Equals(tool, "write_undo", StringComparison.Ordinal))
            {
                return Undo(context);
            }
            if (string.Equals(tool, "write_object_xdata", StringComparison.Ordinal))
            {
                return WriteObjectXData(context);
            }
            if (string.Equals(tool, "write_alignment_plan", StringComparison.Ordinal))
            {
                return WriteAlignmentPlan(context);
            }
            if (string.Equals(tool, "write_eval_csharp", StringComparison.Ordinal))
            {
                return EvalCSharp(context);
            }
            if (string.Equals(tool, "write_entities", StringComparison.Ordinal))
            {
                return WriteEntities(context);
            }
            throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_write_tool:" + tool, "tool"));
        }

        // ---------------- write_preview ----------------

        private static Dictionary<string, object> Preview(ToolContext context)
        {
            string kind = JsonCodec.GetStr(context.Item.Params, "kind", "insert_points");
            if (string.Equals(kind, "command", StringComparison.Ordinal))
            {
                string cmd = JsonCodec.GetStr(context.Item.Params, "command");
                if (string.IsNullOrEmpty(cmd))
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "command_required", "command"));
                }
                string blocked = MatchForbidden(cmd);
                Dictionary<string, object> cmdPreview = new Dictionary<string, object>(StringComparer.Ordinal);
                cmdPreview["kind"] = "command";
                cmdPreview["command"] = cmd.Trim();
                cmdPreview["blocked"] = blocked;
                cmdPreview["paramsDigest"] = ComputeCommandDigest(context.Item.Params);
                cmdPreview["targetFingerprint"] = CurrentFingerprint(context);
                cmdPreview["wrote"] = false;
                cmdPreview["summary"] = blocked == null
                    ? "将执行命令：" + cmd.Trim() + "（未执行）"
                    : "该命令被硬拒（" + blocked + "）：本工具永不保存/关闭/打印文件";
                return cmdPreview;
            }

            List<PointRow> rows;
            string detail;
            if (!TryParsePoints(context.Item.Params, out rows, out detail))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, detail, "points"));
            }

            if (string.Equals(kind, "surface_breakline", StringComparison.Ordinal))
            {
                string surfaceName = JsonCodec.GetStr(context.Item.Params, "surface");
                if (string.IsNullOrEmpty(surfaceName))
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "surface_required", "surface"));
                }
                Dictionary<string, object> bp = new Dictionary<string, object>(StringComparer.Ordinal);
                bp["kind"] = "surface_breakline";
                bp["surface"] = surfaceName.Trim();
                bp["count"] = rows.Count;
                bp["paramsDigest"] = ComputeDigest(context.Item.Params);
                bp["targetFingerprint"] = CurrentFingerprint(context);
                bp["wrote"] = false;
                bp["summary"] = string.Format(CultureInfo.InvariantCulture,
                    "将向曲面「{0}」添加 1 条断裂线（{1} 个顶点）。未做任何修改。", surfaceName.Trim(), rows.Count);
                return bp;
            }

            string layer = LayerName(context.Item.Params);
            string digest = ComputeDigest(context.Item.Params);
            string fingerprint = CurrentFingerprint(context);
            bool willCreateLayer = !LayerExists(context, layer);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["kind"] = "insert_points";
            result["count"] = rows.Count;
            result["layer"] = layer;
            result["willCreateLayer"] = willCreateLayer;
            result["paramsDigest"] = digest;
            result["targetFingerprint"] = fingerprint;
            result["summary"] = string.Format(CultureInfo.InvariantCulture,
                "将新增 {0} 个点位到图层 {1}{2}。未做任何修改。",
                rows.Count, layer, willCreateLayer ? "（该图层不存在，执行时会新建）" : string.Empty);
            result["wrote"] = false;

            HostLog.Info("write", "preview", "count", rows.Count, "layer", layer,
                "willCreateLayer", willCreateLayer);
            return result;
        }

        // ---------------- write_approve ----------------

        private static Dictionary<string, object> Approve(ToolContext context)
        {
            Dictionary<string, object> p = context.Item.Params;
            bool autoApprove = context.Runtime.Config.WriteAutoApprove;

            // 流程确认（软门）：autoApprove=false 时必须先拿到用户明确确认；autoApprove=true 时全自主放行
            if (!autoApprove && !JsonCodec.GetBool(p, "userConfirmation", false))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                    "chat_confirmation_required", "userConfirmation"));
            }

            string digest = JsonCodec.GetStr(p, "paramsDigest");
            string fingerprint = JsonCodec.GetStr(p, "targetFingerprint");
            if (string.IsNullOrEmpty(fingerprint) && autoApprove)
            {
                fingerprint = CurrentFingerprint(context);
            }
            if (string.IsNullOrEmpty(digest) && autoApprove)
            {
                digest = ComputeCanonicalWriteDigest(p);
            }
            if (string.IsNullOrEmpty(digest) || string.IsNullOrEmpty(fingerprint))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                    "paramsDigest_and_targetFingerprint_required", "paramsDigest"));
            }

            // 摘要必须与"按当前请求参数重算"的结果一致（兼容传统 DigestKeys 与 Canonical JSON 全参数摘要）
            string kind = JsonCodec.GetStr(p, "kind", "insert_points");
            bool isCommand = string.Equals(kind, "command", StringComparison.Ordinal);
            string legacyDigest = isCommand ? ComputeCommandDigest(p) : ComputeDigest(p);
            string canonicalDigest = ComputeCanonicalWriteDigest(p);
            string normProvided = NormalizeDigest(digest);
            string matchedDigest = string.Equals(normProvided, NormalizeDigest(canonicalDigest), StringComparison.Ordinal)
                ? canonicalDigest
                : legacyDigest;
            if (!string.Equals(normProvided, NormalizeDigest(legacyDigest), StringComparison.Ordinal) &&
                !string.Equals(normProvided, NormalizeDigest(canonicalDigest), StringComparison.Ordinal))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1002,
                    "approval_params_mismatch", "paramsDigest"));
            }

            string layer = LayerName(p);
            int count = CountPoints(p);
            string confirmedBy = "auto_approve";

            if (!autoApprove)
            {
                // 硬确认：模态框，人点「是」才继续。弹框期间主线程不泵送，其它请求恒 E1004（这是对的）。
                bool isBreakline = string.Equals(kind, "surface_breakline", StringComparison.Ordinal);
                string message = isBreakline
                    ? string.Format(CultureInfo.InvariantCulture,
                        "工程平台请求向曲面「{0}」添加断裂线：\n\n顶点 {1} 个\n图纸：{2}\n\n执行不会保存文件，可用 U 撤销。\n是否允许？",
                        JsonCodec.GetStr(p, "surface"), count, SafeDocName(context))
                    : isCommand
                    ? string.Format(CultureInfo.InvariantCulture,
                        "工程平台请求在当前图纸执行命令：\n\n{0}\n\n图纸：{1}\n\n执行不会保存文件。\n是否允许？",
                        JsonCodec.GetStr(p, "command"), SafeDocName(context))
                    : string.Format(CultureInfo.InvariantCulture,
                        "工程平台请求写入当前图纸：\n\n新增 {0} 个点位到图层 {1}\n图纸：{2}\n\n执行前不会保存文件，可用 U 撤销。\n是否允许？",
                        count, layer, SafeDocName(context));
                DialogResult answer = MessageBox.Show(message, "工程平台 · 写入确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes)
                {
                    HostLog.Warn("write", "approval_denied_by_user", "count", count, "layer", layer);
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1006, "approval_denied_by_user"));
                }
                confirmedBy = "chat+modal_yes";
            }

            string docIdentity = DocumentIdentity(context);
            string token = context.Runtime.Approvals.Issue(matchedDigest, fingerprint, docIdentity,
                context.Runtime.Config.WriteTokenTtlMs);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["approvalToken"] = token;
            result["expiresInMs"] = context.Runtime.Config.WriteTokenTtlMs;
            result["confirmedBy"] = confirmedBy;
            result["paramsDigest"] = matchedDigest;
            result["targetFingerprint"] = fingerprint;
            result["summary"] = autoApprove
                ? "全自主模式（autoApprove=true）已静默签发审批令牌。"
                : string.Format(CultureInfo.InvariantCulture,
                    "已由用户在 CAD 中确认：新增 {0} 个点位到 {1}。令牌 {2} 秒内有效、仅可用一次。",
                    count, layer, context.Runtime.Config.WriteTokenTtlMs / 1000);
            return result;
        }

        /// <summary>
        /// 统一写鉴权入口：
        /// - 当 write.autoApprove=true 且未提供 approvalToken 时，直接全自主静默放行并记审计日志；
        /// - 否则校验 approvalToken（兼容传统 DigestKeys 与 Canonical JSON 全参数摘要）。
        /// </summary>
        private static bool RequireApprovalOrAutoApprove(
            ToolContext context,
            string legacyDigest,
            string fingerprint,
            string docIdentity,
            string toolName)
        {
            string token = JsonCodec.GetStr(context.Item.Params, "approvalToken");
            string canonicalDigest = ComputeCanonicalWriteDigest(context.Item.Params);

            if (context.Runtime.Config.WriteAutoApprove && string.IsNullOrEmpty(token))
            {
                HostLog.Info("write", "auto_approved", "tool", toolName, "op", context.Item.OpId,
                    "digest", canonicalDigest.Length > 15 ? canonicalDigest.Substring(0, 15) : canonicalDigest);
                return true;
            }

            string failDetail;
            if (!context.Runtime.Approvals.TryConsume(token, legacyDigest, fingerprint, docIdentity, out failDetail))
            {
                string canonicalFail;
                if (string.Equals(legacyDigest, canonicalDigest, StringComparison.Ordinal) ||
                    !context.Runtime.Approvals.TryConsume(token, canonicalDigest, fingerprint, docIdentity, out canonicalFail))
                {
                    string code = (failDetail == "approval_params_mismatch" || failDetail == "approval_target_mismatch"
                                   || failDetail == "approval_document_mismatch")
                        ? ErrorCodes.E1002
                        : ErrorCodes.E1006;
                    HostLog.Warn("write", "approval_rejected", "tool", toolName, "detail", failDetail, "op", context.Item.OpId);
                    throw new ToolFailureException(WireError.Of(code, failDetail));
                }
            }
            return false;
        }

        // ---------------- write_insert_points ----------------

        private static Dictionary<string, object> InsertPoints(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }
            if (context.Doc.IsReadOnly)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "readonly_document"));
            }

            // Q-18：若非自动放行模式且缺少审批令牌，先报 approval_missing，避免被后续形状校验掩盖根因
            string rawToken = JsonCodec.GetStr(context.Item.Params, "approvalToken");
            if (!context.Runtime.Config.WriteAutoApprove && string.IsNullOrEmpty(rawToken))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1006, "approval_missing", "approvalToken"));
            }

            List<PointRow> rows;
            string detail;
            if (!TryParsePoints(context.Item.Params, out rows, out detail))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, detail, "points"));
            }

            string digest = ComputeDigest(context.Item.Params);
            string docIdentity = DocumentIdentity(context);
            bool autoApproved = RequireApprovalOrAutoApprove(context, digest, CurrentFingerprint(context), docIdentity, "write_insert_points");

            string layer = LayerName(context.Item.Params);
            List<string> handles = new List<string>(rows.Count);
            int created = 0;

            using (context.Doc.LockDocument())
            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(tr, context.Db, layer);
                BlockTable bt = (BlockTable)tr.GetObject(context.Db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                for (int i = 0; i < rows.Count; i++)
                {
                    context.ThrowIfAborted();
                    PointRow row = rows[i];
                    DBPoint point = new DBPoint(new Point3d(row.E, row.N, row.Z));
                    point.LayerId = layerId;
                    space.AppendEntity(point);
                    tr.AddNewlyCreatedDBObject(point, true);
                    handles.Add(point.Handle.ToString());
                    created++;
                }

                tr.Commit();
            }

            // 让画面上真的出现这些点（PDMODE 为 0/1 时点会看不见，用户会以为没写成功）
            try
            {
                context.Doc.Editor.Regen();
            }
            catch (Exception ex)
            {
                HostLog.Exception("write", "regen_failed", ex);
            }

            HostLog.Info("write", "insert_points_done", "created", created, "layer", layer,
                "regen", true, "saved", false);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["created"] = created;
            result["layer"] = layer;
            result["handles"] = new List<object>(handles.Count <= 200 ? handles.ToArray() : handles.GetRange(0, 200).ToArray());
            result["handlesTruncated"] = handles.Count > 200;
            result["autoApproved"] = autoApproved;
            result["saved"] = false;
            result["undoHint"] = "本次插入未保存；CAD 里执行 U 可撤销";
            result["summary"] = string.Format(CultureInfo.InvariantCulture,
                "已新增 {0} 个点位到图层 {1}（未保存，U 可撤销）。", created, layer);
            return result;
        }

        // ---------------- write_command ----------------
        //
        // 为什么需要它：2026-11-23 在 2025 上发现「改系统变量」这件事无法通过任何 UI 注入完成
        // （2025 命令行是独立 WPF 窗口，拒绝合成输入），而第三方基座的 executeCommand 又主动
        // 封掉了 SETVAR/SAVE/QUIT 等命令。于是把「在命令上下文里执行一条命令」做成一个
        // **走同一套审批门禁**的写工具：preview 算摘要 → approve 弹框给人看**命令原文** → execute 执行。
        //
        // 安全边界（与基座拦截表同一套理由，但这里是白名单式的硬拒）：
        //   * 长度上限 500 字符、单行；
        //   * 保存/关闭/打印/发布一律拒绝 —— 本工具**永不落盘**是产品级承诺（见设计 §二 T2）；
        //   * 只接受「执行」，不接受「交互」：交互式命令会挂住串行队列，我们直接拒绝而不是等超时。
        private static readonly string[] ForbiddenCommandPrefixes =
        {
            "SAVE", "QSAVE", "SAVEAS", "_SAVE", "_QSAVE", "_SAVEAS", "WSAVE",
            "QUIT", "EXIT", "END", "CLOSE", "_QUIT", "_EXIT", "_CLOSE",
            "PLOT", "PUBLISH", "_PLOT", "_PUBLISH", "SCRIPT", "_SCRIPT",
            "PURGE", "AUDIT", "RECOVER", "-PURGE"
        };

        private static Dictionary<string, object> RunCommand(ToolContext context)
        {
            if (context.Doc == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }

            bool sendEnter = JsonCodec.GetBool(context.Item.Params, "sendEnter", false);
            bool cancelCmd = JsonCodec.GetBool(context.Item.Params, "cancelCommand", false);
            string command = JsonCodec.GetStr(context.Item.Params, "command");

            if (cancelCmd)
            {
                string cDigest = ComputeCommandDigest(context.Item.Params);
                RequireApprovalOrAutoApprove(context, cDigest, CurrentFingerprint(context), DocumentIdentity(context), "write_command");
                context.Doc.SendStringToExecute("\x03\x03", true, false, false);
                Dictionary<string, object> cRes = new Dictionary<string, object>(StringComparer.Ordinal);
                cRes["queued"] = true;
                cRes["command"] = "<ESC><ESC>";
                cRes["saved"] = false;
                cRes["summary"] = "已发送取消信号（Ctrl+C Ctrl+C）终止当前交互命令。";
                return cRes;
            }

            if (sendEnter && string.IsNullOrEmpty(command))
            {
                string eDigest = ComputeCommandDigest(context.Item.Params);
                RequireApprovalOrAutoApprove(context, eDigest, CurrentFingerprint(context), DocumentIdentity(context), "write_command");
                context.Doc.SendStringToExecute("\n", true, false, false);
                Dictionary<string, object> eRes = new Dictionary<string, object>(StringComparer.Ordinal);
                eRes["queued"] = true;
                eRes["command"] = "<ENTER>";
                eRes["saved"] = false;
                eRes["summary"] = "已发送回车确认（<ENTER>）。";
                return eRes;
            }

            if (string.IsNullOrEmpty(command))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "command_required", "command"));
            }
            command = command.Trim();
            if (command.Length > 500)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "command_too_long:500", "command"));
            }
            if (command.IndexOf('\n') >= 0 || command.IndexOf('\r') >= 0)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "command_must_be_single_line", "command"));
            }

            // 审批：摘要覆盖「命令原文 + 文档身份」，所以改一个字符令牌即失效
            string digest = ComputeCommandDigest(context.Item.Params);
            string fingerprint = CurrentFingerprint(context);
            string docIdentity = DocumentIdentity(context);

            if (JsonCodec.GetBool(context.Item.Params, "dryRun", false))
            {
                // preview 模式：只算不执行（write_preview 的 command 分支走的是同一个入口）
                Dictionary<string, object> preview = new Dictionary<string, object>(StringComparer.Ordinal);
                preview["kind"] = "command";
                preview["command"] = command;
                preview["blocked"] = MatchForbidden(command);
                preview["paramsDigest"] = digest;
                preview["targetFingerprint"] = fingerprint;
                preview["wrote"] = false;
                preview["summary"] = "将执行命令：" + command + "（未执行）";
                return preview;
            }

            bool autoApproved = RequireApprovalOrAutoApprove(context, digest, fingerprint, docIdentity, "write_command");

            // 记录排队前模型空间实景基线计数（用于执行后自动比对回执 Q-05 / Q-07）
            int beforeEntities = CountModelSpaceFast(context.Db);
            int beforeLayers = CountLayersFast(context.Db);

            bool wrapUndo = JsonCodec.GetBool(context.Item.Params, "wrapUndoGroup", true);
            bool allowDuringCmd = JsonCodec.GetBool(context.Item.Params, "allowDuringCommand", false);
            string passed = command;
            bool isLisp = passed.StartsWith("(", StringComparison.Ordinal);

            // 交互多步命令（allowDuringCommand=true）或无响应构建器时，保持立即返回不阻塞交互流
            if (allowDuringCmd || context.Item == null || context.Item.Builder == null || context.Runtime == null || context.Runtime.Dispatcher == null)
            {
                string immediateExec = passed;
                if (isLisp)
                {
                    immediateExec = wrapUndo
                        ? "(progn (command \"_.UNDO\" \"_Begin\") " + passed + " (command \"_.UNDO\" \"_End\") (princ))"
                        : "(progn " + passed + " (princ))";
                }
                context.Doc.SendStringToExecute(immediateExec + "\n", true, false, false);
                HostLog.Info("write", "command_queued", "command", Shorten(passed), "wrapUndo", wrapUndo,
                    "beforeEntities", beforeEntities, "interactive", allowDuringCmd);

                Dictionary<string, object> imm = new Dictionary<string, object>(StringComparer.Ordinal);
                imm["queued"] = true;
                imm["executionMode"] = "async_queued";
                imm["command"] = passed;
                imm["undoWrapped"] = wrapUndo && isLisp;
                imm["autoApproved"] = autoApproved;
                imm["beforeEntityCount"] = beforeEntities;
                imm["beforeLayerCount"] = beforeLayers;
                imm["verified"] = true;
                imm["saved"] = false;
                imm["summary"] = string.Format(CultureInfo.InvariantCulture,
                    "交互命令已投递：{0}（排队前图元 {1}、图层 {2}）。", passed, beforeEntities, beforeLayers);
                return imm;
            }

            // Q-05 & Q-07：非交互命令走延迟 Idle 实景回执
            // - LISP 表达式用 vl-catch-all-apply 包裹并将 OK / ERR:<原因> 写入临时哨兵文件；
            // - 在下一轮 Application.Idle 回读哨兵状态与 Database 实时实体/图层数，精确返回 verified / afterEntityCount / entityDelta / lispError！
            string sentinelPath = Path.Combine(Path.GetTempPath(), "ep_cmd_" + Guid.NewGuid().ToString("N") + ".txt");
            string lispPath = sentinelPath.Replace('\\', '/');
            string toExecute;
            if (isLisp)
            {
                string undoBegin = wrapUndo ? "(command \"_.UNDO\" \"_Begin\") " : string.Empty;
                string undoEnd = wrapUndo ? "(command \"_.UNDO\" \"_End\") " : string.Empty;
                toExecute = "(progn (vl-load-com) " + undoBegin
                    + "(setq _ep_r_ (vl-catch-all-apply (function (lambda () " + passed + ")))) "
                    + undoEnd
                    + "(setq _ep_f_ (open \"" + lispPath + "\" \"w\")) "
                    + "(if _ep_f_ (progn (write-line (if (vl-catch-all-error-p _ep_r_) (strcat \"ERR:\" (vl-catch-all-error-message _ep_r_)) \"OK\") _ep_f_) (close _ep_f_))) "
                    + "(princ))";
            }
            else
            {
                // 非 LISP 原生命令：紧随其后追加一行哨兵 LISP 捕获 LASTPROMPT 并写盘，保证下一轮 Idle 毫秒级收口
                toExecute = passed + "\n(progn (setq _ep_p_ (getvar \"LASTPROMPT\") _ep_f_ (open \"" + lispPath
                    + "\" \"w\")) (if _ep_f_ (progn (write-line (if _ep_p_ _ep_p_ \"\") _ep_f_) (close _ep_f_))) (princ))";
            }

            context.Doc.SendStringToExecute(toExecute + "\n", true, false, false);
            HostLog.Info("write", "command_queued_deferred", "command", Shorten(passed), "wrapUndo", wrapUndo,
                "isLisp", isLisp, "beforeEntities", beforeEntities);

            DateTime queuedAtUtc = DateTime.UtcNow;
            int waitBudgetMs = Math.Min(Math.Max(context.Item.TimeoutMs - 1000, 2500), 6000);
            DateTime deadlineUtc = queuedAtUtc.AddMilliseconds(waitBudgetMs);

            EngineeringPlatform.Host.Scheduling.WorkItem workItem = context.Item;
            ResponseBuilder builder = context.Item.Builder;
            HostRuntime runtime = context.Runtime;
            Database db = context.Db;
            bool undoWrapped = wrapUndo && isLisp;

            Action<bool, string, int, int, bool> completeDeferred = delegate(
                bool verified, string errorText, int afterEntities, int afterLayers, bool timedOut)
            {
                if (!string.IsNullOrEmpty(sentinelPath))
                {
                    try { if (File.Exists(sentinelPath)) File.Delete(sentinelPath); } catch (Exception) { }
                }

                int entDelta = (beforeEntities >= 0 && afterEntities >= 0) ? (afterEntities - beforeEntities) : 0;
                int lyrDelta = (beforeLayers >= 0 && afterLayers >= 0) ? (afterLayers - beforeLayers) : 0;

                if (!verified && entDelta != 0)
                {
                    builder.AddWarning(WireWarning.Of(ErrorCodes.W1005,
                        string.Format(CultureInfo.InvariantCulture,
                            "partial_execution:命令中途报错但已变更 {0} 个图元，可用 write_undo 撤销", entDelta)));
                }
                else if (timedOut)
                {
                    builder.AddWarning(WireWarning.Of(ErrorCodes.W1005, "command_receipt_timeout:命令未在时限内返回结束哨兵（可能存在语法错误或弹窗阻塞）"));
                }

                Dictionary<string, object> res = new Dictionary<string, object>(StringComparer.Ordinal);
                res["queued"] = true;
                res["executionMode"] = "verified_idle";
                res["command"] = passed;
                res["undoWrapped"] = undoWrapped;
                res["autoApproved"] = autoApproved;
                res["verified"] = verified;
                res["beforeEntityCount"] = beforeEntities;
                res["afterEntityCount"] = afterEntities;
                res["entityDelta"] = entDelta;
                res["beforeLayerCount"] = beforeLayers;
                res["afterLayerCount"] = afterLayers;
                res["layerDelta"] = lyrDelta;
                if (!string.IsNullOrEmpty(errorText))
                {
                    res["lispError"] = errorText;
                }
                if (timedOut)
                {
                    res["timedOutWaitingReceipt"] = true;
                }
                res["saved"] = false;
                res["summary"] = verified
                    ? string.Format(CultureInfo.InvariantCulture,
                        "命令执行已验证：{0}｜图元 {1}→{2}（Δ{3:+#;-#;0}），图层 {4}→{5}（支持 write_undo 撤销）。",
                        passed, beforeEntities, afterEntities, entDelta, beforeLayers, afterLayers)
                    : string.Format(CultureInfo.InvariantCulture,
                        "命令执行未通过验证（{0}）：{1}｜图元 {2}→{3}（Δ{4:+#;-#;0}）。",
                        string.IsNullOrEmpty(errorText) ? (timedOut ? "等待回执超时" : "命令报错") : errorText,
                        passed, beforeEntities, afterEntities, entDelta);

                runtime.Stats.RecordRequest(verified);
                HostLog.Info("write", "command_verified", "op", workItem.OpId, "verified", verified,
                    "before", beforeEntities, "after", afterEntities, "delta", entDelta,
                    "err", string.IsNullOrEmpty(errorText) ? "-" : Shorten(errorText));
                workItem.TryComplete(builder.BuildOk(res, verified ? "verified" : "failed"));
            };

            runtime.Dispatcher.RegisterDeferred(
                workItem,
                deadlineUtc,
                delegate()
                {
                    if (!File.Exists(sentinelPath))
                    {
                        return false;
                    }
                    string marker = string.Empty;
                    try
                    {
                        marker = (File.ReadAllText(sentinelPath, Encoding.UTF8) ?? string.Empty).Trim();
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                    int afterEnt = CountModelSpaceFast(db);
                    int afterLyr = CountLayersFast(db);
                    if (isLisp)
                    {
                        bool failed = marker.StartsWith("ERR:", StringComparison.Ordinal);
                        string errMsg = failed ? marker.Substring(4).Trim() : null;
                        completeDeferred(!failed, errMsg, afterEnt, afterLyr, false);
                    }
                    else
                    {
                        bool promptErr = IsCommandPromptError(marker);
                        completeDeferred(!promptErr, promptErr ? marker : null, afterEnt, afterLyr, false);
                    }
                    return true;
                },
                delegate()
                {
                    // 超时回调可能在看门狗后台线程触发，绝不在后台线程触碰 AutoCAD Database
                    completeDeferred(false, "command_receipt_timeout", beforeEntities, beforeLayers, true);
                });

            return null;
        }

        // ---------------- write_surface_breakline（基座写，B 路线） ----------------
        //
        // 为什么执行放在**后台线程**：基座的 Civil API 调用会排进它自己的主线程队列，
        // 而我们此刻正占着主线程 —— 同步等它就是 2026-11-23 01:07 那次死锁
        //（基座日志 `listSurfaces [1] cancelled durationMs=15005`）。
        // 因此：令牌校验在主线程做完（纯密码学，快），基座调用丢给线程池，响应先回「已排队」，
        // 结果写日志 + 由 read_civil_surfaces 复核。语义与 write_command 一致。
        private static Dictionary<string, object> SurfaceBreakline(ToolContext context)
        {
            if (context.Doc == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }

            string surfaceName = JsonCodec.GetStr(context.Item.Params, "surface");
            if (string.IsNullOrEmpty(surfaceName))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "surface_required", "surface"));
            }

            List<PointRow> rows;
            string detail;
            if (!TryParsePoints(context.Item.Params, out rows, out detail))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, detail, "points"));
            }
            if (rows.Count < 2)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "breakline_needs_two_points", "points"));
            }

            string digest = ComputeDigest(context.Item.Params);
            string docIdentity = DocumentIdentity(context);
            string fingerprint = CurrentFingerprint(context);
            RequireApprovalOrAutoApprove(context, digest, fingerprint, docIdentity, "write_surface_breakline");

            // 基座要的是 {x,y,z}；我们的入参用 e/n/z（工程坐标习惯），在这里换算，不让调用方猜
            List<object> points = new List<object>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Dictionary<string, object> vertex = new Dictionary<string, object>(StringComparer.Ordinal);
                vertex["x"] = rows[i].E;
                vertex["y"] = rows[i].N;
                vertex["z"] = rows[i].Z;
                points.Add(vertex);
            }

            Dictionary<string, object> baseParams = new Dictionary<string, object>(StringComparer.Ordinal);
            baseParams["name"] = surfaceName.Trim();
            baseParams["points"] = points;

            string opId = context.Item.OpId;
            int vertexCount = rows.Count;
            Thread worker = new Thread(delegate()
            {
                Dictionary<string, object> baseResult;
                WireError error;
                bool ok = Base.BaseClient.TryCall("addSurfaceBreakline", baseParams, 30000,
                    out baseResult, out error);
                if (ok)
                {
                    HostLog.Info("write", "base_write_ok", "op", opId, "method", "addSurfaceBreakline",
                        "surface", surfaceName, "vertices", vertexCount);
                }
                else
                {
                    HostLog.Error("write", "base_write_failed", "op", opId, "method", "addSurfaceBreakline",
                        "surface", surfaceName, "detail", error == null ? "-" : error.Detail);
                }
            });
            worker.IsBackground = true;
            worker.Name = "ep-base-write";
            worker.Start();

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["queued"] = true;
            result["tool"] = "write_surface_breakline";
            result["surface"] = surfaceName.Trim();
            result["vertices"] = vertexCount;
            result["saved"] = false;
            result["summary"] = "已提交基座执行：向曲面「" + surfaceName.Trim() + "」添加断裂线（"
                + vertexCount + " 个顶点）。执行结果请用 read_civil_surfaces 复核（断裂线数量/统计变化）。";
            return result;
        }

        // ---------------- write_undo (v3.0 Phase 1) ----------------

        private static Dictionary<string, object> Undo(ToolContext context)
        {
            if (context.Doc == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }

            int steps = JsonCodec.GetInt(context.Item.Params, "steps", 1);
            if (steps < 1) steps = 1;
            if (steps > 50) steps = 50;

            string digest = ComputeCanonicalWriteDigest(context.Item.Params);
            RequireApprovalOrAutoApprove(context, digest, CurrentFingerprint(context), DocumentIdentity(context), "write_undo");

            string cmd = string.Format(CultureInfo.InvariantCulture, "_.UNDO {0}\n", steps);
            context.Doc.SendStringToExecute(cmd, true, false, false);
            HostLog.Info("write", "undo_queued", "steps", steps);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["queued"] = true;
            result["undoneSteps"] = steps;
            result["summary"] = ToolResultHelper.Summary("已提交事务回滚：撤销最近 " + steps + " 步操作。");
            return result;
        }

        // ---------------- write_object_xdata (v3.0 Phase 1) ----------------

        private static Dictionary<string, object> WriteObjectXData(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }
            if (context.Doc.IsReadOnly)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "readonly_document"));
            }

            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            string handleHex = (JsonCodec.GetStr(p, "handle") ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(handleHex))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "handle_required", "handle"));
            }
            long handleVal;
            if (!long.TryParse(handleHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handleVal))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "invalid_hex_handle:" + handleHex, "handle"));
            }

            string regApp = JsonCodec.GetStr(p, "regApp", "EP_META");
            Dictionary<string, object> entries = JsonCodec.GetDict(p, "entries");
            string xrecordKey = JsonCodec.GetStr(p, "xrecordKey");
            if (entries == null || entries.Count == 0)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "entries_required", "entries"));
            }

            string digest = ComputeCanonicalWriteDigest(p);
            RequireApprovalOrAutoApprove(context, digest, CurrentFingerprint(context), DocumentIdentity(context), "write_object_xdata");

            ObjectId id;
            try
            {
                id = context.Db.GetObjectId(false, new Handle(handleVal), 0);
            }
            catch (Exception)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "handle_not_found:" + handleHex, "handle"));
            }

            int writtenCount = 0;
            using (context.Doc.LockDocument())
            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForWrite, false);
                if (obj == null)
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "object_not_found:" + handleHex, "handle"));
                }

                if (!string.IsNullOrEmpty(xrecordKey))
                {
                    if (obj.ExtensionDictionary.IsNull)
                    {
                        obj.CreateExtensionDictionary();
                    }
                    DBDictionary extDict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForWrite);
                    Xrecord xr = new Xrecord();
                    using (ResultBuffer rb = new ResultBuffer())
                    {
                        foreach (KeyValuePair<string, object> kv in entries)
                        {
                            rb.Add(new TypedValue((int)DxfCode.Text, kv.Key + "=" + Convert.ToString(kv.Value, CultureInfo.InvariantCulture)));
                            writtenCount++;
                        }
                        xr.Data = rb;
                    }
                    if (extDict.Contains(xrecordKey))
                    {
                        ObjectId oldId = extDict.GetAt(xrecordKey);
                        DBObject oldObj = tr.GetObject(oldId, OpenMode.ForWrite);
                        oldObj.Erase();
                    }
                    extDict.SetAt(xrecordKey, xr);
                    tr.AddNewlyCreatedDBObject(xr, true);
                }
                else
                {
                    // 注册 RegApp 并写 XData
                    RegAppTable rat = (RegAppTable)tr.GetObject(context.Db.RegAppTableId, OpenMode.ForRead);
                    if (!rat.Has(regApp))
                    {
                        rat.UpgradeOpen();
                        RegAppTableRecord rec = new RegAppTableRecord();
                        rec.Name = regApp;
                        rat.Add(rec);
                        tr.AddNewlyCreatedDBObject(rec, true);
                    }
                    using (ResultBuffer rb = new ResultBuffer())
                    {
                        rb.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, regApp));
                        foreach (KeyValuePair<string, object> kv in entries)
                        {
                            string text = kv.Key + "=" + Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                            rb.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, text));
                            writtenCount++;
                        }
                        obj.XData = rb;
                    }
                }
                tr.Commit();
            }

            HostLog.Info("write", "xdata_written", "handle", handleHex, "regApp", regApp, "count", writtenCount);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["handle"] = handleHex.ToUpperInvariant();
            result["regApp"] = string.IsNullOrEmpty(xrecordKey) ? regApp : null;
            result["xrecordKey"] = xrecordKey;
            result["writtenEntries"] = writtenCount;
            result["summary"] = ToolResultHelper.Summary("已向句柄 " + handleHex.ToUpperInvariant() + " 写入 " + writtenCount + " 条元数据（事务可撤销）。");
            return result;
        }

        // ---------------- write_alignment_plan (v3.0 Phase 1/2 选线落盘原语) ----------------

        private static Dictionary<string, object> WriteAlignmentPlan(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }
            if (context.Doc.IsReadOnly)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "readonly_document"));
            }

            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            string digest = ComputeCanonicalWriteDigest(p);
            RequireApprovalOrAutoApprove(context, digest, CurrentFingerprint(context), DocumentIdentity(context), "write_alignment_plan");

            Dictionary<string, object> planData = p;
            string planPath = JsonCodec.GetStr(p, "planPath");
            if (!string.IsNullOrEmpty(planPath))
            {
                if (!File.Exists(planPath))
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "plan_file_not_found:" + planPath, "planPath"));
                }
                Dictionary<string, object> loaded;
                if (!JsonCodec.TryParseObject(File.ReadAllText(planPath, Encoding.UTF8), out loaded))
                {
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "plan_file_invalid_json", "planPath"));
                }
                planData = loaded;
            }

            string name = JsonCodec.GetStr(p, "name");
            if (string.IsNullOrEmpty(name))
            {
                int pid = JsonCodec.GetInt(planData, "plan_id", 1);
                name = "EP_Route_Plan_" + pid.ToString(CultureInfo.InvariantCulture);
            }
            string layer = JsonCodec.GetStr(p, "layer", "EP-ALIGNMENT");
            string surfaceName = JsonCodec.GetStr(p, "surfaceName");
            bool erasePolyline = JsonCodec.GetBool(p, "eraseSourcePolyline", false);

            List<Point3d> centerline = ExtractPlanPoints(planData);
            if (centerline.Count < 2)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "alignment_needs_at_least_two_points", "points"));
            }

            string polylineHandle = null;
            string alignmentHandle = null;
            string surfaceProfileHandle = null;
            string layoutProfileHandle = null;
            bool nativeCivilCreated = false;
            string civilNote = null;
            double totalLength = 0.0;
            for (int i = 1; i < centerline.Count; i++)
            {
                totalLength += centerline[i - 1].DistanceTo(centerline[i]);
            }

            using (context.Doc.LockDocument())
            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(tr, context.Db, layer);
                BlockTable bt = (BlockTable)tr.GetObject(context.Db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // 1) 创建 2D Polyline（既是通用几何备份，也是 Civil 3D Alignment.Create 的输入）
                Polyline pl = new Polyline(centerline.Count);
                pl.LayerId = layerId;
                for (int i = 0; i < centerline.Count; i++)
                {
                    pl.AddVertexAt(i, new Point2d(centerline[i].X, centerline[i].Y), 0.0, 0.0, 0.0);
                }
                ObjectId plId = ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                polylineHandle = pl.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

                // 2) 若 Civil 3D 可用，反射创建原生 Alignment + 曲面纵断面 + 设计拉坡纵断面
                object civilDoc = CivilApiProbe.GetCivilDocument(context.Doc);
                if (civilDoc != null)
                {
                    try
                    {
                        ObjectId alignId = TryCreateCivilAlignment(civilDoc, context.Db, tr, name, plId, layerId, erasePolyline);
                        if (!alignId.IsNull)
                        {
                            nativeCivilCreated = true;
                            alignmentHandle = alignId.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

                            if (!string.IsNullOrEmpty(surfaceName))
                            {
                                ObjectId surfProfId = TryCreateSurfaceProfile(civilDoc, tr, name + "_EG", alignId, surfaceName, layerId);
                                if (!surfProfId.IsNull)
                                {
                                    surfaceProfileHandle = surfProfId.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                                }
                            }

                            List<Dictionary<string, object>> pvis = ExtractPvis(planData);
                            if (pvis.Count >= 2)
                            {
                                ObjectId layoutProfId = TryCreateLayoutProfile(civilDoc, tr, name + "_FG", alignId, layerId, pvis);
                                if (!layoutProfId.IsNull)
                                {
                                    layoutProfileHandle = layoutProfId.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        civilNote = "civil_native_fallback:" + ex.Message;
                        HostLog.Warn("write", "alignment_civil_fallback", "name", name, "detail", ex.Message);
                    }
                }
                else
                {
                    civilNote = "plain_autocad_polyline_created";
                }

                tr.Commit();
            }

            try { context.Doc.Editor.Regen(); } catch (Exception) { }
            HostLog.Info("write", "alignment_plan_written", "name", name, "points", centerline.Count,
                "nativeCivil", nativeCivilCreated, "alignmentHandle", alignmentHandle ?? "-");

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["name"] = name;
            result["layer"] = layer;
            result["pointCount"] = centerline.Count;
            result["length"] = Math.Round(totalLength, 3);
            result["polylineHandle"] = polylineHandle;
            result["alignmentHandle"] = alignmentHandle;
            result["surfaceProfileHandle"] = surfaceProfileHandle;
            result["layoutProfileHandle"] = layoutProfileHandle;
            result["nativeCivilCreated"] = nativeCivilCreated;
            result["note"] = civilNote;
            result["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【路线落盘】「{0}」长 {1:0.##}m（{2} 桩点）｜Civil3D 原生={3}｜句柄 Alignment={4} Polyline={5}。",
                name, totalLength, centerline.Count, nativeCivilCreated,
                alignmentHandle ?? "-", polylineHandle ?? "-"));
            return result;
        }

        private static List<Point3d> ExtractPlanPoints(Dictionary<string, object> data)
        {
            List<Point3d> pts = new List<Point3d>();
            List<object> raw = JsonCodec.GetList(data, "sampled_centerline")
                ?? JsonCodec.GetList(data, "sampledCenterline")
                ?? JsonCodec.GetList(data, "points");
            if (raw == null)
            {
                Dictionary<string, object> alignDict = JsonCodec.GetDict(data, "alignment");
                if (alignDict != null)
                {
                    raw = JsonCodec.GetList(alignDict, "jds");
                }
            }
            if (raw == null) return pts;

            for (int i = 0; i < raw.Count; i++)
            {
                Dictionary<string, object> row = raw[i] as Dictionary<string, object>;
                if (row != null)
                {
                    double x, y, z = 0.0;
                    bool hasX = TryNumber(row, "x", out x) || TryNumber(row, "e", out x);
                    bool hasY = TryNumber(row, "y", out y) || TryNumber(row, "n", out y);
                    if (!TryNumber(row, "design_z", out z))
                    {
                        TryNumber(row, "z", out z);
                    }
                    if (hasX && hasY)
                    {
                        pts.Add(new Point3d(x, y, z));
                    }
                }
            }
            return pts;
        }

        private static List<Dictionary<string, object>> ExtractPvis(Dictionary<string, object> data)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            List<object> raw = JsonCodec.GetList(data, "pvis");
            if (raw == null)
            {
                Dictionary<string, object> prof = JsonCodec.GetDict(data, "profile");
                if (prof != null)
                {
                    raw = JsonCodec.GetList(prof, "pvis");
                }
            }
            if (raw == null) return list;
            for (int i = 0; i < raw.Count; i++)
            {
                Dictionary<string, object> d = raw[i] as Dictionary<string, object>;
                if (d != null) list.Add(d);
            }
            return list;
        }

        private static ObjectId TryCreateCivilAlignment(
            object civilDoc, Database db, Transaction tr,
            string name, ObjectId polylineId, ObjectId layerId, bool eraseExisting)
        {
            Assembly civilAsm = civilDoc.GetType().Assembly;
            Type alignmentType = civilAsm.GetType("Autodesk.Civil.DatabaseServices.Alignment", false);
            Type polyOptsType = civilAsm.GetType("Autodesk.Civil.DatabaseServices.PolylineOptions", false);
            if (alignmentType == null || polyOptsType == null) return ObjectId.Null;

            ObjectId styleId = FirstStyleId(civilDoc, "AlignmentStyles");
            ObjectId labelSetId = FirstStyleId(civilDoc, "AlignmentLabelSetStyles");

            object opts = Activator.CreateInstance(polyOptsType);
            SetProp(opts, "PlineId", polylineId);
            SetProp(opts, "EraseExistingEntities", eraseExisting);
            SetProp(opts, "AddCurvesBetweenTangents", false);

            // Alignment.Create(CivilDocument, PolylineOptions, string, ObjectId siteId, ObjectId layerId, ObjectId styleId, ObjectId labelSetId)
            MethodInfo[] methods = alignmentType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "Create") continue;
                ParameterInfo[] ps = methods[i].GetParameters();
                if (ps.Length == 7 && ps[1].ParameterType == polyOptsType && ps[2].ParameterType == typeof(string))
                {
                    object res = methods[i].Invoke(null, new object[]
                    {
                        civilDoc, opts, name, ObjectId.Null, layerId, styleId, labelSetId
                    });
                    if (res is ObjectId) return (ObjectId)res;
                }
            }
            return ObjectId.Null;
        }

        private static ObjectId TryCreateSurfaceProfile(
            object civilDoc, Transaction tr, string profileName, ObjectId alignId, string surfaceName, ObjectId layerId)
        {
            Assembly civilAsm = civilDoc.GetType().Assembly;
            Type profileType = civilAsm.GetType("Autodesk.Civil.DatabaseServices.Profile", false);
            if (profileType == null) return ObjectId.Null;

            // 查找曲面 ObjectId
            ObjectId surfaceId = ObjectId.Null;
            MethodInfo getSurfIds = civilDoc.GetType().GetMethod("GetSurfaceIds", Type.EmptyTypes);
            IEnumerable ids = getSurfIds == null ? null : getSurfIds.Invoke(civilDoc, null) as IEnumerable;
            if (ids != null)
            {
                foreach (object item in ids)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId sid = (ObjectId)item;
                    DBObject sobj = tr.GetObject(sid, OpenMode.ForRead, false);
                    PropertyInfo np = sobj == null ? null : sobj.GetType().GetProperty("Name");
                    string sn = np == null ? null : Convert.ToString(np.GetValue(sobj, null), CultureInfo.InvariantCulture);
                    if (string.Equals(sn, surfaceName, StringComparison.OrdinalIgnoreCase))
                    {
                        surfaceId = sid;
                        break;
                    }
                }
            }
            if (surfaceId.IsNull) return ObjectId.Null;

            ObjectId styleId = FirstStyleId(civilDoc, "ProfileStyles");
            ObjectId labelSetId = FirstStyleId(civilDoc, "ProfileLabelSetStyles");
            MethodInfo createFromSurf = profileType.GetMethod("CreateFromSurface",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(ObjectId), typeof(ObjectId), typeof(ObjectId), typeof(ObjectId), typeof(ObjectId) }, null);
            if (createFromSurf == null) return ObjectId.Null;

            object res = createFromSurf.Invoke(null, new object[] { profileName, alignId, surfaceId, layerId, styleId, labelSetId });
            return res is ObjectId ? (ObjectId)res : ObjectId.Null;
        }

        private static ObjectId TryCreateLayoutProfile(
            object civilDoc, Transaction tr, string profileName, ObjectId alignId, ObjectId layerId,
            List<Dictionary<string, object>> pvis)
        {
            Assembly civilAsm = civilDoc.GetType().Assembly;
            Type profileType = civilAsm.GetType("Autodesk.Civil.DatabaseServices.Profile", false);
            if (profileType == null) return ObjectId.Null;

            ObjectId styleId = FirstStyleId(civilDoc, "ProfileStyles");
            ObjectId labelSetId = FirstStyleId(civilDoc, "ProfileLabelSetStyles");
            MethodInfo createByLayout = profileType.GetMethod("CreateByLayout",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(ObjectId), typeof(ObjectId), typeof(ObjectId), typeof(ObjectId) }, null);
            if (createByLayout == null) return ObjectId.Null;

            object res = createByLayout.Invoke(null, new object[] { profileName, alignId, layerId, styleId, labelSetId });
            if (!(res is ObjectId)) return ObjectId.Null;
            ObjectId profId = (ObjectId)res;

            DBObject profObj = tr.GetObject(profId, OpenMode.ForWrite, false);
            object pviColl = profObj == null ? null : profObj.GetType().GetProperty("PVIs")?.GetValue(profObj, null);
            if (pviColl != null)
            {
                MethodInfo addPvi = pviColl.GetType().GetMethod("AddPVI", new[] { typeof(double), typeof(double) });
                if (addPvi != null)
                {
                    List<object> createdPvis = new List<object>();
                    List<double> curveLengths = new List<double>();
                    for (int i = 0; i < pvis.Count; i++)
                    {
                        double sta, elev, clen;
                        bool hasSta = TryNumber(pvis[i], "station_m", out sta) || TryNumber(pvis[i], "station", out sta);
                        bool hasElev = TryNumber(pvis[i], "elevation_m", out elev) || TryNumber(pvis[i], "elevation", out elev);
                        if (!TryNumber(pvis[i], "curve_length_m", out clen))
                        {
                            TryNumber(pvis[i], "curveLength", out clen);
                        }
                        if (hasSta && hasElev)
                        {
                            try
                            {
                                object pviObj = addPvi.Invoke(pviColl, new object[] { sta, elev });
                                createdPvis.Add(pviObj);
                                curveLengths.Add(clen);
                            }
                            catch (Exception) { }
                        }
                    }

                    // 为中间变坡点自动插入对称二次抛物线竖曲线（避免纵断面呈尖折线）
                    object entitiesColl = profObj.GetType().GetProperty("Entities")?.GetValue(profObj, null);
                    if (entitiesColl != null && createdPvis.Count >= 3)
                    {
                        Type pviType = createdPvis[1] == null ? null : createdPvis[1].GetType();
                        MethodInfo addParabola = pviType == null ? null : entitiesColl.GetType().GetMethod(
                            "AddFreeSymmetricParabolaByPVIAndCurveLength",
                            new[] { pviType, typeof(double) });
                        if (addParabola != null)
                        {
                            for (int k = 1; k < createdPvis.Count - 1; k++)
                            {
                                if (createdPvis[k] != null && curveLengths[k] >= 10.0)
                                {
                                    try { addParabola.Invoke(entitiesColl, new object[] { createdPvis[k], curveLengths[k] }); }
                                    catch (Exception) { }
                                }
                            }
                        }
                    }
                }
            }
            return profId;
        }

        private static ObjectId FirstStyleId(object civilDoc, string collectionName)
        {
            try
            {
                object styles = civilDoc.GetType().GetProperty("Styles")?.GetValue(civilDoc, null);
                if (styles == null) return ObjectId.Null;
                object coll = styles.GetType().GetProperty(collectionName)?.GetValue(styles, null);
                if (coll == null)
                {
                    object labelSets = styles.GetType().GetProperty("LabelSetStyles")?.GetValue(styles, null);
                    coll = labelSets == null ? null : labelSets.GetType().GetProperty(collectionName)?.GetValue(labelSets, null);
                }
                IEnumerable en = coll as IEnumerable;
                if (en != null)
                {
                    foreach (object item in en)
                    {
                        if (item is ObjectId && !((ObjectId)item).IsNull) return (ObjectId)item;
                    }
                }
            }
            catch (Exception) { }
            return ObjectId.Null;
        }

        private static void SetProp(object target, string propName, object val)
        {
            PropertyInfo p = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite)
            {
                p.SetValue(target, val, null);
            }
        }

        // ---------------- write_eval_csharp (v3.0 Phase 3 Roslyn 100% 底层释放) ----------------

        private static Dictionary<string, object> EvalCSharp(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }

            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            string code = JsonCodec.GetStr(p, "code");
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "code_required", "code"));
            }
            if (code.Length > 65536)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "code_too_long:65536", "code"));
            }

            bool shadowDryRun = JsonCodec.GetBool(p, "shadowDryRun", false);
            bool commit = JsonCodec.GetBool(p, "commit", true);
            Dictionary<string, object> scriptArgs = JsonCodec.GetDict(p, "args");
            List<string> usings = JsonCodec.GetStringList(p, "usings");

            string digest = ComputeCanonicalWriteDigest(p);
            RequireApprovalOrAutoApprove(context, digest, CurrentFingerprint(context), DocumentIdentity(context), "write_eval_csharp");

            Dictionary<string, object> shadowInfo = null;
            if (shadowDryRun)
            {
                ShadowDryRunResult shadow = ShadowDryRunner.TryDryRun(context.Doc, context.Db, code, scriptArgs, usings, 25000);
                shadowInfo = new Dictionary<string, object>(StringComparer.Ordinal);
                shadowInfo["ok"] = shadow.Ok;
                shadowInfo["skipped"] = shadow.Skipped;
                shadowInfo["exitCode"] = shadow.ExitCode;
                shadowInfo["elapsedMs"] = shadow.ElapsedMs;
                shadowInfo["reason"] = shadow.Reason;
                if (!shadow.Ok)
                {
                    HostLog.Warn("roslyn", "shadow_dry_run_rejected", "reason", shadow.Reason ?? "-");
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E3002,
                        "shadow_dry_run_failed:" + (shadow.Reason ?? "unknown")));
                }
            }

            EvalResult eval;
            using (context.Doc.LockDocument())
            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                eval = RoslynEvalEngine.Execute(code, context.Doc, context.Db, tr, context.Doc.Editor, scriptArgs, usings);
                if (eval.Ok && commit)
                {
                    tr.Commit();
                }
            }

            if (!eval.Ok)
            {
                HostLog.Warn("roslyn", "eval_failed", "error", eval.ErrorMessage ?? "-");
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                    "eval_csharp_failed:" + (eval.ErrorMessage ?? "unknown"), "code"));
            }

            HostLog.Info("roslyn", "eval_ok", "engine", eval.Engine,
                "compileMs", eval.CompileMs, "executeMs", eval.ExecuteMs, "committed", commit);

            List<object> logObjs = new List<object>(eval.Logs.Count);
            for (int i = 0; i < eval.Logs.Count; i++) logObjs.Add(eval.Logs[i]);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["ok"] = true;
            result["engine"] = eval.Engine;
            result["committed"] = commit;
            result["compileMs"] = eval.CompileMs;
            result["executeMs"] = eval.ExecuteMs;
            result["returnValue"] = eval.ReturnValue == null
                ? null
                : (eval.ReturnValue is string || eval.ReturnValue is bool || eval.ReturnValue is int ||
                   eval.ReturnValue is long || eval.ReturnValue is double || eval.ReturnValue is Dictionary<string, object> ||
                   eval.ReturnValue is List<object>
                    ? eval.ReturnValue
                    : Convert.ToString(eval.ReturnValue, CultureInfo.InvariantCulture));
            result["logs"] = logObjs;
            result["shadowDryRun"] = shadowInfo;
            result["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【Roslyn 执行成功】引擎 {0}｜编译 {1}ms，执行 {2}ms，提交事务={3}，日志 {4} 条。",
                eval.Engine, eval.CompileMs, eval.ExecuteMs, commit, logObjs.Count));
            return result;
        }

        // ---------------- write_entities (原生批量建实体：线/多段线/矩形/圆/圆弧/椭圆/文字/点) ----------------

        private static Dictionary<string, object> WriteEntities(ToolContext context)
        {
            if (context.Doc == null || context.Db == null)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "no_document"));
            }
            if (context.Doc.IsReadOnly)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1005, "readonly_document"));
            }

            Dictionary<string, object> p = context.Item.Params ?? new Dictionary<string, object>(StringComparer.Ordinal);
            List<object> rawEntities = JsonCodec.GetList(p, "entities");
            if (rawEntities == null || rawEntities.Count == 0)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "entities_required", "entities"));
            }
            if (rawEntities.Count > 5000)
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "entities_exceed_limit:5000", "entities"));
            }

            string defaultLayer = JsonCodec.GetStr(p, "layer");
            if (string.IsNullOrEmpty(defaultLayer))
            {
                defaultLayer = "0";
            }
            bool strict = JsonCodec.GetBool(p, "strict", false);
            bool zoomExtents = JsonCodec.GetBool(p, "zoomExtents", false);

            string rawToken = JsonCodec.GetStr(p, "approvalToken");
            if (!context.Runtime.Config.WriteAutoApprove && string.IsNullOrEmpty(rawToken))
            {
                throw new ToolFailureException(WireError.Of(ErrorCodes.E1006, "approval_missing", "approvalToken"));
            }

            string digest = ComputeCanonicalWriteDigest(p);
            bool autoApproved = RequireApprovalOrAutoApprove(context, digest, CurrentFingerprint(context), DocumentIdentity(context), "write_entities");

            List<object> handles = new List<object>();
            List<object> failedItems = new List<object>();
            Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.Ordinal);
            int createdCount = 0;
            bool hasBounds = false;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

            using (context.Doc.LockDocument())
            using (Transaction tr = context.Db.TransactionManager.StartTransaction())
            {
                Dictionary<string, ObjectId> layerCache = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                BlockTable bt = (BlockTable)tr.GetObject(context.Db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                for (int i = 0; i < rawEntities.Count; i++)
                {
                    context.ThrowIfAborted();
                    Dictionary<string, object> spec = rawEntities[i] as Dictionary<string, object>;
                    if (spec == null)
                    {
                        if (strict)
                        {
                            throw new ToolFailureException(WireError.Of(ErrorCodes.E1001, "entity_must_be_object:index=" + i, "entities"));
                        }
                        failedItems.Add(MakeFailedEntityItem(i, "unknown", "entity_must_be_object"));
                        continue;
                    }

                    string rawType = (JsonCodec.GetStr(spec, "type") ?? string.Empty).Trim().ToLowerInvariant();
                    string canonicalType;
                    string err;
                    Entity ent = BuildNativeEntity(spec, rawType, out canonicalType, out err);
                    if (ent == null)
                    {
                        if (strict)
                        {
                            throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                                "write_entities_strict_failed:index=" + i + ":" + (err ?? "invalid_entity"), "entities"));
                        }
                        failedItems.Add(MakeFailedEntityItem(i, string.IsNullOrEmpty(rawType) ? "unknown" : rawType, err ?? "invalid_entity"));
                        continue;
                    }

                    try
                    {
                        string lyrName = JsonCodec.GetStr(spec, "layer");
                        if (string.IsNullOrEmpty(lyrName))
                        {
                            lyrName = defaultLayer;
                        }
                        ObjectId lyrId;
                        if (!layerCache.TryGetValue(lyrName, out lyrId))
                        {
                            lyrId = EnsureLayer(tr, context.Db, lyrName);
                            layerCache[lyrName] = lyrId;
                        }
                        ent.LayerId = lyrId;

                        double colorNum;
                        if (TryNumber(spec, "color", out colorNum))
                        {
                            int ci = (int)Math.Round(colorNum);
                            if (ci >= 1 && ci <= 255)
                            {
                                ent.ColorIndex = ci;
                            }
                        }

                        ms.AppendEntity(ent);
                        tr.AddNewlyCreatedDBObject(ent, true);

                        createdCount++;
                        int prev;
                        byType[canonicalType] = byType.TryGetValue(canonicalType, out prev) ? (prev + 1) : 1;
                        if (handles.Count < 500)
                        {
                            handles.Add(ent.Handle.Value.ToString("X", CultureInfo.InvariantCulture));
                        }

                        try
                        {
                            Extents3d ext = ent.GeometricExtents;
                            minX = Math.Min(minX, ext.MinPoint.X);
                            minY = Math.Min(minY, ext.MinPoint.Y);
                            minZ = Math.Min(minZ, ext.MinPoint.Z);
                            maxX = Math.Max(maxX, ext.MaxPoint.X);
                            maxY = Math.Max(maxY, ext.MaxPoint.Y);
                            maxZ = Math.Max(maxZ, ext.MaxPoint.Z);
                            hasBounds = true;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    catch (Exception ex)
                    {
                        try { ent.Dispose(); } catch (Exception) { }
                        if (strict)
                        {
                            throw new ToolFailureException(WireError.Of(ErrorCodes.E1001,
                                "write_entities_strict_failed:index=" + i + ":" + ex.Message, "entities"));
                        }
                        failedItems.Add(MakeFailedEntityItem(i, canonicalType ?? rawType, ex.Message));
                    }
                }

                if (createdCount > 0)
                {
                    tr.Commit();
                }
            }

            if (createdCount > 0)
            {
                try { context.Doc.Editor.Regen(); } catch (Exception) { }
                if (zoomExtents)
                {
                    try { context.Doc.SendStringToExecute("_.ZOOM _E\n", true, false, false); } catch (Exception) { }
                }
            }

            HostLog.Info("write", "entities_written", "requested", rawEntities.Count, "created", createdCount,
                "failed", failedItems.Count, "layer", defaultLayer);

            Dictionary<string, object> byTypeObj = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in byType)
            {
                byTypeObj[kv.Key] = kv.Value;
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["requestedCount"] = rawEntities.Count;
            result["createdCount"] = createdCount;
            result["failedCount"] = failedItems.Count;
            result["failedItems"] = failedItems;
            result["layer"] = defaultLayer;
            result["handles"] = handles;
            result["handlesTruncated"] = createdCount > handles.Count;
            result["byType"] = byTypeObj;
            result["autoApproved"] = autoApproved;
            result["saved"] = false;
            if (hasBounds)
            {
                Dictionary<string, object> extObj = new Dictionary<string, object>(StringComparer.Ordinal);
                extObj["min"] = new List<object> { Math.Round(minX, 6), Math.Round(minY, 6), Math.Round(minZ, 6) };
                extObj["max"] = new List<object> { Math.Round(maxX, 6), Math.Round(maxY, 6), Math.Round(maxZ, 6) };
                result["extents"] = extObj;
            }
            result["undoHint"] = "本次批量创建未保存；CAD 里执行 U 或 write_undo 可单步撤销";
            result["summary"] = ToolResultHelper.Summary(string.Format(CultureInfo.InvariantCulture,
                "【原生批量建实体】请求 {0}，成功 {1}，失败 {2}（默认图层 {3}，未保存，支持 write_undo 撤销）。",
                rawEntities.Count, createdCount, failedItems.Count, defaultLayer));
            return result;
        }

        private static Dictionary<string, object> MakeFailedEntityItem(int index, string type, string error)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["index"] = index;
            d["type"] = type;
            d["error"] = error;
            return d;
        }

        private static Entity BuildNativeEntity(Dictionary<string, object> spec, string rawType, out string canonicalType, out string err)
        {
            canonicalType = rawType;
            err = null;
            if (string.IsNullOrEmpty(rawType))
            {
                err = "type_required";
                return null;
            }

            if (rawType == "line")
            {
                canonicalType = "line";
                Point3d p1, p2;
                if (!TryReadPoint3d(spec, "start", "p1", "from", out p1))
                {
                    double x1, y1, z1 = 0.0;
                    if (TryNumber(spec, "x1", out x1) && TryNumber(spec, "y1", out y1))
                    {
                        TryNumber(spec, "z1", out z1);
                        p1 = new Point3d(x1, y1, z1);
                    }
                    else
                    {
                        err = "line_requires_start_and_end";
                        return null;
                    }
                }
                if (!TryReadPoint3d(spec, "end", "p2", "to", out p2))
                {
                    double x2, y2, z2 = 0.0;
                    if (TryNumber(spec, "x2", out x2) && TryNumber(spec, "y2", out y2))
                    {
                        TryNumber(spec, "z2", out z2);
                        p2 = new Point3d(x2, y2, z2);
                    }
                    else
                    {
                        err = "line_requires_start_and_end";
                        return null;
                    }
                }
                return new Line(p1, p2);
            }

            if (rawType == "polyline" || rawType == "lwpolyline" || rawType == "pline")
            {
                canonicalType = "polyline";
                List<object> pts = JsonCodec.GetList(spec, "points") ?? JsonCodec.GetList(spec, "vertices");
                if (pts == null || pts.Count < 2)
                {
                    err = "polyline_requires_at_least_two_points";
                    return null;
                }
                double width = 0.0;
                TryNumber(spec, "width", out width);
                double elev = 0.0;
                TryNumber(spec, "elevation", out elev);
                bool closed = JsonCodec.GetBool(spec, "closed", false);

                Polyline pl = new Polyline(pts.Count);
                pl.Elevation = elev;
                int vIdx = 0;
                for (int k = 0; k < pts.Count; k++)
                {
                    double vx, vy, bulge;
                    if (!TryParseVertex2d(pts[k], out vx, out vy, out bulge))
                    {
                        pl.Dispose();
                        err = "invalid_polyline_vertex:" + k;
                        return null;
                    }
                    pl.AddVertexAt(vIdx++, new Point2d(vx, vy), bulge, width, width);
                }
                pl.Closed = closed;
                return pl;
            }

            if (rawType == "rectangle" || rawType == "rect")
            {
                canonicalType = "rectangle";
                double x1, y1, x2, y2;
                double elev = 0.0;
                TryNumber(spec, "elevation", out elev);
                Point3d a, b;
                if (TryReadPoint3d(spec, "p1", "min", "start", out a) && TryReadPoint3d(spec, "p2", "max", "end", out b))
                {
                    x1 = a.X; y1 = a.Y;
                    x2 = b.X; y2 = b.Y;
                    if (Math.Abs(elev) < 1e-12) elev = a.Z;
                }
                else
                {
                    double w, h;
                    if (!TryNumber(spec, "width", out w) || !TryNumber(spec, "height", out h) || w <= 0 || h <= 0)
                    {
                        err = "rectangle_requires_p1_p2_or_origin_width_height";
                        return null;
                    }
                    Point3d origin;
                    if (TryReadPoint3d(spec, "origin", "p1", "min", out origin))
                    {
                        x1 = origin.X; y1 = origin.Y;
                        x2 = x1 + w; y2 = y1 + h;
                        if (Math.Abs(elev) < 1e-12) elev = origin.Z;
                    }
                    else if (TryReadPoint3d(spec, "center", "at", null, out origin))
                    {
                        x1 = origin.X - w * 0.5; y1 = origin.Y - h * 0.5;
                        x2 = origin.X + w * 0.5; y2 = origin.Y + h * 0.5;
                        if (Math.Abs(elev) < 1e-12) elev = origin.Z;
                    }
                    else if (TryNumber(spec, "x", out x1) && TryNumber(spec, "y", out y1))
                    {
                        x2 = x1 + w; y2 = y1 + h;
                    }
                    else
                    {
                        err = "rectangle_requires_origin_or_center";
                        return null;
                    }
                }
                if (Math.Abs(x2 - x1) < 1e-9 || Math.Abs(y2 - y1) < 1e-9)
                {
                    err = "rectangle_degenerate";
                    return null;
                }
                double width = 0.0;
                TryNumber(spec, "widthLine", out width);
                Polyline rect = new Polyline(4);
                rect.Elevation = elev;
                rect.AddVertexAt(0, new Point2d(x1, y1), 0.0, width, width);
                rect.AddVertexAt(1, new Point2d(x2, y1), 0.0, width, width);
                rect.AddVertexAt(2, new Point2d(x2, y2), 0.0, width, width);
                rect.AddVertexAt(3, new Point2d(x1, y2), 0.0, width, width);
                rect.Closed = true;
                return rect;
            }

            if (rawType == "circle")
            {
                canonicalType = "circle";
                Point3d center;
                if (!TryReadPoint3d(spec, "center", "at", null, out center))
                {
                    if (!TryReadInlinePoint3d(spec, out center))
                    {
                        err = "circle_requires_center";
                        return null;
                    }
                }
                double radius;
                if (!(TryNumber(spec, "radius", out radius) || TryNumber(spec, "r", out radius)) || radius <= 1e-9)
                {
                    err = "circle_requires_positive_radius";
                    return null;
                }
                return new Circle(center, Vector3d.ZAxis, radius);
            }

            if (rawType == "arc")
            {
                canonicalType = "arc";
                Point3d center;
                if (!TryReadPoint3d(spec, "center", "at", null, out center))
                {
                    if (!TryReadInlinePoint3d(spec, out center))
                    {
                        err = "arc_requires_center";
                        return null;
                    }
                }
                double radius;
                if (!(TryNumber(spec, "radius", out radius) || TryNumber(spec, "r", out radius)) || radius <= 1e-9)
                {
                    err = "arc_requires_positive_radius";
                    return null;
                }
                double startRad, endRad;
                if (!TryReadAngleRad(spec, "startAngle_deg", "startAngle", "startAngle_rad", out startRad) ||
                    !TryReadAngleRad(spec, "endAngle_deg", "endAngle", "endAngle_rad", out endRad))
                {
                    err = "arc_requires_startAngle_and_endAngle";
                    return null;
                }
                return new Arc(center, radius, startRad, endRad);
            }

            if (rawType == "ellipse")
            {
                canonicalType = "ellipse";
                Point3d center;
                if (!TryReadPoint3d(spec, "center", "at", null, out center))
                {
                    if (!TryReadInlinePoint3d(spec, out center))
                    {
                        err = "ellipse_requires_center";
                        return null;
                    }
                }
                Vector3d majorAxis;
                double ratio;
                Point3d majorPt;
                if (TryReadPoint3d(spec, "majorAxis", null, null, out majorPt))
                {
                    majorAxis = new Vector3d(majorPt.X, majorPt.Y, majorPt.Z);
                    if (!TryNumber(spec, "radiusRatio", out ratio))
                    {
                        ratio = 0.5;
                    }
                }
                else
                {
                    double rx, ry;
                    if (!(TryNumber(spec, "majorRadius", out rx) || TryNumber(spec, "rx", out rx)) ||
                        !(TryNumber(spec, "minorRadius", out ry) || TryNumber(spec, "ry", out ry)) ||
                        rx <= 1e-9 || ry <= 1e-9)
                    {
                        err = "ellipse_requires_majorAxis_or_majorRadius_minorRadius";
                        return null;
                    }
                    double rotRad = 0.0;
                    TryReadAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out rotRad);
                    majorAxis = new Vector3d(rx * Math.Cos(rotRad), rx * Math.Sin(rotRad), 0.0);
                    ratio = ry / rx;
                }
                if (majorAxis.Length <= 1e-9 || ratio <= 1e-9)
                {
                    err = "ellipse_invalid_axes";
                    return null;
                }
                if (ratio > 1.0)
                {
                    // AutoCAD Ellipse 要求 0 < radiusRatio <= 1.0；若长短轴颠倒则自动旋转 90° 归一
                    majorAxis = new Vector3d(-majorAxis.Y * ratio, majorAxis.X * ratio, majorAxis.Z);
                    ratio = 1.0 / ratio;
                }
                double startParam = 0.0, endParam = 2.0 * Math.PI;
                if (TryNumber(spec, "startParam", out startParam) || TryReadAngleRad(spec, "startAngle_deg", null, "startAngle_rad", out startParam))
                {
                    // startParam parsed
                }
                if (TryNumber(spec, "endParam", out endParam) || TryReadAngleRad(spec, "endAngle_deg", null, "endAngle_rad", out endParam))
                {
                    // endParam parsed
                }
                return new Ellipse(center, Vector3d.ZAxis, majorAxis, ratio, startParam, endParam);
            }

            if (rawType == "text" || rawType == "dbtext")
            {
                canonicalType = "text";
                string content = JsonCodec.GetStr(spec, "text") ?? JsonCodec.GetStr(spec, "content") ?? JsonCodec.GetStr(spec, "value");
                if (string.IsNullOrEmpty(content))
                {
                    err = "text_requires_content";
                    return null;
                }
                Point3d pos;
                if (!TryReadPoint3d(spec, "position", "insert", "at", out pos) && !TryReadInlinePoint3d(spec, out pos))
                {
                    err = "text_requires_position";
                    return null;
                }
                double height = 2.5;
                if (!TryNumber(spec, "height", out height) || height <= 1e-9)
                {
                    height = 2.5;
                }
                double rotRad = 0.0;
                TryReadAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out rotRad);
                DBText txt = new DBText();
                txt.Position = pos;
                txt.TextString = content;
                txt.Height = height;
                txt.Rotation = rotRad;
                return txt;
            }

            if (rawType == "mtext")
            {
                canonicalType = "mtext";
                string content = JsonCodec.GetStr(spec, "text") ?? JsonCodec.GetStr(spec, "content") ?? JsonCodec.GetStr(spec, "value");
                if (string.IsNullOrEmpty(content))
                {
                    err = "mtext_requires_content";
                    return null;
                }
                Point3d pos;
                if (!TryReadPoint3d(spec, "position", "insert", "at", out pos) && !TryReadInlinePoint3d(spec, out pos))
                {
                    err = "mtext_requires_position";
                    return null;
                }
                double height = 2.5;
                if (!TryNumber(spec, "height", out height) || height <= 1e-9)
                {
                    height = 2.5;
                }
                double width = 0.0;
                TryNumber(spec, "width", out width);
                double rotRad = 0.0;
                TryReadAngleRad(spec, "rotation_deg", "rotation", "rotation_rad", out rotRad);
                MText mt = new MText();
                mt.Location = pos;
                mt.Contents = content;
                mt.TextHeight = height;
                if (width > 0.0)
                {
                    mt.Width = width;
                }
                mt.Rotation = rotRad;
                return mt;
            }

            if (rawType == "point" || rawType == "dbpoint")
            {
                canonicalType = "point";
                Point3d pos;
                if (!TryReadPoint3d(spec, "position", "point", "at", out pos) && !TryReadInlinePoint3d(spec, out pos))
                {
                    err = "point_requires_position";
                    return null;
                }
                return new DBPoint(pos);
            }

            err = "unsupported_entity_type:" + rawType;
            return null;
        }

        private static bool TryReadPoint3d(Dictionary<string, object> dict, string key1, string key2, string key3, out Point3d pt)
        {
            pt = Point3d.Origin;
            if (dict == null) return false;
            object raw;
            if (key1 != null && dict.TryGetValue(key1, out raw) && TryConvertPoint3d(raw, out pt)) return true;
            if (key2 != null && dict.TryGetValue(key2, out raw) && TryConvertPoint3d(raw, out pt)) return true;
            if (key3 != null && dict.TryGetValue(key3, out raw) && TryConvertPoint3d(raw, out pt)) return true;
            return false;
        }

        private static bool TryReadInlinePoint3d(Dictionary<string, object> dict, out Point3d pt)
        {
            pt = Point3d.Origin;
            double x, y, z = 0.0;
            bool hasX = TryNumber(dict, "x", out x) || TryNumber(dict, "e", out x);
            bool hasY = TryNumber(dict, "y", out y) || TryNumber(dict, "n", out y);
            if (!hasX || !hasY) return false;
            TryNumber(dict, "z", out z);
            pt = new Point3d(x, y, z);
            return true;
        }

        private static bool TryConvertPoint3d(object raw, out Point3d pt)
        {
            pt = Point3d.Origin;
            if (raw == null) return false;
            List<object> list = JsonCodec.AsList(raw);
            if (list != null && list.Count >= 2)
            {
                double x, y, z = 0.0;
                if (TryObjNumber(list[0], out x) && TryObjNumber(list[1], out y))
                {
                    if (list.Count >= 3) TryObjNumber(list[2], out z);
                    pt = new Point3d(x, y, z);
                    return true;
                }
                return false;
            }
            Dictionary<string, object> d = raw as Dictionary<string, object>;
            if (d != null)
            {
                return TryReadInlinePoint3d(d, out pt);
            }
            return false;
        }

        private static bool TryParseVertex2d(object raw, out double x, out double y, out double bulge)
        {
            x = 0.0; y = 0.0; bulge = 0.0;
            if (raw == null) return false;
            List<object> list = JsonCodec.AsList(raw);
            if (list != null && list.Count >= 2)
            {
                if (!TryObjNumber(list[0], out x) || !TryObjNumber(list[1], out y)) return false;
                if (list.Count >= 3) TryObjNumber(list[2], out bulge);
                return true;
            }
            Dictionary<string, object> d = raw as Dictionary<string, object>;
            if (d != null)
            {
                bool hasX = TryNumber(d, "x", out x) || TryNumber(d, "e", out x);
                bool hasY = TryNumber(d, "y", out y) || TryNumber(d, "n", out y);
                if (!hasX || !hasY) return false;
                TryNumber(d, "bulge", out bulge);
                return true;
            }
            return false;
        }

        private static bool TryReadAngleRad(Dictionary<string, object> spec, string degKey, string fallbackDegKey, string radKey, out double radians)
        {
            radians = 0.0;
            double v;
            if (degKey != null && TryNumber(spec, degKey, out v))
            {
                radians = v * Math.PI / 180.0;
                return true;
            }
            if (radKey != null && TryNumber(spec, radKey, out v))
            {
                radians = v;
                return true;
            }
            if (fallbackDegKey != null && TryNumber(spec, fallbackDegKey, out v))
            {
                radians = v * Math.PI / 180.0;
                return true;
            }
            return false;
        }

        private static bool TryObjNumber(object raw, out double value)
        {
            value = 0.0;
            if (raw == null) return false;
            if (raw is double) { value = (double)raw; return true; }
            if (raw is int) { value = (int)raw; return true; }
            if (raw is long) { value = (long)raw; return true; }
            string s = raw as string;
            if (s != null) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return false;
        }

        private static readonly HashSet<string> MetaDigestExcluded = new HashSet<string>(StringComparer.Ordinal)
        {
            "approvalToken", "paramsDigest", "targetFingerprint", "userConfirmation", "dryRun"
        };

        /// <summary>统一 Canonical JSON SHA-256 摘要（排除鉴权/元字段，避免白名单漂移导致 E1002）。</summary>
        internal static string ComputeCanonicalWriteDigest(Dictionary<string, object> p)
        {
            Dictionary<string, object> canonical = new Dictionary<string, object>(StringComparer.Ordinal);
            if (p != null)
            {
                foreach (KeyValuePair<string, object> kv in p)
                {
                    if (!MetaDigestExcluded.Contains(kv.Key))
                    {
                        canonical[kv.Key] = kv.Value;
                    }
                }
            }
            return ParamsDigest.Compute(canonical);
        }
        /// <summary>命令模式下参与摘要的键：命令原文 + dryRun 之外的执行语义。</summary>
        internal static string ComputeCommandDigest(Dictionary<string, object> p)
        {
            Dictionary<string, object> canonical = new Dictionary<string, object>(StringComparer.Ordinal);
            canonical["kind"] = "command";
            canonical["command"] = JsonCodec.GetStr(p, "command") ?? string.Empty;
            return ParamsDigest.Compute(canonical);
        }

        private static string MatchForbidden(string command)
        {
            string upper = command.TrimStart('_', '-', '.', '(').ToUpperInvariant();
            for (int i = 0; i < ForbiddenCommandPrefixes.Length; i++)
            {
                string prefix = ForbiddenCommandPrefixes[i].TrimStart('_', '-').ToUpperInvariant();
                if (upper.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return ForbiddenCommandPrefixes[i];
                }
            }
            return null;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return text.Length <= 80 ? text : text.Substring(0, 80) + "…";
        }

        // ---------------- 公共小件 ----------------

        private sealed class PointRow
        {
            public double E;
            public double N;
            public double Z;
        }

        private static bool TryParsePoints(Dictionary<string, object> p, out List<PointRow> rows, out string detail)
        {
            rows = new List<PointRow>();
            detail = null;

            object raw;
            if (p == null || !p.TryGetValue("points", out raw) || raw == null)
            {
                detail = "points_required";
                return false;
            }
            List<object> list = JsonCodec.AsList(raw);
            if (list == null)
            {
                detail = "points_must_be_array";
                return false;
            }
            if (list.Count == 0)
            {
                detail = "points_empty";
                return false;
            }
            int limit = 5000;
            if (list.Count > limit)
            {
                detail = "points_exceed_limit:" + limit;
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                double e;
                double n;
                double z = 0.0;
                Dictionary<string, object> item = list[i] as Dictionary<string, object>;
                if (item != null)
                {
                    bool hasE = TryNumber(item, "e", out e) || TryNumber(item, "x", out e);
                    bool hasN = TryNumber(item, "n", out n) || TryNumber(item, "y", out n);
                    if (!hasE || !hasN)
                    {
                        detail = "point_requires_numeric_e_and_n:index=" + i;
                        return false;
                    }
                    if (!TryNumber(item, "z", out z))
                    {
                        z = 0.0;
                    }
                }
                else
                {
                    List<object> arrPt = JsonCodec.AsList(list[i]);
                    if (arrPt == null || arrPt.Count < 2 || !TryObjNumber(arrPt[0], out e) || !TryObjNumber(arrPt[1], out n))
                    {
                        detail = "point_must_be_object:index=" + i;
                        return false;
                    }
                    if (arrPt.Count >= 3)
                    {
                        TryObjNumber(arrPt[2], out z);
                    }
                }
                PointRow row = new PointRow();
                row.E = e;
                row.N = n;
                row.Z = z;
                rows.Add(row);
            }
            return true;
        }

        private static int CountModelSpaceFast(Database db)
        {
            if (db == null) return 0;
            try
            {
                int count = 0;
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    ObjectId msId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(msId, OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (!id.IsErased) count++;
                    }
                }
                return count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static int CountLayersFast(Database db)
        {
            if (db == null) return 0;
            try
            {
                int count = 0;
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId id in lt)
                    {
                        if (!id.IsErased) count++;
                    }
                }
                return count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string SafeGetLastPrompt()
        {
            try
            {
                object v = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("LASTPROMPT");
                return v == null ? string.Empty : Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsCommandPromptError(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            return prompt.IndexOf("Unknown command", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   prompt.IndexOf("未知命令", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   prompt.IndexOf("; error:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   prompt.IndexOf("; 错误:", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryNumber(Dictionary<string, object> item, string key, out double value)
        {
            value = 0.0;
            object raw;
            if (item == null || !item.TryGetValue(key, out raw) || raw == null)
            {
                return false;
            }
            if (raw is double)
            {
                value = (double)raw;
                return true;
            }
            if (raw is int)
            {
                value = (int)raw;
                return true;
            }
            if (raw is long)
            {
                value = (long)raw;
                return true;
            }
            string text = raw as string;
            if (text != null)
            {
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
            return false;
        }

        private static int CountPoints(Dictionary<string, object> p)
        {
            List<PointRow> rows;
            string detail;
            return TryParsePoints(p, out rows, out detail) ? rows.Count : 0;
        }

        private static string LayerName(Dictionary<string, object> p)
        {
            string layer = JsonCodec.GetStr(p, "layer");
            return string.IsNullOrEmpty(layer) ? "EP-STAKEOUT" : layer;
        }

        /// <summary>写参数摘要：只算 <see cref="DigestKeys"/>，与 preview/approve 双端同源。</summary>
        internal static string ComputeDigest(Dictionary<string, object> p)
        {
            Dictionary<string, object> canonical = new Dictionary<string, object>(StringComparer.Ordinal);
            if (p != null)
            {
                for (int i = 0; i < DigestKeys.Length; i++)
                {
                    object value;
                    if (p.TryGetValue(DigestKeys[i], out value))
                    {
                        canonical[DigestKeys[i]] = value;
                    }
                }
            }
            if (!canonical.ContainsKey("kind"))
            {
                canonical["kind"] = "insert_points";
            }
            if (!canonical.ContainsKey("layer"))
            {
                canonical["layer"] = "EP-STAKEOUT";
            }
            return ParamsDigest.Compute(canonical);
        }

        private static string NormalizeDigest(string digest)
        {
            if (string.IsNullOrEmpty(digest))
            {
                return string.Empty;
            }
            return digest.StartsWith("sha256:", StringComparison.Ordinal)
                ? digest.Substring(7)
                : digest;
        }

        private static string CurrentFingerprint(ToolContext context)
        {
            if (!string.IsNullOrEmpty(context.Fingerprint))
            {
                return context.Fingerprint;
            }
            return context.Runtime.GetSemanticModel(context.Doc, false).Fingerprint ?? "fp1:00000000000000000000000000000000";
        }

        private static string DocumentIdentity(ToolContext context)
        {
            string docName = null;
            string fileName = null;
            try
            {
                docName = context.Doc == null ? null : context.Doc.Name;
            }
            catch (Exception)
            {
                docName = null;
            }
            try
            {
                fileName = context.Db == null ? null : context.Db.Filename;
            }
            catch (Exception)
            {
                fileName = null;
            }
            return ApprovalTokens.DocumentIdentity(docName, fileName);
        }

        private static string SafeDocName(ToolContext context)
        {
            try
            {
                return context.Doc == null ? "-" : (context.Doc.Name ?? "-");
            }
            catch (Exception)
            {
                return "-";
            }
        }

        private static bool LayerExists(ToolContext context, string layer)
        {
            try
            {
                using (Transaction tr = context.Db.TransactionManager.StartOpenCloseTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(context.Db.LayerTableId, OpenMode.ForRead);
                    return lt.Has(layer);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ObjectId EnsureLayer(Transaction tr, Database db, string layer)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layer))
            {
                return lt[layer];
            }
            lt.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord();
            record.Name = layer;
            ObjectId id = lt.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            HostLog.Info("write", "layer_created", "layer", layer);
            return id;
        }
    }
}
