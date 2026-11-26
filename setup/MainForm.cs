using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;

namespace EngineeringPlatform.Setup;

internal sealed class MainForm : Form
{
    private static readonly Color BgHeader = Color.FromArgb(15, 23, 42);
    private static readonly Color BgCanvas = Color.FromArgb(241, 245, 249);
    private static readonly Color BgCard = Color.White;
    private static readonly Color BorderHairline = Color.FromArgb(226, 232, 240);
    private static readonly Color AccentBlue = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentEmerald = Color.FromArgb(5, 150, 105);
    private static readonly Color AccentSlate = Color.FromArgb(51, 65, 85);
    private static readonly Color AccentDanger = Color.FromArgb(220, 38, 38);
    private static readonly Color TextPrimary = Color.FromArgb(15, 23, 42);
    private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
    private static readonly Color RowAltBg = Color.FromArgb(248, 250, 252);

    private readonly DoubleBufferedListView _lvCads;
    private readonly CheckBox _chkDeployGateway;
    private readonly CheckBox _chkAutoWrite;
    private readonly CheckBox _chkTrustCert;
    private readonly CheckBox _chkTrustRootCert;
    private readonly CheckBox _chkAutostart;
    private readonly CheckBox _chkClientConfig;
    private readonly CheckBox _chkCleanGlobalBundle;
    private readonly NumericUpDown _numPort;
    private readonly RichTextBox _txtLog;
    private readonly Label _lblSummary;
    private readonly Button _btnApply;
    private readonly Button _btnUninstall;
    private readonly Button _btnStartGw;
    private readonly Button _btnCopyMcp;

    private List<CadTargetInfo> _targets = new();
    private bool _busy;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Engineering Platform v1.0.0 — 全版本 CAD 一体化安装配置中心";
        MinimumSize = new Size(980, 680);
        Size = new Size(1060, 740);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = BgCanvas;
        DoubleBuffered = true;

