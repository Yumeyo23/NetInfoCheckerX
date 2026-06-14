using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        }

        double minDelay = 9999, maxDelay = 0, totalDelay = 0;
        int successCount = 0, lossCount = 0;
        int minCountIndex = 0, maxCountIndex = 0;
        private ushort _globalIcmpSequence = 0;

        private CancellationTokenSource _cts;
        private bool isRunning = false;
        private bool isSettingsPrinted = false;
        private readonly Random _random = new Random();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);
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
        }

        private void ResetStats()
        {
            minDelay = 9999; maxDelay = 0; totalDelay = 0;
            successCount = 0; lossCount = 0;
            minCountIndex = 0; maxCountIndex = 0;
            isSettingsPrinted = false;
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
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] DNS查询域名: {randomDomain}");
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

                    string timeStr = DateTime.Now.ToString("HH:mm:ss");
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
                        AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"UDP成功: {remoteIp} ={rtt:F1}ms", rowColor, true);

                    }
                    else
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"UDP失败: 请求超时({timeout}ms)", Color.Red, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"UDP错误: {ex.Message}", Color.Red, true);
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
                string timeStr = DateTime.Now.ToString("HH:mm:ss");

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
                    AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);

                    string displayTarget = ipAddr.AddressFamily == AddressFamily.InterNetworkV6
                        ? $"[{targetIp}]:{port}"
                        : $"{targetIp}:{port}";

                    AppendColorText($"TCP成功: {displayTarget} ={rtt:F1}ms", rowColor, true);
                }
                else
                {
                    lossCount++;
                    AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                    AppendColorText($"TCP失败: 连接超时({timeout}ms)", Color.Red, true);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    lossCount++;
                    AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                    AppendColorText($"TCP错误: {ex.Message}", Color.Red, true);
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
                    AppendColorText("     ==== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ====", ColorTranslator.FromHtml("#a8a5ff"), true);
                    AppendColorText("当前选中 ICMP 协议，请先阅读下列提示：", Color.Lime, true);
                    AppendColorText("    🔰 ICMP Ping 已更新Socket指定网卡测试 (精度0.1ms) 💦", Color.White, true);
                    AppendColorText("        ❤若指定网卡时频繁意外丢包, 影响判断, 请选\"ICMP兼容模式\"网卡, ", Color.Yellow, true);
                    AppendColorText("          以使用原生Ping更稳定, 但无法识别/指定网卡 (精度1ms)", Color.Yellow, true);
                    AppendColorText("        ❤还有问题，可尝试以管理员运行查询器X后再测❤ ", Color.LightPink, true);
                    AppendColorText("    ICMP 无端口测试，不支持分片, 最大包受本机MTU影响(MTU-28=最大包)\n", Color.White, true);
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
                    AppendColorText("     ==== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ====", ColorTranslator.FromHtml("#a8a5ff"), true);
                    AppendColorText("当前选中 UDP 协议，请先阅读下列提示：", Color.Lime, true);
                    AppendColorText("  针对 🔥DNS(53) NTP(123) STUN(3478/3489/19302)🔥 端口已优化测试方法；", Color.LightPink, true);
                    AppendColorText("    🔰 其他端口将发送随机字节数据测试 💦", Color.White, true);
                    AppendColorText("       ❤ 建议优先使用上述3种协议的UDP服务器测试, ", Color.Yellow, true);
                    AppendColorText("       ❤ 上述3种协议会测试 ", Color.Yellow, false);
                    AppendColorText(" \"发起请求-收到回复\" ", Color.LightPink, false);
                    AppendColorText("整个过程的真实延迟\n\n", Color.Yellow, false);
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
                    AppendColorText("     ==== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ====", ColorTranslator.FromHtml("#a8a5ff"), true);
                    AppendColorText("当前选中 TCP 协议，请先阅读下列提示：", Color.Lime, true);
                    AppendColorText("    通过 🔰 TcpClient 🔰 尝试握手连接；包大小无影响延迟已禁用设置 💦", Color.White, true);
                    AppendColorText("      ❤ 通常用于探测 🔥 80/443 🔥 等端口是否开放\n", Color.White, true);
                    AppendColorText("    ❤ 延迟颜色对照表", Color.LightSkyBlue, true);
                    AppendColorMap();
                    txtPort.Text = "80";
                }
                comboLocalEnd.Enabled = true;
                txtPort.Enabled = true;
                txtPackage.Enabled = false;
            }
        }

        private void PingPP_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            AppendColorText("✧ 正在检查系统环境，请稍候 ✧\n", Color.White, true);
            using (Graphics g = this.CreateGraphics())
            {
                if (g.DpiX > 96)
                {
                    Font modernFont = new Font("Cascadia Mono", 9.5F, FontStyle.Regular);
                    richTextBox1.Font = modernFont;
                }
            }

            RadioProtocol_CheckedChanged(radioICMP, null);
            Task.Run(() => PingPPLoadAll());
            LoadSettings();
            CloudControl.UsedTimesCounter("PingPP");
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
            CloudControl.LoadPingServers(comboTarget);
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
            SaveSettings();
            if (isRunning)
            {
                _cts?.Cancel();
                isRunning = false;
            }
            _cts?.Dispose();
            _cts = null;
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (isRunning)
            {
                AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                AppendColorText("正在停止上次测试", ColorTranslator.FromHtml("#a8a5ff"), true);
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

                    AppendColorText($"域名 [{input}] 解析出以下 IP：", Color.Yellow, true);
                    foreach (var ip in addresses)
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

            ResetStats();
            richTextBox1.Clear();
            _cts = new CancellationTokenSource();
            isRunning = true;
            btnStart.Text = "停止";
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
                        using (Ping warmUpPing = new Ping())
                        {
                            var firstTask = warmUpPing.SendPingAsync(finalIP, 100);
                            if (await Task.WhenAny(firstTask, Task.Delay(100, warmupCts.Token)) == firstTask)
                            {
                                await firstTask;
                            }

                            await Task.Delay(10, warmupCts.Token);

                            var secondTask = warmUpPing.SendPingAsync(finalIP, timeout);
                            if (await Task.WhenAny(secondTask, Task.Delay(200, warmupCts.Token)) == secondTask)
                            {
                                await secondTask;
                            }
                        }
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

                    await Task.Delay(1000, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    double savedMin = minDelay, savedMax = maxDelay, savedTotal = totalDelay;
                    int savedSuccess = successCount, savedLoss = lossCount;
                    int savedMinIdx = minCountIndex, savedMaxIdx = maxCountIndex;

                    string stopTimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    isRunning = false;
                    btnStart.Text = "开测";
                    AppendColorText($"[停止时间] {stopTimeStr}", Color.Yellow, true);
                    AppendColorText(" ■ 用户手动停止测试", Color.Yellow, true);

                    comboTarget.Enabled = true;
                    txtMaxDelay.Enabled = true;
                    txtPackage.Enabled = true;
                    txtPort.Enabled = true;
                    comboLocalEnd.Enabled = true;
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

                    minDelay = savedMin; maxDelay = savedMax; totalDelay = savedTotal;
                    successCount = savedSuccess; lossCount = savedLoss;
                    minCountIndex = savedMinIdx; maxCountIndex = savedMaxIdx;
                    UpdateStats();
                }
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

            using (Ping pingSender = new Ping())
            {
                try
                {
                    string timeStr = DateTime.Now.ToString("HH:mm:ss");
                    int currentTotal = successCount + lossCount + 1;

                    Task<PingReply> pingTask = pingSender.SendPingAsync(targetIp, timeout, buffer);
                    var completedTask = await Task.WhenAny(pingTask, Task.Delay(timeout, token));

                    if (completedTask == pingTask)
                    {
                        PingReply reply = await pingTask;

                        if (reply.Status == IPStatus.Success)
                        {
                            double rtt = reply.RoundtripTime;
                            if (rtt < 0.1) rtt = 0.1;

                            if (rtt > timeout)
                            {
                                lossCount++;
                                AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                                AppendColorText($"ICMP失败: 实际延迟{rtt:F1}ms > 超时{timeout}ms", Color.Red, true);
                            }
                            else
                            {
                                successCount++;
                                UpdateDelay(rtt);
                                PrintTestSettings("系统默认 (ICMP兼容模式)");

                                Color rowColor = GetRttColor(rtt);
                                AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
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
                                    AppendColorText($"ICMP成功: {reply.Address} ={rtt:F1}ms{ttlInfo}", rowColor, true);
                                }
                            }
                        }
                        else
                        {
                            lossCount++;
                            AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                            AppendColorText($"ICMP失败: {reply.Status}", Color.Red, true);
                        }
                    }
                    else
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMP失败: 请求超时({timeout}ms)", Color.Red, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMP错误: {ex.Message}", Color.Red, true);
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
                lossCount++;
                AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                AppendColorText($"ICMP错误: 本机网卡IP({localEndPoint.Address})与目标IP({ipAddr})地址族不匹配", Color.Red, true);
                UpdateStats();
                return;
            }

            // ==================== IPv4 分支 ====================
            if (addrFamily == AddressFamily.InterNetwork)
            {
                Socket raw = null;
                try
                {
                    raw = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
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

                    string timeStr = DateTime.Now.ToString("HH:mm:ss");
                    int currentTotal = successCount + lossCount + 1;
                    Stopwatch sw = Stopwatch.StartNew();

                    byte[] recvBuffer = new byte[4096];
                    EndPoint receiveEP = new IPEndPoint(IPAddress.Any, 0);

                    bool receivedMatch = false;
                    double rtt = 0;
                    int ttl = 0;
                    IPAddress replyAddress = null;

                    while (!token.IsCancellationRequested && sw.ElapsedMilliseconds < timeout)
                    {
                        int remaining = (int)(timeout - sw.ElapsedMilliseconds);
                        if (remaining <= 0) break;

                        int recvTimeout = Math.Min(remaining, 200);
                        var receiveTask = Task.Factory.FromAsync(
                            (callback, state) => raw.BeginReceiveFrom(recvBuffer, 0, recvBuffer.Length, SocketFlags.None, ref receiveEP, callback, state),
                            (ar) => raw.EndReceiveFrom(ar, ref receiveEP),
                            null);
                        var completed = await Task.WhenAny(receiveTask, Task.Delay(recvTimeout, token));
                        if (completed == receiveTask)
                        {
                            int received = await receiveTask;
                            int ipHeaderLen = (recvBuffer[0] & 0x0F) * 4;
                            if (received >= ipHeaderLen + 8)
                            {
                                int icmpOffset = ipHeaderLen;
                                byte icmpType = recvBuffer[icmpOffset];
                                ushort respId = (ushort)((recvBuffer[icmpOffset + 4] << 8) | recvBuffer[icmpOffset + 5]);
                                ushort respSeq = (ushort)((recvBuffer[icmpOffset + 6] << 8) | recvBuffer[icmpOffset + 7]);

                                if (icmpType == 0 && respId == identifier && respSeq == _globalIcmpSequence)
                                {
                                    sw.Stop();
                                    rtt = sw.Elapsed.TotalMilliseconds;
                                    ttl = recvBuffer[8];
                                    replyAddress = ((IPEndPoint)receiveEP).Address;
                                    receivedMatch = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (receivedMatch)
                    {
                        if (rtt > timeout)
                        {
                            lossCount++;
                            AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
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
                            AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                            AppendColorText($"ICMP成功: {replyAddress} ={rtt:F1}ms {limitInfo}", rowColor, true);
                        }
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMP失败: 请求超时({timeout}ms)", Color.Red, true);
                    }
                }
                catch (SocketException sex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMP错误: {sex.Message}", Color.Red, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMP错误: {ex.Message}", Color.Red, true);
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
                        lossCount++;
                        AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMPv6错误: 本地地址({localEndPoint.Address})不是IPv6地址", Color.Red, true);
                        UpdateStats();
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
                            lossCount++;
                            AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                            AppendColorText($"ICMPv6错误: 无法探测本地IPv6出口地址", Color.Red, true);
                            UpdateStats();
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

                    string timeStr = DateTime.Now.ToString("HH:mm:ss");
                    int currentTotal = successCount + lossCount + 1;
                    Stopwatch sw = Stopwatch.StartNew();

                    byte[] recvBuffer = new byte[4096];
                    EndPoint receiveEP = new IPEndPoint(IPAddress.IPv6Any, 0);
                    bool receivedMatch = false;
                    double rtt = 0;

                    while (!token.IsCancellationRequested && sw.ElapsedMilliseconds < timeout)
                    {
                        int remaining = (int)(timeout - sw.ElapsedMilliseconds);
                        if (remaining <= 0) break;
                        int recvTimeout = Math.Min(remaining, 200);
                        var receiveTask = Task.Factory.FromAsync(
                            (callback, state) => raw6.BeginReceiveFrom(recvBuffer, 0, recvBuffer.Length, SocketFlags.None, ref receiveEP, callback, state),
                            (ar) => raw6.EndReceiveFrom(ar, ref receiveEP),
                            null);
                        var completed = await Task.WhenAny(receiveTask, Task.Delay(recvTimeout, token));
                        if (completed == receiveTask)
                        {
                            int received = await receiveTask;
                            int icmpOffset = (received > 40 && (recvBuffer[0] >> 4) == 6) ? 40 : 0;
                            if (received >= icmpOffset + 8)
                            {
                                byte respType = recvBuffer[icmpOffset];
                                ushort respId = (ushort)((recvBuffer[icmpOffset + 4] << 8) | recvBuffer[icmpOffset + 5]);
                                ushort respSeq = (ushort)((recvBuffer[icmpOffset + 6] << 8) | recvBuffer[icmpOffset + 7]);

                                if (respType == 129 && respId == identifier && respSeq == _globalIcmpSequence)
                                {
                                    sw.Stop();
                                    rtt = sw.Elapsed.TotalMilliseconds;
                                    receivedMatch = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (receivedMatch)
                    {
                        if (rtt > timeout)
                        {
                            lossCount++;
                            AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                            AppendColorText($"ICMPv6失败: 请求超时({timeout}ms)", Color.Red, true);
                        }
                        else
                        {
                            successCount++;
                            UpdateDelay(rtt);
                            Color rowColor = GetRttColor(rtt);
                            AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                            AppendColorText($"ICMPv6成功: {targetIp} ={rtt:F1}ms", rowColor, true);
                        }
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMPv6失败: 请求超时({timeout}ms)", Color.Red, true);
                    }
                }
                catch (SocketException sex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMPv6错误: {sex.Message}", Color.Red, true);
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        lossCount++;
                        AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"ICMPv6错误: {ex.Message}", Color.Red, true);
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

            lossCount++;
            AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
            AppendColorText($"ICMP错误: 未知地址族 {addrFamily}", Color.Red, true);
            UpdateStats();
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

            string minStr = (successCount > 0) ? $"{minDelay:F1}ms({minCountIndex})" : "-";
            string maxStr = (successCount > 0) ? $"{maxDelay:F1}ms({maxCountIndex})" : "-";

            double avgDelay = successCount > 0 ? totalDelay / successCount : 0;
            double lossRate = (successCount + lossCount) > 0 ? (double)lossCount / (successCount + lossCount) * 100 : 0;

            lblMin2.Text = minStr;
            lblMax2.Text = maxStr;
            lblAvg2.Text = $"{avgDelay:F1}ms";
            lblLoss2.Text = $"{lossRate:F1}%";
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
            if (string.IsNullOrEmpty(richTextBox1.Text))
            {
                MessageBox.Show("当前没有测试结果可以保存喵", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "请选择保存测试结果的位置";
                sfd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                string pingType = String.Empty;
                if (radioICMP.Checked == true)
                {
                    pingType = radioICMP.Text;
                }
                if (radioTCP.Checked == true)
                {
                    pingType = radioTCP.Text;
                }
                if (radioUDP.Checked == true)
                {
                    pingType = radioUDP.Text;
                }

                string saveTime = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                sfd.FileName = $"NICX_Ping_{pingType}_{comboTarget.Text}_{saveTime}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder();

                        sb.AppendLine($"=== 欢迎使用 Ping+ ❤ 网络综合查询器X by Yumeyo ===");
                        sb.AppendLine($"\n🔰 本次 Ping+ 统计数据");
                        sb.AppendLine($"[目标/协议]  {comboTarget.Text} [${pingType}]");
                        sb.AppendLine($"[最小延迟(次序)]  {lblMin2.Text}");
                        sb.AppendLine($"[最大延迟(次序)]  {lblMax2.Text}");
                        sb.AppendLine($"[平均延迟(次序)]  {lblAvg2.Text}");
                        sb.AppendLine($"[丢包率]  {lblLoss2.Text}");
                        sb.AppendLine($"\n🔥 本次 Ping+ 输出详情");
                        sb.AppendLine(richTextBox1.Text);
                        sb.AppendLine($"=== 感谢使用 Ping+ ❤ 网络综合查询器X by Yumeyo ===");
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

                Point currentPkgLocation = this.Location;

                PingPP newForm = new PingPP();

                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentPkgLocation;

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
            richTextBox1.SelectionStart = richTextBox1.Text.Length;
            richTextBox1.SelectionLength = 0;
            richTextBox1.SelectionColor = color;
            richTextBox1.AppendText(addNewLine ? text + Environment.NewLine : text);
            richTextBox1.ScrollToCaret();
        }

        private void AppendColorMap()
        {
            var colors = new[] {
        Color.Lime, Color.MediumSpringGreen, Color.FromArgb(185,210,50),
        Color.Gold, Color.Orange, Color.Tomato, Color.Red
    };
            var labels = new[] { "     ≤15ms ", " 30ms  ", " 50ms  ", " 100ms ", " 200ms ", " 500ms ", " >错误" };
            var arrows = new[] { "     >>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>" };

            AppendColorText("    ===========================================================", ColorTranslator.FromHtml("#a8a5ff"), true);

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

            AppendColorText("    ===========================================================", ColorTranslator.FromHtml("#a8a5ff"), true);
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

            string settingsLine = $"[测试设置] 网卡 {displayName} / 协议 {protocolName}";

            if (radioICMP.Checked) settingsLine += $" / 超时 {timeout}ms / 字节 {bufferSize}";
            else if (radioTCP.Checked) settingsLine += $" / 端口 {port} / 超时 {timeout}ms";
            else settingsLine += $" / 端口 {port} / 超时 {timeout}ms";

            AppendColorText(settingsLine + "\n", Color.LightPink, true);
        }
    }
}
