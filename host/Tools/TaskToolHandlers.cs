using System;
using System.Collections.Generic;
using System.Globalization;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Scheduling;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 四个 task_* 工具（M0 只读控制语义）：
    /// task_status / task_list / task_cancel / task_reconcile。
    /// task_cancel 只置取消位，由 handler 在对象批边界停止；M0 无写入故无副作用。
    /// </summary>
    internal static class TaskToolHandlers
    {
        public static Dictionary<string, object> Handle(ToolContext context)
        {
            string tool = context.Item.Tool;
            switch (tool)
            {
                case "task_status":
                    return Status(context);
                case "task_list":
                    return List(context);
                case "task_cancel":
                    return Cancel(context);
                case "task_reconcile":
                    return Reconcile(context);
                default:
                    throw new ToolFailureException(WireError.Of(ErrorCodes.E2001, "unknown_task_tool:" + tool, "tool"));
            }
        }

        private static Dictionary<string, object> Status(ToolContext context)
        {
            string opId = JsonCodec.GetStr(context.Item.Params, "opId");
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);

            TaskRecord record = context.Runtime.FindTask(opId);
            if (record == null)
            {
                WorkItem active = context.Runtime.Dispatcher.Find(opId);
                if (active != null)
                {
                    result["opId"] = opId;
                    result["status"] = active.CancelRequested ? "cancelled" : "running";
                    result["tool"] = active.Tool;
                    result["startedAtUtc"] = TimeUtil.ToIso(active.StartedUtc);
                    result["elapsedMs"] = active.ElapsedMs;
                    result["errorCode"] = null;
                    result["summary"] = ToolResultHelper.Summary("【任务状态】" + opId + " 正在执行（" + active.Tool + "）");
                    result["progress"] = active.CancelRequested ? "取消已请求" : "执行中";
                    return result;
                }

                result["opId"] = opId;
                result["status"] = "notFound";
                result["tool"] = null;
                result["startedAtUtc"] = null;
                result["elapsedMs"] = null;
                result["errorCode"] = null;
                result["summary"] = ToolResultHelper.Summary("【任务状态】" + opId + " 不在宿主记录中（可能宿主已重启）；只读请求可用新 opId 安全重试");
                return result;
            }

            result["opId"] = record.OpId;
            result["status"] = record.Status;
            result["tool"] = record.Tool;
            result["startedAtUtc"] = TimeUtil.ToIso(record.StartedUtc);
            result["elapsedMs"] = record.ElapsedMs;
            result["errorCode"] = record.ErrorCode;
            result["summary"] = ToolResultHelper.Summary("【任务状态】" + record.OpId + " " + record.Status
                + "（" + record.Tool + "，耗时 " + record.ElapsedMs + " ms"
                + (string.IsNullOrEmpty(record.ErrorCode) ? string.Empty : "，错误 " + record.ErrorCode) + "）");
            return result;
        }

        private static Dictionary<string, object> List(ToolContext context)
        {
            int limit = JsonCodec.GetInt(context.Item.Params, "limit", 20);
            if (limit < 1)
            {
                limit = 1;
            }
            if (limit > 200)
            {
                limit = 200;
            }
            string sinceText = JsonCodec.GetStr(context.Item.Params, "sinceUtc");
            DateTime since = string.IsNullOrEmpty(sinceText) ? DateTime.MinValue : TimeUtil.ParseIso(sinceText);

            List<TaskRecord> records = context.Runtime.RecentTasks(limit, since);
            List<object> tasks = new List<object>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                TaskRecord record = records[i];
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["opId"] = record.OpId;
                item["tool"] = record.Tool;
                item["status"] = record.Status;
                item["startedAtUtc"] = TimeUtil.ToIso(record.StartedUtc);
                item["elapsedMs"] = record.ElapsedMs;
                item["errorCode"] = record.ErrorCode;
                tasks.Add(item);
            }

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["summary"] = ToolResultHelper.Summary("【最近任务】返回 " + tasks.Count + " 条"
                + (since == DateTime.MinValue ? string.Empty : "（自 " + TimeUtil.ToIso(since) + " 起）"));
            result["tasks"] = tasks;
            return result;
        }

        private static Dictionary<string, object> Cancel(ToolContext context)
        {
            string opId = JsonCodec.GetStr(context.Item.Params, "opId");
            bool accepted = context.Runtime.CancelTask(opId);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["accepted"] = accepted;
            result["status"] = accepted ? "cancelled" : "unknown";
            result["reason"] = accepted ? "cancelled_at_batch_boundary" : "op_not_running";
            result["summary"] = ToolResultHelper.Summary(accepted
                ? "【取消任务】" + opId + " 已受理，将在对象批边界停止（M0 无写入，无副作用）"
                : "【取消任务】" + opId + " 未在运行（可能已完成或宿主重启）");
            return result;
        }

        private static Dictionary<string, object> Reconcile(ToolContext context)
        {
            string opId = JsonCodec.GetStr(context.Item.Params, "opId");
            TaskRecord record = context.Runtime.FindTask(opId);

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["opId"] = opId;
            result["checkedAtUtc"] = TimeUtil.NowIso();

            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
            if (record == null)
            {
                result["decision"] = "unknown";
                evidence["reason"] = "host_task_not_found";
                evidence["advice"] = "只读请求未完成，可安全重试，但必须使用新的 opId";
                result["evidence"] = evidence;
                result["advice"] = "可安全重试，请用新的 opId 重新发起";
                result["summary"] = ToolResultHelper.Summary("【对账】" + opId + " 判定 unknown：宿主任务表无此记录"
                    + "（可能宿主已重启）；只读请求可安全重试，但必须用新 opId，不自动重放");
                return result;
            }

            if (string.Equals(record.Status, "verified", StringComparison.Ordinal))
            {
                result["decision"] = "verified";
                evidence["elapsedMs"] = record.ElapsedMs;
                evidence["tool"] = record.Tool;
                result["advice"] = null;
                result["summary"] = ToolResultHelper.Summary("【对账】" + opId + " 判定 verified：宿主已完成本次只读任务（"
                    + record.Tool + "，" + record.ElapsedMs + " ms）");
            }
            else if (string.Equals(record.Status, "failed", StringComparison.Ordinal)
                || string.Equals(record.Status, "cancelled", StringComparison.Ordinal))
            {
                result["decision"] = "failed";
                evidence["status"] = record.Status;
                evidence["errorCode"] = record.ErrorCode;
                result["advice"] = "按错误码修正后，用新 opId 重试";
                result["summary"] = ToolResultHelper.Summary("【对账】" + opId + " 判定 failed：" + record.Status
                    + (string.IsNullOrEmpty(record.ErrorCode) ? string.Empty : "，错误码 " + record.ErrorCode));
            }
            else
            {
                result["decision"] = "unknown";
                evidence["status"] = record.Status;
                evidence["reason"] = "task_not_finished";
                result["advice"] = "可安全重试，请用新的 opId 重新发起";
                result["summary"] = ToolResultHelper.Summary("【对账】" + opId + " 判定 unknown：任务状态 " + record.Status
                    + "，无法确认结果；可安全重试但必须用新 opId");
            }

            result["evidence"] = evidence;
            return result;
        }
    }
}
