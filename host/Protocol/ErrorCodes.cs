using System;
using System.Collections.Generic;

namespace EngineeringPlatform.Host.Protocol
{
    /// <summary>
    /// 错误码 / 告警码常量与工厂。码表唯一真相源是 protocol/errors.json，本文件是它的 C# 镜像。
    /// 改动必须双端同步（C# / Python / errors.json）。
    /// </summary>
    internal static class ErrorCodes
    {
        // 宿主侧错误码
        public const string E1001 = "E1001"; // 参数无效 / 帧超限 / 非法 JSON
        public const string E1002 = "E1002"; // 目标与审批不符（M0 预留）
        public const string E1003 = "E1003"; // 宿主忙碌（cmdactive / queue_full / session_busy）
        public const string E1004 = "E1004"; // 模态对话框（主线程未泵送）
        public const string E1005 = "E1005"; // 无文档 / 文档只读
        public const string E1006 = "E1006"; // 审批令牌无效（M0：收到非空令牌即拒）
        public const string E2001 = "E2001"; // 工具不在目录
        public const string E2002 = "E2002"; // 动态编译失败（M0 不可达）
        public const string E2003 = "E2003"; // 命中禁止能力（写域）
        public const string E3001 = "E3001"; // 后置验证失败（M0 不可达）
        public const string E3002 = "E3002"; // 结果不明
        public const string E4001 = "E4001"; // 协议/会话不匹配
        public const string E5001 = "E5001"; // 磁盘/配额
        public const string E6001 = "E6001"; // 权限不足（M0 不可达）

        // 告警码
        public const string W1001 = "W1001"; // Civil 3D API 不可用
        public const string W1002 = "W1002"; // 客户端次版本高于宿主
        public const string W1003 = "W1003"; // 心跳超时
        public const string W1004 = "W1004"; // 指纹漂移
        public const string W1005 = "W1005"; // 采集耗时偏高
        public const string W1006 = "W1006"; // 字体回退
        public const string W1007 = "W1007"; // 工具目录不一致
        public const string W1008 = "W1008"; // 结果被截断
        public const string W1009 = "W1009"; // 图元数超上限
    }

    /// <summary>线上错误对象（envelope.schema.json#/$defs/Error）。</summary>
    internal sealed class WireError
    {
        public string Code = ErrorCodes.E1001;
        public string Message = "参数无效";
        public string Field;
        public string Detail;
        public bool Retriable = true;
        public string UserAdvice;

        /// <summary>按码表构造：自动补中文 message / retriable / 用户建议。</summary>
        public static WireError Of(string code, string detail = null, string field = null, string messageOverride = null)
        {
            ErrorMeta meta = ErrorMeta.Lookup(code);
            WireError err = new WireError();
            err.Code = code;
            err.Message = messageOverride ?? meta.Message;
            err.Detail = detail;
            err.Field = field;
            err.Retriable = meta.Retriable;
            err.UserAdvice = meta.UserAdvice;
            return err;
        }

