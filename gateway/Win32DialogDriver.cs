using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace EngineeringPlatform.Gateway;

/// <summary>
/// 进程外 Win32 / MFC 模态对话框驱动（v3.1.1）：
/// - 专为南方 CASS 10 / 飞时达 FastTFT / 天正 / 鸿业等 MFC (#32770) 模态对话框设计；
/// - 运行在 ep-mcp.exe 独立进程中，AutoCAD 主线程被 CDialog::DoModal() 挂起时依然毫秒级读写控件；
/// - 完整读取 Edit / Button / CheckBox / RadioButton / ComboBox / GroupBox / Static / Tab / ListView，
///   按空间几何自动将左侧/上方 Static 文字配对为输入框中文标签；
/// - 写值自动补发 MFC WM_COMMAND (EN_CHANGE / BN_CLICKED / CBN_SELCHANGE)，点按钮一律用异步 PostMessage 防死锁。
/// </summary>
internal sealed class Win32DialogDriver
{
    private static readonly string[] CadProcessNames = ["acad", "zwcad", "gcad", "gstarcad"];

    public JsonObject ListDialogs(JsonObject args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new JsonObject { ["summary"] = "仅 Windows 支持 MFC 对话框探测。", ["dialogs"] = new JsonArray() };
        }

        var targetPids = ResolveTargetPids(args);
        string? titleFilter = args["titleContains"]?.GetValue<string>();
        bool includeStatics = args["includeStatics"]?.GetValue<bool>() == true;

        var dialogs = EnumerateDialogs(targetPids, titleFilter, includeStatics);
        var arr = new JsonArray();
        foreach (var d in dialogs) arr.Add(d);

        string summary = dialogs.Count == 0
            ? $"【MFC 弹窗探测】目标 CAD（PID={string.Join(",", targetPids)}）当前无可见模态对话框。"
            : $"【MFC 弹窗探测】发现 {dialogs.Count} 个可见弹窗：{string.Join(" / ", dialogs.Select(d => $"「{d["title"]}」({d["interactiveCount"]}个交互控件)"))}";

