using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class ConnectionTest : Form
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "ConnectionTest";

        private void SaveSettings()
        {
            try
            {
                WritePrivateProfileString(IniSection, "ServerPort", txtServerPort.Text, IniPath);
                WritePrivateProfileString(IniSection, "MaxTry", txtMaxTry.Text, IniPath);
                WritePrivateProfileString(IniSection, "MaxFail", txtMaxFail.Text, IniPath);
                WritePrivateProfileString(IniSection, "Thread", txtThread.Text, IniPath);
                WritePrivateProfileString(IniSection, "TimeReset", txtTimeReset.Text, IniPath);
                if (!string.IsNullOrEmpty(comboServer.Text))
                    WritePrivateProfileString(IniSection, "Server", comboServer.Text, IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var sb = new StringBuilder(256);
                string val;
                GetPrivateProfileString(IniSection, "ServerPort", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtServerPort.Text = val;
                GetPrivateProfileString(IniSection, "MaxTry", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtMaxTry.Text = val;
                GetPrivateProfileString(IniSection, "MaxFail", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtMaxFail.Text = val;
                GetPrivateProfileString(IniSection, "Thread", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtThread.Text = val;
                GetPrivateProfileString(IniSection, "TimeReset", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtTimeReset.Text = val;
                GetPrivateProfileString(IniSection, "Server", "", sb, sb.Capacity, IniPath);
                string server = sb.ToString();
                if (!string.IsNullOrEmpty(server) && comboServer.Items.Count > 0)
                {
                    int idx = -1;
                    for (int i = 0; i < comboServer.Items.Count; i++)
                        if (comboServer.Items[i].ToString() == server) { idx = i; break; }
                    if (idx >= 0) comboServer.SelectedIndex = idx;
                    else comboServer.Text = server;
                }
            }
            catch { }
        }
        private CancellationTokenSource cts;
        private long successCount = 0;
        private long failCount = 0;
        private long totalTried = 0;
        private bool isTesting = false;
        private long lastLoggedSuccess = 0;
        private long lastLoggedFail = 0;
        private ConcurrentBag<Socket> socketPool = new ConcurrentBag<Socket>();
        private Stopwatch testStopwatch = new Stopwatch();
        private long lastProgressSuccessCount = 0;
        private DateTime stallStartTime = DateTime.MinValue;
        private DateTime lastStallWarning = DateTime.MinValue;

        public ConnectionTest()
        {
            InitializeComponent();
            txtServerPort.KeyPress += TextBoxDigitsOnly_KeyPress;
            txtMaxTry.KeyPress += TextBoxDigitsOnly_KeyPress;
            txtMaxFail.KeyPress += TextBoxDigitsOnly_KeyPress;
            txtThread.KeyPress += TextBoxDigitsOnly_KeyPress;
            txtTimeReset.KeyPress += TextBoxDigitsOnly_KeyPress;

            txtServerPort.Leave += TextBoxClampValue_Leave;
            txtMaxTry.Leave += TextBoxClampValue_Leave;
            txtMaxFail.Leave += TextBoxClampValue_Leave;
            txtThread.Leave += TextBoxClampValue_Leave;
            txtTimeReset.Leave += TextBoxClampValue_Leave;
        }

        private async Task ApplyConnectionThemeAsync()
        {
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoControls = {
        label2, lblOK, lblFail, lblTime, lblVersion
    };
            foreach (var c in yumeyoControls) { if (c != null) c.ForeColor = yumeyoColor; }

            Control[] contrastControls = {
        lblMaxTry, lblMaxFail, lblThread, lblTimeRest, label1, lblNIC,
    };
            foreach (var c in contrastControls)
            {
                if (c != null) c.ForeColor = contrastColor;
            }

            Control[] editControls = {
        comboServer, comboNIC, txtMaxTry, txtMaxFail, txtThread,
        txtTimeReset, txtSuccess, txtFail, txtServerPort
    };

            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = textBack;

                    if (c is TextBox txt)
                    {
                        txt.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                    }

                    if (c is ComboBox cb)
                    {
                        cb.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                    }
                }
            }

            Control[] buttons = { btnStart, btnUnlockTCPUDP };
            foreach (var b in buttons)
            {
                if (b != null && b is Button btn)
                {
                    if (isLight)
                    {
                        btn.ForeColor = Color.Black;
                        btn.BackColor = SystemColors.Control;
                        btn.UseVisualStyleBackColor = true;
                        btn.FlatStyle = FlatStyle.Standard;
                    }
                    else
                    {
                        btn.ForeColor = Color.White;
                        btn.BackColor = btnDarkBack;
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.FlatAppearance.BorderColor = Color.DimGray;
                        btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                    }
                }
            }
        }

        private void TextBoxDigitsOnly_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
            {
                e.Handled = true;
            }
        }

        private void TextBoxClampValue_Leave(object sender, EventArgs e)
        {
            if (!(sender is TextBox txt)) return;
            if (!int.TryParse(txt.Text, out int val)) return;
            if (val < 1) val = 1;

            if (txt == txtServerPort || txt == txtMaxTry)
                val = Math.Min(val, 65535);
            else if (txt == txtMaxFail)
                val = Math.Min(val, 99999);
            else if (txt == txtThread)
                val = Math.Min(val, 999);
            else if (txt == txtTimeReset)
                val = Math.Min(val, 999);

            if (val.ToString() != txt.Text)
                txt.Text = val.ToString();
        }

        private void ConnectionTest_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            _ = ApplyConnectionThemeAsync();
            var portStatus = GetSystemDynamicPortRange();
            InitNICList();
            lblVersion.Text = Global.exeName + " " + Global.Version + " | " + Others.GetCurrentTime();
            this.Text = $"最大连接数测试(TCP) ✧ NICX (mP:{portStatus.num}({portStatus.start}))";
            CloudControl.LoadConnectionServers(comboServer);
            CloudControl.ApplyDevTitle(this);
            timer1.Start();
            CloudControl.UsedTimesCounter("连接数测试");
            LoadSettings();
        }

        private void EnsureSelectedNICValid()
        {
            string selectedText = "";
            this.Invoke(new Action(() => { selectedText = comboNIC.Text; }));
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.StartsWith("0.0.0.0") || selectedText.StartsWith("::")) return;

            InitNICList();

            bool found = false;
            foreach (var item in comboNIC.Items)
            {
                dynamic dynItem = item;
                string itemText = dynItem.Text ?? "";
                if (itemText == selectedText)
                {
                    comboNIC.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found && comboNIC.Items.Count > 0) comboNIC.SelectedIndex = 0;
        }

        private void InitNICList()
        {
            comboNIC.Items.Clear();

            var defaultItem = new { Text = "0.0.0.0 (Any)", Value = "0.0.0.0" };
            var defaultItemV6 = new { Text = ":: (IPv6 Any)", Value = "::" };

            comboNIC.DisplayMember = "Text";
            comboNIC.ValueMember = "Value";
            comboNIC.Items.Add(defaultItem);
            comboNIC.Items.Add(defaultItemV6);

            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                {
                    comboNIC.Items.Add(new
                    {
                        Text = nicAddress.DisplayText,
                        Value = nicAddress.AddressText
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("初始化网卡失败: " + ex.Message, "获取失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }

            if (comboNIC.Items.Count > 0) comboNIC.SelectedIndex = 0;
        }

        private async Task<string> HandleDNSAsync(string input)
        {
            if (IPAddress.TryParse(input, out _)) return input;

            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(input);
                var allIPs = addresses.Where(a =>
                    a.AddressFamily == AddressFamily.InterNetwork ||
                    a.AddressFamily == AddressFamily.InterNetworkV6).ToList();

                if (allIPs.Count == 0) return null;

                comboServer.Items.Clear();
                comboServer.Items.Add(input);
                foreach (var ip in allIPs)
                {
                    comboServer.Items.Add(ip.ToString());
                }

                txtFail.AppendText($"\r\n[DNS]域名 {input} 解析出 {allIPs.Count} 个 IP，请选择一个后开测");
                comboServer.DroppedDown = true;
                comboServer.SelectedIndex = 1;

                return null;
            }
            catch (Exception ex)
            {
                txtFail.AppendText($"\r\n[DNS错误] {ex.Message}");
                return null;
            }
        }

        private void SetUIState(bool state)
        {
            btnUnlockTCPUDP.Enabled = state;
            comboNIC.Enabled = state;
            comboServer.Enabled = state;
            txtServerPort.Enabled = state;
            txtMaxTry.Enabled = state;
            txtMaxFail.Enabled = state;
            txtThread.Enabled = state;
            txtTimeReset.Enabled = state;

            btnStart.Text = state ? "开测" : "停止";
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            EnsureSelectedNICValid();

            if (isTesting)
            {
                StopTest("用户手动停止");
                return;
            }

            string input = comboServer.Text.Trim().ToLower();

            if (input.StartsWith("http://")) input = input.Substring(7);
            else if (input.StartsWith("https://")) input = input.Substring(8);

            if (input.Contains("/"))
            {
                int slashIndex = input.IndexOf('/');
                string afterSlash = input.Substring(slashIndex + 1);
                string portDigits = new string(afterSlash.Where(char.IsDigit).ToArray());

                if (!string.IsNullOrEmpty(portDigits))
                {
                    txtServerPort.Text = portDigits;
                }
                input = input.Substring(0, slashIndex);
            }

            input = Regex.Replace(input, @"[^a-z0-9\.\:\-_]", "");
            if (string.IsNullOrEmpty(input))
            {
                SystemSounds.Beep.Play();
                return;
            }

            comboServer.Text = input;

            if (!ValidateInputs(out int port, out int maxTry, out int maxFail, out int threads, out int interval))
            {
                return;
            }

            txtSuccess.Text = "成功次数统计";
            txtFail.Text = "失败次数统计";

            string targetIP = comboServer.Text.Trim();

            if (!IPAddress.TryParse(targetIP, out _))
            {
                string resolvedIP = await HandleDNSAsync(targetIP);
                if (string.IsNullOrEmpty(resolvedIP))
                {
                    return;
                }
                targetIP = resolvedIP;
            }

            string localIPText = "";
            this.Invoke(new Action(() => { localIPText = comboNIC.Text.Trim(); }));

            string cleanLocalIP = localIPText.Split(' ')[0]
                                             .Replace("[", "")
                                             .Replace("]", "");

            IPAddress.TryParse(targetIP, out IPAddress targetAddr);
            IPAddress.TryParse(cleanLocalIP, out IPAddress localAddr);

            if (targetAddr != null && localAddr != null)
            {
                bool isTargetV6 = targetAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                bool isLocalV6 = localAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

                bool isAny = (cleanLocalIP == "0.0.0.0" || cleanLocalIP == "::");

                if (isTargetV6 != isLocalV6)
                {
                    string errorMsg = isLocalV6
                                ? "协议错误(本地IPv6, 目标IPv4)"
                                : "协议错误(本地IPv4, 目标IPv6)";

                    txtFail.AppendText($"\r\n[启动失败] {errorMsg}");
                    txtFail.SelectionStart = txtFail.Text.Length;
                    txtFail.ScrollToCaret();

                    SystemSounds.Beep.Play();
                    return;
                }
            }

            ResetStatistics();

            isTesting = true;
            SetUIState(false);

            testStopwatch.Restart();
            timerUpdate.Start();

            cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            StartTestingEngine(targetIP, port, maxTry, maxFail, threads, interval, cleanLocalIP);
        }

        private void StopTest(string reason)
        {
            if (!isTesting) return;

            isTesting = false;

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            testStopwatch.Stop();
            timerUpdate.Stop();

            Task.Run(() =>
            {
                foreach (var s in socketPool)
                {
                    try { s.Dispose(); } catch { }
                }
                while (!socketPool.IsEmpty) socketPool.TryTake(out _);
            });

            if (!this.IsDisposed && !this.Disposing)
            {
                this.BeginInvoke(new Action(() =>
                {
                    SetUIState(true);
                    var portStatus = GetSystemDynamicPortRange();
                    this.Text = $"最大连接数测试(TCP) ✧ NICX (mP:{portStatus.num}({portStatus.start}))";
                    CloudControl.ApplyDevTitle(this);
                    string stopLog = $"\r\n==========================\r\n" +
                                     $" [测试停止] {reason}\r\n" +
                                     $" ❤ 最终成功：{successCount}\r\n" +
                                     $" ❤ 最终失败：{failCount}\r\n" +
                                     $"==========================";
                    txtFail.AppendText(stopLog);

                    txtFail.SelectionStart = txtFail.Text.Length;
                    txtFail.ScrollToCaret();
                }));
            }
        }

        private bool ValidateInputs(out int port, out int maxTry, out int maxFail, out int threads, out int interval)
        {
            port = 0; maxTry = 0; maxFail = 0; threads = 0; interval = 0;

            if (!int.TryParse(txtServerPort.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("端口号需 1-65535 之间");
                return false;
            }
            if (!int.TryParse(txtThread.Text, out threads) || threads < 1 || threads > 999)
            {
                MessageBox.Show("线程数需 1-999 之间");
                return false;
            }
            if (!int.TryParse(txtTimeReset.Text, out interval) || interval < 10 || interval > 999)
            {
                MessageBox.Show("间隔时间需 10-999ms 之间");
                return false;
            }
            if (!int.TryParse(txtMaxFail.Text, out maxFail) || maxFail < 10 || maxFail > 99999)
            {
                MessageBox.Show("失败上限需 100-99999 之间");
                return false;
            }
            if (!int.TryParse(txtMaxTry.Text, out maxTry) || maxTry < 100 || maxTry > 65535)
            {
                MessageBox.Show("尝试上限需 100-65535 之间");
                return false;
            }

            return true;
        }

        private void ResetStatistics()
        {
            successCount = 0;
            failCount = 0;
            totalTried = 0;
            lastLoggedSuccess = 0;
            lastLoggedFail = 0;
            lastProgressSuccessCount = 0;
            stallStartTime = DateTime.MinValue;
            lastStallWarning = DateTime.MinValue;

            lblOK2.Text = "-";
            lblFail2.Text = "-";
            lblTime2.Text = "-";

            txtSuccess.Text = "成功次数统计\n";
            txtFail.Text = "失败次数统计\n";
        }

        private async Task DoConnectAsync(string targetIP, int port, string localIP, int interval, int maxTry, int maxFail, CancellationToken token)
        {
            if (!IPAddress.TryParse(targetIP, out IPAddress targetAddr)) return;
            AddressFamily family = targetAddr.AddressFamily;

            while (!token.IsCancellationRequested && isTesting)
            {
                if (Interlocked.Read(ref totalTried) >= maxTry) break;
                Interlocked.Increment(ref totalTried);

                Socket socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);

                socket.NoDelay = true;
                socket.LingerState = new LingerOption(true, 0);

                try
                {
                    if (family == AddressFamily.InterNetwork)
                    {
                        if (localIP == "::") return;

                        socket.Bind(new IPEndPoint(IPAddress.Parse(localIP), 0));
                    }
                    else if (family == AddressFamily.InterNetworkV6)
                    {
                        if (localIP == "0.0.0.0") return;
                        socket.Bind(new IPEndPoint(IPAddress.Parse(localIP), 0));
                    }

                    var result = socket.BeginConnect(targetAddr, port, null, null);

                    using (token.Register(() => { try { socket.Close(); } catch { } }))
                    {
                        bool success = result.AsyncWaitHandle.WaitOne(2000, true);

                        try { socket.EndConnect(result); } catch { }

                        if (success && socket.Connected && isTesting)
                        {
                            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                            socketPool.Add(socket);
                            Interlocked.Increment(ref successCount);
                            UpdateLocalIP(socket.LocalEndPoint.ToString());
                        }
                        else
                        {
                            Interlocked.Increment(ref failCount);
                            socket.Close();
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failCount);
                    try { socket.Close(); } catch { }
                }

                UpdateStatsLabels();
                CheckLimits(maxTry, maxFail);

                if (interval > 0)
                {
                    try { await Task.Delay(interval, token); }
                    catch { break; }
                }
            }
        }

        private void StartTestingEngine(string targetIP, int port, int maxTry, int maxFail, int threads, int interval, string localIP)
        {
            for (int i = 0; i < threads; i++)
            {
                Task.Run(() => DoConnectAsync(targetIP, port, localIP, interval, maxTry, maxFail, cts.Token));
            }
        }

        private void UpdateStatsLabels()
        {
            long currentOK = Interlocked.Read(ref successCount);
            long currentFail = Interlocked.Read(ref failCount);

            this.BeginInvoke(new Action(() =>
            {
                lblOK2.Text = currentOK.ToString();
                lblFail2.Text = currentFail.ToString();

                if (currentOK >= lastLoggedSuccess + 100)
                {
                    lastLoggedSuccess = (currentOK / 100) * 100;
                    string nowTime = Others.GetCurrentTimeHMS();
                    txtSuccess.AppendText($"\r\n[{nowTime}]TCP={comboServer.Text} 成功={lastLoggedSuccess}");
                }

                if (currentFail >= lastLoggedFail + 10)
                {
                    lastLoggedFail = (currentFail / 10) * 10;
                    string nowTime = Others.GetCurrentTimeHMS();
                    txtFail.AppendText($"\r\n[{nowTime}]TCP={comboServer.Text} 失败={lastLoggedFail}");
                }
            }));
        }

        private void CheckLimits(int maxTry, int maxFail)
        {
            if (totalTried >= maxTry)
            {
                StopTest("已达到尝试次数上限");
            }
            else if (failCount >= maxFail)
            {
                StopTest("已达到失败次数上限");
            }
        }

        private void UpdateLocalIP(string localEndPoint)
        {
            this.BeginInvoke(new Action(() =>
            {
                string actualIP = "";
                if (localEndPoint.Contains("]"))
                {
                    actualIP = localEndPoint.Split(']')[0].Replace("[", "");
                }
                else
                {
                    actualIP = localEndPoint.Split(':')[0];
                }

                if (comboNIC.Text.Contains("Any") || comboNIC.Text.Contains("0.0.0.0") || comboNIC.Text.Contains("::"))
                {
                    bool foundMatch = false;

                    for (int i = 0; i < comboNIC.Items.Count; i++)
                    {
                        dynamic item = comboNIC.Items[i];

                        if (item.Value.ToString() == actualIP)
                        {
                            comboNIC.SelectedIndex = i;
                            foundMatch = true;
                            break;
                        }
                    }

                    if (!foundMatch)
                    {
                        comboNIC.Text = actualIP;
                    }
                }
            }));
        }

        private void comboServer_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void ConnectionTest_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            if (isTesting)
            {
                StopTest("窗口关闭");
            }

            if (cts != null)
            {
                cts.Dispose();
            }
            timer1.Stop();
            timer1.Dispose();
            timerUpdate.Stop();
            timerUpdate.Dispose();
        }

        private async void timerUpdate_Tick(object sender, EventArgs e)
        {
            if (isTesting)
            {
                lblTime2.Text = testStopwatch.Elapsed.TotalSeconds.ToString("0");

                string memInfo = GetMemoryUsageString();

                this.Text = $"最大连接数测试(TCP) ✧ NICX ({memInfo})";
                CloudControl.ApplyDevTitle(this);

                long currentSuccess = Interlocked.Read(ref successCount);
                if (currentSuccess > lastProgressSuccessCount)
                {
                    lastProgressSuccessCount = currentSuccess;
                    stallStartTime = DateTime.MinValue;
                    lastStallWarning = DateTime.MinValue;
                }
                else
                {
                    DateTime now = DateTime.Now;
                    if (stallStartTime == DateTime.MinValue)
                    {
                        stallStartTime = now;
                    }
                    else
                    {
                        double stallSeconds = (now - stallStartTime).TotalSeconds;
                        if (stallSeconds >= 90)
                        {
                            StopTest("连续90秒无成功连接，自动停止");
                            return;
                        }
                        if (stallSeconds >= 10 && (lastStallWarning == DateTime.MinValue || (now - lastStallWarning).TotalSeconds >= 10))
                        {
                            lastStallWarning = now;
                            string nowTime = Others.GetCurrentTimeHMS();
                            txtFail.AppendText($"\r\n[{nowTime}] 已{stallSeconds:F0}秒无新连接，再{90 - stallSeconds:F0}秒将自动停止");
                        }
                    }
                }
            }
        }

        private string GetMemoryUsageString()
        {
            using (var proc = System.Diagnostics.Process.GetCurrentProcess())
            {
                double usedMs = proc.WorkingSet64 / 1024.0 / 1024.0;
                double commitMs = proc.PrivateMemorySize64 / 1024.0 / 1024.0;

                return $"Mem: {usedMs:F2}/{commitMs:F2}MB";
            }
        }

        private void btnUnlockTCPUDP_Click(object sender, EventArgs e)
        {
            var (currentStart, currentNum) = GetSystemDynamicPortRange();
            bool isUnlocked = currentNum > 16384;

            if (isUnlocked)
            {
                string restoreMsg = "当前系统连接数大于默认，还原默认吗？\n（若当前未解锁到最大，请还原一次后再次解锁）";
                if (MessageBox.Show(restoreMsg, "操作确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ExecuteNetshCommand("int ipv4 set dynamicport tcp start=49152 num=16384 & netsh int ipv4 set dynamicport udp start=49152 num=16384");
                    MessageBox.Show("已恢复系统默认连接数设置", "操作成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else
            {
                if (MessageBox.Show("解锁系统默认TCP/UDP连接数到64511 (最大值)？\n\n注意：此处解锁的是Windows对单张网卡的限制，\n若您要测试超过单张网卡最大值的连接数，\n请多开本窗口并选择不同网卡开启测试", "操作确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    ExecuteNetshCommand("int ipv4 set dynamicport tcp start=1025 num=64511 & netsh int ipv4 set dynamicport udp start=1025 num=64511");
                    MessageBox.Show("已解锁最大连接数设置", "操作成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            var portStatus = GetSystemDynamicPortRange();
            this.Text = $"最大连接数测试(TCP) ✧ NICX (mP:{portStatus.num}({portStatus.start}))";
            CloudControl.ApplyDevTitle(this);
        }

        private void ExecuteNetshCommand(string netshArgs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c netsh " + netshArgs)
                {
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                Process p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex}", "设置出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private (int start, int num) GetSystemDynamicPortRange()
        {
            int start = 49152;
            int num = 16384;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "int ipv4 show dynamicport tcp")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.Default
                };

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();

                    var matches = System.Text.RegularExpressions.Regex.Matches(output, @":\s*(\d+)");
                    if (matches.Count >= 2)
                    {
                        int.TryParse(matches[0].Groups[1].Value, out start);
                        int.TryParse(matches[1].Groups[1].Value, out num);
                    }
                }
            }
            catch { /* 报错则返回默认值 */ }

            return (start, num);
        }

        private void label1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (isTesting)
                {
                    StopTest("重载窗口");
                }

                if (cts != null)
                {
                    cts.Dispose();
                }

                Point currentPkgLocation = this.Location;

                ConnectionTest newForm = new ConnectionTest();

                if (this.pictureBox1.Image != null)
                {
                    newForm.pictureBox1.Image = this.pictureBox1.Image;
                }

                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentPkgLocation;
                newForm.Show();
                this.Close();
            }
        }

        private void txtServerPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void txtMaxTry_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void txtMaxFail_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void txtThread_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void txtTimeReset_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            lblVersion.Text = Global.exeName + " " + Global.Version + " | " + Others.GetCurrentTime();
        }
    }
}
