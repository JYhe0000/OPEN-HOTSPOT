using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace OpenHotspot
{
    public sealed class MainForm : Form
    {
        private readonly HotspotService _service = new HotspotService();
        private readonly AppConfig _config;

        private ComboBox _wiredCombo;
        private ComboBox _hostedCombo;
        private TextBox _ssidBox;
        private TextBox _passwordBox;
        private CheckBox _showPassword;
        private TextBox _ipBox;
        private TextBox _maskBox;
        private TextBox _gatewayBox;
        private CheckBox _autoIpCheck;
        private CheckBox _restoreCheck;
        private Button _startButton;
        private Button _stopButton;
        private Button _refreshButton;
        private Button _diagnoseButton;
        private Label _statusDot;
        private Label _statusLabel;
        private Label _detailLabel;
        private TextBox _logBox;
        private NotifyIcon _tray;
        private ToolStripMenuItem _trayStart;
        private ToolStripMenuItem _trayStop;
        private System.Windows.Forms.Timer _statusTimer;

        private bool _busy;
        private bool _statusQuerying;
        private bool _reallyExit;
        private bool _balloonShown;

        public MainForm()
        {
            _config = AppConfig.Load();
            BuildUi();
            BuildTray();
            LoadConfigIntoUi();
            RefreshAdapters();
            ApplyState(null);

            Shown += delegate(object sender, EventArgs e)
            {
                Log("OpenHotspot 已就绪，当前以管理员权限运行。");
                RefreshStatus();
            };

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 5000;
            _statusTimer.Tick += delegate(object sender, EventArgs e) { RefreshStatus(); };
            _statusTimer.Start();
        }

        private void BuildUi()
        {
            Text = "OpenHotspot - Windows 7 Wi-Fi 热点";
            ClientSize = new Size(660, 680);
            MinimumSize = new Size(600, 600);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9F);
            try
            {
                System.Drawing.Icon appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) Icon = appIcon;
            }
            catch
            {
            }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Fill;
            statusPanel.Margin = new Padding(0);
            statusPanel.BackColor = Color.FromArgb(243, 247, 251);

            _statusDot = new Label();
            _statusDot.Text = "●";
            _statusDot.Font = new Font("Segoe UI", 20F, FontStyle.Regular);
            _statusDot.ForeColor = Color.Gray;
            _statusDot.AutoSize = false;
            _statusDot.Size = new Size(34, 34);
            _statusDot.Location = new Point(16, 24);
            _statusDot.TextAlign = ContentAlignment.MiddleCenter;
            statusPanel.Controls.Add(_statusDot);

            _statusLabel = new Label();
            _statusLabel.Text = "正在读取状态……";
            _statusLabel.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            _statusLabel.AutoSize = true;
            _statusLabel.Location = new Point(54, 16);
            statusPanel.Controls.Add(_statusLabel);

            _detailLabel = new Label();
            _detailLabel.Text = "";
            _detailLabel.ForeColor = Color.FromArgb(90, 98, 110);
            _detailLabel.AutoSize = true;
            _detailLabel.Location = new Point(56, 46);
            statusPanel.Controls.Add(_detailLabel);

            root.Controls.Add(statusPanel, 0, 0);

            TableLayoutPanel settings = new TableLayoutPanel();
            settings.Dock = DockStyle.Top;
            settings.AutoSize = true;
            settings.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            settings.ColumnCount = 2;
            settings.RowCount = 9;
            settings.Padding = new Padding(14, 8, 14, 4);
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 9; i++) settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _wiredCombo = new ComboBox();
            _wiredCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _wiredCombo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _wiredCombo.AutoCompleteSource = AutoCompleteSource.ListItems;
            AddSettingRow(settings, 0, "有线连接", _wiredCombo);

            _hostedCombo = new ComboBox();
            _hostedCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _hostedCombo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _hostedCombo.AutoCompleteSource = AutoCompleteSource.ListItems;
            AddSettingRow(settings, 1, "热点网卡", _hostedCombo);

            _ssidBox = new TextBox();
            AddSettingRow(settings, 2, "Wi-Fi 名称", _ssidBox);

            TableLayoutPanel passwordPanel = new TableLayoutPanel();
            passwordPanel.ColumnCount = 2;
            passwordPanel.RowCount = 1;
            passwordPanel.Margin = new Padding(0);
            passwordPanel.AutoSize = true;
            passwordPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            passwordPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            passwordPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64F));
            passwordPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _passwordBox = new TextBox();
            _passwordBox.Dock = DockStyle.Fill;
            _passwordBox.UseSystemPasswordChar = true;
            _passwordBox.Margin = new Padding(0, 3, 0, 3);
            _showPassword = new CheckBox();
            _showPassword.Text = "显示";
            _showPassword.AutoSize = true;
            _showPassword.Anchor = AnchorStyles.Left;
            _showPassword.Margin = new Padding(6, 4, 0, 0);
            _showPassword.CheckedChanged += delegate
            {
                _passwordBox.UseSystemPasswordChar = !_showPassword.Checked;
            };
            passwordPanel.Controls.Add(_passwordBox, 0, 0);
            passwordPanel.Controls.Add(_showPassword, 1, 0);
            AddSettingRow(settings, 3, "Wi-Fi 密码", passwordPanel);

            _ipBox = new TextBox();
            AddSettingRow(settings, 4, "静态 IP", _ipBox);

            _maskBox = new TextBox();
            AddSettingRow(settings, 5, "子网掩码", _maskBox);

            _gatewayBox = new TextBox();
            AddSettingRow(settings, 6, "默认网关", _gatewayBox);

            _autoIpCheck = new CheckBox();
            _autoIpCheck.Text = "IP 不一致时自动设置静态 IP";
            _autoIpCheck.AutoSize = true;
            _autoIpCheck.Anchor = AnchorStyles.Left;
            _autoIpCheck.Margin = new Padding(0, 6, 0, 4);
            settings.Controls.Add(_autoIpCheck, 1, 7);

            _restoreCheck = new CheckBox();
            _restoreCheck.Text = "停止热点时恢复启动前的 IP 配置";
            _restoreCheck.AutoSize = true;
            _restoreCheck.Anchor = AnchorStyles.Left;
            _restoreCheck.Margin = new Padding(0, 2, 0, 6);
            settings.Controls.Add(_restoreCheck, 1, 8);

            root.Controls.Add(settings, 0, 1);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Top;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.AutoSize = false;
            buttons.Height = 48;
            buttons.Padding = new Padding(14, 6, 14, 8);

            _startButton = MakeButton("启动热点", Color.FromArgb(46, 125, 50));
            _stopButton = MakeButton("停止热点", Color.FromArgb(198, 40, 40));
            _refreshButton = MakeLightButton("刷新网卡");
            _diagnoseButton = MakeLightButton("诊断");

            _startButton.Click += delegate { StartHotspot(); };
            _stopButton.Click += delegate { StopHotspot(); };
            _refreshButton.Click += delegate { RefreshAdapters(); RefreshStatus(); };
            _diagnoseButton.Click += delegate { Diagnose(); };

            buttons.Controls.Add(_startButton);
            buttons.Controls.Add(_stopButton);
            buttons.Controls.Add(_refreshButton);
            buttons.Controls.Add(_diagnoseButton);
            root.Controls.Add(buttons, 0, 2);

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.WordWrap = true;
            _logBox.Dock = DockStyle.Fill;
            _logBox.Margin = new Padding(14, 0, 14, 14);
            _logBox.BackColor = Color.White;
            _logBox.Font = new Font("Consolas", 9F);
            root.Controls.Add(_logBox, 0, 3);
        }

        private static void AddSettingRow(TableLayoutPanel panel, int row, string labelText, Control control)
        {
            Label label = new Label();
            label.Text = labelText;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 6, 8, 6);
            panel.Controls.Add(label, 0, row);

            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 4, 0, 4);
            panel.Controls.Add(control, 1, row);
        }

        private static Button MakeButton(string text, Color color)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(112, 34);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = color;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
            button.Margin = new Padding(0, 0, 10, 0);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button MakeLightButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(96, 34);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(40, 44, 52);
            button.FlatAppearance.BorderColor = Color.FromArgb(200, 206, 213);
            button.FlatAppearance.BorderSize = 1;
            button.Margin = new Padding(0, 0, 10, 0);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            System.Drawing.Icon trayIcon = Icon;
            if (trayIcon == null)
            {
                try { trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { }
            }
            _tray.Icon = trayIcon == null ? SystemIcons.Application : trayIcon;
            _tray.Text = "OpenHotspot";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem showItem = new ToolStripMenuItem("显示主界面");
            showItem.Click += delegate { ShowMainWindow(); };

            _trayStart = new ToolStripMenuItem("启动热点");
            _trayStart.Click += delegate { StartHotspot(); };

            _trayStop = new ToolStripMenuItem("停止热点");
            _trayStop.Enabled = false;
            _trayStop.Click += delegate { StopHotspot(); };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate
            {
                _reallyExit = true;
                Close();
            };

            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_trayStart);
            menu.Items.Add(_trayStop);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowMainWindow(); };
        }

        private void LoadConfigIntoUi()
        {
            _wiredCombo.Text = _config.WiredName;
            _hostedCombo.Text = _config.HostedName;
            _ssidBox.Text = _config.Ssid;
            _passwordBox.Text = _config.Password;
            _ipBox.Text = _config.Ip;
            _maskBox.Text = _config.Mask;
            _gatewayBox.Text = _config.Gateway;
            _autoIpCheck.Checked = _config.AutoSetStaticIp;
            _restoreCheck.Checked = _config.RestoreOnStop;
            _showPassword.Checked = false;
            _passwordBox.UseSystemPasswordChar = true;
        }

        private void RefreshAdapters()
        {
            string wired = SelectedName(_wiredCombo);
            string hosted = SelectedName(_hostedCombo);
            try
            {
                List<AdapterInfo> adapters = NetworkInventory.GetAdapters();
                _wiredCombo.BeginUpdate();
                _hostedCombo.BeginUpdate();
                _wiredCombo.Items.Clear();
                _hostedCombo.Items.Clear();
                foreach (AdapterInfo adapter in adapters)
                {
                    _wiredCombo.Items.Add(new AdapterItem(adapter));
                    _hostedCombo.Items.Add(new AdapterItem(adapter));
                }
                _wiredCombo.EndUpdate();
                _hostedCombo.EndUpdate();

                if (!string.IsNullOrEmpty(wired)) _wiredCombo.Text = wired;
                if (!string.IsNullOrEmpty(hosted)) _hostedCombo.Text = hosted;
                else
                {
                    AdapterInfo virtualAdapter = NetworkInventory.FindVirtualAdapter(adapters);
                    if (virtualAdapter != null) _hostedCombo.Text = virtualAdapter.Name;
                }
                Log("已读取 " + adapters.Count.ToString() + " 个网络连接。");
            }
            catch (Exception ex)
            {
                Log("读取网卡列表失败：" + ex.Message);
            }
        }

        private static string SelectedName(ComboBox combo)
        {
            AdapterItem item = combo.SelectedItem as AdapterItem;
            if (item != null) return item.Adapter.Name;
            return combo.Text == null ? "" : combo.Text.Trim();
        }

        private void StartHotspot()
        {
            if (_busy) return;
            AppConfig config = CollectConfig(true);
            if (config == null) return;
            SaveConfig(config);
            RunOperation(true, config);
        }

        private void StopHotspot()
        {
            if (_busy) return;
            AppConfig config = CollectConfig(false);
            if (config == null) return;
            RunOperation(false, config);
        }

        private void Diagnose()
        {
            if (_busy) return;
            AppConfig config = CollectConfig(false);
            if (config == null) return;
            SetBusy(true);
            Log("开始诊断……");
            RunOnStaThread(delegate
            {
                try { _service.Diagnose(config, LogFromThread); }
                catch (Exception ex) { LogFromThread("诊断失败：" + ex.Message); }
                SafeInvoke(delegate
                {
                    SetBusy(false);
                    RefreshStatus();
                });
            });
        }

        private AppConfig CollectConfig(bool validate)
        {
            AppConfig config = new AppConfig();
            config.WiredName = SelectedName(_wiredCombo);
            config.HostedName = SelectedName(_hostedCombo);
            config.Ssid = _ssidBox.Text == null ? "" : _ssidBox.Text.Trim();
            config.Password = _passwordBox.Text;
            config.Ip = _ipBox.Text == null ? "" : _ipBox.Text.Trim();
            config.Mask = _maskBox.Text == null ? "" : _maskBox.Text.Trim();
            config.Gateway = _gatewayBox.Text == null ? "" : _gatewayBox.Text.Trim();
            config.AutoSetStaticIp = _autoIpCheck.Checked;
            config.RestoreOnStop = _restoreCheck.Checked;

            if (!validate) return config;

            if (config.WiredName.Length == 0)
            {
                ShowValidation("请选择或填写有线连接名称。");
                return null;
            }
            if (config.HostedName.Length == 0)
            {
                ShowValidation("请选择或填写热点网卡名称。");
                return null;
            }
            if (config.Ssid.Length == 0 || config.Ssid.Length > 32)
            {
                ShowValidation("Wi-Fi 名称须为 1 到 32 个字符。");
                return null;
            }
            if (config.Ssid.IndexOf('"') >= 0)
            {
                ShowValidation("Wi-Fi 名称不能包含双引号。");
                return null;
            }
            string key = config.Password;
            if (key == null || key.Length < 8 || key.Length > 63)
            {
                ShowValidation("Wi-Fi 密码须为 8 到 63 个字符。");
                return null;
            }
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c < (char)0x21 || c > (char)0x7E || c == '"')
                {
                    ShowValidation("Wi-Fi 密码只能包含可见 ASCII 字符，不能包含空格或双引号。");
                    return null;
                }
            }

            IPAddress address;
            if (!IPAddress.TryParse(config.Ip, out address))
            {
                ShowValidation("静态 IP 格式不正确。");
                return null;
            }
            if (!IPAddress.TryParse(config.Mask, out address))
            {
                ShowValidation("子网掩码格式不正确。");
                return null;
            }
            if (!IPAddress.TryParse(config.Gateway, out address))
            {
                ShowValidation("默认网关格式不正确。");
                return null;
            }
            return config;
        }

        private void SaveConfig(AppConfig config)
        {
            try { config.Save(); }
            catch (Exception ex) { Log("保存配置失败：" + ex.Message); }
        }

        private void RunOperation(bool start, AppConfig config)
        {
            SetBusy(true);
            Log(start ? "开始启动热点……" : "开始停止热点……");

            RunOnStaThread(delegate
            {
                OperationResult result;
                try
                {
                    if (start) result = _service.Start(config, LogFromThread);
                    else result = _service.Stop(config, LogFromThread);
                }
                catch (Exception ex)
                {
                    result = new OperationResult();
                    result.Success = false;
                    result.Message = ex.Message;
                    result.State = null;
                }

                SafeInvoke(delegate
                {
                    SetBusy(false);
                    ApplyState(result.State);
                    if (result.Success)
                    {
                        Log(result.Message == null ? "操作完成。" : result.Message);
                        if (result.State != null && !string.IsNullOrEmpty(result.State.HostedName))
                        {
                            string actualHosted = result.State.HostedName;
                            if (!string.Equals(SelectedName(_hostedCombo), actualHosted, StringComparison.OrdinalIgnoreCase))
                            {
                                _hostedCombo.Text = actualHosted;
                                SaveConfig(CollectConfig(false));
                            }
                        }
                    }
                    else
                    {
                        string message = result.Message == null ? "操作失败。" : result.Message;
                        Log("失败：" + message);
                        MessageBox.Show(this, message, "OpenHotspot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
            });
        }

        private void RefreshStatus()
        {
            if (_busy || _statusQuerying || IsDisposed) return;
            _statusQuerying = true;
            AppConfig config = CollectConfig(false);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                HotspotState status = null;
                try { status = _service.GetStatus(config); }
                catch { }
                finally { _statusQuerying = false; }

                SafeInvoke(delegate
                {
                    if (!_busy) ApplyState(status);
                });
            });
        }

        private void ApplyState(HotspotState state)
        {
            if (state == null)
            {
                SetStatus(false, "热点未启动", "点击“启动热点”开始共享。");
                if (_trayStart != null) _trayStart.Enabled = true;
                if (_trayStop != null) _trayStop.Enabled = false;
                return;
            }

            string ssid = state.Ssid == null ? "" : state.Ssid;
            string wiredName = state.WiredName == null ? "" : state.WiredName;
            string hostedName = state.HostedName == null ? "" : state.HostedName;
            string wiredIp = state.WiredIp == null ? "" : state.WiredIp;

            string detail = "SSID: " + ssid + "    有线: " + wiredName + Environment.NewLine + "热点网卡: " + hostedName;
            if (wiredIp.Length > 0)
            {
                detail = "SSID: " + ssid + "    有线 IP: " + wiredIp + Environment.NewLine + "热点网卡: " + hostedName;
            }

            SetStatus(state.Running, state.Running ? "热点已启动" : "热点未启动", detail);
            if (_trayStart != null) _trayStart.Enabled = !state.Running;
            if (_trayStop != null) _trayStop.Enabled = state.Running;
        }

        private void SetStatus(bool running, string title, string detail)
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { SetStatus(running, title, detail); });
                return;
            }

            _statusDot.ForeColor = running ? Color.SeaGreen : Color.Gray;
            _statusLabel.Text = title;
            _detailLabel.Text = detail;

            if (_tray != null)
            {
                string text = "OpenHotspot - " + title;
                if (text.Length > 63) text = text.Substring(0, 63);
                try { _tray.Text = text; }
                catch { }
            }
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { SetBusy(busy); });
                return;
            }

            _busy = busy;
            _startButton.Enabled = !busy;
            _stopButton.Enabled = !busy;
            _refreshButton.Enabled = !busy;
            _diagnoseButton.Enabled = !busy;
            if (busy)
            {
                if (_trayStart != null) _trayStart.Enabled = false;
                if (_trayStop != null) _trayStop.Enabled = false;
            }
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void Log(string message)
        {
            if (message == null) return;
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { Log(message); });
                return;
            }

            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            _logBox.AppendText(line + Environment.NewLine);
            if (_logBox.TextLength > 80000)
            {
                _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - 50000);
            }
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        private void LogFromThread(string message)
        {
            SafeInvoke(delegate { Log(message); });
        }

        private static void RunOnStaThread(ThreadStart action)
        {
            Thread thread = new Thread(action);
            thread.IsBackground = true;
            try { thread.SetApartmentState(ApartmentState.STA); }
            catch { }
            thread.Start();
        }

        private void SafeInvoke(MethodInvoker action)
        {
            if (action == null) return;
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch
            {
            }
        }

        private void ShowValidation(string message)
        {
            MessageBox.Show(this, message, "OpenHotspot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowMainWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void HideToTray()
        {
            Hide();
            if (!_balloonShown && _tray != null)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(2500, "OpenHotspot",
                    "程序仍在托盘中运行。右键托盘图标可快速启动或停止热点。",
                    ToolTipIcon.Info);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Dispose();
            }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.OnFormClosed(e);
        }

        private sealed class AdapterItem
        {
            private readonly AdapterInfo _adapter;

            public AdapterItem(AdapterInfo adapter)
            {
                _adapter = adapter;
            }

            public AdapterInfo Adapter
            {
                get { return _adapter; }
            }

            public override string ToString()
            {
                return _adapter.Name + "  -  " + _adapter.Description;
            }
        }
    }
}