        return new JsonObject
        {
            ["summary"] = summary,
            ["targetPids"] = new JsonArray(targetPids.Select(p => JsonValue.Create(p)).ToArray()),
            ["count"] = dialogs.Count,
            ["dialogs"] = arr
        };
    }

    public async Task<JsonObject> WaitDialogAsync(JsonObject args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new JsonObject { ["found"] = false, ["summary"] = "仅 Windows 支持 MFC 对话框等待。" };
        }

        var targetPids = ResolveTargetPids(args);
        string? titleFilter = args["titleContains"]?.GetValue<string>();
        int timeoutMs = Math.Clamp(args["timeoutMs"]?.GetValue<int>() ?? 5000, 200, 30000);
        bool includeStatics = args["includeStatics"]?.GetValue<bool>() == true;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            var dialogs = EnumerateDialogs(targetPids, titleFilter, includeStatics);
            if (dialogs.Count > 0)
            {
                var top = dialogs[0];
                return new JsonObject
                {
                    ["summary"] = $"【捕获到 MFC 弹窗】「{top["title"]}」（PID={top["pid"]}，{top["interactiveCount"]} 个交互控件，等待 {sw.ElapsedMilliseconds}ms）",
                    ["found"] = true,
                    ["elapsedMs"] = sw.ElapsedMilliseconds,
                    ["dialog"] = top,
                    ["allCount"] = dialogs.Count
                };
            }
            await Task.Delay(100, ct);
        }

        return new JsonObject
        {
            ["summary"] = $"【等待弹窗超时】在 {timeoutMs}ms 内未检测到匹配的 CAD 模态对话框。",
            ["found"] = false,
            ["timedOut"] = true,
            ["elapsedMs"] = sw.ElapsedMilliseconds
        };
    }

    public async Task<JsonObject> InteractDialogAsync(JsonObject args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("windows_only");
        }

        var targetPids = ResolveTargetPids(args);
        string? titleFilter = args["dialogTitle"]?.GetValue<string>() ?? args["titleContains"]?.GetValue<string>();
        string? dlgHwndHex = args["dialogHwnd"]?.GetValue<string>();
        var actions = args["actions"] as JsonArray;
        if (actions == null || actions.Count == 0)
        {
            throw new ArgumentException("actions_required");
        }

        IntPtr dlgHwnd = IntPtr.Zero;
        if (!string.IsNullOrEmpty(dlgHwndHex) && TryParseHwnd(dlgHwndHex, out var parsedHwnd))
        {
            if (IsWindowVisible(parsedHwnd))
            {
                GetWindowThreadProcessId(parsedHwnd, out uint p);
                if (targetPids.Count == 0 || targetPids.Contains((int)p))
                {
                    dlgHwnd = parsedHwnd;
                }
            }
        }

        if (dlgHwnd == IntPtr.Zero)
        {
            var matches = FindDialogHwnds(targetPids, titleFilter);
            if (matches.Count == 0)
            {
                throw new InvalidOperationException("dialog_not_found: 未找到匹配的可见 CAD 模态对话框，请先调 ui_list_dialogs 确认。");
            }
            dlgHwnd = matches[0].Hwnd;
        }

        string dlgTitle = GetWindowTextSafe(dlgHwnd);
        var stepResults = new JsonArray();
        int okCount = 0;

        foreach (var actNode in actions)
        {
            if (actNode is not JsonObject act) continue;
            string op = (act["action"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            int delayMs = Math.Clamp(act["delayMs"]?.GetValue<int>() ?? 120, 0, 3000);

            var step = new JsonObject { ["action"] = op };
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
                    // 每步重新扫描控件，以反映上一步点击/单选后 MFC 动态启用或切换的控件状态
                    var controls = InspectControlsRaw(dlgHwnd);
                    var ctrl = MatchControl(controls, act);
                    if (ctrl == null)
                    {
                        throw new InvalidOperationException($"control_not_found: target={act["target"]} ctrlId={act["ctrlId"]}");
                    }

                    step["matchedCtrlId"] = ctrl.CtrlId;
                    step["matchedLabel"] = ctrl.Label;
                    step["matchedType"] = ctrl.ControlType;

                    switch (op)
                    {
                        case "set_text":
                        {
                            string val = act["value"]?.GetValue<string>() ?? "";
                            SetEditTextMfc(ctrl.Hwnd, ctrl.CtrlId, val);
                            string readBack = GetWindowTextSafe(ctrl.Hwnd);
                            step["ok"] = true;
                            step["value"] = val;
                            step["readBack"] = readBack;
                            okCount++;
                            break;
                        }
                        case "set_check":
                        {
                            bool wantChecked = act["checked"]?.GetValue<bool>() ?? true;
                            SetCheckMfc(ctrl.Hwnd, ctrl.CtrlId, ctrl.ControlType, wantChecked);
                            await Task.Delay(80, ct);
                            int state = (int)SendMessageTimeoutSafe(ctrl.Hwnd, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                            step["ok"] = true;
                            step["checked"] = state == BST_CHECKED;
                            okCount++;
                            break;
                        }
                        case "select_combo":
                        {
                            string? wantText = act["value"]?.GetValue<string>();
                            int? wantIdx = act["index"]?.GetValue<int>();
                            int chosen = SelectComboMfc(ctrl.Hwnd, ctrl.CtrlId, wantText, wantIdx);
                            step["ok"] = true;
                            step["selectedIndex"] = chosen;
                            step["readBack"] = GetWindowTextSafe(ctrl.Hwnd);
                            okCount++;
                            break;
                        }
                        case "select_tab":
                        {
                            int tabIdx = Math.Max(0, act["index"]?.GetValue<int>() ?? 0);
                            SendMessageTimeoutSafe(ctrl.Hwnd, TCM_SETCURFOCUS, new IntPtr(tabIdx), IntPtr.Zero);
                            SendMessageTimeoutSafe(ctrl.Hwnd, TCM_SETCURSEL, new IntPtr(tabIdx), IntPtr.Zero);
                            step["ok"] = true;
                            step["selectedTab"] = tabIdx;
                            okCount++;
                            break;
                        }
                        case "browse_file":
                        {
                            string filePath = act["value"]?.GetValue<string>() ?? "";
                            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("value_file_path_required");
                            string fileDlgTitle = await BrowseFileViaChildDialogAsync(targetPids, dlgHwnd, ctrl.Hwnd, ctrl.CtrlId, filePath, ct);
                            step["ok"] = true;
                            step["value"] = filePath;
                            step["fileDialogTitle"] = fileDlgTitle;
                            okCount++;
                            break;
                        }
                        case "send_key":
                        {
                            string key = (act["value"]?.GetValue<string>() ?? "ENTER").Trim().ToUpperInvariant();
                            int vk = key switch { "ESC" => 0x1B, "TAB" => 0x09, "SPACE" => 0x20, _ => 0x0D };
                            PostMessage(ctrl.Hwnd, WM_KEYDOWN, new IntPtr(vk), IntPtr.Zero);
                            PostMessage(ctrl.Hwnd, WM_KEYUP, new IntPtr(vk), IntPtr.Zero);
                            step["ok"] = true;
                            step["key"] = key;
                            okCount++;
                            break;
                        }
                        case "click":
                        {
                            ClickButtonMfc(ctrl.Hwnd, ctrl.CtrlId);
                            step["ok"] = true;
                            step["posted"] = true;
                            okCount++;
                            break;
                        }
                        default:
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
                await Task.Delay(delayMs, ct);
            }
        }

        bool stillOpen = IsWindowVisible(dlgHwnd);
        var remDlgs = FindDialogHwnds(targetPids, null);
        var remArr = new JsonArray();
        foreach (var rd in remDlgs)
        {
            remArr.Add(new JsonObject { ["hwnd"] = $"0x{rd.Hwnd.ToInt64():X}", ["title"] = rd.Title });
        }
        return new JsonObject
        {
            ["summary"] = $"【MFC 弹窗交互】对「{dlgTitle}」执行 {actions.Count} 步操作（成功 {okCount} 步，弹窗当前{(stillOpen ? "仍打开" : "已关闭")}，当前可见弹窗数={remDlgs.Count}）。",
            ["dialogTitle"] = dlgTitle,
            ["dialogHwnd"] = $"0x{dlgHwnd.ToInt64():X}",
            ["stillOpen"] = stillOpen,
            ["openDialogsAfter"] = remArr,
            ["steps"] = stepResults
        };
    }

    // ---------------- Win32 控件枚举与空间标签配对 ----------------

    private static List<int> ResolveTargetPids(JsonObject args)
    {
        var pids = new List<int>();
        if (args["targetPid"] != null)
        {
            try
            {
                int p = args["targetPid"]!.GetValue<int>();
                if (p > 0) pids.Add(p);
            }
            catch { }
        }
        if (pids.Count > 0) return pids;

        foreach (string name in CadProcessNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    if (!proc.HasExited) pids.Add(proc.Id);
                }
            }
            catch { }
        }
        return pids.Distinct().ToList();
    }

    private sealed record DialogHwndInfo(IntPtr Hwnd, int Pid, string Title, string ClassName, RECT Rect);

    private static List<DialogHwndInfo> FindDialogHwnds(List<int> targetPids, string? titleFilter)
    {
        var result = new List<DialogHwndInfo>();
        if (targetPids.Count == 0) return result;
        var pidSet = new HashSet<int>(targetPids);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pidSet.Contains((int)pid)) return true;

            string cls = GetClassNameSafe(hwnd);
            bool isStdDialog = cls.Equals("#32770", StringComparison.OrdinalIgnoreCase);
            bool isOwnedPopup = false;
            if (!isStdDialog)
            {
                IntPtr owner = GetWindow(hwnd, GW_OWNER);
                uint style = (uint)GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                isOwnedPopup = owner != IntPtr.Zero && (style & WS_POPUP) != 0 && (style & WS_CAPTION) != 0;
            }
            if (!isStdDialog && !isOwnedPopup) return true;

            GetWindowRect(hwnd, out RECT rc);
            if ((rc.Right - rc.Left) < 80 || (rc.Bottom - rc.Top) < 40) return true;

            string title = GetWindowTextSafe(hwnd);
            if (!string.IsNullOrEmpty(titleFilter) &&
                !title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            result.Add(new DialogHwndInfo(hwnd, (int)pid, title, cls, rc));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static List<JsonObject> EnumerateDialogs(List<int> targetPids, string? titleFilter, bool includeStatics)
    {
        var list = new List<JsonObject>();
        foreach (var d in FindDialogHwnds(targetPids, titleFilter))
        {
            var rawControls = InspectControlsRaw(d.Hwnd);
            var ctrlArr = new JsonArray();
            int interactiveCount = 0;

            foreach (var c in rawControls)
            {
                bool isInteractive = c.ControlType is "Edit" or "Button" or "CheckBox" or "RadioButton" or "ComboBox" or "TabControl" or "ListView";
                if (isInteractive) interactiveCount++;
                if (!isInteractive && !includeStatics) continue;

                var cObj = new JsonObject
                {
                    ["hwnd"] = $"0x{c.Hwnd.ToInt64():X}",
                    ["ctrlId"] = c.CtrlId,
                    ["type"] = c.ControlType,
                    ["className"] = c.ClassName,
                    ["label"] = c.Label,
                    ["text"] = c.Text,
                    ["enabled"] = c.Enabled,
                    ["rect"] = new JsonArray(c.RelLeft, c.RelTop, c.RelRight, c.RelBottom)
                };
                if (c.Checked != null) cObj["checked"] = c.Checked.Value;
                if (c.ReadOnly) cObj["readOnly"] = true;
                if (c.ComboItems != null)
                {
                    var items = new JsonArray();
                    foreach (var it in c.ComboItems) items.Add(it);
                    cObj["items"] = items;
                    cObj["selectedIndex"] = c.ComboSelectedIndex;
                }
                ctrlArr.Add(cObj);
            }

            list.Add(new JsonObject
            {
                ["hwnd"] = $"0x{d.Hwnd.ToInt64():X}",
                ["pid"] = d.Pid,
                ["title"] = d.Title,
                ["className"] = d.ClassName,
                ["rect"] = new JsonArray(d.Rect.Left, d.Rect.Top, d.Rect.Right, d.Rect.Bottom),
                ["interactiveCount"] = interactiveCount,
                ["controls"] = ctrlArr
            });
        }
        return list;
    }

    private sealed class RawControl
    {
        public IntPtr Hwnd;
        public int CtrlId;
        public string ClassName = "";
        public string ControlType = "";
        public string Text = "";
        public string Label = "";
        public bool Enabled;
        public bool ReadOnly;
        public bool? Checked;
        public List<string>? ComboItems;
        public int ComboSelectedIndex = -1;
        public int RelLeft, RelTop, RelRight, RelBottom;
    }

    private static List<RawControl> InspectControlsRaw(IntPtr dlgHwnd)
    {
        GetWindowRect(dlgHwnd, out RECT dlgRc);
        var controls = new List<RawControl>();

        EnumChildWindows(dlgHwnd, (child, _) =>
        {
            if (!IsWindowVisible(child)) return true;
            string cls = GetClassNameSafe(child);
            uint style = (uint)GetWindowLongPtr(child, GWL_STYLE).ToInt64();
            int ctrlId = GetDlgCtrlID(child);
            string text = GetWindowTextSafe(child);
            GetWindowRect(child, out RECT rc);

            var item = new RawControl
            {
                Hwnd = child,
                CtrlId = ctrlId,
                ClassName = cls,
                Text = text,
                Enabled = IsWindowEnabled(child),
                RelLeft = rc.Left - dlgRc.Left,
                RelTop = rc.Top - dlgRc.Top,
                RelRight = rc.Right - dlgRc.Left,
                RelBottom = rc.Bottom - dlgRc.Top
            };

            if (cls.Equals("Button", StringComparison.OrdinalIgnoreCase))
            {
                uint bsType = style & BS_TYPEMASK;
                if (bsType == BS_GROUPBOX)
                {
                    item.ControlType = "GroupBox";
                    item.Label = CleanMnemonic(text);
                }
                else if (bsType is BS_CHECKBOX or BS_AUTOCHECKBOX or BS_3STATE or BS_AUTO3STATE)
                {
                    item.ControlType = "CheckBox";
                    item.Label = CleanMnemonic(text);
                    int chk = (int)SendMessageTimeoutSafe(child, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                    item.Checked = chk == BST_CHECKED;
                }
                else if (bsType is BS_RADIOBUTTON or BS_AUTORADIOBUTTON)
                {
                    item.ControlType = "RadioButton";
                    item.Label = CleanMnemonic(text);
                    int chk = (int)SendMessageTimeoutSafe(child, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                    item.Checked = chk == BST_CHECKED;
                }
                else
                {
                    item.ControlType = "Button";
                    item.Label = CleanMnemonic(text);
                }
            }
            else if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) || cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase))
            {
                item.ControlType = "Edit";
                item.ReadOnly = (style & ES_READONLY) != 0;
            }
            else if (cls.Equals("ComboBox", StringComparison.OrdinalIgnoreCase))
            {
                item.ControlType = "ComboBox";
                item.ComboItems = ReadComboItems(child, out int selIdx);
                item.ComboSelectedIndex = selIdx;
            }
            else if (cls.Equals("Static", StringComparison.OrdinalIgnoreCase))
            {
                item.ControlType = "Static";
                item.Label = CleanMnemonic(text);
            }
            else if (cls.Equals("SysTabControl32", StringComparison.OrdinalIgnoreCase))
            {
                item.ControlType = "TabControl";
            }
            else if (cls.Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
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

        // 空间几何配对：为每个 Edit / ComboBox 寻找紧邻左侧或上方的 Static 标签与所属 GroupBox
        var statics = controls.Where(c => c.ControlType == "Static" && !string.IsNullOrWhiteSpace(c.Label)).ToList();
        var groups = controls.Where(c => c.ControlType == "GroupBox" && !string.IsNullOrWhiteSpace(c.Label)).ToList();

        foreach (var c in controls)
        {
            if (c.ControlType is not ("Edit" or "ComboBox")) continue;

            int midY = (c.RelTop + c.RelBottom) / 2;
            int h = Math.Max(16, c.RelBottom - c.RelTop);

            // 1) 优先匹配左侧同行 Static 标签
            RawControl? bestStatic = null;
            double bestDist = double.MaxValue;
            foreach (var st in statics)
            {
                int stMidY = (st.RelTop + st.RelBottom) / 2;
                int dy = Math.Abs(stMidY - midY);
                int gapX = c.RelLeft - st.RelRight;
                if (dy <= Math.Max(18, h) && gapX >= -14 && gapX <= 260)
                {
                    double d = Math.Max(0, gapX) + dy * 2.5;
                    if (d < bestDist) { bestDist = d; bestStatic = st; }
                }
            }
            // 2) 若左侧没有，匹配正上方紧邻 Static 标签
            if (bestStatic == null)
            {
                foreach (var st in statics)
                {
                    int gapY = c.RelTop - st.RelBottom;
                    bool overlapX = st.RelLeft <= c.RelRight && st.RelRight >= c.RelLeft - 20;
                    if (overlapX && gapY >= -6 && gapY <= 36)
                    {
                        double d = Math.Max(0, gapY) + Math.Abs(st.RelLeft - c.RelLeft) * 0.5;
                        if (d < bestDist) { bestDist = d; bestStatic = st; }
                    }
                }
            }

            string lbl = bestStatic?.Label ?? "";
            // 附加所属 GroupBox 上下文
            var ownerGroup = groups
                .Where(g => c.RelLeft >= g.RelLeft && c.RelRight <= g.RelRight && c.RelTop >= g.RelTop && c.RelBottom <= g.RelBottom)
                .OrderBy(g => (g.RelRight - g.RelLeft) * (g.RelBottom - g.RelTop))
                .FirstOrDefault();
            if (ownerGroup != null && !string.IsNullOrEmpty(ownerGroup.Label))
            {
                lbl = string.IsNullOrEmpty(lbl) ? ownerGroup.Label : $"{ownerGroup.Label} > {lbl}";
            }
            c.Label = string.IsNullOrEmpty(lbl) ? $"{c.ControlType}#{c.CtrlId}" : lbl;
        }

        return controls;
    }

    private static RawControl? MatchControl(List<RawControl> controls, JsonObject act)
    {
        // 1) 按 hwnd 精确匹配
        string? hwndHex = act["hwnd"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(hwndHex) && TryParseHwnd(hwndHex, out var hw))
        {
            var byHwnd = controls.FirstOrDefault(c => c.Hwnd == hw);
            if (byHwnd != null) return byHwnd;
        }

        // 2) 按 ctrlId 精确匹配
        if (act["ctrlId"] != null)
        {
            try
            {
                int id = act["ctrlId"]!.GetValue<int>();
                var byId = controls.FirstOrDefault(c => c.CtrlId == id && c.ControlType != "Static" && c.ControlType != "GroupBox");
                if (byId != null) return byId;
            }
            catch { }
        }

        // 3) 按 target（标签/按钮文本）模糊匹配
        string target = (act["target"]?.GetValue<string>() ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return null;
        string op = (act["action"]?.GetValue<string>() ?? "").ToLowerInvariant();

        IEnumerable<RawControl> pool = op switch
        {
            "set_text" => controls.Where(c => c.ControlType is "Edit" or "ComboBox"),
            "set_check" => controls.Where(c => c.ControlType is "CheckBox" or "RadioButton"),
            "select_combo" => controls.Where(c => c.ControlType == "ComboBox"),
            "select_tab" => controls.Where(c => c.ControlType == "TabControl"),
            "click" or "browse_file" => controls.Where(c => c.ControlType is "Button" or "CheckBox" or "RadioButton"),
            _ => controls.Where(c => c.ControlType != "Static" && c.ControlType != "GroupBox")
        };

        var list = pool.ToList();
        // 先精确相等，再包含匹配
        return list.FirstOrDefault(c => c.Label.Equals(target, StringComparison.OrdinalIgnoreCase) || CleanMnemonic(c.Text).Equals(target, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(c => c.Label.Contains(target, StringComparison.OrdinalIgnoreCase) || CleanMnemonic(c.Text).Contains(target, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetEditTextMfc(IntPtr hEdit, int ctrlId, string value)
    {
        SendMessageTimeoutString(hEdit, WM_SETTEXT, IntPtr.Zero, value, SMTO_ABORTIFHUNG, 2000, out _);
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
        int current = (int)SendMessageTimeoutSafe(hBtn, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
        bool isChecked = current == BST_CHECKED;
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
            if (parent != IntPtr.Zero)
            {
                PostMessage(parent, WM_COMMAND, MakeWParam(ctrlId, BN_CLICKED), hBtn);
            }
        }
    }

    private static async Task<string> BrowseFileViaChildDialogAsync(
        List<int> targetPids, IntPtr ownerDlg, IntPtr browseBtn, int browseCtrlId, string filePath, CancellationToken ct)
    {
        ClickButtonMfc(browseBtn, browseCtrlId);
        IntPtr fileDlg = IntPtr.Zero;
        string fileDlgTitle = "";
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && !ct.IsCancellationRequested)
        {
            var dlgs = FindDialogHwnds(targetPids, null);
            var hit = dlgs.FirstOrDefault(d => d.Hwnd != ownerDlg);
            if (hit != null)
            {
                fileDlg = hit.Hwnd;
                fileDlgTitle = hit.Title;
                break;
            }
            await Task.Delay(80, ct);
        }
        if (fileDlg == IntPtr.Zero) throw new InvalidOperationException("file_dialog_timeout_after_browse_click");

        await Task.Delay(120, ct);
        var ctrls = InspectControlsRaw(fileDlg);
        var fileInput = ctrls.FirstOrDefault(c => c.CtrlId == 1148 && c.ControlType == "Edit")
            ?? ctrls.FirstOrDefault(c => c.CtrlId == 1148)
            ?? ctrls.FirstOrDefault(c => c.ControlType == "Edit")
            ?? throw new InvalidOperationException("file_dialog_edit_not_found:" + fileDlgTitle);

        SetEditTextMfc(fileInput.Hwnd, fileInput.CtrlId, filePath);
        await Task.Delay(100, ct);

        var okBtn = ctrls.FirstOrDefault(c => c.CtrlId == 1 && c.ControlType == "Button");
        if (okBtn != null) ClickButtonMfc(okBtn.Hwnd, okBtn.CtrlId);
        else PostMessage(fileDlg, WM_COMMAND, MakeWParam(1, BN_CLICKED), IntPtr.Zero);

        var closeSw = Stopwatch.StartNew();
        while (closeSw.ElapsedMilliseconds < 4000 && IsWindowVisible(fileDlg) && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct);
            foreach (var sub in FindDialogHwnds(targetPids, null))
            {
                if (sub.Hwnd != ownerDlg && sub.Hwnd != fileDlg &&
                    (sub.Title.Contains("确认", StringComparison.OrdinalIgnoreCase) ||
                     sub.Title.Contains("替换", StringComparison.OrdinalIgnoreCase) ||
                     sub.Title.Contains("Confirm", StringComparison.OrdinalIgnoreCase)))
                {
                    PostMessage(sub.Hwnd, WM_COMMAND, MakeWParam(6, BN_CLICKED), IntPtr.Zero);
                }
            }
        }
        return fileDlgTitle;
    }

    private static int SelectComboMfc(IntPtr hCombo, int ctrlId, string? value, int? index)
    {
        var items = ReadComboItems(hCombo, out _);
        int chosen = -1;
        if (index.HasValue && index.Value >= 0 && index.Value < items.Count)
        {
            chosen = index.Value;
        }
        else if (!string.IsNullOrEmpty(value))
        {
            chosen = items.FindIndex(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (chosen < 0) chosen = items.FindIndex(s => s.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
        if (chosen < 0)
        {
            throw new InvalidOperationException($"combo_item_not_found: value={value}, available=[{string.Join(", ", items.Take(10))}]");
        }
        SendMessageTimeoutSafe(hCombo, CB_SETCURSEL, new IntPtr(chosen), IntPtr.Zero);
        IntPtr parent = GetParent(hCombo);
        if (parent != IntPtr.Zero)
        {
            PostMessage(parent, WM_COMMAND, MakeWParam(ctrlId, CBN_SELCHANGE), hCombo);
        }
        return chosen;
    }

    private static List<string> ReadComboItems(IntPtr hCombo, out int selectedIndex)
    {
        var list = new List<string>();
        selectedIndex = (int)SendMessageTimeoutSafe(hCombo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        int count = (int)SendMessageTimeoutSafe(hCombo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0 || count > 500) return list;
        for (int i = 0; i < count; i++)
        {
            int len = (int)SendMessageTimeoutSafe(hCombo, CB_GETLBTEXTLEN, new IntPtr(i), IntPtr.Zero);
            if (len < 0 || len > 1024) continue;
            var sb = new StringBuilder(len + 2);
            SendMessageTimeoutSb(hCombo, CB_GETLBTEXT, new IntPtr(i), sb, SMTO_ABORTIFHUNG, 1000, out _);
            list.Add(sb.ToString());
        }
        return list;
    }

    private static string GetWindowTextSafe(IntPtr hwnd)
    {
        int len = (int)SendMessageTimeoutSafe(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0 || len > 4096) return "";
        var sb = new StringBuilder(len + 2);
        SendMessageTimeoutSb(hwnd, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG, 1500, out _);
        return sb.ToString().Trim();
    }

    private static string GetClassNameSafe(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string CleanMnemonic(string s) =>
        (s ?? "").Replace("&", "").Trim().TrimEnd(':', '：');

    private static IntPtr MakeWParam(int low, int high) =>
        new((high << 16) | (low & 0xFFFF));

    private static bool TryParseHwnd(string hex, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        string clean = hex.Trim();
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        if (long.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out long v))
        {
            hwnd = new IntPtr(v);
            return true;
        }
        return false;
    }

    private static IntPtr SendMessageTimeoutSafe(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        SendMessageTimeout(hwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 2000, out IntPtr res);
        return res;
    }

    // ---------------- P/Invoke ----------------

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
