using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using EngineeringPlatform.Host.Diagnostics;
using EngineeringPlatform.Host.Protocol;
using EngineeringPlatform.Host.Semantic;

namespace EngineeringPlatform.Host.LocalPanel
{
    /// <summary>
    /// 降级面板（方案 14.8 / 设计 §3.8）。
    /// 只有 4 个区块：① 宿主状态；② 最近 50 条任务；③ 取消当前任务；④ 导出日志。
    /// 没有任何 AI、规划、生成、写入控件——这是硬约束，不允许后续悄悄加回来。
    /// </summary>
    internal sealed class LocalPanelForm : Form
    {
        private readonly HostRuntime _runtime;
        private readonly Timer _timer;

        private readonly Label _statusLabel = new Label();
        private readonly ListView _taskList = new ListView();
        private readonly Button _cancelButton = new Button();
        private readonly Button _exportButton = new Button();
        private readonly Button _refreshButton = new Button();
        private string _currentRunningOpId;

        public LocalPanelForm(HostRuntime runtime)
        {
            _runtime = runtime ?? HostRuntime.Instance;

            Text = "工程平台宿主 · 降级面板（只读）";
            Width = 900;
            Height = 520;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei", 9f);

            _statusLabel.SetBounds(12, 12, 860, 96);
            _statusLabel.AutoSize = false;
            _statusLabel.BorderStyle = BorderStyle.FixedSingle;
            _statusLabel.Padding = new Padding(8);
            _statusLabel.Text = "正在读取宿主状态…";

            _taskList.SetBounds(12, 116, 860, 320);
            _taskList.View = View.Details;
            _taskList.FullRowSelect = true;
            _taskList.GridLines = true;
            _taskList.Columns.Add("时间", 150);
            _taskList.Columns.Add("opId", 220);
            _taskList.Columns.Add("工具", 150);
            _taskList.Columns.Add("状态", 90);
            _taskList.Columns.Add("耗时(ms)", 90);
            _taskList.Columns.Add("错误码", 90);

            _cancelButton.SetBounds(12, 446, 160, 30);
            _cancelButton.Text = "取消当前任务";
            _cancelButton.Click += OnCancelClick;

            _exportButton.SetBounds(184, 446, 160, 30);
            _exportButton.Text = "导出日志";
            _exportButton.Click += OnExportClick;

            _refreshButton.SetBounds(356, 446, 120, 30);
            _refreshButton.Text = "刷新";
            _refreshButton.Click += delegate { RefreshData(); };

            Controls.Add(_statusLabel);
            Controls.Add(_taskList);
            Controls.Add(_cancelButton);
            Controls.Add(_exportButton);
            Controls.Add(_refreshButton);

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { RefreshData(); };
            Load += delegate { RefreshData(); _timer.Start(); };
            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); };
        }

        private void RefreshData()
        {
            try
            {
                var sessions = _runtime.Sessions;
                System.Text.StringBuilder status = new System.Text.StringBuilder();
                status.AppendLine("实例：" + sessions.HostInstanceId + "    版本：" + sessions.HostVersion);
                status.AppendLine("管道：" + sessions.PipeName);
                status.AppendLine("会话数：" + sessions.ActiveSessionCount + "    连接数：" + _runtime.ConnectionCount
                    + "    队列：" + _runtime.Dispatcher.QueueLength);
                status.AppendLine("主线程泵送：" + (_runtime.Dispatcher.IsPumping(3000) ? "正常" : "异常（可能有模态对话框）")
                    + "    缓存命中率：" + (_runtime.CacheHitRate * 100D).ToString("0", CultureInfo.InvariantCulture) + "%");
                status.AppendLine("指纹：" + (string.IsNullOrEmpty(_runtime.LastFingerprint) ? "（尚未采集）" : _runtime.LastFingerprint));
                status.Append("Civil 3D：" + (CivilApiProbe.Available ? "可用" : "未安装（代理对象按缺口上报）")
                    + "    写入通道：关闭（M0）");
                _statusLabel.Text = status.ToString();

                List<TaskRecord> records = _runtime.RecentTasks(50, DateTime.MinValue);
                _currentRunningOpId = null;
                _taskList.BeginUpdate();
                _taskList.Items.Clear();
                for (int i = 0; i < records.Count; i++)
                {
                    TaskRecord record = records[i];
                    ListViewItem item = new ListViewItem(TimeUtil.ToIso(record.StartedUtc));
                    item.SubItems.Add(record.OpId ?? string.Empty);
                    item.SubItems.Add(record.Tool ?? string.Empty);
                    item.SubItems.Add(record.Status ?? string.Empty);
                    item.SubItems.Add(record.ElapsedMs.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(record.ErrorCode ?? string.Empty);
                    if (string.Equals(record.Status, "running", StringComparison.Ordinal))
                    {
                        _currentRunningOpId = record.OpId;
                    }
                    _taskList.Items.Add(item);
                }
                _taskList.EndUpdate();

                _cancelButton.Enabled = !string.IsNullOrEmpty(_currentRunningOpId);
            }
            catch (Exception ex)
            {
                HostLog.Exception("panel", "refresh_failed", ex);
            }
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentRunningOpId))
            {
                MessageBox.Show(this, "当前没有执行中的任务。", "取消任务", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool accepted = _runtime.CancelTask(_currentRunningOpId);
            MessageBox.Show(this,
                accepted ? "已受理取消，将在对象批边界停止（M0 无写入，无副作用）。" : "该任务已结束或不存在。",
                "取消任务", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshData();
        }

        private void OnExportClick(object sender, EventArgs e)
        {
            try
            {
                string exportRoot = Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EngineeringPlatform"),
                    "exports");
                Directory.CreateDirectory(exportRoot);
                string target = Path.Combine(exportRoot,
                    "host-logs-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z.zip");
                HostLog.ExportZip(target);
                MessageBox.Show(this, "日志已导出到：\n" + target, "导出日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                HostLog.Exception("panel", "export_failed", ex);
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出日志", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
