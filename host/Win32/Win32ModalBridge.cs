using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EngineeringPlatform.Host.Protocol;

namespace EngineeringPlatform.Host.Win32
{
    /// <summary>
    /// 宿主内进程级 MFC (#32770) 模态对话框桥（在管道后台线程直接运行，不经主线程 Idle）：
    /// - 当 AutoCAD 主线程被南方 CASS / 飞时达 / 天正的 CDialog::DoModal() 挂起时，
    ///   本类仍在 acad.exe 同一进程与同一交互桌面（WinSta0\Default）上运行，无任何 UAC/桌面隔离问题；
    /// - 零 Autodesk API 引用，纯 Win32 user32.dll，支持读取完整控件树并异步交互。
    /// </summary>
    internal static class Win32ModalBridge
    {
        public static bool IsUiDialogTool(string tool)
        {
            return string.Equals(tool, "ui_list_dialogs", StringComparison.Ordinal) ||
                   string.Equals(tool, "ui_wait_dialog", StringComparison.Ordinal) ||
                   string.Equals(tool, "ui_interact_dialog", StringComparison.Ordinal);
        }

        public static bool TryExecuteOffMainThread(
            string tool,
            Dictionary<string, object> p,
            out Dictionary<string, object> result,
            out WireError error)
        {
            result = null;
            error = null;
            try
            {
                if (string.Equals(tool, "ui_list_dialogs", StringComparison.Ordinal))
                {
                    result = HandleListDialogs(p);
                    return true;
                }
                if (string.Equals(tool, "ui_wait_dialog", StringComparison.Ordinal))
                {
                    result = HandleWaitDialog(p);
                    return true;
                }
                if (string.Equals(tool, "ui_interact_dialog", StringComparison.Ordinal))
                {
                    result = HandleInteractDialog(p);
                    return true;
                }
                error = WireError.Of(ErrorCodes.E2001, "unknown_ui_tool:" + tool);
                return false;
            }
            catch (Exception ex)
            {
                error = WireError.Of(ErrorCodes.E1001, "ui_dialog_error:" + ex.Message);
                return false;
            }
        }

        private static Dictionary<string, object> HandleListDialogs(Dictionary<string, object> p)
        {
            int selfPid = Process.GetCurrentProcess().Id;
            string titleFilter = JsonCodec.GetStr(p, "titleContains");
            bool includeStatics = JsonCodec.GetBool(p, "includeStatics", false);

            List<Dictionary<string, object>> dialogs = EnumerateDialogs(selfPid, titleFilter, includeStatics);
            List<object> arr = new List<object>(dialogs.Count);
            List<string> titles = new List<string>(dialogs.Count);
            for (int i = 0; i < dialogs.Count; i++)
            {
                arr.Add(dialogs[i]);
                titles.Add("「" + JsonCodec.GetStr(dialogs[i], "title", "") + "」(" + JsonCodec.GetInt(dialogs[i], "interactiveCount", 0) + "个控件)");
            }

            string summary = dialogs.Count == 0
                ? string.Format(CultureInfo.InvariantCulture, "【MFC 弹窗探测·进程内】CAD (PID={0}) 当前无可见模态对话框。", selfPid)
                : string.Format(CultureInfo.InvariantCulture, "【MFC 弹窗探测·进程内】发现 {0} 个可见弹窗：{1}", dialogs.Count, string.Join(" / ", titles.ToArray()));

            Dictionary<string, object> res = new Dictionary<string, object>(StringComparer.Ordinal);
            res["summary"] = summary;
            res["source"] = "in_process_host";
            res["pid"] = selfPid;
            res["count"] = dialogs.Count;
            res["dialogs"] = arr;
            return res;
        }

        private static Dictionary<string, object> HandleWaitDialog(Dictionary<string, object> p)
        {
            int selfPid = Process.GetCurrentProcess().Id;
            string titleFilter = JsonCodec.GetStr(p, "titleContains");
            int timeoutMs = JsonCodec.GetInt(p, "timeoutMs", 5000);
            if (timeoutMs < 200) timeoutMs = 200;
            if (timeoutMs > 30000) timeoutMs = 30000;
            bool includeStatics = JsonCodec.GetBool(p, "includeStatics", false);

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                List<Dictionary<string, object>> dialogs = EnumerateDialogs(selfPid, titleFilter, includeStatics);
                if (dialogs.Count > 0)
                {
                    Dictionary<string, object> top = dialogs[0];
                    Dictionary<string, object> found = new Dictionary<string, object>(StringComparer.Ordinal);
                    found["summary"] = string.Format(CultureInfo.InvariantCulture,
                        "【捕获到 MFC 弹窗·进程内】「{0}」（PID={1}，{2} 个交互控件，等待 {3}ms）",
                        JsonCodec.GetStr(top, "title", ""), selfPid, JsonCodec.GetInt(top, "interactiveCount", 0), sw.ElapsedMilliseconds);
                    found["source"] = "in_process_host";
                    found["found"] = true;
                    found["elapsedMs"] = sw.ElapsedMilliseconds;
                    found["dialog"] = top;
                    found["allCount"] = dialogs.Count;
                    return found;
                }
                Thread.Sleep(100);
            }

