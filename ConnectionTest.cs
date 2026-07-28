using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class ConnectionTest : Form
    {
        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);
        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);
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
                WritePrivateProfileString(IniSection, "HoldConnections", chkHold.Checked ? "1" : "0", IniPath);
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
                GetPrivateProfileString(IniSection, "HoldConnections", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString()))
                    chkHold.Checked = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
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
        private volatile bool isTesting = false;
        private volatile bool isHoldingConnections = false;
        private bool holdConnectionsAfterLimit = false;
        private volatile bool isReleasingConnections = false;
        private readonly object testStateLock = new object();
        private long testRunId = 0;
        private Task currentTestTask = Task.CompletedTask;
        private long lastLoggedSuccess = 0;
        private long lastLoggedFail = 0;
        private ConcurrentBag<Socket> socketPool = new ConcurrentBag<Socket>();
        private string detectedLocalEndPoint;
        private bool detectedLocalEndPointDisplayed = false;
        private Stopwatch testStopwatch = new Stopwatch();
        private Font holdTimeBoldFont;
        private Font holdTimeRegularFont;
        private Font holdButtonBoldFont;
        private Font holdButtonRegularFont;
        private bool holdTimeUseBoldFont = true;
        private long lastProgressSuccessCount = 0;
        private DateTime stallStartTime = DateTime.MinValue;
        private DateTime lastStallWarning = DateTime.MinValue;
        private System.Windows.Forms.Timer titleTimer;

        public ConnectionTest()
        {
            InitializeComponent();
            holdTimeBoldFont = lblTime2.Font;
            holdTimeRegularFont = new Font(lblTime2.Font, FontStyle.Regular);
            holdButtonBoldFont = btnStart.Font;
            holdButtonRegularFont = new Font(btnStart.Font, FontStyle.Regular);
            titleTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            titleTimer.Tick += titleTimer_Tick;
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

        private void ApplyConnectionTheme()
        {
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoControls = {
        label2, lblOK, lblFail, lblTime, lblVersion
    };
            foreach (var c in yumeyoControls) { if (c != null) c.ForeColor = yumeyoColor; }

            Control[] contrastControls = {
        lblMaxTry, lblMaxFail, lblThread, lblTimeRest, label1, lblNIC, chkHold,
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
                        btn.FlatAppearance.MouseOverBackColor = Global.Yumeyo;
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
            ApplyConnectionTheme();
            var portStatus = GetSystemDynamicPortRange();
            InitNICList();
            lblVersion.Text = Global.exeName + " " + Global.Version + " | " + Others.GetCurrentTime();
            this.Text = $"最大连接数测试(TCP) ✧ NICX (mP:{portStatus.num}({portStatus.start}))";
            CloudControl.LoadConnectionServers(comboServer);
            CloudControl.ApplyDevTitle(this);
            timer1.Start();
            titleTimer.Start();
            CloudControl.UsedTimesCounter("连接数测试");
            LoadSettings();
        }

        private void EnsureSelectedNICValid()
        {
            string selectedText = comboNIC.Text;
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
            chkHold.Enabled = state;

            btnStart.Font = holdButtonBoldFont;
            btnStart.Text = state ? "开测" : "停止";
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (isReleasingConnections) return;

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

            string localIPText = comboNIC.Text.Trim();

            string cleanLocalIP = localIPText.Split(' ')[0]
                                             .Replace("[", "")
                                             .Replace("]", "");

            IPAddress.TryParse(targetIP, out IPAddress targetAddr);
            IPAddress.TryParse(cleanLocalIP, out IPAddress localAddr);

            if (targetAddr == null || localAddr == null)
            {
                txtFail.AppendText("\r\n[启动失败] 目标地址或本地地址无效");
                SystemSounds.Beep.Play();
                return;
            }

            if (targetAddr != null && localAddr != null)
            {
                bool isTargetV6 = targetAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                bool isLocalV6 = localAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

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

            long currentTestRunId;
            lock (testStateLock)
            {
                currentTestRunId = Interlocked.Increment(ref testRunId);
                holdConnectionsAfterLimit = chkHold.Checked;
                isHoldingConnections = false;
                isReleasingConnections = false;
                isTesting = true;
            }
            SetUIState(false);

            testStopwatch.Restart();
            timerUpdate.Start();

            cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            currentTestTask = StartTestingEngine(targetAddr, port, maxTry, maxFail, threads, interval, localAddr,
                cts.Token, currentTestRunId, startSignal.Task);
            startSignal.SetResult(true);
        }

        private void StopTest(string reason, long? expectedRunId = null)
        {
            ConcurrentBag<Socket> socketsToDispose;
            CancellationTokenSource sourceToDispose;
            Task workersToWait;
            lock (testStateLock)
            {
                if (!isTesting || (expectedRunId.HasValue && testRunId != expectedRunId.Value)) return;

                isTesting = false;
                isHoldingConnections = false;
                isReleasingConnections = true;
                socketsToDispose = socketPool;
                socketPool = new ConcurrentBag<Socket>();
                sourceToDispose = cts;
                cts = null;
                workersToWait = currentTestTask ?? Task.CompletedTask;
                currentTestTask = Task.CompletedTask;
            }

            if (sourceToDispose != null)
            {
                try { sourceToDispose.Cancel(); } catch { }
            }

            testStopwatch.Stop();
            PostToUI(() =>
            {
                timerUpdate.Stop();
                btnStart.Enabled = false;
                btnStart.Font = holdButtonBoldFont;
                btnStart.Text = "正在释放...";
            });

            _ = CompleteStopAsync(reason, socketsToDispose, workersToWait, sourceToDispose);
        }

        private async Task CompleteStopAsync(string reason, ConcurrentBag<Socket> socketsToDispose,
            Task workersToWait, CancellationTokenSource sourceToDispose)
        {
            try
            {
                try { await workersToWait.ConfigureAwait(false); } catch { }

                await Task.Run(() =>
                {
                    foreach (var socket in socketsToDispose)
                    {
                        try { socket.Dispose(); } catch { }
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                if (sourceToDispose != null)
                {
                    try { sourceToDispose.Dispose(); } catch { }
                }

                lock (testStateLock)
                {
                    isReleasingConnections = false;
                }
            }

            PostToUI(() =>
            {
                holdTimeUseBoldFont = true;
                lblTime2.Font = holdTimeBoldFont;
                btnStart.Font = holdButtonBoldFont;
                UpdateStatsLabels();
                SetUIState(true);
                btnStart.Enabled = true;
                var portStatus = GetSystemDynamicPortRange();
                this.Text = $"最大连接数测试(TCP) ✧ NICX (mP:{portStatus.num}({portStatus.start}))";
                CloudControl.ApplyDevTitle(this);
                string stopLog = $"\r\n==========================\r\n" +
                                 $" [测试停止] {reason}\r\n" +
                                 $" ❤ 最终成功：{Interlocked.Read(ref successCount)}\r\n" +
                                 $" ❤ 最终失败：{Interlocked.Read(ref failCount)}\r\n" +
                                 $"==========================";
                txtFail.AppendText(stopLog);
                txtFail.SelectionStart = txtFail.Text.Length;
                txtFail.ScrollToCaret();
            });
        }

        private void PostToUI(Action action)
        {
            if (this.IsDisposed || this.Disposing || !this.IsHandleCreated) return;

            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (InvalidOperationException) { }
        }

        private void HandleAutomaticStop(string reason, long runId)
        {
            bool shouldHold;
            lock (testStateLock)
            {
                if (!isTesting || testRunId != runId) return;
                shouldHold = holdConnectionsAfterLimit;
            }

            if (shouldHold)
            {
                EnterHoldState(reason, runId);
            }
            else
            {
                StopTest(reason, runId);
            }
        }

        private void EnterHoldState(string reason, long runId)
        {
            CancellationTokenSource activeSource;
            lock (testStateLock)
            {
                if (!isTesting || testRunId != runId || isHoldingConnections) return;
                isHoldingConnections = true;
                activeSource = cts;
            }

            testStopwatch.Stop();
            try { activeSource?.CancelAfter(Timeout.Infinite); } catch { }

            PostToUI(() =>
            {
                lblTime2.Text = testStopwatch.Elapsed.TotalSeconds.ToString("0");
                btnStart.Text = "停止";
                string holdLog = $"\r\n==========================\r\n" +
                                 $" [进入连接保持状态] {reason}\r\n" +
                                 $" 请点击“停止”或关闭窗口以释放连接\r\n" +
                                 $"==========================";
                txtFail.AppendText(holdLog);
                txtFail.SelectionStart = txtFail.Text.Length;
                txtFail.ScrollToCaret();
            });
        }

        private bool IsCurrentTestRun(long runId)
        {
            return isTesting && Interlocked.Read(ref testRunId) == runId;
        }

        private bool TryKeepConnectedSocket(Socket socket, long runId)
        {
            lock (testStateLock)
            {
                if (!isTesting || testRunId != runId) return false;
                socketPool.Add(socket);
                if (detectedLocalEndPoint == null && socket.LocalEndPoint is IPEndPoint localEndPoint)
                {
                    detectedLocalEndPoint = localEndPoint.Address.ToString();
                }
                Interlocked.Increment(ref successCount);
                return true;
            }
        }

        private bool TryReserveAttempt(int maxTry, long runId)
        {
            lock (testStateLock)
            {
                if (!isTesting || testRunId != runId || isHoldingConnections) return false;
                if (totalTried >= maxTry) return false;
                totalTried++;
                return true;
            }
        }

        private void RecordFailure(long runId)
        {
            lock (testStateLock)
            {
                if (!isTesting || testRunId != runId) return;
                Interlocked.Increment(ref failCount);
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
                MessageBox.Show("失败上限需 10-99999 之间");
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
            detectedLocalEndPoint = null;
            detectedLocalEndPointDisplayed = false;
            holdTimeUseBoldFont = true;
            lblTime2.Font = holdTimeBoldFont;
            btnStart.Font = holdButtonBoldFont;

            lblOK2.Text = "-";
            lblFail2.Text = "-";
            lblTime2.Text = "-";

            txtSuccess.Text = "成功次数统计\n";
            txtFail.Text = "失败次数统计\n";
        }

        private static async Task<bool> ConnectWithTimeoutAsync(Socket socket, IPAddress targetAddress, int port,
            int timeoutMilliseconds, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (var timeoutTimer = new System.Threading.Timer(_ =>
            {
                if (completion.TrySetResult(false))
                {
                    try { socket.Close(); } catch { }
                }
            }, null, timeoutMilliseconds, Timeout.Infinite))
            using (token.Register(() =>
            {
                if (completion.TrySetCanceled())
                {
                    try { socket.Close(); } catch { }
                }
            }))
            {
                try
                {
                    socket.BeginConnect(targetAddress, port, asyncResult =>
                    {
                        try
                        {
                            socket.EndConnect(asyncResult);
                            completion.TrySetResult(socket.Connected);
                        }
                        catch
                        {
                            completion.TrySetResult(false);
                        }
                    }, null);
                }
                catch
                {
                    completion.TrySetResult(false);
                }

                try
                {
                    return await completion.Task.ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
            }
        }

        private async Task DoConnectAsync(IPAddress targetAddress, int port, IPAddress localAddress, int interval,
            int maxTry, int maxFail, CancellationToken token, long runId, Task startSignal)
        {
            await startSignal.ConfigureAwait(false);

            AddressFamily family = targetAddress.AddressFamily;

            while (!token.IsCancellationRequested && IsCurrentTestRun(runId) && !isHoldingConnections)
            {
                if (!TryReserveAttempt(maxTry, runId)) break;

                Socket socket = null;
                bool socketWasKept = false;
                try
                {
                    socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                        LingerState = new LingerOption(true, 0)
                    };

                    socket.Bind(new IPEndPoint(localAddress, 0));

                    bool connected = await ConnectWithTimeoutAsync(socket, targetAddress, port, 2000, token)
                        .ConfigureAwait(false);

                    if (connected && socket.Connected)
                    {
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        if (TryKeepConnectedSocket(socket, runId))
                        {
                            socketWasKept = true;
                        }
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        RecordFailure(runId);
                    }
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                    {
                        RecordFailure(runId);
                    }
                }
                finally
                {
                    if (!socketWasKept && socket != null)
                    {
                        try { socket.Dispose(); } catch { }
                    }
                }

                CheckLimits(maxTry, maxFail, runId);

                if (interval > 0)
                {
                    try { await Task.Delay(interval, token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { break; }
                }
            }
        }

        private Task StartTestingEngine(IPAddress targetAddress, int port, int maxTry, int maxFail, int threads,
            int interval, IPAddress localAddress, CancellationToken token, long runId, Task startSignal)
        {
            var workers = new Task[threads];
            for (int i = 0; i < threads; i++)
            {
                workers[i] = DoConnectAsync(targetAddress, port, localAddress, interval, maxTry, maxFail, token, runId,
                    startSignal);
            }
            return Task.WhenAll(workers);
        }

        private void UpdateStatsLabels()
        {
            long currentOK = Interlocked.Read(ref successCount);
            long currentFail = Interlocked.Read(ref failCount);

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
        }

        private void CheckLimits(int maxTry, int maxFail, long runId)
        {
            if (!IsCurrentTestRun(runId)) return;

            if (Interlocked.Read(ref totalTried) >= maxTry)
            {
                HandleAutomaticStop("已达到尝试次数上限", runId);
            }
            else if (Interlocked.Read(ref failCount) >= maxFail)
            {
                HandleAutomaticStop("已达到失败次数上限", runId);
            }
        }

        private void UpdateLocalIP(string actualIP)
        {
            if (string.IsNullOrEmpty(actualIP)) return;

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
            titleTimer.Stop();
            titleTimer.Dispose();
            timerUpdate.Stop();
            timerUpdate.Dispose();
            if (holdTimeRegularFont != null)
            {
                if (ReferenceEquals(lblTime2.Font, holdTimeRegularFont))
                {
                    lblTime2.Font = holdTimeBoldFont;
                }
                holdTimeRegularFont.Dispose();
                holdTimeRegularFont = null;
            }
            if (holdButtonRegularFont != null)
            {
                if (ReferenceEquals(btnStart.Font, holdButtonRegularFont))
                {
                    btnStart.Font = holdButtonBoldFont;
                }
                holdButtonRegularFont.Dispose();
                holdButtonRegularFont = null;
            }
        }

        private void timerUpdate_Tick(object sender, EventArgs e)
        {
            if (isTesting)
            {
                lblTime2.Text = testStopwatch.Elapsed.TotalSeconds.ToString("0");
                UpdateStatsLabels();

                if (!detectedLocalEndPointDisplayed)
                {
                    string localAddress;
                    lock (testStateLock)
                    {
                        localAddress = detectedLocalEndPoint;
                    }
                    if (!string.IsNullOrEmpty(localAddress))
                    {
                        UpdateLocalIP(localAddress);
                        detectedLocalEndPointDisplayed = true;
                    }
                }

                if (!isHoldingConnections && cts != null && cts.IsCancellationRequested)
                {
                    HandleAutomaticStop("已达到10分钟测试时限", Interlocked.Read(ref testRunId));
                    return;
                }

                if (isHoldingConnections)
                {
                    holdTimeUseBoldFont = !holdTimeUseBoldFont;
                    lblTime2.Font = holdTimeUseBoldFont
                        ? holdTimeBoldFont
                        : holdTimeRegularFont;
                    btnStart.Font = holdTimeUseBoldFont
                        ? holdButtonBoldFont
                        : holdButtonRegularFont;
                    return;
                }

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
                            HandleAutomaticStop("连续90秒无成功连接，自动停止",
                                Interlocked.Read(ref testRunId));
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
                SaveSettings();

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

        private void titleTimer_Tick(object sender, EventArgs e)
        {
            if (isTesting)
            {
                string memInfo = GetMemoryUsageString();
                this.Text = $"最大连接数测试(TCP) ✧ NICX ({memInfo})";
                CloudControl.ApplyDevTitle(this);
            }
        }
    }
}