        // ==================== 1. 顶部深蓝渐变横幅 ====================
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 82,
            BackColor = BgHeader
        };
        header.Paint += (_, pe) =>
        {
            if (header.ClientRectangle.Width <= 0 || header.ClientRectangle.Height <= 0) return;
            using var br = new LinearGradientBrush(header.ClientRectangle,
                Color.FromArgb(15, 23, 42), Color.FromArgb(30, 41, 59), LinearGradientMode.Horizontal);
            pe.Graphics.FillRectangle(br, header.ClientRectangle);
            using var accentPen = new Pen(AccentBlue, 3f);
            pe.Graphics.DrawLine(accentPen, 0, header.Height - 2, header.Width, header.Height - 2);
        };

        var titleRow = new FlowLayoutPanel
        {
            Location = new Point(22, 12),
            Size = new Size(460, 32),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        var lblTitle = new Label
        {
            Text = "Engineering Platform",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 14.5f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 10, 0)
        };
        var lblBadge = new Label
        {
            Text = "v1.0.0",
            ForeColor = Color.FromArgb(191, 219, 254),
            BackColor = Color.FromArgb(30, 64, 175),
            Font = new Font("Consolas", 8.8f, FontStyle.Bold),
            AutoSize = false,
            Size = new Size(56, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 5, 0, 0)
        };
        titleRow.Controls.Add(lblTitle);
        titleRow.Controls.Add(lblBadge);

        var lblSub = new Label
        {
            Text = "一站式部署统一 MCP 多驱动网关与全版本 AutoCAD / Civil 3D / 中望 / 浩辰 适配插件",
            ForeColor = Color.FromArgb(148, 163, 184),
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 9f),
            AutoSize = true,
            Location = new Point(24, 50)
        };

        header.Controls.Add(titleRow);
        header.Controls.Add(lblSub);

        // ==================== 2. 快捷选择工具栏 ====================
        var toolbarWrap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(16, 10, 16, 4),
            BackColor = BgCanvas
        };
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = BgCanvas
        };
        toolbar.Controls.Add(MakeToolButton("全选全部", (_, _) => SetSelection(_ => true)));
        toolbar.Controls.Add(MakeToolButton("仅选 Civil 3D", (_, _) => SetSelection(c => c.IsCivil3D)));
        toolbar.Controls.Add(MakeToolButton("仅选插件版 (2017–2026)", (_, _) => SetSelection(c => c.DriverMode != CadDriverMode.ComGeneric)));
        toolbar.Controls.Add(MakeToolButton("清空选择", (_, _) => SetSelection(_ => false)));
        toolbar.Controls.Add(MakeToolButton("手动添加 CAD...", OnAddManualCad));
        toolbar.Controls.Add(MakeToolButton("重新扫描", (_, _) => RefreshCadList()));
        toolbarWrap.Controls.Add(toolbar);

        // ==================== 3. CAD 列表白卡片 ====================
        var listOuter = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 4, 16, 8),
            BackColor = BgCanvas
        };
        var listCard = CreateCardPanel();
        var listCardHeader = CreateCardHeader("本机已发现的 CAD 环境", "勾选需要启用自动接入的 CAD 实例，系统将按版本自动分配最优驱动模式");

        _lvCads = new DoubleBufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            BackColor = BgCard,
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = new ImageList { ImageSize = new Size(1, 28) }
        };
        _lvCads.Columns.Add("CAD 产品名称", 320);
        _lvCads.Columns.Add("版本", 75);
        _lvCads.Columns.Add("自动匹配驱动", 150);
        _lvCads.Columns.Add("当前状态", 175);
        _lvCads.Columns.Add("安装目录", 280);
        _lvCads.ItemChecked += (_, _) => UpdateSelectionSummary();

        var lvPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 8), BackColor = BgCard };
        lvPad.Controls.Add(_lvCads);
        listCard.Controls.Add(lvPad);
        listCard.Controls.Add(listCardHeader);
        listOuter.Controls.Add(listCard);

        // ==================== 4. 部署与安全选项白卡片 ====================
        var optOuter = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 136,
            Padding = new Padding(16, 0, 16, 8),
            BackColor = BgCanvas
        };
        var optCard = CreateCardPanel();
        var optHeader = CreateCardHeader("部署与安全选项", "按需配置网关、写入审批模式与证书信任策略");

        var optGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(14, 4, 14, 6),
            BackColor = BgCard
        };
        optGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
        optGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36f));
        optGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29f));
        optGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        optGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        optGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.4f));

        _chkDeployGateway = MakeOptionCheck("部署统一 MCP 网关 (ep-mcp.exe)", true);
        _chkAutoWrite = MakeOptionCheck("开启全自主写通道 (autoApprove)", true);
        _chkTrustCert = MakeOptionCheck("信任签名证书 + TRUSTEDPATHS", true);
        _chkClientConfig = MakeOptionCheck("生成 MCP 客户端配置 (HTTP + stdio)", true);
        _chkCleanGlobalBundle = MakeOptionCheck("清理旧全局包（按勾选精准隔离）", true);
        _chkTrustRootCert = MakeOptionCheck("同步导入用户根证书 (免安全弹窗)", true);
        _chkAutostart = MakeOptionCheck("开机后台自启 HTTP 网关", CadInstallerEngine.IsGatewayAutostartEnabled());

        var portWrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 2, 0, 0),
            BackColor = BgCard
        };
        var lblPort = new Label
        {
            Text = "HTTP 网关端口：",
            AutoSize = true,
            ForeColor = TextPrimary,
            Margin = new Padding(0, 4, 6, 0)
        };
        _numPort = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Value = 18080,
            Width = 78,
            Font = new Font("Consolas", 9.5f),
            BorderStyle = BorderStyle.FixedSingle
        };
        portWrap.Controls.Add(lblPort);
        portWrap.Controls.Add(_numPort);

        optGrid.Controls.Add(_chkDeployGateway, 0, 0);
        optGrid.Controls.Add(_chkAutoWrite, 1, 0);
        optGrid.Controls.Add(_chkTrustCert, 2, 0);
        optGrid.Controls.Add(_chkClientConfig, 0, 1);
        optGrid.Controls.Add(_chkCleanGlobalBundle, 1, 1);
        optGrid.Controls.Add(_chkTrustRootCert, 2, 1);
        optGrid.Controls.Add(_chkAutostart, 0, 2);
        optGrid.Controls.Add(portWrap, 1, 2);

        optCard.Controls.Add(optGrid);
        optCard.Controls.Add(optHeader);
        optOuter.Controls.Add(optCard);

        // ==================== 5. 深色终端日志卡片 ====================
        var logOuter = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 152,
            Padding = new Padding(16, 0, 16, 8),
            BackColor = BgCanvas
        };
        var logCard = new Panel { Dock = DockStyle.Fill, BackColor = BgHeader, Padding = new Padding(1) };
        var logBar = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(30, 41, 59), Padding = new Padding(12, 0, 8, 0) };
        var lblLogTitle = new Label
        {
            Text = "●  实时执行日志",
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
            Dock = DockStyle.Left,
            Width = 140,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var btnClearLog = new Button
        {
            Text = "清空",
            Dock = DockStyle.Right,
            Width = 52,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(148, 163, 184),
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 8.2f),
            Cursor = Cursors.Hand
        };
        btnClearLog.FlatAppearance.BorderSize = 0;
        btnClearLog.Click += (_, _) => _txtLog.Clear();
        logBar.Controls.Add(btnClearLog);
        logBar.Controls.Add(lblLogTitle);

        _txtLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = BgHeader,
            ForeColor = Color.FromArgb(226, 232, 240),
            Font = new Font("Consolas", 9.2f),
            ScrollBars = RichTextBoxScrollBars.Vertical
        };
        var logInner = new Panel { Dock = DockStyle.Fill, BackColor = BgHeader, Padding = new Padding(10, 6, 10, 6) };
        logInner.Controls.Add(_txtLog);
        logCard.Controls.Add(logInner);
        logCard.Controls.Add(logBar);
        logOuter.Controls.Add(logCard);

        // ==================== 6. 底部悬浮操作栏 ====================
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            BackColor = BgCard,
            Padding = new Padding(20, 12, 20, 12)
        };
        footer.Paint += (_, pe) =>
        {
            using var p = new Pen(BorderHairline, 1f);
            pe.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
        };
        _lblSummary = new Label
        {
            AutoSize = true,
            ForeColor = AccentSlate,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Location = new Point(20, 21)
        };
        var btnBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = BgCard
        };
        _btnApply = MakePrimaryButton("一键应用配置", AccentEmerald, Color.White, 142, OnApplyClicked, bold: true);
        _btnStartGw = MakePrimaryButton("启动并检测网关", AccentBlue, Color.White, 134, OnStartGatewayClicked);
        _btnCopyMcp = MakePrimaryButton("复制 MCP 配置", AccentSlate, Color.White, 126, OnCopyMcpConfigClicked);
        _btnUninstall = MakePrimaryButton("全部卸载", Color.FromArgb(254, 242, 242), AccentDanger, 96, OnUninstallClicked, border: Color.FromArgb(254, 202, 202));

        btnBar.Controls.Add(_btnApply);
        btnBar.Controls.Add(_btnStartGw);
        btnBar.Controls.Add(_btnCopyMcp);
        btnBar.Controls.Add(_btnUninstall);
        footer.Controls.Add(_lblSummary);
        footer.Controls.Add(btnBar);

        Controls.Add(listOuter);
        Controls.Add(optOuter);
        Controls.Add(logOuter);
        Controls.Add(footer);
        Controls.Add(toolbarWrap);
        Controls.Add(header);

        Load += (_, _) => RefreshCadList();
    }

    private static Panel CreateCardPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = BgCard, Padding = new Padding(1) };
        p.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderHairline, 1f);
            pe.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        return p;
    }

    private static Panel CreateCardHeader(string title, string sub)
    {
        var hdr = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = Color.FromArgb(248, 250, 252),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(14, 9, 14, 0)
        };
        hdr.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderHairline, 1f);
            pe.Graphics.DrawLine(pen, 0, hdr.Height - 1, hdr.Width, hdr.Height - 1);
        };
        var lblT = new Label
        {
            Text = title,
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 0)
        };
        hdr.Controls.Add(lblT);
        if (!string.IsNullOrEmpty(sub))
        {
            var lblS = new Label
            {
                Text = sub,
                ForeColor = TextMuted,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 0)
            };
            hdr.Controls.Add(lblS);
        }
        return hdr;
    }

    private static CheckBox MakeOptionCheck(string text, bool isChecked)
    {
        return new CheckBox
        {
            Text = text,
            Checked = isChecked,
            Dock = DockStyle.Fill,
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9.2f),
            Margin = new Padding(2, 0, 4, 0),
            Cursor = Cursors.Hand
        };
    }

    private static Button MakeToolButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 0, 8, 0),
            BackColor = BgCard,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9f),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 246, 255);
        b.Click += onClick;
        return b;
    }

    private static Button MakePrimaryButton(string text, Color bg, Color fg, int width, EventHandler onClick, bool bold = false, Color? border = null)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 38,
            Margin = new Padding(10, 0, 0, 0),
            BackColor = bg,
            ForeColor = fg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = border.HasValue ? 1 : 0;
        if (border.HasValue) b.FlatAppearance.BorderColor = border.Value;
        b.Click += onClick;
        return b;
    }

    private void RefreshCadList()
    {
        _targets = CadDiscovery.DiscoverAll();
        RenderTargets(preserveManual: false);
        AppendLog($"已扫描本机 CAD：共发现 {_targets.Count} 个实例（满血插件版 {_targets.Count(x => x.DriverMode != CadDriverMode.ComGeneric)} 个，通用 COM 版 {_targets.Count(x => x.DriverMode == CadDriverMode.ComGeneric)} 个）。", LogLevel.Info);
    }

    private void RenderTargets(bool preserveManual)
    {
        _lvCads.BeginUpdate();
        _lvCads.Items.Clear();
        for (int i = 0; i < _targets.Count; i++)
        {
            var cad = _targets[i];
            var rowBg = (i % 2 == 1) ? RowAltBg : BgCard;
            var item = new ListViewItem(cad.ProductName)
            {
                Tag = cad,
                Checked = cad.IsConfigured,
                BackColor = rowBg,
                UseItemStyleForSubItems = false
            };
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, cad.Release) { BackColor = rowBg, ForeColor = TextMuted });
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, cad.DriverModeLabel)
            {
                BackColor = rowBg,
                ForeColor = cad.DriverMode == CadDriverMode.ComGeneric ? Color.FromArgb(124, 58, 237) : AccentBlue
            });
            var statusColor = cad.RunningPid.HasValue
                ? Color.FromArgb(217, 119, 6)
                : (cad.IsConfigured ? Color.FromArgb(22, 163, 74) : TextMuted);
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, cad.StatusLabel)
            {
                BackColor = rowBg,
                ForeColor = statusColor,
                Font = new Font("Microsoft YaHei UI", 9.2f, cad.IsConfigured || cad.RunningPid.HasValue ? FontStyle.Bold : FontStyle.Regular)
            });
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, cad.InstallPath) { BackColor = rowBg, ForeColor = TextMuted });

            _lvCads.Items.Add(item);
        }
        _lvCads.EndUpdate();
        UpdateSelectionSummary();
    }

    private void SetSelection(Func<CadTargetInfo, bool> predicate)
    {
        foreach (ListViewItem item in _lvCads.Items)
        {
            if (item.Tag is CadTargetInfo cad)
            {
                item.Checked = predicate(cad);
            }
        }
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var chosen = GetCheckedTargets();
        int pCount = chosen.Count(x => x.DriverMode != CadDriverMode.ComGeneric);
        int cCount = chosen.Count(x => x.DriverMode == CadDriverMode.ComGeneric);
        _lblSummary.Text = $"已选 {chosen.Count} / {_targets.Count} 个 CAD（满血插件 {pCount}，通用 COM {cCount}）";
    }

    private List<CadTargetInfo> GetCheckedTargets()
    {
        var res = new List<CadTargetInfo>();
        foreach (ListViewItem item in _lvCads.Items)
        {
            if (item.Checked && item.Tag is CadTargetInfo cad) res.Add(cad);
        }
        return res;
    }

    private void OnAddManualCad(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 CAD 主程序（acad.exe / zwcad.exe / gcad.exe）",
            Filter = "CAD 可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var custom = CadDiscovery.CreateManualTarget(dlg.FileName);
        if (_targets.Any(x => x.Id.Equals(custom.Id, StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog($"该 CAD 路径已在列表中：{custom.InstallPath}", LogLevel.Warn);
            return;
        }
        custom.IsConfigured = true;
        _targets.Add(custom);
        RenderTargets(preserveManual: true);
        AppendLog($"已添加自定义 CAD：{custom.ProductName} ({custom.InstallPath})", LogLevel.Success);
    }

    private async void OnApplyClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        var selected = GetCheckedTargets();
        var opts = new InstallOptions
        {
            DeployGateway = _chkDeployGateway.Checked,
            EnableAutoWrite = _chkAutoWrite.Checked,
            TrustCodeSignCert = _chkTrustCert.Checked,
            TrustRootCert = _chkTrustRootCert.Checked,
            EnableGatewayAutostart = _chkAutostart.Checked,
            GenerateClientConfig = _chkClientConfig.Checked,
            CleanGlobalBundleOnSelective = _chkCleanGlobalBundle.Checked,
            GatewayPort = (int)_numPort.Value
        };

        SetBusy(true);
        try
        {
            var summary = await Task.Run(() =>
                CadInstallerEngine.ApplyConfiguration(_targets, selected, opts, LogFromWorker));
            RefreshCadList();
            MessageBox.Show(
                this,
                $"配置已完成！\n\n• 满血插件 CAD：{summary.ConfiguredPluginCount} 个\n• 通用 COM CAD：{summary.ConfiguredComCount} 个\n• 已移除未勾选：{summary.RemovedCount} 个\n• 告警数：{summary.Warnings}\n\n重启对应 CAD 后即自动生效。",
                "配置成功",
                MessageBoxButtons.OK,
                summary.Warnings == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"配置异常：{ex.Message}", LogLevel.Error);
            MessageBox.Show(this, ex.Message, "配置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnUninstallClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        var ans = MessageBox.Show(
            this,
            "确定卸载本机全部 AutoCAD / Civil 3D 的 Engineering Platform 自动加载配置？\n（不会影响您已保存的工程图纸。）",
            "确认卸载",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (ans != DialogResult.Yes) return;

        SetBusy(true);
        try
        {
            await Task.Run(() => CadInstallerEngine.UninstallAll(_targets, dryRun: false, LogFromWorker));
            RefreshCadList();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStartGatewayClicked(object? sender, EventArgs e)
    {
        string gw = CadInstallerEngine.GatewayExePath;
        if (!File.Exists(gw))
        {
            AppendLog("尚未部署 ep-mcp.exe，请先点击「一键应用配置」。", LogLevel.Warn);
            return;
        }
        int port = (int)_numPort.Value;
        string url = $"http://127.0.0.1:{port}/health";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                AppendLog($"网关已在运行：{url} → {await resp.Content.ReadAsStringAsync()}", LogLevel.Success);
                return;
            }
        }
        catch { }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = gw,
                Arguments = $"--port {port} --backend auto",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            await Task.Delay(1200);
            var resp = await http.GetAsync(url);
            string body = await resp.Content.ReadAsStringAsync();
            AppendLog($"网关启动成功：{url} → {body}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            AppendLog($"启动网关失败：{ex.Message}", LogLevel.Error);
        }
    }

    private void OnCopyMcpConfigClicked(object? sender, EventArgs e)
    {
        string path = CadInstallerEngine.ClientConfigPath;
        if (!File.Exists(path))
        {
            CadInstallerEngine.ApplyConfiguration(
                _targets,
                GetCheckedTargets(),
                new InstallOptions
                {
                    DeployGateway = false,
                    TrustCodeSignCert = false,
                    GenerateClientConfig = true,
                    CleanGlobalBundleOnSelective = false,
                    GatewayPort = (int)_numPort.Value
                },
                AppendLog);
        }
        if (File.Exists(path))
        {
            Clipboard.SetText(File.ReadAllText(path));
            AppendLog($"已复制 MCP 客户端配置到剪贴板（源文件：{path}）", LogLevel.Success);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnApply.Enabled = !busy;
        _btnUninstall.Enabled = !busy;
        _btnStartGw.Enabled = !busy;
        _btnCopyMcp.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void LogFromWorker(string message, LogLevel level)
    {
        if (InvokeRequired) BeginInvoke(() => AppendLog(message, level));
        else AppendLog(message, level);
    }

    private void AppendLog(string message, LogLevel level)
    {
        Color color = level switch
        {
            LogLevel.Success => Color.FromArgb(74, 222, 128),
            LogLevel.Warn => Color.FromArgb(250, 204, 21),
            LogLevel.Error => Color.FromArgb(248, 113, 113),
            _ => Color.FromArgb(226, 232, 240)
        };
        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.SelectionLength = 0;
        _txtLog.SelectionColor = color;
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _txtLog.ScrollToCaret();
    }

    private sealed class DoubleBufferedListView : ListView
    {
        public DoubleBufferedListView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }
}