            Dictionary<string, object> missed = new Dictionary<string, object>(StringComparer.Ordinal);
            missed["summary"] = string.Format(CultureInfo.InvariantCulture, "【等待弹窗超时】在 {0}ms 内未检测到匹配的 CAD 模态对话框。", timeoutMs);
            missed["source"] = "in_process_host";
            missed["found"] = false;
            missed["timedOut"] = true;
            missed["elapsedMs"] = sw.ElapsedMilliseconds;
            return missed;
        }

        private static Dictionary<string, object> HandleInteractDialog(Dictionary<string, object> p)
        {
            int selfPid = Process.GetCurrentProcess().Id;
            string titleFilter = JsonCodec.GetStr(p, "dialogTitle") ?? JsonCodec.GetStr(p, "titleContains");
            string dlgHwndHex = JsonCodec.GetStr(p, "dialogHwnd");
            List<object> actions = JsonCodec.GetList(p, "actions");
            if (actions == null || actions.Count == 0)
            {
                throw new ArgumentException("actions_required");
            }

            IntPtr dlgHwnd = IntPtr.Zero;
            IntPtr parsed;
            if (!string.IsNullOrEmpty(dlgHwndHex) && TryParseHwnd(dlgHwndHex, out parsed) && IsWindowVisible(parsed))
            {
                uint pidCheck;
                GetWindowThreadProcessId(parsed, out pidCheck);
                if ((int)pidCheck == selfPid)
                {
                    dlgHwnd = parsed;
                }
            }

            if (dlgHwnd == IntPtr.Zero)
            {
                List<DialogRef> matches = FindDialogHwnds(selfPid, titleFilter);
                if (matches.Count == 0)
                {
                    throw new InvalidOperationException("dialog_not_found");
                }
                dlgHwnd = matches[0].Hwnd;
            }

            string dlgTitle = GetWindowTextSafe(dlgHwnd);
            List<object> stepResults = new List<object>();
            int okCount = 0;

            for (int i = 0; i < actions.Count; i++)
            {
                Dictionary<string, object> act = actions[i] as Dictionary<string, object>;
                if (act == null) continue;
                string op = (JsonCodec.GetStr(act, "action") ?? string.Empty).Trim().ToLowerInvariant();
                int delayMs = JsonCodec.GetInt(act, "delayMs", 120);
                if (delayMs < 0) delayMs = 0;
                if (delayMs > 3000) delayMs = 3000;

                Dictionary<string, object> step = new Dictionary<string, object>(StringComparer.Ordinal);
                step["action"] = op;
                try
                {
                    if (op == "close" || op == "cancel")
                    {
                        PostMessage(dlgHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        step["ok"] = true;
                        okCount++;
                    }
                    else
                    {
                        List<RawControl> controls = InspectControlsRaw(dlgHwnd);
                        RawControl ctrl = MatchControl(controls, act, op);
                        if (ctrl == null)
                        {
                            throw new InvalidOperationException("control_not_found:" + (JsonCodec.GetStr(act, "target") ?? JsonCodec.GetStr(act, "ctrlId") ?? ""));
                        }
                        step["matchedCtrlId"] = ctrl.CtrlId;
                        step["matchedLabel"] = ctrl.Label;
                        step["matchedType"] = ctrl.ControlType;

                        if (op == "set_text")
                        {
                            string val = JsonCodec.GetStr(act, "value") ?? string.Empty;
                            SetEditTextMfc(ctrl.Hwnd, ctrl.CtrlId, val);
                            step["ok"] = true;
                            step["value"] = val;
                            step["readBack"] = GetWindowTextSafe(ctrl.Hwnd);
                            okCount++;
                        }
                        else if (op == "set_check")
                        {
                            bool wantChecked = JsonCodec.GetBool(act, "checked", true);
                            SetCheckMfc(ctrl.Hwnd, ctrl.CtrlId, ctrl.ControlType, wantChecked);
                            Thread.Sleep(80);
                            int st = (int)SendMessageTimeoutSafe(ctrl.Hwnd, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                            step["ok"] = true;
                            step["checked"] = (st == BST_CHECKED);
                            okCount++;
                        }
                        else if (op == "select_combo")
                        {
                            string wantVal = JsonCodec.GetStr(act, "value");
                            int wantIdx = JsonCodec.HasKey(act, "index") ? JsonCodec.GetInt(act, "index", -1) : -1;
                            int chosen = SelectComboMfc(ctrl.Hwnd, ctrl.CtrlId, wantVal, wantIdx);
                            step["ok"] = true;
                            step["selectedIndex"] = chosen;
                            step["readBack"] = GetWindowTextSafe(ctrl.Hwnd);
                            okCount++;
                        }
                        else if (op == "select_tab")
                        {
                            int tabIdx = JsonCodec.GetInt(act, "index", 0);
                            SelectTabMfc(ctrl.Hwnd, tabIdx);
                            step["ok"] = true;
                            step["selectedTab"] = tabIdx;
                            okCount++;
                        }
                        else if (op == "browse_file")
                        {
                            string filePath = JsonCodec.GetStr(act, "value") ?? string.Empty;
                            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("value_file_path_required");
                            string fileDlgTitle = BrowseFileViaChildDialog(selfPid, dlgHwnd, ctrl.Hwnd, ctrl.CtrlId, filePath);
                            step["ok"] = true;
                            step["value"] = filePath;
                            step["fileDialogTitle"] = fileDlgTitle;
                            okCount++;
                        }
                        else if (op == "send_key")
                        {
                            string key = (JsonCodec.GetStr(act, "value") ?? "ENTER").Trim().ToUpperInvariant();
                            int vk = key == "ESC" ? 0x1B : (key == "TAB" ? 0x09 : (key == "SPACE" ? 0x20 : 0x0D));
                            PostMessage(ctrl.Hwnd, WM_KEYDOWN, new IntPtr(vk), IntPtr.Zero);
                            PostMessage(ctrl.Hwnd, WM_KEYUP, new IntPtr(vk), IntPtr.Zero);
                            step["ok"] = true;
                            step["key"] = key;
                            okCount++;
                        }
                        else if (op == "click")
                        {
                            ClickButtonMfc(ctrl.Hwnd, ctrl.CtrlId);
                            step["ok"] = true;
                            step["posted"] = true;
                            okCount++;
                        }
                        else
                        {
                            throw new ArgumentException("unsupported_ui_action:" + op);
                        }
                    }
                }
                catch (Exception ex)
                {
                    step["ok"] = false;
                    step["error"] = ex.Message;
                }

                stepResults.Add(step);
                if (delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }
            }

            bool stillOpen = IsWindowVisible(dlgHwnd);
            List<DialogRef> remaining = FindDialogHwnds(selfPid, null);
            List<object> remList = new List<object>(remaining.Count);
            for (int r = 0; r < remaining.Count; r++)
            {
                Dictionary<string, object> rd = new Dictionary<string, object>(StringComparer.Ordinal);
                rd["hwnd"] = "0x" + remaining[r].Hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);
                rd["title"] = remaining[r].Title;
                remList.Add(rd);
            }

            Dictionary<string, object> res = new Dictionary<string, object>(StringComparer.Ordinal);
            res["summary"] = string.Format(CultureInfo.InvariantCulture,
                "【MFC 弹窗交互·进程内】对「{0}」执行 {1} 步操作（成功 {2} 步，弹窗当前{3}，当前可见弹窗数={4}）。",
                dlgTitle, actions.Count, okCount, stillOpen ? "仍打开" : "已关闭", remaining.Count);
            res["source"] = "in_process_host";
            res["dialogTitle"] = dlgTitle;
            res["dialogHwnd"] = "0x" + dlgHwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);
            res["stillOpen"] = stillOpen;
            res["openDialogsAfter"] = remList;
            res["steps"] = stepResults;
            return res;
        }

