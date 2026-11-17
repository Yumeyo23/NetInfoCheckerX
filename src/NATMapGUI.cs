using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class NATMapGUI : Form
    {
        private sealed class AddressItem
        {
            internal string Address, Display;
            public override string ToString() { return Display; }
        }

        private readonly ConcurrentQueue<string> pendingLog = new ConcurrentQueue<string>();
        private readonly string iniPath = Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private NatMapSession session;
        private NatMapOptions runningOptions;
        private bool loading = true, stopping, closeAfterStop;
        private System.Windows.Forms.Timer logTimer;
        private string executablePath;
        private readonly string defaultTitle;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hwnd, int wMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        private const int HTCAPTION = 0x0002;

        public NATMapGUI()
        {
            InitializeComponent();
            defaultTitle = Text;
            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime || DesignMode) return;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            comboProtocol.SelectedIndex = 0;
            comboMode.SelectedIndex = 0;
            comboBindMode.SelectedIndex = 0;
            CloudControl.LoadNatMapStunServers(comboStun);
            RefreshAddresses("");
            LoadConfiguration();
            if (!File.Exists(executablePath)) executablePath = FindExecutable();
            foreach (Control control in new Control[] { comboProtocol, comboMode, comboNic, comboStun,
                comboBindMode, txtHttp, txtTarget })
                control.TextChanged += (sender, e) => UpdateControls();
            foreach (NumericUpDown number in new[] { numBindPort, numBindEnd, numStunPort, numHttpPort,
                numKeepAlive, numCheckCycle, numTargetPort, numTimeout })
                number.ValueChanged += (sender, e) => UpdateControls();
            comboProtocol.SelectedIndexChanged += (sender, e) => UpdateControls();
            comboMode.SelectedIndexChanged += (sender, e) => UpdateControls();
            comboNic.SelectedIndexChanged += (sender, e) => UpdateControls();
            comboStun.SelectedIndexChanged += (sender, e) =>
            {
                string server = comboStun.SelectedItem as string;
                if (server != null) LoadServer(comboStun, numStunPort, server, 3478);
            };
            foreach (ComboBox combo in new[] { comboProtocol, comboMode, comboNic, comboStun, comboBindMode })
                combo.DrawItem += DrawComboItem;
            btnStart.Click += StartMapping;
            btnStop.Click += async (sender, e) => await StopMappingAsync();
            comboBindMode.SelectedIndexChanged += (sender, e) => UpdateControls();
            btnRefreshNic.Click += (sender, e) => { RefreshAddresses(SelectedAddress()); UpdateControls(); };

            btnClear.Click += (sender, e) => { string ignored; while (pendingLog.TryDequeue(out ignored)) { } txtLog.Clear(); };
            btnSaveLog.Click += SaveLog;
            FormClosing += OnFormClosing;
            Shown += (sender, e) =>
            {
                Rectangle working = Screen.FromControl(this).WorkingArea;
                if (Height > working.Height) Height = Math.Max(MinimumSize.Height, working.Height);
            };
            logTimer = new System.Windows.Forms.Timer(components) { Interval = 150 };
            logTimer.Tick += (sender, e) => FlushLog();
            logTimer.Start();
            ApplyTheme(this);
            loading = false;
            UpdateControls();
            if (!File.Exists(executablePath)) EnqueueLog("未找到 NATMap 核心，请将 natmap-win-x64.exe 放在主程序旁边。");
        }

        private string SelectedAddress()
        {
            return (comboNic.SelectedItem as AddressItem)?.Address ?? "";
        }

        private void RefreshAddresses(string selected)
        {
            comboNic.Items.Clear();
            comboNic.Items.Add(new AddressItem { Address = "", Display = "系统自动选择" });
            try
            {
                foreach (var info in NicHelper.GetUsableIPAddresses(includeIPv6: false))
                    comboNic.Items.Add(new AddressItem { Address = info.AddressText, Display = info.DisplayText });
            }
            catch (Exception ex) { EnqueueLog("读取网卡失败：" + ex.Message); }
            var item = comboNic.Items.Cast<AddressItem>().FirstOrDefault(x => x.Address == selected);
            comboNic.SelectedItem = item ?? comboNic.Items[0];
            if (!string.IsNullOrEmpty(selected) && item == null)
                EnqueueLog("原IPv4已不可用，已改为自动选择：" + selected);
        }

        private static string FindExecutable()
        {
            string preferred = Path.Combine(Application.StartupPath, "natmap-win-x64.exe");
            string fallback = Path.Combine(Application.StartupPath, "natmap.exe");
            return File.Exists(preferred) || !File.Exists(fallback) ? preferred : fallback;
        }

        private NatMapOptions ReadOptions()
        {
            return new NatMapOptions
            {
                Udp = comboProtocol.SelectedIndex == 1,
                Forward = comboMode.SelectedIndex == 0,
                LocalAddress = SelectedAddress(),
                BindPort = GetBindPortArgument(),
                StunServer = comboStun.Text.Trim() + ":" + numStunPort.Value.ToString(CultureInfo.InvariantCulture),
                HttpServer = txtHttp.Text.Trim() + ":" + numHttpPort.Value.ToString(CultureInfo.InvariantCulture),
                TargetAddress = txtTarget.Text.Trim(),
                TargetPort = (int)numTargetPort.Value,
                KeepAlive = (int)numKeepAlive.Value,
                CheckCycle = (int)numCheckCycle.Value,
                ForwardTimeout = (int)numTimeout.Value
            };
        }

        private string GetBindPortArgument()
        {
            if (comboBindMode.SelectedIndex == 1) return "0";
            string first = numBindPort.Value.ToString(CultureInfo.InvariantCulture);
            if (comboBindMode.SelectedIndex < 2) return first;
            return first + (comboBindMode.SelectedIndex == 2 ? "-" : "~") + numBindEnd.Value.ToString(CultureInfo.InvariantCulture);
        }

        private void UpdateControls()
        {
            if (loading) return;
            bool active = session != null;
            bool udp = comboProtocol.SelectedIndex == 1;
            bool forward = comboMode.SelectedIndex == 0;
            comboProtocol.Enabled = comboMode.Enabled = comboNic.Enabled = comboStun.Enabled = comboBindMode.Enabled = !active;
            txtHttp.ReadOnly = txtTarget.ReadOnly = active;
            numKeepAlive.Enabled = numStunPort.Enabled = !active;
            numHttpPort.Enabled = !active && !udp;
            numBindPort.Enabled = !active && comboBindMode.SelectedIndex != 1;
            bool range = comboBindMode.SelectedIndex >= 2;
            lblBindRange.Visible = numBindEnd.Visible = range;
            numBindEnd.Enabled = range && !active;
            btnRefreshNic.Enabled = !active;
            btnStart.Enabled = !active && !stopping;
            btnStop.Enabled = active && !stopping;
            txtHttp.Enabled = !udp;
            numCheckCycle.Enabled = udp && !active;
            txtTarget.Enabled = forward;
            numTargetPort.Enabled = numTimeout.Enabled = forward && !active;
        }

        private void StartMapping(object sender, EventArgs e)
        {
            if (session != null || stopping) return;
            NatMapSession created = null;
            try
            {
                var options = ReadOptions();
                string arguments = options.BuildArguments();
                string executable = executablePath;
                if (!File.Exists(executable)) throw new FileNotFoundException("未找到 NATMap 核心，请将 natmap-win-x64.exe 放在主程序旁边。", executable);
                if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("随附的 NATMap 核心需要 64 位 Windows。");
                CheckForwardLoop(options, null);
                SaveConfiguration();
                txtPublic.Clear(); txtLocal.Clear();
                lblStatus.Text = "正在建立映射…";
                lblUpdated.Text = "--";
                runningOptions = options;
                created = new NatMapSession();
                session = created;
                NatMapSession owner = created;
                created.Output += (line, error) =>
                {
                    NatMapMapping result;
                    if (!error && NatMapMapping.TryParse(line, out result))
                        EnqueueLog("[核心] 公网=" + result.PublicEndPoint + ", 本机=" + result.LocalEndPoint +
                            ", 协议=" + result.Protocol.ToUpperInvariant() + ", IP4P=" + result.Ip4p);
                    else
                        EnqueueLog((error ? "[核心] " : "") + line);
                };
                created.Mapped += mapping => PostToUi(() => HandleMapping(owner, mapping));
                created.Exited += code => PostToUi(() => FinishSession(owner, code));
                UpdateControls();
                EnqueueLog("启动：\"" + executable + "\" " + arguments);
                created.Start(executable, arguments);
                UpdateMappingTitle((string.IsNullOrEmpty(options.LocalAddress) ? "自动" : options.LocalAddress) + ":" + options.BindPort);
                EnqueueLog("NATMap 已启动, 正在等待映射");
            }
            catch (Exception ex)
            {
                if (created != null) { created.Dispose(); if (ReferenceEquals(session, created)) session = null; }
                Text = defaultTitle;
                lblStatus.Text = "启动失败";
                EnqueueLog("启动失败：" + ex.Message);
                UpdateControls();
                MessageBox.Show(this, ex.Message, "无法启动映射", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void HandleMapping(NatMapSession owner, NatMapMapping mapping)
        {
            if (!ReferenceEquals(session, owner)) return;
            try { CheckForwardLoop(runningOptions, mapping); }
            catch (ArgumentException ex)
            {
                EnqueueLog("已停止映射：" + ex.Message);
                DisposeSession();
                lblStatus.Text = "转发目标与入口冲突";
                UpdateControls();
                return;
            }
            txtPublic.Text = mapping.PublicEndPoint.ToString();
            txtLocal.Text = mapping.LocalEndPoint.ToString();
            UpdateMappingTitle(txtLocal.Text);
            lblStatus.Text = mapping.Protocol.ToUpperInvariant() + " 已取得映射";
            lblUpdated.Text = DateTime.Now.ToString("HH:mm:ss");
            EnqueueLog("映射更新：" + txtPublic.Text + " → " + txtLocal.Text +
                (runningOptions.Forward ? " → " + runningOptions.TargetAddress + ":" +
                (runningOptions.TargetPort == 0 ? mapping.PublicEndPoint.Port : runningOptions.TargetPort) : "（直接绑定）"));
            UpdateControls();
        }

        private void UpdateMappingTitle(string localEndPoint)
        {
            string detail = " [" + (runningOptions.Forward ? "转发" : "绑定") + "]" + localEndPoint;
            const string marker = "映射工具";
            int index = defaultTitle.IndexOf(marker, StringComparison.Ordinal);
            Text = index >= 0 ? defaultTitle.Insert(index + marker.Length, detail) : defaultTitle + detail;
        }

        private static void CheckForwardLoop(NatMapOptions options, NatMapMapping mapping)
        {
            if (!options.Forward) return;
            IPAddress target;
            if (!IPAddress.TryParse(options.TargetAddress, out target))
            {
                // 不在 UI 线程进行 DNS 查询；localhost 作为明确的本机目标处理。
                if (!string.Equals(options.TargetAddress, "localhost", StringComparison.OrdinalIgnoreCase)) return;
                target = IPAddress.Loopback;
            }
            int bind;
            int localPort = mapping != null ? mapping.LocalEndPoint.Port :
                (int.TryParse(options.BindPort, out bind) ? bind : 0);
            int targetPort = options.TargetPort == 0 ? (mapping?.PublicEndPoint.Port ?? 0) : options.TargetPort;
            if (localPort == 0 || localPort != targetPort) return;
            bool local = IPAddress.IsLoopback(target) ||
                NicHelper.GetUsableIPAddresses(includeIPv6: false).Any(x => x.Address.Equals(target));
            if (local) throw new ArgumentException("转发目标指向本机入口自身，请使用不同的入口端口与服务端口。");
        }

        private async System.Threading.Tasks.Task StopMappingAsync()
        {
            var owner = session;
            if (owner == null || stopping) return;
            stopping = true;
            lblStatus.Text = "正在停止…";
            UpdateControls();
            try
            {
                await owner.StopAsync();
                FinishSession(owner, 0);
            }
            catch (Exception ex)
            {
                closeAfterStop = false;
                lblStatus.Text = "停止失败，可重试";
                EnqueueLog("停止失败：" + ex.Message);
            }
            finally
            {
                stopping = false;
                UpdateControls();
                if (closeAfterStop && session == null) Close();
            }
        }

        private void FinishSession(NatMapSession owner, int exitCode)
        {
            if (!ReferenceEquals(session, owner)) return;
            bool requested = stopping;
            session = null;
            owner.Dispose();
            Text = defaultTitle;
            lblStatus.Text = requested ? "已停止" : "核心已退出（退出码 " + exitCode + "）";
            txtPublic.Clear(); txtLocal.Clear();
            lblUpdated.Text = "--";
            EnqueueLog(requested ? "映射已停止。" : "NATMap 已退出，退出码：" + exitCode + "。请检查上方核心日志。");
            FlushLog();
            UpdateControls();
            if (closeAfterStop && !stopping) Close();
        }

        private async void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (session != null)
            {
                e.Cancel = true;
                closeAfterStop = true;
                if (!stopping) await StopMappingAsync();
                return;
            }
            SaveConfiguration();
        }

        private void DisposeSession()
        {
            session?.Dispose();
            session = null;
            if (!IsDisposed && !Disposing) Text = defaultTitle;
        }

        private void PostToUi(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) action(); })); }
            catch (InvalidOperationException) { }
        }

        private void EnqueueLog(string text)
        {
            if (IsDisposed || Disposing) return;
            if (pendingLog.Count >= 1000) return;
            pendingLog.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text);
        }

        private void FlushLog()
        {
            var batch = new StringBuilder();
            string line;
            for (int i = 0; i < 250 && pendingLog.TryDequeue(out line); i++) batch.AppendLine(line);
            if (batch.Length == 0) return;
            if (txtLog.TextLength > 150000)
            {
                string text = txtLog.Text;
                int start = text.IndexOf('\n', text.Length - 100000);
                txtLog.Text = start >= 0 ? text.Substring(start + 1) : "";
            }
            txtLog.AppendText(batch.ToString());
        }

        private void SaveLog(object sender, EventArgs e)
        {
            FlushLog();
            using (var dialog = new SaveFileDialog { Filter = "文本文件 (*.txt)|*.txt", FileName = "NICX_NATMap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, txtLog.Text, new UTF8Encoding(true)); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "日志导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private string ReadConfig(string key, string fallback)
        {
            if (Global.isTemp) return fallback;
            var value = new StringBuilder(1024);
            IniFileHelper.GetPrivateProfileString("NATMap", key, fallback, value, value.Capacity, iniPath);
            return value.ToString();
        }

        private void LoadConfiguration()
        {
            comboProtocol.SelectedIndex = ReadConfig("Protocol", "TCP") == "UDP" ? 1 : 0;
            comboMode.SelectedIndex = ReadConfig("Mode", "Forward") == "Bind" ? 1 : 0;
            RefreshAddresses(ReadConfig("LocalAddress", ""));
            LoadBindPort(ReadConfig("BindPort", numBindPort.Value.ToString(CultureInfo.InvariantCulture)));
            LoadServer(comboStun, numStunPort, ReadConfig("StunServer", "turn.cloudflare.com:3478"), 3478);
            LoadServer(txtHttp, numHttpPort, ReadConfig("HttpServer", "223.6.6.6:80"), 80);
            txtTarget.Text = ReadConfig("TargetAddress", "127.0.0.1");
            LoadNumber(numTargetPort, "TargetPort", decimal.ToInt32(numTargetPort.Value));
            LoadNumber(numKeepAlive, "KeepAlive", 30);
            LoadNumber(numCheckCycle, "CheckCycle", 1);
            LoadNumber(numTimeout, "ForwardTimeout", 300);
            executablePath = ReadConfig("Executable", FindExecutable());
        }

        private void LoadBindPort(string value)
        {
            if (!NatMapOptions.IsBindPortValid(value)) value = numBindPort.Value.ToString(CultureInfo.InvariantCulture);
            string[] ports = value.Split('-', '~');
            int first = int.Parse(ports[0], CultureInfo.InvariantCulture);
            if (first > 0) numBindPort.Value = first;
            comboBindMode.SelectedIndex = first == 0 ? 1 : ports.Length == 1 ? 0 : value.Contains("~") ? 3 : 2;
            if (ports.Length > 1) numBindEnd.Value = int.Parse(ports[1], CultureInfo.InvariantCulture);
        }

        private static void LoadServer(Control hostControl, NumericUpDown portControl, string value, int defaultPort)
        {
            int separator = value.LastIndexOf(':');
            int port;
            if (separator > 0 && int.TryParse(value.Substring(separator + 1), out port) && port >= 1 && port <= 65535)
            {
                hostControl.Text = value.Substring(0, separator);
                portControl.Value = port;
            }
            else
            {
                hostControl.Text = value;
                portControl.Value = defaultPort;
            }
        }

        private void LoadNumber(NumericUpDown control, string key, int fallback)
        {
            int value;
            if (!int.TryParse(ReadConfig(key, fallback.ToString(CultureInfo.InvariantCulture)), out value)) value = fallback;
            control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, value));
        }

        private void SaveConfiguration()
        {
            if (Global.isTemp) return;
            var o = ReadOptions();
            try { o.BuildArguments(); }
            catch (ArgumentException) { return; }
            string[,] values =
            {
                { "Protocol", o.Udp ? "UDP" : "TCP" }, { "Mode", o.Forward ? "Forward" : "Bind" },
                { "LocalAddress", o.LocalAddress }, { "BindPort", o.BindPort }, { "StunServer", o.StunServer },
                { "HttpServer", o.HttpServer }, { "TargetAddress", o.TargetAddress },
                { "TargetPort", o.TargetPort.ToString(CultureInfo.InvariantCulture) },
                { "KeepAlive", o.KeepAlive.ToString(CultureInfo.InvariantCulture) },
                { "CheckCycle", o.CheckCycle.ToString(CultureInfo.InvariantCulture) },
                { "ForwardTimeout", o.ForwardTimeout.ToString(CultureInfo.InvariantCulture) }, { "Executable", executablePath }
            };
            for (int i = 0; i < values.GetLength(0); i++)
            {
                if (IniFileHelper.WritePrivateProfileString("NATMap", values[i, 0], values[i, 1], iniPath) == 0)
                { EnqueueLog("无法保存 NATMap 设置，请检查配置文件是否可写。"); break; }
            }
        }


        private void MyMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_SYSCOMMAND, SC_MOVE + HTCAPTION, 0);
            }
        }
        private void NATMapGUI_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            CloudControl.UsedTimesCounter("NATMap");
        }

        private void ApplyTheme(Control parent)
        {
            bool light = Global.isThemelight;
            Color foreground = light ? Global.colorBlack : Global.colorWhite;
            Color background = light ? Global.themeLight : Global.themeBlack;
            Color input = light ? SystemColors.Window : Color.FromArgb(32, 32, 32);
            parent.ForeColor = foreground;
            parent.BackColor = background;
            foreach (Control control in parent.Controls)
            {
                ApplyTheme(control);
                if (control is TextBox || control is ComboBox || control is NumericUpDown) control.BackColor = input;
                if (control is TextBox textBox) textBox.BorderStyle = light ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                if (control is NumericUpDown number) number.BorderStyle = light ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                if (control is ComboBox combo)
                {
                    combo.FlatStyle = light ? FlatStyle.Standard : FlatStyle.Flat;
                    combo.DrawMode = light ? DrawMode.Normal : DrawMode.OwnerDrawFixed;
                }
                if (control is Button button)
                {
                    button.UseVisualStyleBackColor = light;
                    button.FlatStyle = light ? FlatStyle.Standard : FlatStyle.Flat;
                    if (!light)
                    {
                        button.BackColor = Color.FromArgb(60, 60, 60);
                        button.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
                        button.FlatAppearance.MouseOverBackColor = Global.Yumeyo2;
                    }
                }
            }
        }

        private void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            var combo = (ComboBox)sender;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color background = selected ? Global.Yumeyo2 : combo.BackColor;
            Color foreground = !combo.Enabled ? SystemColors.GrayText : selected ? Color.Black : combo.ForeColor;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            string text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
            TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
    }
}