        /// <summary>序列化为封套可用的字典（只包含非空可选字段）。</summary>
        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["code"] = Code;
            dict["message"] = Message;
            if (!string.IsNullOrEmpty(Field))
            {
                dict["field"] = Field;
            }
            if (!string.IsNullOrEmpty(Detail))
            {
                dict["detail"] = Detail;
            }
            dict["retriable"] = Retriable;
            if (!string.IsNullOrEmpty(UserAdvice))
            {
                dict["userAdvice"] = UserAdvice;
            }
            return dict;
        }
    }

    /// <summary>线上告警对象（envelope.schema.json#/$defs/Warning）。</summary>
    internal sealed class WireWarning
    {
        public string Code;
        public string Message;
        public string Detail;

        public static WireWarning Of(string code, string detail = null, string messageOverride = null)
        {
            WireWarning w = new WireWarning();
            w.Code = code;
            w.Message = messageOverride ?? WarningMeta.MessageOf(code);
            w.Detail = detail;
            return w;
        }

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["code"] = Code;
            dict["message"] = Message;
            if (!string.IsNullOrEmpty(Detail))
            {
                dict["detail"] = Detail;
            }
            return dict;
        }
    }

    /// <summary>错误码元信息（与 protocol/errors.json 对齐）。</summary>
    internal sealed class ErrorMeta
    {
        public string Code;
        public string Message;
        public bool Retriable;
        public string UserAdvice;

        private static readonly Dictionary<string, ErrorMeta> Table = BuildTable();

        public static ErrorMeta Lookup(string code)
        {
            ErrorMeta meta;
            if (code != null && Table.TryGetValue(code, out meta))
            {
                return meta;
            }
            ErrorMeta fallback = new ErrorMeta();
            fallback.Code = code ?? ErrorCodes.E1001;
            fallback.Message = "未登记的错误码";
            fallback.Retriable = false;
            fallback.UserAdvice = "请把 opId 与错误码反馈给平台维护者";
            return fallback;
        }

        private static Dictionary<string, ErrorMeta> BuildTable()
        {
            Dictionary<string, ErrorMeta> t = new Dictionary<string, ErrorMeta>(StringComparer.Ordinal);
            Add(t, ErrorCodes.E1001, "参数无效", true, "检查入参范围与类型后，用新的 opId 重试");
            Add(t, ErrorCodes.E1002, "目标图纸或对象与审批不符", false, "重新发起审批流程");
            Add(t, ErrorCodes.E1003, "宿主忙碌", true, "等待当前 CAD 命令结束或关闭占用会话后重试");
            Add(t, ErrorCodes.E1004, "存在模态对话框", true, "请先在 AutoCAD 中关闭对话框（打印、图层管理器等）后重试");
            Add(t, ErrorCodes.E1005, "文档被锁或只读", true, "打开一张可读图纸，或解除文件只读/签出状态");
            Add(t, ErrorCodes.E1006, "审批令牌无效或过期", false, "M0 写入通道关闭，不要携带 approvalToken");
            Add(t, ErrorCodes.E2001, "API 索引中不存在该能力", true, "用 tools/list 核对工具名（注意 snake_case 拼写）");
            Add(t, ErrorCodes.E2002, "动态代码编译失败", true, "查看编译诊断后修订");
            Add(t, ErrorCodes.E2003, "命中禁止能力", false, "M0 只读：写域工具（cad_/civil_/dyn_/file_）一律不可用");
            Add(t, ErrorCodes.E3001, "后置验证失败", false, "结果不可作为正式成果，需人工复核");
            Add(t, ErrorCodes.E3002, "结果不明", false, "用 task_reconcile 对账；不要自动重放，重试请用新 opId");
            Add(t, ErrorCodes.E4001, "协议或宿主版本不匹配", false, "升级 MCP 或宿主到同一主版本；令牌过期时重新 hello");
            Add(t, ErrorCodes.E5001, "磁盘空间或配额不足", true, "清理磁盘或调整缓存目录权限后重试");
            Add(t, ErrorCodes.E6001, "角色权限不足", false, "申请对应角色权限");
            return t;
        }

        private static void Add(Dictionary<string, ErrorMeta> table, string code, string message, bool retriable, string advice)
        {
            ErrorMeta meta = new ErrorMeta();
            meta.Code = code;
            meta.Message = message;
            meta.Retriable = retriable;
            meta.UserAdvice = advice;
            table[code] = meta;
        }
    }

    /// <summary>告警码 → 中文说明。</summary>
    internal static class WarningMeta
    {
        private static readonly Dictionary<string, string> Table = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ErrorCodes.W1001, "Civil 3D 原生 API 不可用" },
            { ErrorCodes.W1002, "客户端协议次版本高于宿主" },
            { ErrorCodes.W1003, "心跳超时" },
            { ErrorCodes.W1004, "图纸已变化" },
            { ErrorCodes.W1005, "采集耗时偏高" },
            { ErrorCodes.W1006, "字体回退" },
            { ErrorCodes.W1007, "工具目录不一致" },
            { ErrorCodes.W1008, "结果被截断" },
            { ErrorCodes.W1009, "对象数量超采集上限" }
        };

        public static string MessageOf(string code)
        {
            string message;
            if (code != null && Table.TryGetValue(code, out message))
            {
                return message;
            }
            return "未知告警";
        }
    }

    /// <summary>
    /// 业务失败异常：handler 内部抛出，由 router 统一转成 ok=false 的错误封套。
    /// 绝不用于控制正常流程。
    /// </summary>
    internal sealed class ToolFailureException : Exception
    {
        public WireError Error { get; private set; }

        public ToolFailureException(WireError error)
            : base(error != null ? error.Code + ":" + error.Message : "tool failure")
        {
            Error = error ?? WireError.Of(ErrorCodes.E1001, "unknown_tool_failure");
        }

        public ToolFailureException(string code, string detail = null, string field = null)
            : this(WireError.Of(code, detail, field))
        {
        }
    }
}