        private sealed class DialogRef
        {
            public IntPtr Hwnd;
            public int Pid;
            public string Title;
            public string ClassName;
            public RECT Rect;
        }

        private sealed class RawControl
        {
            public IntPtr Hwnd;
            public int CtrlId;
            public string ClassName = string.Empty;
            public string ControlType = string.Empty;
            public string Text = string.Empty;
            public string Label = string.Empty;
            public bool Enabled;
            public bool ReadOnly;
            public bool HasChecked;
            public bool Checked;
            public List<string> ComboItems;
            public int ComboSelectedIndex = -1;
            public int RelLeft, RelTop, RelRight, RelBottom;
        }

        private static List<DialogRef> FindDialogHwnds(int targetPid, string titleFilter)
        {
            List<DialogRef> result = new List<DialogRef>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd)) return true;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if ((int)pid != targetPid) return true;

                string cls = GetClassNameSafe(hwnd);
                bool isStdDialog = string.Equals(cls, "#32770", StringComparison.OrdinalIgnoreCase);
                bool isOwnedPopup = false;
                if (!isStdDialog)
                {
                    IntPtr owner = GetWindow(hwnd, GW_OWNER);
                    uint style = (uint)GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                    isOwnedPopup = owner != IntPtr.Zero && (style & WS_POPUP) != 0 && (style & WS_CAPTION) != 0;
                }
                if (!isStdDialog && !isOwnedPopup) return true;

                RECT rc;
                GetWindowRect(hwnd, out rc);
                if ((rc.Right - rc.Left) < 80 || (rc.Bottom - rc.Top) < 40) return true;

                string title = GetWindowTextSafe(hwnd);
                if (!string.IsNullOrEmpty(titleFilter) && title.IndexOf(titleFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }

                DialogRef dr = new DialogRef();
                dr.Hwnd = hwnd;
                dr.Pid = (int)pid;
                dr.Title = title;
                dr.ClassName = cls;
                dr.Rect = rc;
                result.Add(dr);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static List<Dictionary<string, object>> EnumerateDialogs(int targetPid, string titleFilter, bool includeStatics)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            List<DialogRef> dlgs = FindDialogHwnds(targetPid, titleFilter);
            for (int i = 0; i < dlgs.Count; i++)
            {
                DialogRef d = dlgs[i];
                List<RawControl> rawControls = InspectControlsRaw(d.Hwnd);
                List<object> ctrlArr = new List<object>();
                int interactiveCount = 0;

                for (int j = 0; j < rawControls.Count; j++)
                {
                    RawControl c = rawControls[j];
                    bool isInteractive = c.ControlType == "Edit" || c.ControlType == "Button" ||
                                         c.ControlType == "CheckBox" || c.ControlType == "RadioButton" ||
                                         c.ControlType == "ComboBox" || c.ControlType == "TabControl" ||
                                         c.ControlType == "ListView";
                    if (isInteractive) interactiveCount++;
                    if (!isInteractive && !includeStatics) continue;

                    Dictionary<string, object> cObj = new Dictionary<string, object>(StringComparer.Ordinal);
                    cObj["hwnd"] = "0x" + c.Hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);
                    cObj["ctrlId"] = c.CtrlId;
                    cObj["type"] = c.ControlType;
                    cObj["className"] = c.ClassName;
                    cObj["label"] = c.Label;
                    cObj["text"] = c.Text;
                    cObj["enabled"] = c.Enabled;
                    cObj["rect"] = new List<object> { c.RelLeft, c.RelTop, c.RelRight, c.RelBottom };
                    if (c.HasChecked) cObj["checked"] = c.Checked;
                    if (c.ReadOnly) cObj["readOnly"] = true;
                    if (c.ComboItems != null)
                    {
                        List<object> its = new List<object>(c.ComboItems.Count);
                        for (int k = 0; k < c.ComboItems.Count; k++) its.Add(c.ComboItems[k]);
                        cObj["items"] = its;
                        cObj["selectedIndex"] = c.ComboSelectedIndex;
                    }
                    ctrlArr.Add(cObj);
                }

                Dictionary<string, object> dObj = new Dictionary<string, object>(StringComparer.Ordinal);
                dObj["hwnd"] = "0x" + d.Hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);
                dObj["pid"] = d.Pid;
                dObj["title"] = d.Title;
                dObj["className"] = d.ClassName;
                dObj["rect"] = new List<object> { d.Rect.Left, d.Rect.Top, d.Rect.Right, d.Rect.Bottom };
                dObj["interactiveCount"] = interactiveCount;
                dObj["controls"] = ctrlArr;
                list.Add(dObj);
            }
            return list;
        }

        private static List<RawControl> InspectControlsRaw(IntPtr dlgHwnd)
        {
            RECT dlgRc;
            GetWindowRect(dlgHwnd, out dlgRc);
            List<RawControl> controls = new List<RawControl>();

            EnumChildWindows(dlgHwnd, delegate(IntPtr child, IntPtr lParam)
            {
                if (!IsWindowVisible(child)) return true;
                string cls = GetClassNameSafe(child);
                uint style = (uint)GetWindowLongPtr(child, GWL_STYLE).ToInt64();
                int ctrlId = GetDlgCtrlID(child);
                string text = GetWindowTextSafe(child);
                RECT rc;
                GetWindowRect(child, out rc);

                RawControl item = new RawControl();
                item.Hwnd = child;
                item.CtrlId = ctrlId;
                item.ClassName = cls;
                item.Text = text;
                item.Enabled = IsWindowEnabled(child);
                item.RelLeft = rc.Left - dlgRc.Left;
                item.RelTop = rc.Top - dlgRc.Top;
                item.RelRight = rc.Right - dlgRc.Left;
                item.RelBottom = rc.Bottom - dlgRc.Top;

                if (string.Equals(cls, "Button", StringComparison.OrdinalIgnoreCase))
                {
                    uint bsType = style & BS_TYPEMASK;
                    if (bsType == BS_GROUPBOX)
                    {
                        item.ControlType = "GroupBox";
                        item.Label = CleanMnemonic(text);
                    }
                    else if (bsType == BS_CHECKBOX || bsType == BS_AUTOCHECKBOX || bsType == BS_3STATE || bsType == BS_AUTO3STATE)
                    {
                        item.ControlType = "CheckBox";
                        item.Label = CleanMnemonic(text);
                        item.HasChecked = true;
                        item.Checked = ((int)SendMessageTimeoutSafe(child, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == BST_CHECKED);
                    }
                    else if (bsType == BS_RADIOBUTTON || bsType == BS_AUTORADIOBUTTON)
                    {
                        item.ControlType = "RadioButton";
                        item.Label = CleanMnemonic(text);
                        item.HasChecked = true;
                        item.Checked = ((int)SendMessageTimeoutSafe(child, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == BST_CHECKED);
                    }
                    else
                    {
                        item.ControlType = "Button";
                        item.Label = CleanMnemonic(text);
                    }
                }
                else if (string.Equals(cls, "Edit", StringComparison.OrdinalIgnoreCase) || cls.IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.ControlType = "Edit";
                    item.ReadOnly = (style & ES_READONLY) != 0;
                }
                else if (string.Equals(cls, "ComboBox", StringComparison.OrdinalIgnoreCase))
                {
                    item.ControlType = "ComboBox";
                    int selIdx;
                    item.ComboItems = ReadComboItems(child, out selIdx);
                    item.ComboSelectedIndex = selIdx;
                }
                else if (string.Equals(cls, "Static", StringComparison.OrdinalIgnoreCase))
                {
                    item.ControlType = "Static";
                    item.Label = CleanMnemonic(text);
                }
                else if (string.Equals(cls, "SysTabControl32", StringComparison.OrdinalIgnoreCase))
                {
                    item.ControlType = "TabControl";
                }
                else if (string.Equals(cls, "SysListView32", StringComparison.OrdinalIgnoreCase))
                {
                    item.ControlType = "ListView";
                }
                else
                {
                    item.ControlType = cls;
                    item.Label = CleanMnemonic(text);
                }

                controls.Add(item);
                return true;
            }, IntPtr.Zero);

            List<RawControl> statics = new List<RawControl>();
            List<RawControl> groups = new List<RawControl>();
            for (int i = 0; i < controls.Count; i++)
            {
                if (controls[i].ControlType == "Static" && !string.IsNullOrWhiteSpace(controls[i].Label)) statics.Add(controls[i]);
                else if (controls[i].ControlType == "GroupBox" && !string.IsNullOrWhiteSpace(controls[i].Label)) groups.Add(controls[i]);
            }

            for (int i = 0; i < controls.Count; i++)
            {
                RawControl c = controls[i];
                if (c.ControlType != "Edit" && c.ControlType != "ComboBox") continue;
                int midY = (c.RelTop + c.RelBottom) / 2;
                int h = Math.Max(16, c.RelBottom - c.RelTop);

                RawControl bestStatic = null;
                double bestDist = double.MaxValue;
                for (int s = 0; s < statics.Count; s++)
                {
                    RawControl st = statics[s];
                    int stMidY = (st.RelTop + st.RelBottom) / 2;
                    int dy = Math.Abs(stMidY - midY);
                    int gapX = c.RelLeft - st.RelRight;
                    if (dy <= Math.Max(18, h) && gapX >= -14 && gapX <= 260)
                    {
                        double d = Math.Max(0, gapX) + dy * 2.5;
                        if (d < bestDist) { bestDist = d; bestStatic = st; }
                    }
                }
                if (bestStatic == null)
                {
                    for (int s = 0; s < statics.Count; s++)
                    {
                        RawControl st = statics[s];
                        int gapY = c.RelTop - st.RelBottom;
                        bool overlapX = st.RelLeft <= c.RelRight && st.RelRight >= c.RelLeft - 20;
                        if (overlapX && gapY >= -6 && gapY <= 36)
                        {
                            double d = Math.Max(0, gapY) + Math.Abs(st.RelLeft - c.RelLeft) * 0.5;
                            if (d < bestDist) { bestDist = d; bestStatic = st; }
                        }
                    }
                }

                string lbl = bestStatic == null ? string.Empty : bestStatic.Label;
                RawControl ownerGroup = null;
                long bestArea = long.MaxValue;
                for (int g = 0; g < groups.Count; g++)
                {
                    RawControl gp = groups[g];
                    if (c.RelLeft >= gp.RelLeft && c.RelRight <= gp.RelRight && c.RelTop >= gp.RelTop && c.RelBottom <= gp.RelBottom)
                    {
                        long area = (long)(gp.RelRight - gp.RelLeft) * (long)(gp.RelBottom - gp.RelTop);
                        if (area < bestArea) { bestArea = area; ownerGroup = gp; }
                    }
                }
                if (ownerGroup != null && !string.IsNullOrEmpty(ownerGroup.Label))
                {
                    lbl = string.IsNullOrEmpty(lbl) ? ownerGroup.Label : (ownerGroup.Label + " > " + lbl);
                }
                c.Label = string.IsNullOrEmpty(lbl) ? (c.ControlType + "#" + c.CtrlId) : lbl;
            }

            return controls;
        }

        private static RawControl MatchControl(List<RawControl> controls, Dictionary<string, object> act, string op)
        {
            string hwndHex = JsonCodec.GetStr(act, "hwnd");
            IntPtr hw;
            if (!string.IsNullOrEmpty(hwndHex) && TryParseHwnd(hwndHex, out hw))
            {
                for (int i = 0; i < controls.Count; i++)
                {
                    if (controls[i].Hwnd == hw) return controls[i];
                }
            }

            if (JsonCodec.HasKey(act, "ctrlId"))
            {
                int id = JsonCodec.GetInt(act, "ctrlId", int.MinValue);
                if (id != int.MinValue)
                {
                    for (int i = 0; i < controls.Count; i++)
                    {
                        if (controls[i].CtrlId == id && controls[i].ControlType != "Static" && controls[i].ControlType != "GroupBox")
                            return controls[i];
                    }
                }
            }

            string target = (JsonCodec.GetStr(act, "target") ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(target)) return null;

            RawControl partial = null;
            for (int i = 0; i < controls.Count; i++)
            {
                RawControl c = controls[i];
                if (c.ControlType == "Static" || c.ControlType == "GroupBox") continue;
                if (op == "set_text" && c.ControlType != "Edit" && c.ControlType != "ComboBox") continue;
                if (op == "set_check" && c.ControlType != "CheckBox" && c.ControlType != "RadioButton") continue;
                if (op == "select_combo" && c.ControlType != "ComboBox") continue;
                if (op == "select_tab" && c.ControlType != "TabControl") continue;
                if ((op == "click" || op == "browse_file") && c.ControlType != "Button" && c.ControlType != "CheckBox" && c.ControlType != "RadioButton") continue;

                string cleanText = CleanMnemonic(c.Text);
                if (string.Equals(c.Label, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cleanText, target, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
                if (partial == null && (c.Label.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cleanText.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    partial = c;
                }
            }
            return partial;
        }

        private static void SetEditTextMfc(IntPtr hEdit, int ctrlId, string value)
        {
            IntPtr unused;
            SendMessageTimeoutString(hEdit, WM_SETTEXT, IntPtr.Zero, value, SMTO_ABORTIFHUNG, 2000, out unused);
            IntPtr parent = GetParent(hEdit);
            if (parent != IntPtr.Zero)
            {
                SendMessageTimeoutSafe(parent, WM_COMMAND, MakeWParam(ctrlId, EN_UPDATE), hEdit);
                SendMessageTimeoutSafe(parent, WM_COMMAND, MakeWParam(ctrlId, EN_CHANGE), hEdit);
            }
        }

        private static void ClickButtonMfc(IntPtr hBtn, int ctrlId)
        {
            IntPtr parent = GetParent(hBtn);
            if (parent != IntPtr.Zero && ctrlId > 0)
            {
                PostMessage(parent, WM_COMMAND, MakeWParam(ctrlId, BN_CLICKED), hBtn);
            }
            else
            {
                PostMessage(hBtn, WM_LBUTTONDOWN, new IntPtr(1), IntPtr.Zero);
                PostMessage(hBtn, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                PostMessage(hBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static void SetCheckMfc(IntPtr hBtn, int ctrlId, string ctrlType, bool wantChecked)
        {
            int cur = (int)SendMessageTimeoutSafe(hBtn, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
            bool isChecked = (cur == BST_CHECKED);
            if (ctrlType == "RadioButton")
            {
                if (wantChecked && !isChecked)
                {
                    SendMessageTimeoutSafe(hBtn, BM_SETCHECK, new IntPtr(BST_CHECKED), IntPtr.Zero);
                    ClickButtonMfc(hBtn, ctrlId);
                }
                return;
            }
            if (isChecked != wantChecked)
            {
                SendMessageTimeoutSafe(hBtn, BM_SETCHECK, new IntPtr(wantChecked ? BST_CHECKED : BST_UNCHECKED), IntPtr.Zero);
                IntPtr parent = GetParent(hBtn);
                if (parent != IntPtr.Zero) PostMessage(parent, WM_COMMAND, MakeWParam(ctrlId, BN_CLICKED), hBtn);
            }
        }

        private static void SelectTabMfc(IntPtr hTab, int tabIndex)
        {
            if (tabIndex < 0) tabIndex = 0;
            SendMessageTimeoutSafe(hTab, TCM_SETCURFOCUS, new IntPtr(tabIndex), IntPtr.Zero);
            SendMessageTimeoutSafe(hTab, TCM_SETCURSEL, new IntPtr(tabIndex), IntPtr.Zero);
        }

        /// <summary>
        /// CASS/飞时达文件浏览闭环：点击 [...] 按钮 → 等待二级文件选择框(#32770) → 填入路径 → 点击打开/保存 → 自动确认覆盖。
        /// </summary>
        private static string BrowseFileViaChildDialog(int selfPid, IntPtr ownerDlg, IntPtr browseBtn, int browseCtrlId, string filePath)
        {
            ClickButtonMfc(browseBtn, browseCtrlId);
            IntPtr fileDlg = IntPtr.Zero;
            string fileDlgTitle = string.Empty;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                List<DialogRef> dlgs = FindDialogHwnds(selfPid, null);
                for (int i = 0; i < dlgs.Count; i++)
                {
                    if (dlgs[i].Hwnd != ownerDlg)
                    {
                        fileDlg = dlgs[i].Hwnd;
                        fileDlgTitle = dlgs[i].Title;
                        break;
                    }
                }
                if (fileDlg != IntPtr.Zero) break;
                Thread.Sleep(80);
            }
            if (fileDlg == IntPtr.Zero)
            {
                throw new InvalidOperationException("file_dialog_timeout_after_browse_click");
            }

            Thread.Sleep(120);
            List<RawControl> ctrls = InspectControlsRaw(fileDlg);
            RawControl fileInput = null;
            for (int i = 0; i < ctrls.Count; i++)
            {
                if (ctrls[i].CtrlId == 1148 && (ctrls[i].ControlType == "Edit" || ctrls[i].ControlType == "ComboBox" || ctrls[i].ClassName.IndexOf("ComboBox", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    fileInput = ctrls[i];
                    if (ctrls[i].ControlType == "Edit") break;
                }
            }
            if (fileInput == null)
            {
                for (int i = 0; i < ctrls.Count; i++)
                {
                    if (ctrls[i].ControlType == "Edit") { fileInput = ctrls[i]; break; }
                }
            }
            if (fileInput == null)
            {
                throw new InvalidOperationException("file_dialog_edit_not_found:" + fileDlgTitle);
            }

            SetEditTextMfc(fileInput.Hwnd, fileInput.CtrlId, filePath);
            Thread.Sleep(100);

            RawControl okBtn = null;
            for (int i = 0; i < ctrls.Count; i++)
            {
                if (ctrls[i].CtrlId == 1 && ctrls[i].ControlType == "Button") { okBtn = ctrls[i]; break; }
            }
            if (okBtn != null) ClickButtonMfc(okBtn.Hwnd, okBtn.CtrlId);
            else PostMessage(fileDlg, WM_COMMAND, MakeWParam(1, BN_CLICKED), IntPtr.Zero);

            // 等待文件框关闭，若保存文件弹出「确认另存为/是否替换」则自动点「是(6)」
            Stopwatch closeSw = Stopwatch.StartNew();
            while (closeSw.ElapsedMilliseconds < 4000 && IsWindowVisible(fileDlg))
            {
                Thread.Sleep(100);
                List<DialogRef> subDlgs = FindDialogHwnds(selfPid, null);
                for (int i = 0; i < subDlgs.Count; i++)
                {
                    if (subDlgs[i].Hwnd != ownerDlg && subDlgs[i].Hwnd != fileDlg)
                    {
                        string t = subDlgs[i].Title ?? string.Empty;
                        if (t.IndexOf("确认", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("替换", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Confirm", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            PostMessage(subDlgs[i].Hwnd, WM_COMMAND, MakeWParam(6, BN_CLICKED), IntPtr.Zero);
                        }
                    }
                }
            }
            return fileDlgTitle;
        }

        private static int SelectComboMfc(IntPtr hCombo, int ctrlId, string value, int index)
        {
            int selDummy;
            List<string> items = ReadComboItems(hCombo, out selDummy);
            int chosen = -1;
            if (index >= 0 && index < items.Count)
            {
                chosen = index;
            }
            else if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase)) { chosen = i; break; }
                }
                if (chosen < 0)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i].IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) { chosen = i; break; }
                    }
                }
            }
            if (chosen < 0) throw new InvalidOperationException("combo_item_not_found");
            SendMessageTimeoutSafe(hCombo, CB_SETCURSEL, new IntPtr(chosen), IntPtr.Zero);
            IntPtr parent = GetParent(hCombo);
            if (parent != IntPtr.Zero) PostMessage(parent, WM_COMMAND, MakeWParam(ctrlId, CBN_SELCHANGE), hCombo);
            return chosen;
        }

        private static List<string> ReadComboItems(IntPtr hCombo, out int selectedIndex)
        {
            List<string> list = new List<string>();
            selectedIndex = (int)SendMessageTimeoutSafe(hCombo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
            int count = (int)SendMessageTimeoutSafe(hCombo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0 || count > 500) return list;
            for (int i = 0; i < count; i++)
            {
                int len = (int)SendMessageTimeoutSafe(hCombo, CB_GETLBTEXTLEN, new IntPtr(i), IntPtr.Zero);
                if (len < 0 || len > 1024) continue;
                StringBuilder sb = new StringBuilder(len + 2);
                IntPtr unused;
                SendMessageTimeoutSb(hCombo, CB_GETLBTEXT, new IntPtr(i), sb, SMTO_ABORTIFHUNG, 1000, out unused);
                list.Add(sb.ToString());
            }
            return list;
        }

        private static string GetWindowTextSafe(IntPtr hwnd)
        {
            int len = (int)SendMessageTimeoutSafe(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (len <= 0 || len > 4096) return string.Empty;
            StringBuilder sb = new StringBuilder(len + 2);
            IntPtr unused;
            SendMessageTimeoutSb(hwnd, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG, 1500, out unused);
            return sb.ToString().Trim();
        }

        private static string GetClassNameSafe(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(128);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string CleanMnemonic(string s)
        {
            return (s ?? string.Empty).Replace("&", string.Empty).Trim().TrimEnd(':', '：');
        }

        private static IntPtr MakeWParam(int low, int high)
        {
            return new IntPtr((high << 16) | (low & 0xFFFF));
        }

        private static bool TryParseHwnd(string hex, out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            string clean = (hex ?? string.Empty).Trim();
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(2);
            long v;
            if (long.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
            {
                hwnd = new IntPtr(v);
                return true;
            }
            return false;
        }

        private static IntPtr SendMessageTimeoutSafe(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            IntPtr res;
            SendMessageTimeout(hwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 2000, out res);
            return res;
        }

        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint TCM_SETCURSEL = 0x130C;
        private const uint TCM_SETCURFOCUS = 0x1330;
        private const uint BM_GETCHECK = 0x00F0;
        private const uint BM_SETCHECK = 0x00F1;
        private const uint BM_CLICK = 0x00F5;
        private const uint CB_GETCOUNT = 0x0146;
        private const uint CB_GETCURSEL = 0x0147;
        private const uint CB_GETLBTEXT = 0x0148;
        private const uint CB_GETLBTEXTLEN = 0x0149;
        private const uint CB_SETCURSEL = 0x014E;
        private const int BST_UNCHECKED = 0;
        private const int BST_CHECKED = 1;
        private const int BN_CLICKED = 0;
        private const int CBN_SELCHANGE = 1;
        private const int EN_CHANGE = 0x0300;
        private const int EN_UPDATE = 0x0400;
        private const int GWL_STYLE = -16;
        private const uint GW_OWNER = 4;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint ES_READONLY = 0x0800;
        private const uint BS_TYPEMASK = 0x000F;
        private const uint BS_CHECKBOX = 0x0002;
        private const uint BS_AUTOCHECKBOX = 0x0003;
        private const uint BS_RADIOBUTTON = 0x0004;
        private const uint BS_3STATE = 0x0005;
        private const uint BS_AUTO3STATE = 0x0006;
        private const uint BS_GROUPBOX = 0x0007;
        private const uint BS_AUTORADIOBUTTON = 0x0009;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutString(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutSb(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    }
}
