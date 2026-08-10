using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class PingPP : Form
    {
        public PingPP()
        {
            InitializeComponent();
            this.MinimumSize = this.Size;
            _limitTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _limitTimer.Tick += LimitTimer_Tick;
        }

        double minDelay = 9999, maxDelay = 0, totalDelay = 0;
        int successCount = 0, lossCount = 0;
        double _lastRtt = -1;
        private readonly List<(double diff, DateTime time)> _jitterWindow = new List<(double diff, DateTime time)>();
        private static readonly TimeSpan JitterWindowSize = TimeSpan.FromSeconds(1);
        private int _tickCurrentSec = -1;
        private readonly List<(int secBucket, int count)> _tickPerSec = new List<(int secBucket, int count)>();
        int minCountIndex = 0, maxCountIndex = 0;
        private ushort _globalIcmpSequence = 0;

        private CancellationTokenSource _cts;
        private bool isRunning = false;
        private bool _isClosing = false;
        private static int _activeTests = 0;
        private bool isSettingsPrinted = false;
        private readonly Random _random = new Random();
        private bool _fatalError;
        private string _fatalErrorMessage;
        private string _lastExceptionMessage;
        private DateTime _sessionStartTime;
        private string _startTimeStr;
        private int _shardIndex;
        private int _lastShardSec;
        private double _baselineTps;
        private int _nextCheckSec;
        private double _scheduleDelaySum;
        private int _scheduleDelayCount;
        private int _loopIterationCount;
        private int _rateDegradationCount;
        private double _baselineIterations;
        private System.Windows.Forms.Timer _limitTimer;
        private int _remainingSeconds;
        private PingChart _chartForm;
        private bool _chartDisabled;
        private PrivateFontCollection _privateFonts;
        private Font _pingOutputFont;
        private bool _privateFontLeaseAcquired;

        // PrivateFontCollection 只让 GDI+ 看到字体；RichEdit 还需要
        // 进程私有的 GDI 字体资源，才能按字体名称正确找到它。
        private const uint FontResourcePrivate = 0x0010;
        private const int WmFontChange = 0x001D;
        private const string CascadiaMonoFamilyName = "Cascadia Mono";
        private static readonly object PingPrivateFontSync = new object();
        private static string _registeredPingFontPath;
        private static int _registeredPingFontUsers;
        private static bool _pingPrivateFontRegistered;

        // 高频输出缓冲
        private readonly List<(string text, Color color, bool newLine)> _outputBuffer = new List<(string text, Color color, bool newLine)>();
        private readonly Stopwatch _flushWatch = Stopwatch.StartNew();
        private bool _useBufferedOutput;
        private bool _suppressOutput;
        private bool _whiteTextOnly;
        private const int FlushIntervalMs = 100;
        private int _flushIntervalMs = 100;

        // UpdateStats 节流
        private readonly Stopwatch _statsWatch = Stopwatch.StartNew();
        private const int StatsIntervalMs = 200;
        private bool _forceStats;

        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);
        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);
        [DllImport("winmm.dll")]
        private static extern int timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")]
        private static extern int timeEndPeriod(uint uPeriod);
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AddFontResourceEx(string fileName, uint flags,
            IntPtr reserved);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveFontResourceEx(string fileName, uint flags,
            IntPtr reserved);
        private const int WM_SETREDRAW = 0x000B;
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "PingPP";

        private void SaveSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(comboTarget.Text))
                    WritePrivateProfileString(IniSection, "Target", comboTarget.Text, IniPath);
                WritePrivateProfileString(IniSection, "MaxDelay", txtMaxDelay.Text, IniPath);
                WritePrivateProfileString(IniSection, "Package", txtPackage.Text, IniPath);
                WritePrivateProfileString(IniSection, "Port", txtPort.Text, IniPath);
                string proto = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");
                WritePrivateProfileString(IniSection, "Protocol", proto, IniPath);
                if (comboFreq.SelectedItem != null)
                    WritePrivateProfileString(IniSection, "Freq", comboFreq.SelectedItem.ToString(), IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var sb = new StringBuilder(256);
                GetPrivateProfileString(IniSection, "Target", "", sb, sb.Capacity, IniPath);
                string target = sb.ToString();
                if (!string.IsNullOrEmpty(target) && comboTarget.Items.Count > 0)
                {
                    int idx = -1;
                    for (int i = 0; i < comboTarget.Items.Count; i++)
                        if (comboTarget.Items[i].ToString() == target) { idx = i; break; }
                    if (idx >= 0) comboTarget.SelectedIndex = idx;
                    else comboTarget.Text = target;
                }
                string val;
                GetPrivateProfileString(IniSection, "MaxDelay", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtMaxDelay.Text = val;
                GetPrivateProfileString(IniSection, "Package", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtPackage.Text = val;
                GetPrivateProfileString(IniSection, "Port", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtPort.Text = val;
                GetPrivateProfileString(IniSection, "Protocol", "", sb, sb.Capacity, IniPath);
                string proto = sb.ToString();
                if (proto == "TCP") radioTCP.Checked = true;
                else if (proto == "UDP") radioUDP.Checked = true;
                else if (proto == "ICMP") radioICMP.Checked = true;
                GetPrivateProfileString(IniSection, "Freq", "", sb, sb.Capacity, IniPath);
                string freq = sb.ToString();
                if (!string.IsNullOrEmpty(freq))
                {
                    for (int i = 0; i < comboFreq.Items.Count; i++)
                        if (comboFreq.Items[i].ToString() == freq) { comboFreq.SelectedIndex = i; break; }
                }
            }
            catch { }
        }

        private void FillRandomBytes(byte[] buffer)
        {
            lock (_random)
            {
                _random.NextBytes(buffer);
            }
        }

        private void UpdateDelay(double rtt)
        {
            int currentTotal = successCount + lossCount;

            if (rtt < minDelay)
            {
                minDelay = rtt;
                minCountIndex = currentTotal;
            }
            if (rtt > maxDelay)
            {
                maxDelay = rtt;
                maxCountIndex = currentTotal;
            }
            totalDelay += rtt;

            if (_lastRtt >= 0)
            {
                _jitterWindow.Add((rtt - _lastRtt, DateTime.Now));
            }
            _lastRtt = rtt;

            int sec = (int)(DateTime.Now - _sessionStartTime).TotalSeconds;
            if (sec != _tickCurrentSec)
            {
                _tickPerSec.Add((sec, 1));
                _tickCurrentSec = sec;
                if (_tickPerSec.Count > 5) _tickPerSec.RemoveAt(0);
            }
            else
            {
                var last = _tickPerSec[_tickPerSec.Count - 1];
                _tickPerSec[_tickPerSec.Count - 1] = (last.secBucket, last.count + 1);
            }

            if (!_suppressOutput && _chartForm != null && !_chartForm.IsDisposed)
            {
                double elapsed = (DateTime.Now - _sessionStartTime).TotalSeconds;
                _chartForm.AddDataPoint(elapsed, rtt);
            }
        }

        private void ResetStats()
        {
            minDelay = 9999; maxDelay = 0; totalDelay = 0;
            successCount = 0; lossCount = 0;
            minCountIndex = 0; maxCountIndex = 0;
            _lastRtt = -1; _jitterWindow.Clear();
            _tickCurrentSec = -1; _tickPerSec.Clear();
            _baselineTps = 0; _nextCheckSec = 0; _rateDegradationCount = 0;
            _scheduleDelaySum = 0; _scheduleDelayCount = 0;
            _loopIterationCount = 0; _rateDegradationCount = 0; _baselineIterations = 0;
            _lastShardSec = 0;
            isSettingsPrinted = false;
            if (_chartForm != null && !_chartForm.IsDisposed)
            {
                try { _chartForm.SetInfo("", "", 1); } catch { }
            }
            UpdateStats();
        }

        private IPEndPoint GetLocalEndPoint()
        {
            string selected = comboLocalEnd.Text;

            if (selected.Contains("0.0.0.0")) return new IPEndPoint(IPAddress.Any, 0);
            if (selected.Contains("::")) return new IPEndPoint(IPAddress.IPv6Any, 0);

            if (selected.Contains(" "))
            {
                selected = selected.Split(' ')[0];
            }

            if (IPAddress.TryParse(selected, out IPAddress localIp))
            {
                return new IPEndPoint(localIp, 0);
            }

            return new IPEndPoint(IPAddress.Any, 0);
        }

        private async Task ExecuteUdpPing(string targetIp, int port, int timeout, CancellationToken token)
        {
            IPAddress targetAddr = IPAddress.Parse(targetIp);
            IPEndPoint remoteEP = new IPEndPoint(targetAddr, port);

            using (Socket socket = new Socket(targetAddr.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                try
                {
                    if (targetAddr.AddressFamily == AddressFamily.InterNetwork)
                        socket.DontFragment = true;
                    socket.Bind(GetLocalEndPoint());
                    socket.ReceiveTimeout = timeout;

                    try
                    {
                        var localEp = (IPEndPoint)socket.LocalEndPoint;
                        if (localEp != null && (localEp.Address.Equals(IPAddress.Any) || localEp.Address.Equals(IPAddress.IPv6Any)))
                        {
                            string outbound = GetActualLocalIp(targetIp);
                            if (outbound != null)
                            {
                                PrintTestSettings(outbound.ToString());
                            }
                        }
                    }
                    catch { }

                    byte[] sendData;
                    if (port == 53)
                    {
                        string randomDomain = GenerateRandomDomain();
                        sendData = BuildSimpleDnsQuery(randomDomain);
                        Debug.WriteLine($"[{GetTimeStr()}] DNS查询域名: {randomDomain}");
                    }
                    else if (port == 123)
                    {
                        sendData = new byte[48];
                        sendData[0] = 0x1B;
                    }
                    else if (port == 3478 || port == 3489 || port == 19302)
                    {
                        sendData = new byte[20];

                        sendData[0] = 0x00;
                        sendData[1] = 0x01;

                        sendData[2] = 0x00;
                        sendData[3] = 0x00;

                        sendData[4] = 0x21;
                        sendData[5] = 0x12;
                        sendData[6] = 0xA4;
                        sendData[7] = 0x42;

                        byte[] ts = BitConverter.GetBytes(DateTime.Now.Ticks);
                        Array.Copy(ts, 0, sendData, 8, Math.Min(ts.Length, 12));
                    }
                    else
                    {
                        sendData = new byte[int.TryParse(txtPackage.Text, out int b) ? b : 32];
                        FillRandomBytes(sendData);
                    }

                    byte[] receiveBuffer = new byte[4096];
                    EndPoint receiveEP = new IPEndPoint(targetAddr.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

                    string timeStr = GetTimeStr();
                    var sw = new Stopwatch();

                    var receiveTask = Task.Factory.FromAsync(
                        (callback, state) => socket.BeginReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref receiveEP, callback, state),
                        (ar) => socket.EndReceiveFrom(ar, ref receiveEP),
                        null);

                    sw.Start();
                    socket.SendTo(sendData, remoteEP);

                    var completed = await Task.WhenAny(receiveTask, Task.Delay(timeout, token));
                    int currentTotal = successCount + lossCount + 1;

                    if (completed == receiveTask)
                    {
                        int received = receiveTask.Result;
                        sw.Stop();
                        double rtt = sw.Elapsed.TotalMilliseconds;
                        if (rtt < 0.1) rtt = 0.1;

                        successCount++;
                        UpdateDelay(rtt);

                        string localIpToPrint = "系统默认";
                        try
                        {
                            var localEp = (IPEndPoint)socket.LocalEndPoint;
                            if (localEp != null)
                            {
                                localIpToPrint = localEp.Address.ToString();
                            }
                        }
                        catch { }

                        PrintTestSettings(localIpToPrint);

                        string remoteIp = ((IPEndPoint)receiveEP).Address.ToString();

                        Color rowColor = GetRttColor(rtt);
                        AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                        AppendColorText($"UDP成功: {remoteIp} ={FormatRtt(rtt)}ms", rowColor, true);

                    }
                    else
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                        AppendColorText($"UDP失败: 请求超时({timeout}ms)", Color.Red, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (IsRepeatingException(ex.Message))
                        {
                            _fatalError = true;
                            _fatalErrorMessage = $"[UDP] {ex.Message}";
                            return;
                        }
                        lossCount++;
                        AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                        AppendColorText($"UDP错误: {ex.Message}", Color.Yellow, true);
                    }
                }
                UpdateStats();
            }
        }

        private string GenerateRandomDomain()
        {
            Random random = new Random(Guid.NewGuid().GetHashCode());

            string[] tlds = { ".nstool.netease.com" };

            int nameLength = random.Next(4, 16);
            string domainName = GenerateRandomString(nameLength, random);

            string tld = tlds[random.Next(tlds.Length)];

            return domainName + tld;
        }

        private string GenerateRandomString(int length, Random random)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            char[] result = new char[length];

            for (int i = 0; i < length; i++)
            {
                result[i] = chars[random.Next(chars.Length)];
            }

            if (char.IsDigit(result[0]))
            {
                result[0] = chars[random.Next(26)];
            }

            return new string(result);
        }

        private byte[] BuildSimpleDnsQuery(string domain)
        {
            byte[] header = new byte[] {
        0x00, 0x01,
        0x01, 0x00,
        0x00, 0x01,
        0x00, 0x00,
        0x00, 0x00,
        0x00, 0x00
    };

            Random rand = new Random();
            byte[] transId = BitConverter.GetBytes((ushort)rand.Next(0, 65535));
            if (BitConverter.IsLittleEndian)
                Array.Reverse(transId);

            header[0] = transId[0];
            header[1] = transId[1];

            List<byte> queryBytes = new List<byte>();
            string[] labels = domain.Split('.');

            foreach (string label in labels)
            {
                queryBytes.Add((byte)label.Length);
                queryBytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }

            queryBytes.Add(0x00);

            queryBytes.AddRange(new byte[] { 0x00, 0x01 });

            queryBytes.AddRange(new byte[] { 0x00, 0x01 });

            byte[] fullQuery = new byte[header.Length + queryBytes.Count];
            Buffer.BlockCopy(header, 0, fullQuery, 0, header.Length);
            Buffer.BlockCopy(queryBytes.ToArray(), 0, fullQuery, header.Length, queryBytes.Count);

            return fullQuery;
        }

        private async Task ExecuteTcpPing(string targetIp, int port, int timeout, CancellationToken token)
        {
            IPAddress ipAddr = IPAddress.Parse(targetIp);
            IPEndPoint remoteEP = new IPEndPoint(ipAddr, port);

            Socket socket = null;
            try
            {
                socket = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                socket.LingerState = new LingerOption(false, 0);
                socket.ExclusiveAddressUse = false;
                socket.NoDelay = true;

                socket.Bind(GetLocalEndPoint());
                string timeStr = GetTimeStr();

                var sw = new Stopwatch();

                var connectTask = Task.Factory.FromAsync(
                    socket.BeginConnect,
                    socket.EndConnect,
                    remoteEP,
                    null
                );
                int currentTotal = successCount + lossCount + 1;
                sw.Start();

                var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeout, token));

                if (completedTask == connectTask && socket.Connected)
                {
                    sw.Stop();
                    double rtt = sw.Elapsed.TotalMilliseconds;

                    if (rtt < 0.1) rtt = 0.1;
                    successCount++;
                    UpdateDelay(rtt);

                    string actualIp = ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
                    PrintTestSettings(actualIp);

                    Color rowColor = GetRttColor(rtt);
                    AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);

                    string displayTarget = ipAddr.AddressFamily == AddressFamily.InterNetworkV6
                        ? $"[{targetIp}]:{port}"
                        : $"{targetIp}:{port}";

                    AppendColorText($"TCP成功: {displayTarget} ={FormatRtt(rtt)}ms", rowColor, true);
                }
                else
                {
                    lossCount++;
                    AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                    AppendColorText($"TCP失败: 连接超时({timeout}ms)", Color.Red, true);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    if (IsRepeatingException(ex.Message))
                    {
                        _fatalError = true;
                        _fatalErrorMessage = $"[TCP] {ex.Message}";
                        return;
                    }
                    lossCount++;
                    AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                    AppendColorText($"TCP错误: {ex.Message}", Color.Yellow, true);
                }
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(0); socket.Dispose(); } catch { }
                }
                UpdateStats();
            }
        }

        private void RadioProtocol_CheckedChanged(object sender, EventArgs e)
        {
            UpdateProtocolUI(sender, true);
        }
        private void UpdateProtocolUI(object sender, bool autoClear)
        {
            if (!(sender as RadioButton).Checked) return;

            if (autoClear)
            {
                richTextBox1.Clear();
                ResetStats();
            }

            if (radioICMP.Checked)
            {
                if (autoClear)
                {
                    AppendColorText("     ==== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ====", Global.Yumeyo2, true);
                    AppendColorText("当前选中 ICMP 协议，请先阅读下列提示：", Color.Lime, true);
                    AppendColorText("    🔰 ICMP Ping 已更新Socket指定网卡测试 (精度0.1ms) 💦", Color.White, true);
                    AppendColorText("        ❤若指定网卡时频繁意外丢包, 影响判断, 请选\"ICMP兼容模式\"网卡, ", Color.Yellow, true);
                    AppendColorText("          以使用原生Ping更稳定, 但无法识别/指定网卡 (精度1ms)", Color.Yellow, true);
                    AppendColorText("        ❤还有问题，可尝试以管理员运行查询器X后再测❤ ", Color.LightPink, true);
                    AppendColorText("    ICMP 无端口测试，不支持分片, 最大包受本机MTU影响(MTU-28=最大包)", Color.White, true);
                    AppendColorText("    🚀 查询器X已更新Ping发包频率设置(Tickrate), 请按需设置, 避免滥用❤ \n", Color.Gold, true);
                    AppendColorText("    ❤ 延迟颜色对照表", Color.LightSkyBlue, true);
                    AppendColorMap();
                    txtPort.Text = "0";
                }
                comboLocalEnd.Enabled = true;
                txtPort.Enabled = false;
                txtPackage.Enabled = true;
            }
            else if (radioUDP.Checked)
            {
                if (autoClear)
                {
                    AppendColorText("     ==== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ====", Global.Yumeyo2, true);
                    AppendColorText("当前选中 UDP 协议，请先阅读下列提示：", Color.Lime, true);
                    AppendColorText("  针对 🔥DNS(53) NTP(123) STUN(3478/3489/19302)🔥 端口已优化测试方法；", Color.LightPink, true);
                    AppendColorText("    🔰 其他端口将发送随机字节数据测试 💦", Color.White, true);
                    AppendColorText("       ❤ 建议优先使用上述3种协议的UDP服务器测试, ", Color.Yellow, true);
                    AppendColorText("       ❤ 上述3种协议会测试 ", Color.Yellow, false);
                    AppendColorText(" \"发起请求-收到回复\" ", Color.LightPink, false);
                    AppendColorText("整个过程的真实延迟\n", Color.Yellow, false);
                    AppendColorText("    🚀 查询器X已更新Ping发包频率设置(Tickrate), 请按需设置, 避免滥用❤ \n", Color.Gold, true);
                    AppendColorText("    ❤ 延迟颜色对照表", Color.LightSkyBlue, true);
                    AppendColorMap();
                    txtPort.Text = "53";
                }
                comboLocalEnd.Enabled = true;
                txtPort.Enabled = true;
                txtPackage.Enabled = true;
            }
            else if (radioTCP.Checked)
            {
                if (autoClear)
                {
                    AppendColorText("     ==== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ====", Global.Yumeyo2, true);
                    AppendColorText("当前选中 TCP 协议，请先阅读下列提示：", Color.Lime, true);
                    AppendColorText("    通过 🔰 TcpClient 🔰 尝试握手连接；包大小无影响延迟已禁用设置 💦", Color.White, true);
                    AppendColorText("      ❤ 通常用于探测 🔥 80/443 🔥 等端口是否开放\n", Color.White, true);
                    AppendColorText("    🚀 查询器X已更新Ping发包频率设置(Tickrate), 请按需设置, 避免滥用❤ \n", Color.Gold, true);
                    AppendColorText("    ❤ 延迟颜色对照表", Color.LightSkyBlue, true);
                    AppendColorMap();
                    txtPort.Text = "80";
                }
                comboLocalEnd.Enabled = true;
                txtPort.Enabled = true;
                txtPackage.Enabled = false;
            }
        }

        private void CleanupTempFiles()
        {
            if (_activeTests > 0) return;
            try
            {
                foreach (string file in System.IO.Directory.GetFiles(Application.StartupPath, "NICX_Ping_Temp_*.txt"))
                    System.IO.File.Delete(file);
            }
            catch { }
        }

        private bool ChartDependenciesAvailable()
        {
            string dir = Application.StartupPath;
            if (!File.Exists(Path.Combine(dir, "ScottPlot.WinForms.dll")) ||
                !File.Exists(Path.Combine(dir, "ScottPlot.dll")))
            {
                MessageBox.Show("未找到绘制折线图所需依赖，折线图窗口已禁用，如有需要请检查相关依赖是否完整",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private void PingPP_Load(object sender, EventArgs e)
        {
            ApplyHighDpiOutputFont();
            CleanupTempFiles();
            AppendColorText("✧ 正在检查系统环境，请稍候 ✧\n", Color.White, true);
            RadioProtocol_CheckedChanged(radioICMP, null);
            Task.Run(() => PingPPLoadAll());
            LoadSettings();
            CloudControl.LoadPingServers(comboTarget);
            if (comboFreq.SelectedIndex < 0) comboFreq.SelectedIndex = 0;
            CloudControl.UsedTimesCounter("PingPP");

            if (ChartDependenciesAvailable())
            {
                _chartForm = new PingChart();
                _chartForm.Location = new Point(this.Left, this.Bottom + 8);
                _chartForm.Show();
            }
            else
            {
                _chartDisabled = true;
            }
        }

        private void ApplyHighDpiOutputFont()
        {
            float dpi = DeviceDpi;
            try
            {
                using (Graphics graphics = CreateGraphics())
                    dpi = Math.Max(dpi, graphics.DpiX);
            }
            catch { }

            // 100% 缩放保留设计器中的新宋体；只有高于 100%（96 DPI）
            // 时才应用 Cascadia Mono。
            if (dpi <= 96F) return;

            Font selectedFont = null;
            string fontPath = Path.Combine(Application.StartupPath, "CascadiaMono.ttf");

            // 优先使用程序旁的 TTF，并且只注册到当前进程。
            if (File.Exists(fontPath))
            {
                PrivateFontCollection fontCollection = null;
                try
                {
                    fontCollection = new PrivateFontCollection();
                    fontCollection.AddFontFile(fontPath);
                    FontFamily family = fontCollection.Families.FirstOrDefault(item =>
                        string.Equals(item.Name, CascadiaMonoFamilyName,
                            StringComparison.OrdinalIgnoreCase));

                    if (family != null && TryAcquirePingPrivateFont(fontPath))
                    {
                        _privateFontLeaseAcquired = true;
                        SendMessage(richTextBox1.Handle, WmFontChange, 0, 0);
                        selectedFont = new Font(family, 9F, FontStyle.Regular,
                            GraphicsUnit.Point);
                        _privateFonts = fontCollection;
                        fontCollection = null;
                        Debug.WriteLine("Ping+ 输出字体：进程私有 CascadiaMono.ttf");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Ping+ 加载私有字体失败：" + ex.Message);
                    if (_privateFontLeaseAcquired)
                    {
                        ReleasePingPrivateFont();
                        _privateFontLeaseAcquired = false;
                    }
                }
                finally
                {
                    fontCollection?.Dispose();
                }
            }

            // 本地 TTF 不可用时，仅在系统确实安装了该字体族时回退；
            // 否则 new Font 会静默替换成默认字体。
            if (selectedFont == null && IsFontFamilyInstalled(CascadiaMonoFamilyName))
            {
                try
                {
                    selectedFont = new Font(CascadiaMonoFamilyName, 9F,
                        FontStyle.Regular, GraphicsUnit.Point);
                    Debug.WriteLine("Ping+ 输出字体：系统 Cascadia Mono");
                }
                catch { }
            }

            if (selectedFont != null)
            {
                _pingOutputFont = selectedFont;
                richTextBox1.Font = selectedFont;
                ApplyPingOutputSelectionFont();
                toolTip1.SetToolTip(richTextBox1,
                    "可使用Ctrl+滚轮缩放字体大小\n当前输出字体: " +
                    selectedFont.FontFamily.Name +
                    (_privateFontLeaseAcquired ? " (程序目录)" : " (系统)"));
            }
        }

        private void ApplyPingOutputSelectionFont()
        {
            if (_pingOutputFont == null || richTextBox1.IsDisposed) return;

            try
            {
                richTextBox1.SelectionLength = 0;
                richTextBox1.SelectionFont = _pingOutputFont;
            }
            catch { }
        }

        private static bool IsFontFamilyInstalled(string familyName)
        {
            try
            {
                using (var installedFonts = new InstalledFontCollection())
                {
                    return installedFonts.Families.Any(item =>
                        string.Equals(item.Name, familyName,
                            StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { return false; }
        }

        private static bool TryAcquirePingPrivateFont(string fontPath)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(fontPath); }
            catch { return false; }

            lock (PingPrivateFontSync)
            {
                if (_pingPrivateFontRegistered)
                {
                    if (!string.Equals(_registeredPingFontPath, fullPath,
                        StringComparison.OrdinalIgnoreCase)) return false;

                    _registeredPingFontUsers++;
                    return true;
                }

                if (AddFontResourceEx(fullPath, FontResourcePrivate, IntPtr.Zero) <= 0)
                    return false;

                _registeredPingFontPath = fullPath;
                _registeredPingFontUsers = 1;
                _pingPrivateFontRegistered = true;
                return true;
            }
        }

        private static void ReleasePingPrivateFont()
        {
            lock (PingPrivateFontSync)
            {
                if (!_pingPrivateFontRegistered || _registeredPingFontUsers <= 0)
                    return;

                _registeredPingFontUsers--;
                if (_registeredPingFontUsers != 0) return;

                if (RemoveFontResourceEx(_registeredPingFontPath,
                    FontResourcePrivate, IntPtr.Zero))
                {
                    _registeredPingFontPath = null;
                    _pingPrivateFontRegistered = false;
                }
            }
        }

        private void ReleasePingOutputFont()
        {
            // 先让 RichEdit 不再引用私有字体，再释放 GDI+/GDI 资源。
            try
            {
                if (!richTextBox1.IsDisposed && _pingOutputFont != null)
                    richTextBox1.Font = this.Font;
            }
            catch { }

            try { _pingOutputFont?.Dispose(); } catch { }
            _pingOutputFont = null;
            try { _privateFonts?.Dispose(); } catch { }
            _privateFonts = null;

            if (_privateFontLeaseAcquired)
            {
                ReleasePingPrivateFont();
                _privateFontLeaseAcquired = false;
            }
        }

        private void EnsureSelectedNICValid()
        {
            string selectedText = comboLocalEnd.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.Contains("系统默认") ||
                selectedText.Contains("ICMP兼容模式") || selectedText.StartsWith("0.0.0.0") ||
                selectedText.StartsWith("::")) return;

            PingPPLoadAll();

            bool found = false;
            foreach (var item in comboLocalEnd.Items)
            {
                if (item.ToString() == selectedText)
                {
                    comboLocalEnd.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found && comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;
        }

        private void PingPPLoadAll()
        {
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;

            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                {
                    comboLocalEnd.Items.Add(nicAddress.DisplayText);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取网卡列表失败: " + ex.Message);
            }

            _ = Task.Run(async () => await WarmUpAsync());
            CloudControl.ApplyDevTitle(this);
        }

        private async Task WarmUpAsync()
        {
            try
            {
                // 1. JIT 预热：触碰所有热路径类型和方法，触发即时编译
                GetRttColor(0); GetRttColor(100); GetRttColor(300);
                IPAddress.Parse("127.0.0.1");
                Dns.GetHostAddresses("localhost");
                var sw = Stopwatch.StartNew(); sw.Stop();

                // 2. ICMP：一次本地回环 ping，初始化 Ping 类内部句柄
                using (var ping = new Ping())
                {
                    var t = ping.SendPingAsync("127.0.0.1", 100);
                    await Task.WhenAny(t, Task.Delay(200));
                }

                // 3. TCP：一次本地连接（端口关着，秒拒），预热 TCP socket + Connect 路径
                using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    sock.ReceiveTimeout = 10; sock.SendTimeout = 10;
                    try { sock.Connect("127.0.0.1", 65500); } catch { }
                }

                // 4. UDP：一次本地发包，预热 UDP socket + SendTo 路径
                using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    try { sock.SendTo(new byte[1], new IPEndPoint(IPAddress.Loopback, 53)); } catch { }
                }
            }
            catch { }
        }

        private static void PreJitExecuteMethods()
        {
            try
            {
                var type = typeof(PingPP);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var methods = new[] { "ExecuteIcmpPing", "ExecuteNativeIcmpPing", "ExecuteTcpPing", "ExecuteUdpPing" };
                foreach (var name in methods)
                {
                    var m = type.GetMethod(name, flags);
                    if (m != null) RuntimeHelpers.PrepareMethod(m.MethodHandle);
                }

                // 同时预热内部辅助方法
                var helpers = new[] { "GetLocalEndPoint", "GetRttColor", "UpdateStats" };
                foreach (var name in helpers)
                {
                    var m = type.GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (m != null) RuntimeHelpers.PrepareMethod(m.MethodHandle);
                }
            }
            catch { }
        }

        private void PingPP_FormClosing(object sender, FormClosingEventArgs e)
        {
            _isClosing = true;
            SaveSettings();
            _cts?.Cancel();
            _limitTimer?.Stop();
            if (_activeTests <= 1) CleanupTempFiles();
            try { _chartForm?.Shutdown(); _chartForm?.Dispose(); } catch { }
            ReleasePingOutputFont();
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (isRunning)
            {
                AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                AppendColorText("正在停止上次测试", Global.Yumeyo2, true);
                _cts?.Cancel();
                await Task.Delay(10);
                _cts?.Cancel();
                return;
            }

            EnsureSelectedNICValid();

            if ((radioTCP.Checked || radioUDP.Checked) && comboLocalEnd.Text.Contains("ICMP兼容模式"))
            {
                richTextBox1.Clear();
                AppendColorText("\n\nTCP/UDP Test 需绑定指定网卡，ICMP兼容模式下不支持。\n请选择本机 IP 网卡或切换到 ICMP 协议。\n", Color.Yellow, true);
                return;
            }

            if (comboLocalEnd.Text.Contains("ICMP兼容模式") && GetPingFrequency() != 1)
            {
                ResetStats();
                richTextBox1.Clear();
                AppendColorText("\n\nICMP兼容模式使用系统原生Ping，精度仅1ms，效率有限。\n不支持自定义测试频率，请将频率改回 1 或切换到非兼容模式网卡。\n", Color.Yellow, true);
                return;
            }

            string input = comboTarget.Text.Trim().ToLower();

            if (input.StartsWith("http://")) input = input.Substring(7);
            else if (input.StartsWith("https://")) input = input.Substring(8);

            if (input.Contains("/"))
            {
                input = input.Split('/')[0];
            }

            input = Regex.Replace(input, @"[^a-z0-9\.\:\-_]", "");

            if (string.IsNullOrEmpty(input))
            {
                SystemSounds.Beep.Play();
                return;
            }

            comboTarget.Text = input;

            bool isDirectIp = IPAddress.TryParse(input, out _);
            bool isSelectedIp = input.Contains(" (来自域名:");

            if (!isDirectIp && !isSelectedIp)
            {
                try
                {
                    richTextBox1.Clear();
                    AppendColorText($"[DNS] 正在解析域名: {input} \n", Color.Yellow, true);

                    comboTarget.Items.Clear();
                    comboTarget.Items.Add(input);

                    IPAddress[] addresses = await Task.Run(() => Dns.GetHostAddresses(input));
                    // 去重：在某些网络环境下 Dns.GetHostAddresses 可能返回重复 IP
                    var uniqueAddresses = addresses.Distinct().ToArray();

                    AppendColorText($"域名 [{input}] 解析出以下 IP：", Color.Yellow, true);
                    foreach (var ip in uniqueAddresses)
                    {
                        string ipStr = ip.ToString();
                        if (!comboTarget.Items.Contains(ipStr))
                        {
                            comboTarget.Items.Add(ipStr);
                        }
                        richTextBox1.AppendText($" -> {ipStr}\n");
                    }
                    comboTarget.DroppedDown = true;
                    if (comboTarget.Items.Count == 2)
                    {
                        comboTarget.SelectedIndex = 1;
                        AppendColorText("\n DNS已解析。", Color.Yellow, true);
                        AppendColorText("    已经选择了，再次点击“开测”。\n", Color.Yellow, true);
                    }
                    else
                    {
                        AppendColorText("\n DNS已解析。", Color.Yellow, true);
                        AppendColorText("    请选择一个 IP 后，再次点击“开测”。\n", Color.Yellow, true);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    AppendColorText($"解析失败: {ex.Message}\n", Color.Orange, true);
                    return;
                }
            }

            string finalIP = input;
            comboTarget.Text = finalIP;

            // ICMP Raw Socket 模式下预校验地址族兼容性
            _fatalError = false;
            _fatalErrorMessage = null;
            _lastExceptionMessage = null;
            _sessionStartTime = DateTime.Now;
            _startTimeStr = _sessionStartTime.ToString("yyyyMMdd_HHmmss");
            _shardIndex = 0;
            if (radioICMP.Checked && !comboLocalEnd.Text.Contains("ICMP兼容模式"))
            {
                IPAddress targetAddr = IPAddress.Parse(finalIP);
                IPEndPoint localEp = GetLocalEndPoint();
                if ((targetAddr.AddressFamily == AddressFamily.InterNetwork && localEp.Address.Equals(IPAddress.IPv6Any)) ||
                    (targetAddr.AddressFamily == AddressFamily.InterNetworkV6 && localEp.Address.Equals(IPAddress.Any)))
                {
                    ResetStats();
                    richTextBox1.Clear();
                    AppendColorText($"\n\n目标IP [{finalIP}] 与所选网卡 [{comboLocalEnd.Text}] 的地址族不兼容。", Color.Yellow, true);
                    AppendColorText("请选择与目标IP协议版本一致的网卡后重试。\n", Color.Yellow, true);
                    comboTarget.Enabled = true;
                    txtMaxDelay.Enabled = true;
                    txtPackage.Enabled = true;
                    txtPort.Enabled = true;
                    comboLocalEnd.Enabled = true;
                    comboFreq.Enabled = true;
                    btnSave.Enabled = true;
                    radioICMP.Enabled = true;
                    radioTCP.Enabled = true;
                    radioUDP.Enabled = true;
                    return;
                }
            }

            CleanupTempFiles();
            ResetStats();
            richTextBox1.Clear();
            _cts = new CancellationTokenSource();
            isRunning = true;
            _activeTests++;
            btnStart.Text = "停止";
            timeBeginPeriod(1);
            //设置Ping太快时加入倒计时防止滥刷 当tick≥8时
            if (GetPingFrequency() > 8)
            {
                _remainingSeconds = Global.isUnlimitedTime ? 0 : 300;
                this.Text = Global.isUnlimitedTime
                    ? "Ping+ ✧ NetInfoCheckerX (0)"
                    : "Ping+ ✧ NetInfoCheckerX (300)";
                CloudControl.ApplyDevTitle(this);
                _limitTimer.Start();
            }
            SetControlsEnabled(false);

            string startTimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            AppendColorText($"[开测时间] {startTimeStr}", Color.Yellow, true);
            AppendColorText($"[测试目标] {input}", Color.Cyan, true);

            int timeout = int.TryParse(txtMaxDelay.Text, out int t) ? t : 2000;
            int port = int.TryParse(txtPort.Text, out int p) ? p : 80;
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;

            SetControlsEnabled(false);

            try
            {
                using (var warmupCts = new CancellationTokenSource(2000))
                {
                    PreJitExecuteMethods();

                    if (radioICMP.Checked)
                    {
                        try
                        {
                            IPAddress warmupAddr;
                            if (!IPAddress.TryParse(finalIP, out warmupAddr))
                                warmupAddr = (await Dns.GetHostAddressesAsync(finalIP)).FirstOrDefault();
                            if (warmupAddr != null)
                            {
                                using (Ping warmUpPing = new Ping())
                                {
                                    await warmUpPing.SendPingAsync(warmupAddr, Math.Min(timeout, 1000),
                                        new byte[32], new PingOptions(128, true));
                                }
                            }
                        }
                        catch { }
                    }
                    else if (radioTCP.Checked)
                    {
                        using (Socket s = new Socket(IPAddress.Parse(finalIP).AddressFamily,
                                                    SocketType.Stream, ProtocolType.Tcp))
                        {
                            s.LingerState = new LingerOption(false, 0);
                            s.ReceiveTimeout = 50;
                            s.SendTimeout = 50;

                            try
                            {
                                s.Bind(GetLocalEndPoint());

                                var connectTask = s.ConnectAsync(finalIP, port);
                                if (await Task.WhenAny(connectTask, Task.Delay(100, warmupCts.Token)) == connectTask)
                                {
                                    await connectTask;
                                }
                            }
                            catch { }
                        }
                    }
                    else if (radioUDP.Checked)
                    {
                        using (Socket s = new Socket(IPAddress.Parse(finalIP).AddressFamily,
                                                    SocketType.Dgram, ProtocolType.Udp))
                        {
                            s.Bind(GetLocalEndPoint());
                            s.ReceiveTimeout = 50;

                            try
                            {
                                var sendTask = s.SendToAsync(new ArraySegment<byte>(new byte[1]),
                                                            SocketFlags.None,
                                                            new IPEndPoint(IPAddress.Parse(finalIP), port));
                                await Task.WhenAny(sendTask, Task.Delay(50, warmupCts.Token));
                            }
                            catch { }
                        }
                    }

                    await Task.Delay(30, warmupCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch { }

            // Raw Socket ICMP 预热：消除首次测试的冷启动延迟
            if (radioICMP.Checked && !comboLocalEnd.Text.Contains("ICMP兼容模式"))
            {
                try
                {
                    int warmupTimeout = Math.Min(timeout, 500);
                    using (var warmupCts2 = new CancellationTokenSource(warmupTimeout + 300))
                    {
                        _suppressOutput = true;
                        await ExecuteIcmpPing(finalIP, warmupTimeout, warmupCts2.Token);
                    }
                }
                catch { }
                finally { _suppressOutput = false; _fatalError = false; }
                ResetStats();
                richTextBox1.Clear();
                AppendColorText($"[开测时间] {startTimeStr}", Color.Yellow, true);
                AppendColorText($"[测试目标] {input}", Color.Cyan, true);
            }

            string protocolName = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");
            string localDisplay = radioICMP.Checked ? "系统默认" : comboLocalEnd.Text;
            if (localDisplay.Contains("Any") && !radioICMP.Checked)
            {
                localDisplay = "自动选择中";
            }

            string version = radioICMP.Checked ? "IP" : (IPAddress.Parse(finalIP).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6");
            string checkTarget = comboLocalEnd.Text.Contains("::") ? "IPv6" : (comboLocalEnd.Text.Contains("0.0.0.0") ? "IPv4" : "");

            if (comboLocalEnd.Text.Contains("Any"))
            {
                AppendColorText($"[检测网卡] 未指定出口网卡, 开始测试[{checkTarget}]实际出口网卡", Color.LightGreen, true);
            }

            try
            {
                int freq = GetPingFrequency();
                long intervalTicks = (long)(Stopwatch.Frequency / (double)freq);
                _useBufferedOutput = freq >= 4;
                _whiteTextOnly = freq > 8;
                _flushIntervalMs = freq >= 64 ? 200 : 100;
                _flushWatch.Restart();
                _statsWatch.Restart();

                long nextFireTime = Stopwatch.GetTimestamp();

                if (!_chartDisabled && (_chartForm == null || _chartForm.IsDisposed))
                {
                    _chartForm = new PingChart();
                    _chartForm.Location = new Point(this.Left, this.Bottom + 8);
                    _chartForm.Show();
                }
                _chartForm?.SetInfo(finalIP, protocolName, freq);

                while (!_cts.Token.IsCancellationRequested)
                {
                    if (this.IsDisposed) break;

                    if (radioICMP.Checked)
                    {
                        if (comboLocalEnd.Text.Contains("ICMP兼容模式"))
                        {
                            await ExecuteNativeIcmpPing(finalIP, timeout, _cts.Token);
                        }
                        else
                        {
                            await ExecuteIcmpPing(finalIP, timeout, _cts.Token);
                        }
                    }
                    else if (radioTCP.Checked) await ExecuteTcpPing(finalIP, port, timeout, _cts.Token);
                    else await ExecuteUdpPing(finalIP, port, timeout, _cts.Token);

                    _loopIterationCount++;

                    if (_fatalError)
                    {
                        _useBufferedOutput = false;
                        _whiteTextOnly = false;
                        FlushOutputBuffer();
                        _cts.Cancel();
                        break;
                    }

                    // 批量刷新输出
                    if (_useBufferedOutput && _flushWatch.ElapsedMilliseconds >= _flushIntervalMs)
                    {
                        FlushOutputBuffer();
                        _flushWatch.Restart();
                    }

                    nextFireTime += intervalTicks;
                    long now = Stopwatch.GetTimestamp();
                    long waitTicks = nextFireTime - now;

                    // 记录调度延迟（仅软件侧开销，不受丢包影响）
                    double delayMs = waitTicks < 0 ? (-waitTicks * 1000.0 / Stopwatch.Frequency) : 0;
                    _scheduleDelaySum += delayMs;
                    _scheduleDelayCount++;

                    int shardBefore = _shardIndex;
                    CheckDegradation();
                    if (_shardIndex != shardBefore)
                    {
                        // 分片操作阻塞了IO，重置调度基准
                        nextFireTime = Stopwatch.GetTimestamp();
                        _scheduleDelaySum = 0;
                        _scheduleDelayCount = 0;
                        _loopIterationCount = 0;
                    }

                    if (waitTicks > 0)
                    {
                        int waitMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
                        if (waitMs >= 2)
                            await Task.Delay(waitMs - 1, _cts.Token);
                        while (Stopwatch.GetTimestamp() < nextFireTime)
                        {
                            if (_cts.Token.IsCancellationRequested) break;
                        }
                    }
                    else if (waitTicks < -intervalTicks * 5)
                    {
                        // 落后太多，重置时间基准
                        nextFireTime = now;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                try
                {
                    _useBufferedOutput = false;
                    _whiteTextOnly = false;
                    if (isRunning) { _activeTests--; }
                    isRunning = false;
                    _limitTimer.Stop();
                    timeEndPeriod(1);

                    if (!this.IsDisposed && this.IsHandleCreated)
                    {
                        FlushOutputBuffer();
                        this.Text = "Ping+ ✧ NetInfoCheckerX";
                        CloudControl.ApplyDevTitle(this);
                        string stopTimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        btnStart.Text = "开测";

                        if (_fatalError)
                        {
                            ResetStats();
                            AppendColorText($"{_fatalErrorMessage}", Color.Yellow, true);
                            AppendColorText(" ■ 报错自动停止测试\n", Color.Yellow, true);
                        }
                        else
                        {
                            AppendColorText($"[停止时间] {stopTimeStr} (本次测试总时长: {FormatDuration(DateTime.Now - _sessionStartTime)})", Color.Yellow, true);
                            AppendColorText(" ■ 用户手动停止测试", Color.Yellow, true);
                        }

                        comboTarget.Enabled = true;
                        txtMaxDelay.Enabled = true;
                        txtPackage.Enabled = true;
                        txtPort.Enabled = true;
                        comboLocalEnd.Enabled = true;
                        comboFreq.Enabled = true;
                        btnSave.Enabled = true;
                        if (radioICMP.Checked)
                        {
                            txtPort.Enabled = false;
                            txtPackage.Enabled = true;
                        }
                        else if (radioTCP.Checked)
                        {
                            txtPort.Enabled = true;
                            txtPackage.Enabled = false;
                        }
                        else
                        {
                            txtPort.Enabled = true;
                            txtPackage.Enabled = true;
                        }
                        radioICMP.Enabled = true;
                        radioTCP.Enabled = true;
                        radioUDP.Enabled = true;
                        _forceStats = true;
                        UpdateStats();
                    }
                }
                catch { }
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        private void comboTarget_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private async Task ExecuteNativeIcmpPing(string targetIp, int timeout, CancellationToken token)
        {
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;
            byte[] buffer = new byte[bufferSize];
            FillRandomBytes(buffer);

            IPAddress targetAddr;
            if (!IPAddress.TryParse(targetIp, out targetAddr))
            {
                try { targetAddr = (await Dns.GetHostAddressesAsync(targetIp)).FirstOrDefault(); }
                catch { lossCount++; UpdateStats(); return; }
            }
            if (targetAddr == null) { lossCount++; UpdateStats(); return; }

            var options = new PingOptions(128, true);

            using (Ping pingSender = new Ping())
            {
                string timeStr = GetTimeStr();
                int currentTotal = successCount + lossCount + 1;

                try
                {
                    PingReply reply = await pingSender.SendPingAsync(targetAddr, timeout, buffer, options);

                    if (reply.Status == IPStatus.Success)
                    {
                        double rtt = reply.RoundtripTime;
                        if (rtt < 0.1) rtt = 0.1;

                        if (rtt > timeout)
                        {
                            lossCount++;
                            AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                            AppendColorText($"ICMP失败: 实际延迟{FormatRtt(rtt)}ms > 超时{timeout}ms", Color.Red, true);
                        }
                        else
                        {
                            successCount++;
                            UpdateDelay(rtt);
                            PrintTestSettings("系统默认 (ICMP兼容模式)");

                            Color rowColor = GetRttColor(rtt);
                            AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                            string ttlInfo = "";
                            if (reply.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ttlInfo = $" TTL={reply.Options?.Ttl}";
                            }
                            if (rtt == 0.1)
                            {
                                AppendColorText($"ICMP成功: {reply.Address} <1ms{ttlInfo}", rowColor, true);
                            }
                            else
                            {
                                AppendColorText($"ICMP成功: {reply.Address} ={FormatRtt(rtt)}ms{ttlInfo}", rowColor, true);
                            }
                        }
                    }
                    else
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                        AppendColorText($"ICMP失败: {reply.Status}", Color.Red, true);
                    }
                }
                catch (PingException pex)
                {
                    if (pex.InnerException is OperationCanceledException) { }
                    else if (!token.IsCancellationRequested)
                    {
                        if (IsRepeatingException(pex.Message))
                        {
                            _fatalError = true;
                            _fatalErrorMessage = $"[ICMP] {pex.Message}";
                            return;
                        }
                        lossCount++;
                        AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                        AppendColorText($"ICMP错误: {pex.Message}", Color.Yellow, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (IsRepeatingException(ex.Message))
                        {
                            _fatalError = true;
                            _fatalErrorMessage = $"[ICMP] {ex.Message}";
                            return;
                        }
                        lossCount++;
                        AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                        AppendColorText($"ICMP错误: {ex.Message}", Color.Yellow, true);
                    }
                }
                finally
                {
                    UpdateStats();
                }
            }
        }
        private async Task ExecuteIcmpPing(string targetIp, int timeout, CancellationToken token)
        {
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;
            byte[] payload = new byte[bufferSize];
            FillRandomBytes(payload);
            ushort identifier = (ushort)(Process.GetCurrentProcess().Id & 0xFFFF);
            unchecked { _globalIcmpSequence++; }

            IPAddress ipAddr = IPAddress.Parse(targetIp);
            var addrFamily = ipAddr.AddressFamily;
            IPEndPoint localEndPoint = GetLocalEndPoint();

            if ((addrFamily == AddressFamily.InterNetwork && localEndPoint.Address.Equals(IPAddress.IPv6Any)) ||
                (addrFamily == AddressFamily.InterNetworkV6 && localEndPoint.Address.Equals(IPAddress.Any)))
            {
                _fatalError = true;
                _fatalErrorMessage = $"本机网卡IP({localEndPoint.Address})与目标IP({ipAddr})地址族不匹配";
                return;
            }

            // ==================== IPv4 分支 ====================
            if (addrFamily == AddressFamily.InterNetwork)
            {
                Socket raw = null;
                try
                {
                    raw = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
                    raw.DontFragment = true;
                    raw.Bind(localEndPoint);

                    try
                    {
                        var localEp = (IPEndPoint)raw.LocalEndPoint;
                        if (localEp != null && localEp.Address.Equals(IPAddress.Any))
                        {
                            string outbound = GetActualLocalIp(targetIp);
                            if (outbound != null) PrintTestSettings(outbound);
                        }
                    }
                    catch { }

                    byte[] icmpPacket = BuildIcmpEchoPacket(8, 0, identifier, _globalIcmpSequence, payload);
                    raw.SendTo(icmpPacket, new IPEndPoint(ipAddr, 0));

                    string timeStr = GetTimeStr();
                    int currentTotal = successCount + lossCount + 1;
                    Stopwatch sw = Stopwatch.StartNew();

                    bool receivedMatch = false;
                    double rtt = 0;
                    int ttl = 0;
                    IPAddress replyAddress = null;

                    using (token.Register(() => { try { raw.Close(); } catch { } }))
                    {
                        await Task.Run(() =>
                        {
                            byte[] buf = new byte[4096];
                            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                            while (sw.ElapsedMilliseconds < timeout)
                            {
                                int rem = (int)(timeout - sw.ElapsedMilliseconds);
                                if (rem <= 0) break;
                                raw.ReceiveTimeout = Math.Min(rem, 200);
                                try
                                {
                                    int len = raw.ReceiveFrom(buf, ref ep);
                                    int ipHdrLen = (buf[0] & 0x0F) * 4;
                                    if (len >= ipHdrLen + 8)
                                    {
                                        byte t = buf[ipHdrLen];
                                        ushort rid = (ushort)((buf[ipHdrLen + 4] << 8) | buf[ipHdrLen + 5]);
                                        ushort rseq = (ushort)((buf[ipHdrLen + 6] << 8) | buf[ipHdrLen + 7]);
                                        if (t == 0 && rid == identifier && rseq == _globalIcmpSequence)
                                        {
                                            sw.Stop();
                                            rtt = sw.Elapsed.TotalMilliseconds;
                                            ttl = buf[8];
                                            replyAddress = ((IPEndPoint)ep).Address;
                                            receivedMatch = true;
                                            return;
                                        }
                                    }
                                }
                                catch (SocketException) { continue; }
                                catch (ObjectDisposedException) { return; }
                            }
                        });
                    }

                    if (receivedMatch)
                    {
                        if (rtt > timeout)
                        {
                            lossCount++;
                            AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                            AppendColorText($"ICMP失败: 请求超时({timeout}ms)", Color.Red, true);
                        }
                        else
                        {
                            successCount++;
                            UpdateDelay(rtt);
                            string actualIp = ((IPEndPoint)raw.LocalEndPoint).Address.ToString();
                            PrintTestSettings(actualIp);
                            string limitInfo = ttl > 0 ? $"TTL={ttl}" : "";
                            Color rowColor = GetRttColor(rtt);
                            AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                            AppendColorText($"ICMP成功: {replyAddress} ={FormatRtt(rtt)}ms {limitInfo}", rowColor, true);
                        }
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                        AppendColorText($"ICMP失败: 请求超时({timeout}ms)", Color.Red, true);
                    }
                }
                catch (SocketException sex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (IsRepeatingException(sex.Message))
                        {
                            _fatalError = true;
                            _fatalErrorMessage = $"[ICMP] {sex.Message}";
                            return;
                        }
                        lossCount++;
                        AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                        AppendColorText($"ICMP错误: {sex.Message}", Color.Yellow, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (IsRepeatingException(ex.Message))
                        {
                            _fatalError = true;
                            _fatalErrorMessage = $"[ICMP] {ex.Message}";
                            return;
                        }
                        lossCount++;
                        AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                        AppendColorText($"ICMP错误: {ex.Message}", Color.Yellow, true);
                    }
                }
                finally
                {
                    raw?.Close();
                    raw?.Dispose();
                    UpdateStats();
                }
                return;
            }

            // ==================== IPv6 分支 ====================
            if (addrFamily == AddressFamily.InterNetworkV6)
            {
                Socket raw6 = null;
                string finalLocalIp = "::";
                try
                {
                    if (localEndPoint.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        _fatalError = true;
                        _fatalErrorMessage = $"本地地址({localEndPoint.Address})不是IPv6地址";
                        return;
                    }

                    raw6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Raw, ProtocolType.IcmpV6);
                    raw6.Bind(localEndPoint);

                    _globalIcmpSequence++;
                    IPAddress srcForChecksum = localEndPoint.Address;
                    if (srcForChecksum.Equals(IPAddress.IPv6Any))
                    {
                        finalLocalIp = GetActualLocalIp(targetIp);
                        if (!string.IsNullOrEmpty(finalLocalIp) && finalLocalIp != "::")
                        {
                            srcForChecksum = IPAddress.Parse(finalLocalIp);
                            PrintTestSettings(finalLocalIp);
                        }
                        else
                        {
                            _fatalError = true;
                            _fatalErrorMessage = "无法探测本地IPv6出口地址";
                            return;
                        }
                    }
                    else
                    {
                        finalLocalIp = srcForChecksum.ToString();
                        PrintTestSettings(finalLocalIp);
                    }

                    byte[] icmpPacketNoChecksum = BuildIcmpv6PacketWithoutChecksum(128, 0, identifier, _globalIcmpSequence, payload);
                    byte[] icmpWithChecksum = BuildIcmpv6WithChecksum(srcForChecksum, ipAddr, icmpPacketNoChecksum);
                    raw6.SendTo(icmpWithChecksum, new IPEndPoint(ipAddr, 0));

                    string timeStr = GetTimeStr();
                    int currentTotal = successCount + lossCount + 1;
                    Stopwatch sw = Stopwatch.StartNew();

                    bool receivedMatch = false;
                    double rtt = 0;

                    using (token.Register(() => { try { raw6.Close(); } catch { } }))
                    {
                        await Task.Run(() =>
                        {
                            byte[] buf = new byte[4096];
                            EndPoint ep = new IPEndPoint(IPAddress.IPv6Any, 0);
                            while (sw.ElapsedMilliseconds < timeout)
                            {
                                int rem = (int)(timeout - sw.ElapsedMilliseconds);
                                if (rem <= 0) break;
                                raw6.ReceiveTimeout = Math.Min(rem, 200);
                                try
                                {
                                    int len = raw6.ReceiveFrom(buf, ref ep);
                                    int icmpOff = (len > 40 && (buf[0] >> 4) == 6) ? 40 : 0;
                                    if (len >= icmpOff + 8)
                                    {
                                        byte rt = buf[icmpOff];
                                        ushort rid = (ushort)((buf[icmpOff + 4] << 8) | buf[icmpOff + 5]);
                                        ushort rseq = (ushort)((buf[icmpOff + 6] << 8) | buf[icmpOff + 7]);
                                        if (rt == 129 && rid == identifier && rseq == _globalIcmpSequence)
                                        {
                                            sw.Stop();
                                            rtt = sw.Elapsed.TotalMilliseconds;
                                            receivedMatch = true;
                                            return;
                                        }
                                    }
                                }
                                catch (SocketException) { continue; }
                                catch (ObjectDisposedException) { return; }
                            }
                        });
                    }

                    if (receivedMatch)
                    {
                        if (rtt > timeout)
                        {
                            lossCount++;
                            AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                            AppendColorText($"ICMPv6失败: 请求超时({timeout}ms)", Color.Red, true);
                        }
                        else
                        {
                            successCount++;
                            UpdateDelay(rtt);
                            Color rowColor = GetRttColor(rtt);
                            AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                            AppendColorText($"ICMPv6成功: {targetIp} ={FormatRtt(rtt)}ms", rowColor, true);
                        }
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", GetTimestampColor(), false);
                        AppendColorText($"ICMPv6失败: 请求超时({timeout}ms)", Color.Red, true);
                    }
                }
                catch (SocketException sex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (IsRepeatingException(sex.Message))
                        {
                            _fatalError = true;
                            _fatalErrorMessage = $"[ICMPv6] {sex.Message}";
                            return;
                        }
                        lossCount++;
                        AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                        AppendColorText($"ICMPv6错误: {sex.Message}", Color.Yellow, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (IsRepeatingException(ex.Message))
                        {
                            _fatalError = true;
                            _fatalErrorMessage = $"[ICMPv6] {ex.Message}";
                            return;
                        }
                        lossCount++;
                        AppendColorText($"[{GetTimeStr()}] ", GetTimestampColor(), false);
                        AppendColorText($"ICMPv6错误: {ex.Message}", Color.Yellow, true);
                    }
                }
                finally
                {
                    raw6?.Close();
                    raw6?.Dispose();
                    UpdateStats();
                }
                return;
            }

            _fatalError = true;
            _fatalErrorMessage = $"[ICMP] 未知地址族 {addrFamily}";
        }

        private static byte[] BuildIcmpEchoPacket(byte type, byte code, ushort identifier, ushort sequence, byte[] payload)
        {
            int headerLen = 8;
            byte[] packet = new byte[headerLen + payload.Length];

            packet[0] = type;
            packet[1] = code;
            packet[2] = 0;
            packet[3] = 0;
            packet[4] = (byte)(identifier >> 8);
            packet[5] = (byte)(identifier & 0xFF);
            packet[6] = (byte)(sequence >> 8);
            packet[7] = (byte)(sequence & 0xFF);
            Buffer.BlockCopy(payload, 0, packet, headerLen, payload.Length);

            ushort csum = ComputeChecksum(packet);
            packet[2] = (byte)(csum >> 8);
            packet[3] = (byte)(csum & 0xFF);

            return packet;
        }

        private static ushort ComputeChecksum(byte[] data)
        {
            uint sum = 0;
            int i = 0;
            while (i + 1 < data.Length)
            {
                sum += (uint)((data[i] << 8) | data[i + 1]);
                i += 2;
            }
            if (i < data.Length)
            {
                sum += (uint)(data[i] << 8);
            }
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            return (ushort)~sum;
        }
        private static byte[] BuildIcmpv6PacketWithoutChecksum(byte type, byte code, ushort identifier, ushort sequence, byte[] payload)
        {
            int headerLen = 8;
            byte[] packet = new byte[headerLen + payload.Length];
            packet[0] = type;
            packet[1] = code;
            packet[2] = 0;
            packet[3] = 0;
            packet[4] = (byte)(identifier >> 8);
            packet[5] = (byte)(identifier & 0xFF);
            packet[6] = (byte)(sequence >> 8);
            packet[7] = (byte)(sequence & 0xFF);
            Buffer.BlockCopy(payload, 0, packet, headerLen, payload.Length);
            return packet;
        }

        private static byte[] BuildIcmpv6WithChecksum(IPAddress src, IPAddress dst, byte[] icmpWithoutChecksum)
        {
            int pseudoLen = 16 + 16 + 4 + 4;
            int totalLen = pseudoLen + icmpWithoutChecksum.Length;
            byte[] buf = new byte[totalLen];

            Buffer.BlockCopy(src.GetAddressBytes(), 0, buf, 0, 16);
            Buffer.BlockCopy(dst.GetAddressBytes(), 0, buf, 16, 16);
            uint upperLen = (uint)icmpWithoutChecksum.Length;
            buf[32] = (byte)((upperLen >> 24) & 0xFF);
            buf[33] = (byte)((upperLen >> 16) & 0xFF);
            buf[34] = (byte)((upperLen >> 8) & 0xFF);
            buf[35] = (byte)(upperLen & 0xFF);
            buf[36] = 0;
            buf[37] = 0;
            buf[38] = 0;
            buf[39] = 58;

            Buffer.BlockCopy(icmpWithoutChecksum, 0, buf, 40, icmpWithoutChecksum.Length);

            ushort csum = ComputeChecksum(buf);

            byte[] icmpWithChecksum = new byte[icmpWithoutChecksum.Length];
            Buffer.BlockCopy(icmpWithoutChecksum, 0, icmpWithChecksum, 0, icmpWithoutChecksum.Length);
            icmpWithChecksum[2] = (byte)(csum >> 8);
            icmpWithChecksum[3] = (byte)(csum & 0xFF);

            return icmpWithChecksum;
        }


        private void UpdateStats()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateStats));
                return;
            }

            if (_suppressOutput) return;

            // 高频时节流更新
            bool forced = _forceStats;
            _forceStats = false;
            if (!forced && _statsWatch.ElapsedMilliseconds < StatsIntervalMs)
                return;
            _statsWatch.Restart();

            string minStr = (successCount > 0) ? $"{FormatRtt(minDelay)}ms({minCountIndex})" : "-";
            string maxStr = (successCount > 0) ? $"{FormatRtt(maxDelay)}ms({maxCountIndex})" : "-";

            double avgDelay = successCount > 0 ? totalDelay / successCount : 0;
            double lossRate = (successCount + lossCount) > 0 ? (double)lossCount / (successCount + lossCount) * 100 : 0;

            lblMin2.Text = minStr;
            lblMax2.Text = maxStr;
            if (GetPingFrequency() >= 2)
            {
                int currentSec = (int)(DateTime.Now - _sessionStartTime).TotalSeconds;
                int lastSecTicks = 0;
                for (int i = _tickPerSec.Count - 1; i >= 0; i--)
                {
                    if (_tickPerSec[i].secBucket == currentSec - 1)
                    {
                        lastSecTicks = _tickPerSec[i].count;
                        break;
                    }
                }
                lblAvg2.Text = lastSecTicks > 0
                    ? $"{FormatRtt(avgDelay)}ms({lastSecTicks})"
                    : $"{FormatRtt(avgDelay)}ms";
            }
            else
            {
                lblAvg2.Text = $"{FormatRtt(avgDelay)}ms";
            }

            if (GetPingFrequency() > 1)
            {
                lblLoss.Text = "抖";

                // 剪除超过1秒的旧数据
                DateTime cutoff = DateTime.Now - JitterWindowSize;
                _jitterWindow.RemoveAll(e => e.time < cutoff);

                if (_jitterWindow.Count > 0)
                {
                    double sumSigned = 0, sumAbs = 0;
                    foreach (var e in _jitterWindow)
                    {
                        sumSigned += e.diff;
                        sumAbs += Math.Abs(e.diff);
                    }
                    double avgAbsJitter = sumAbs / _jitterWindow.Count;
                    char sign = sumSigned >= 0 ? '+' : '-';
                    lblLoss2.Text = $"{sign}{avgAbsJitter:F3}ms({lossRate:F1}%)";
                }
                else
                {
                    lblLoss2.Text = $"-ms({lossRate:F1}%)";
                }
            }
            else
            {
                lblLoss.Text = "丢";
                lblLoss2.Text = $"{lossRate:F1}%";
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            comboTarget.Enabled = enabled;
            txtMaxDelay.Enabled = enabled;
            txtPackage.Enabled = enabled;
            txtPort.Enabled = enabled;
            radioICMP.Enabled = enabled;
            radioTCP.Enabled = enabled;
            radioUDP.Enabled = enabled;
            comboLocalEnd.Enabled = enabled;
            comboFreq.Enabled = enabled;
            btnSave.Enabled = enabled;

            if (enabled)
            {
                UpdateProtocolUI(
                    radioICMP.Checked ? radioICMP : (radioTCP.Checked ? radioTCP : radioUDP),
                    false
                );
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_startTimeStr) || string.IsNullOrEmpty(richTextBox1.Text))
            {
                MessageBox.Show("当前没有测试记录可以保存", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "请选择保存测试结果的位置";
                sfd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                string pingType = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");

                sfd.FileName = $"NICX_Ping_{pingType}_{comboTarget.Text}_{_startTimeStr}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder();

                        sb.AppendLine($"=== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ===");
                        sb.AppendLine($"\n🔰 本次 Ping+ 统计数据");
                        sb.AppendLine($"[目标/协议]  {comboTarget.Text} [{pingType}]");
                        sb.AppendLine($"[最小延迟(次序)]  {lblMin2.Text}");
                        sb.AppendLine($"[最大延迟(次序)]  {lblMax2.Text}");
                        sb.AppendLine($"[平均延迟(tick/s)]  {lblAvg2.Text}");
                        sb.AppendLine($"[丢包/抖动]  {lblLoss2.Text}");
                        sb.AppendLine($"\n🔥 本次 Ping+ 输出详情");

                        // 拼接所有分片文件
                        string pattern = $"NICX_Ping_Temp_*_{pingType}_{comboTarget.Text}_{_startTimeStr}.txt";
                        string[] shardFiles = System.IO.Directory.GetFiles(Application.StartupPath, pattern);
                        if (shardFiles.Length > 0)
                        {
                            // 按分片号排序
                            Array.Sort(shardFiles, (a, b) =>
                            {
                                int ExtractNum(string path)
                                {
                                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                                    var parts = name.Split('_');
                                    return parts.Length > 3 && int.TryParse(parts[3], out int n) ? n : 0;
                                }
                                return ExtractNum(a).CompareTo(ExtractNum(b));
                            });

                            foreach (string file in shardFiles)
                            {
                                string content = System.IO.File.ReadAllText(file, Encoding.UTF8);
                                // 去除末行分片标记
                                int lastNewline = content.TrimEnd('\r', '\n').LastIndexOf('\n');
                                if (lastNewline >= 0)
                                {
                                    string lastLine = content.Substring(lastNewline + 1).Trim();
                                    if (lastLine.StartsWith("测试记录分片"))
                                        content = content.Substring(0, lastNewline + 1);
                                }
                                sb.Append(content);
                            }
                        }

                        // 追加当前文本框内容（去除首行续接标记）
                        {
                            string rtbText = richTextBox1.Text;
                            int firstNewline = rtbText.IndexOf('\n');
                            if (firstNewline >= 0)
                            {
                                string firstLine = rtbText.Substring(0, firstNewline).Trim();
                                if (firstLine.StartsWith("接测试记录分片"))
                                    rtbText = rtbText.Substring(firstNewline + 1);
                            }
                            sb.Append(rtbText);
                            if (!rtbText.EndsWith("\n"))
                                sb.AppendLine();
                        }

                        sb.AppendLine($"\n=== 感谢使用 Ping+ ❤ 网络综合查询器X by Yumeyo ===");
                        sb.AppendLine($"======== 导出于 NetInfoCheckerX by Yumeyo ========\n");

                        System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);

                        MessageBox.Show($"保存[{sfd.FileName}]成功!", "保存成功了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void lblTarget_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (isRunning)
                {
                    _cts?.Cancel();
                }

                SaveSettings();

                Point currentLocation = this.Location;
                Size currentSize = this.Size;

                PingPP newForm = new PingPP();

                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentLocation;
                newForm.Size = currentSize;

                newForm.Show();
                this.Close();
                this.Dispose();
            }
        }

        private Color GetRttColor(double rtt)
        {
            if (rtt <= 15) return Color.Lime;
            if (rtt <= 30) return Color.MediumSpringGreen;
            if (rtt <= 50) return Color.FromArgb(185, 210, 50);
            if (rtt <= 100) return Color.Gold;
            if (rtt <= 200) return Color.Orange;
            if (rtt <= 500) return Color.Tomato;
            return Color.Red;
        }

        private void txtPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void txtMaxDelay_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void txtPackage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnStart_Click(sender, e);
            }
        }

        private void AppendColorText(string text, Color color, bool addNewLine = false)
        {
            if (_suppressOutput || _isClosing) return;
            Color actualColor = _whiteTextOnly ? Color.White : color;
            if (_useBufferedOutput)
            {
                _outputBuffer.Add((text, actualColor, addNewLine));
                return;
            }
            richTextBox1.SelectionStart = richTextBox1.Text.Length;
            richTextBox1.SelectionLength = 0;
            if (_pingOutputFont != null)
            {
                try { richTextBox1.SelectionFont = _pingOutputFont; } catch { }
            }
            richTextBox1.SelectionColor = actualColor;
            richTextBox1.AppendText(addNewLine ? text + Environment.NewLine : text);
            richTextBox1.ScrollToCaret();
        }

        private void FlushOutputBuffer()
        {
            if (_outputBuffer.Count == 0) return;

            SendMessage(richTextBox1.Handle, WM_SETREDRAW, 0, 0);

            if (_whiteTextOnly)
            {
                var sb = new StringBuilder();
                int count = _outputBuffer.Count;
                for (int i = 0; i < count; i++)
                {
                    var (text, _, newLine) = _outputBuffer[i];
                    sb.Append(text);
                    if (newLine) sb.AppendLine();
                }
                richTextBox1.SelectionStart = richTextBox1.Text.Length;
                richTextBox1.SelectionLength = 0;
                if (_pingOutputFont != null)
                {
                    try { richTextBox1.SelectionFont = _pingOutputFont; } catch { }
                }
                richTextBox1.SelectionColor = Color.White;
                richTextBox1.AppendText(sb.ToString());
            }
            else
            {
                int count = _outputBuffer.Count;
                for (int i = 0; i < count; i++)
                {
                    var (text, color, newLine) = _outputBuffer[i];
                    richTextBox1.SelectionStart = richTextBox1.Text.Length;
                    richTextBox1.SelectionLength = 0;
                    if (_pingOutputFont != null)
                    {
                        try { richTextBox1.SelectionFont = _pingOutputFont; } catch { }
                    }
                    richTextBox1.SelectionColor = color;
                    richTextBox1.AppendText(newLine ? text + Environment.NewLine : text);
                }
            }
            _outputBuffer.Clear();

            SendMessage(richTextBox1.Handle, WM_SETREDRAW, 1, 0);
            richTextBox1.Invalidate();
            richTextBox1.ScrollToCaret();
        }

        private void AutoSaveAndClear()
        {
            _shardIndex++;
            string target = comboTarget.Text.Trim();
            string proto = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");
            string fileName = $"NICX_Ping_Temp_{_shardIndex}_{proto}_{target}_{_startTimeStr}.txt";
            string filePath = Path.Combine(Application.StartupPath, fileName);

            // Flush any pending output before saving
            FlushOutputBuffer();

            // Snapshot text and write file on background thread
            string rtfText = richTextBox1.Text;
            Task.Run(() =>
            {
                try
                {
                    string shardFooter = $"测试记录分片{_shardIndex}\n";
                    System.IO.File.WriteAllText(filePath, rtfText + shardFooter, Encoding.UTF8);
                }
                catch { }
            });

            richTextBox1.ResetText();
            AppendColorText($"接测试记录分片{_shardIndex}", Color.Gray, true);

            _outputBuffer.Clear();

            // 重置监测状态（保留基准）
            _rateDegradationCount = 0;
            _scheduleDelaySum = 0;
            _scheduleDelayCount = 0;
            _loopIterationCount = 0;
            int sec = (int)(DateTime.Now - _sessionStartTime).TotalSeconds;
            _nextCheckSec = ((sec / 2) + 2) * 2;
        }

        private void CheckDegradation()
        {
            int currentSec = (int)(DateTime.Now - _sessionStartTime).TotalSeconds;

            // 时间分片：每301秒(5分01秒)自动分片，不受tick速率/行数影响
            if (!_suppressOutput && currentSec - _lastShardSec >= 301)
            {
                AutoSaveAndClear();
                _lastShardSec = currentSec;
                return;
            }

            int freq = GetPingFrequency();
            if (freq < 4) return;

            // 基准采集稍延长到前4秒，降低 JIT、首次发包和绘图初始化造成的误判。
            // 迭代基准仍折算为每2秒，后续检查频率与原设计保持一致。
            if (_baselineTps == 0 && _nextCheckSec == 0 && currentSec >= 4)
            {
                if (_scheduleDelayCount > 0)
                    _baselineTps = _scheduleDelaySum / _scheduleDelayCount;
                _baselineIterations = _loopIterationCount * 2.0 / Math.Max(1, currentSec);
                _nextCheckSec = currentSec + 2;
                _scheduleDelaySum = 0;
                _scheduleDelayCount = 0;
                _loopIterationCount = 0;
            }
            // 监测：每2秒评估（双指标并联）
            else if (_nextCheckSec > 0 && currentSec >= _nextCheckSec)
            {
                bool degraded = false;

                // 指标A：测试程序内部延迟是否过大
                if (_scheduleDelayCount > 0)
                {
                    double avgDelay = _scheduleDelaySum / _scheduleDelayCount;
                    if (_baselineTps > 0.01 && avgDelay > _baselineTps * 3.5 && avgDelay > 4.0)
                        degraded = true;
                }

                // 指标B：实际速率低于基准75%（仍需连续2窗，保留高频Ping保护力度）
                if (_baselineIterations > 0 && _loopIterationCount < _baselineIterations * 0.75)
                    _rateDegradationCount++;
                else
                    _rateDegradationCount = 0;

                _scheduleDelaySum = 0;
                _scheduleDelayCount = 0;
                _loopIterationCount = 0;

                // 指标A单窗即触发，指标B连降2窗触发
                if (!_suppressOutput && (degraded || _rateDegradationCount >= 2))
                {
                    AutoSaveAndClear();
                }
                else
                {
                    _nextCheckSec += 2;
                }
            }
        }

        private void AppendColorMap()
        {
            var colors = new[] {
        Color.Lime, Color.MediumSpringGreen, Color.FromArgb(185,210,50),
        Color.Gold, Color.Orange, Color.Tomato, Color.Red
    };
            var labels = new[] { "     ≤15ms ", " 30ms  ", " 50ms  ", " 100ms ", " 200ms ", " 500ms ", " >错误" };
            var arrows = new[] { "     >>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>" };

            AppendColorText("    ===========================================================", Global.Yumeyo2, true);

            for (int i = 0; i < labels.Length; i++)
            {
                AppendColorText(labels[i], colors[i], false);
                if (i < labels.Length - 1) AppendColorText("|", Color.Gray, false);
            }
            richTextBox1.AppendText("\n");

            for (int i = 0; i < arrows.Length; i++)
            {
                AppendColorText(arrows[i], colors[i], false);
                if (i < arrows.Length - 1) AppendColorText(" ", Color.Black, false);
            }
            richTextBox1.AppendText("\n");

            AppendColorText("    ===========================================================", Global.Yumeyo2, true);
        }

        private string GetActualLocalIp(string targetIp)
        {
            if (string.IsNullOrEmpty(targetIp)) return targetIp.Contains(":") ? "::" : "0.0.0.0";
            try
            {
                bool isIpv6 = targetIp.Contains(":");
                AddressFamily family = isIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
                using (var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(IPAddress.Parse(targetIp), 1);
                    if (socket.LocalEndPoint is IPEndPoint localEndPoint)
                    {
                        string ip = localEndPoint.Address.ToString();
                        return ip.Contains("%") ? ip.Split('%')[0] : ip;
                    }
                }
            }
            catch { }
            return targetIp.Contains(":") ? "::" : "0.0.0.0";
        }

        private void PrintTestSettings(string actualIp)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => PrintTestSettings(actualIp)));
                return;
            }

            if (actualIp == "0.0.0.0" || actualIp == "::") return;

            if (isSettingsPrinted) return;
            isSettingsPrinted = true;

            string displayName = actualIp;
            for (int i = 0; i < comboLocalEnd.Items.Count; i++)
            {
                string itemStr = comboLocalEnd.Items[i].ToString();
                if (itemStr.StartsWith(actualIp + " ("))
                {
                    displayName = itemStr;
                    comboLocalEnd.SelectedIndex = i;
                    break;
                }
            }
            foreach (var item in comboLocalEnd.Items)
            {
                string itemStr = item.ToString();
                if (itemStr.StartsWith(actualIp + " ("))
                {
                    displayName = itemStr;
                    break;
                }
            }

            if (displayName == "0.0.0.0" || displayName == "::") displayName = "系统默认";

            string protocolName = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");
            int timeout = int.TryParse(txtMaxDelay.Text, out int t) ? t : 500;
            int port = int.TryParse(txtPort.Text, out int p) ? p : 80;
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;

            string settingsLine = $"[测试设置] 网卡{displayName} / 协议{protocolName}";

            if (radioICMP.Checked) settingsLine += $" / 超时{timeout}ms / 字节{bufferSize}";
            else if (radioTCP.Checked) settingsLine += $" / 端口{port} / 超时{timeout}ms";
            else settingsLine += $" / 端口{port} / 超时{timeout}ms";

            settingsLine += $" / Tick{GetPingFrequency()}";

            AppendColorText(settingsLine + "\n", Color.LightPink, true);
        }

        private int GetPingFrequency()
        {
            if (comboFreq.SelectedItem != null && int.TryParse(comboFreq.SelectedItem.ToString(), out int freq))
                return freq;
            return 1;
        }

        private int GetPingIntervalMs()
        {
            int freq = GetPingFrequency();
            if (freq < 1) freq = 1;
            return 1000 / freq;
        }

        private void LimitTimer_Tick(object sender, EventArgs e)
        {
            if (Global.isUnlimitedTime)
            {
                _remainingSeconds++;
                this.Text = $"Ping+ ✧ NetInfoCheckerX ({_remainingSeconds})";
                CloudControl.ApplyDevTitle(this);
            }
            else
            {
                _remainingSeconds--;
                if (_remainingSeconds <= 0)
                {
                    _limitTimer.Stop();
                    if (isRunning)
                    {
                        _cts?.Cancel();
                    }
                    return;
                }
                this.Text = $"Ping+ ✧ NetInfoCheckerX ({_remainingSeconds})";
                CloudControl.ApplyDevTitle(this);
            }
        }

        private string GetTimeStr()
        {
            int freq = GetPingFrequency();
            return DateTime.Now.ToString(freq > 1 ? "HH:mm:ss.fff" : "HH:mm:ss");
        }

        private bool IsRepeatingException(string message)
        {
            if (_lastExceptionMessage == message) return true;
            _lastExceptionMessage = message;
            return false;
        }

        private Color GetTimestampColor()
        {
            if (GetPingFrequency() <= 1)
                return Global.Yumeyo2;

            int secBucket = (int)(DateTime.Now - _sessionStartTime).TotalSeconds;
            return (secBucket % 2 == 0) ? Global.Yumeyo2 : ColorTranslator.FromHtml("#ffa5cf");
        }

        private string FormatRtt(double rtt)
        {
            int freq = GetPingFrequency();
            return rtt.ToString(freq > 1 ? "F3" : "F1");
        }

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60)
                return $"{ts.TotalSeconds:F1}秒";
            if (ts.TotalMinutes < 60)
                return $"{ts.Minutes}分{ts.Seconds}秒";
            return $"{ts.Hours}时{ts.Minutes}分{ts.Seconds}秒";
        }
    }
}
