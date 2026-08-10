using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class DNSSelect : Form
    {
        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);
        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "DNSSelect";

        private void SaveSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(comboTLD.Text))
                    WritePrivateProfileString(IniSection, "TLD", comboTLD.Text, IniPath);
                WritePrivateProfileString(IniSection, "Timeout", txtTimeout.Text, IniPath);
                if (radioDOH1.Checked) WritePrivateProfileString(IniSection, "Mode1", "DOH", IniPath);
                else WritePrivateProfileString(IniSection, "Mode1", "DNS", IniPath);
                if (radioDOH2.Checked) WritePrivateProfileString(IniSection, "Mode2", "DOH", IniPath);
                else WritePrivateProfileString(IniSection, "Mode2", "DNS", IniPath);
                if (radioDOH3.Checked) WritePrivateProfileString(IniSection, "Mode3", "DOH", IniPath);
                else WritePrivateProfileString(IniSection, "Mode3", "DNS", IniPath);
                if (radioDOH4.Checked) WritePrivateProfileString(IniSection, "Mode4", "DOH", IniPath);
                else WritePrivateProfileString(IniSection, "Mode4", "DNS", IniPath);
                if (!string.IsNullOrEmpty(comboServer1.Text))
                    WritePrivateProfileString(IniSection, "Server1", comboServer1.Text, IniPath);
                if (!string.IsNullOrEmpty(comboServer2.Text))
                    WritePrivateProfileString(IniSection, "Server2", comboServer2.Text, IniPath);
                if (!string.IsNullOrEmpty(comboServer3.Text))
                    WritePrivateProfileString(IniSection, "Server3", comboServer3.Text, IniPath);
                if (!string.IsNullOrEmpty(comboServer4.Text))
                    WritePrivateProfileString(IniSection, "Server4", comboServer4.Text, IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var sb = new StringBuilder(256);
                string val;
                GetPrivateProfileString(IniSection, "TLD", "", sb, sb.Capacity, IniPath);
                string tld = sb.ToString();
                if (!string.IsNullOrEmpty(tld) && comboTLD.Items.Count > 0)
                {
                    int idx = -1;
                    for (int i = 0; i < comboTLD.Items.Count; i++)
                        if (comboTLD.Items[i].ToString() == tld) { idx = i; break; }
                    if (idx >= 0) comboTLD.SelectedIndex = idx;
                    else comboTLD.Text = tld;
                }
                GetPrivateProfileString(IniSection, "Timeout", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtTimeout.Text = val;
                GetPrivateProfileString(IniSection, "Mode1", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) { if (val == "DOH") radioDOH1.Checked = true; else if (val == "DNS") radioDNS1.Checked = true; }
                GetPrivateProfileString(IniSection, "Mode2", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) { if (val == "DOH") radioDOH2.Checked = true; else if (val == "DNS") radioDNS2.Checked = true; }
                GetPrivateProfileString(IniSection, "Mode3", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) { if (val == "DOH") radioDOH3.Checked = true; else if (val == "DNS") radioDNS3.Checked = true; }
                GetPrivateProfileString(IniSection, "Mode4", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) { if (val == "DOH") radioDOH4.Checked = true; else if (val == "DNS") radioDNS4.Checked = true; }
                GetPrivateProfileString(IniSection, "Server1", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) RestoreComboText(comboServer1, val);
                GetPrivateProfileString(IniSection, "Server2", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) RestoreComboText(comboServer2, val);
                GetPrivateProfileString(IniSection, "Server3", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) RestoreComboText(comboServer3, val);
                GetPrivateProfileString(IniSection, "Server4", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) RestoreComboText(comboServer4, val);
            }
            catch { }
        }

        private void RestoreComboText(ComboBox combo, string text)
        {
            for (int i = 0; i < combo.Items.Count; i++)
                if (combo.Items[i].ToString() == text) { combo.SelectedIndex = i; return; }
            combo.Text = text;
        }
        private bool isTesting = false;
        private int remainingSeconds = 300;
        private CancellationTokenSource cts;

        private static readonly Random globalRnd = new Random();

        public enum TestResultStatus
        {
            Success,
            LogicError,
            NetworkError
        }
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hwnd, int wMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        private const int HTCAPTION = 0x0002;

        private void MyMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_SYSCOMMAND, SC_MOVE + HTCAPTION, 0);
            }
        }
        public DNSSelect()
        {
            InitializeComponent();
        }

        private void DNSSelect_Load(object sender, EventArgs e)
        {
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            this.MinimumSize = this.Size;
            timer2.Start();

            CloudControl.LoadDNSTLD(comboTLD);
            CloudControl.LoadDNSServers(comboServer1);
            CloudControl.LoadDNSServers(comboServer2);
            CloudControl.LoadDNSServers(comboServer3);
            CloudControl.LoadDNSServers(comboServer4);

            Task.Run(() => DNSSelectLoadALL());
            CloudControl.UsedTimesCounter("DNS真选");
            LoadSettings();
        }

        // 自动刷新网卡：当系统网卡变化导致选中网卡不存在时，刷新列表并恢复默认
        private void EnsureSelectedNICValid()
        {
            string selectedText = comboLocalEnd.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.StartsWith("0.0.0.0") || selectedText.StartsWith("::")) return;

            DNSSelectLoadALL();

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

        private void DNSSelectLoadALL()
        {
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");
            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;
            lblExeName.Text = Global.exeName + " " + Global.Version;
            DrawLatencyLegend(richServer4);
            label2.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | 耗时(ms)";

            //获取本机所有网卡
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
            // 开发调试服务器列表（仅在窗口载入时加载一次，此处不再重复加载）
            CloudControl.ApplyDevTitle(this);
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (!isTesting)
            {
                EnsureSelectedNICValid();

                string tld = comboTLD.Text;
                if (string.IsNullOrEmpty(tld)) { MessageBox.Show("请填入根域名"); return; }

                int timeout;
                if (!int.TryParse(txtTimeout.Text, out timeout)) timeout = 2000;

                string selectedIpInfo = comboLocalEnd.Text;
                string finalIp = selectedIpInfo.Split(' ')[0];

                if (finalIp == "0.0.0.0" || finalIp == "::")
                {
                    AddressFamily family = (finalIp == "0.0.0.0") ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                    string realIp = GetRoutingLocalIp(family);

                    if (!string.IsNullOrEmpty(realIp))
                    {
                        for (int i = 0; i < comboLocalEnd.Items.Count; i++)
                        {
                            if (comboLocalEnd.Items[i].ToString().StartsWith(realIp))
                            {
                                comboLocalEnd.SelectedIndex = i;
                                finalIp = realIp;
                                break;
                            }
                        }
                    }
                }

                isTesting = true;
                btnStart.Text = "停止";
                ToggleUI(false);
                this.Text = Global.isUnlimitedTime
                    ? "DNS真选 ✧ NICX (0)"
                    : "DNS真选 ✧ NICX (300)";
                CloudControl.ApplyDevTitle(this);
                cts = new CancellationTokenSource();
                remainingSeconds = Global.isUnlimitedTime ? 0 : 300;
                richServer1.Text = String.Empty;
                richServer2.Text = String.Empty;
                richServer3.Text = String.Empty;
                richServer4.Text = String.Empty;
                timer1.Start();
                for (int i = 1; i <= 4; i++)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    ComboBox cb = (ComboBox)this.Controls.Find("comboServer" + i, true)[0];
                    RadioButton rbDoh = (RadioButton)this.Controls.Find("radioDOH" + i, true)[0];
                    RichTextBox rtb = (RichTextBox)this.Controls.Find("richServer" + i, true)[0];

                    rtb.Clear();
                    if (!string.IsNullOrEmpty(cb.Text))
                    {
                        string serverAddr = cb.Text;
                        bool isDohMode = rbDoh.Checked;
                        int index = i;

                        _ = Task.Run(async () =>
                        {
                            await StartTestLoop(index, serverAddr, isDohMode, tld, finalIp, timeout);
                        }, cts.Token);
                    }

                    await Task.Delay(250);
                }
            }
            else
            {
                StopTesting();
            }
        }

        //DNS方法
        private async Task<(long elapsed, TestResultStatus status)> UdpDnsTest(string server, string domain, string localIp, int timeout)
        {
            if (cts.Token.IsCancellationRequested) return (-1, TestResultStatus.NetworkError);

            if (server == "系统默认")
            {
                var dnsAddr = NicHelper.GetFirstSystemDns(AddressFamily.InterNetwork);
                if (dnsAddr == null) return (-1, TestResultStatus.NetworkError);
                server = dnsAddr.ToString();
            }

            IPAddress serverAddr;
            if (!IPAddress.TryParse(server, out serverAddr))
            {
                try
                {
                    var hosts = await Dns.GetHostAddressesAsync(server);
                    serverAddr = hosts[0];
                }
                catch { return (-1, TestResultStatus.NetworkError); }
            }
            IPEndPoint remoteEP = new IPEndPoint(serverAddr, 53);
            Stopwatch sw = new Stopwatch();
            using (Socket socket = new Socket(remoteEP.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
                    socket.ReceiveTimeout = timeout;
                    byte[] request = BuildDnsQueryPacket(domain);

                    sw.Start();
                    socket.SendTo(request, remoteEP);
                    byte[] response = new byte[512];
                    EndPoint senderRemote = new IPEndPoint(remoteEP.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

                    // 注意：Socket.ReceiveFrom 是同步阻塞的，它不直接支持 Token
                    // 但我们可以通过 catch 块和后续检查来拦截
                    socket.ReceiveFrom(response, ref senderRemote);
                    sw.Stop();

                    if (cts.Token.IsCancellationRequested) return (-1, TestResultStatus.NetworkError);

                    int rcode = response[3] & 0x0F;
                    return rcode == 0 ? (sw.ElapsedMilliseconds, TestResultStatus.Success) : (sw.ElapsedMilliseconds, TestResultStatus.LogicError);
                }
                catch { return (-1, TestResultStatus.NetworkError); }
            }

        }

        // 手动构造DNS包，不需要深究，直接复制即可
        private byte[] BuildDnsQueryPacket(string domain)
        {
            List<byte> packet = new List<byte>();
            // Header: ID(2), Flags(2), Questions(2), AnswerRRs(2), AuthorityRRs(2), AdditionalRRs(2)
            packet.AddRange(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            // Question: 把域名 163.com 变成 [3]163[3]com[0] 这种格式
            string[] parts = domain.Split('.');
            foreach (string part in parts)
            {
                packet.Add((byte)part.Length);
                packet.AddRange(Encoding.ASCII.GetBytes(part));
            }
            packet.Add(0x00); // 结束符

            // Type A (0x0001), Class IN (0x0001)
            packet.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 });
            return packet.ToArray();
        }

        //DOH测试方法
        private async Task<(long elapsed, TestResultStatus status)> DohDnsTest(string baseUrl, string domain, int timeout)
        {
            // 点了停止直接返回失败，不再发起新请求
            if (cts.Token.IsCancellationRequested) return (-1, TestResultStatus.NetworkError);

            string fullUrl = baseUrl;
            if (!fullUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) fullUrl = "https://" + fullUrl;
            if (!fullUrl.Contains("/resolve") && !fullUrl.Contains("/dns-query")) fullUrl = fullUrl.TrimEnd('/') + "/resolve";
            fullUrl += (fullUrl.Contains("?") ? "&" : "?") + $"name={domain}&type=1";

            Stopwatch sw = new Stopwatch();
            try
            {
                // 关键：将外部的 cts.Token 与超时的 timeoutCts 合并
                using (var timeoutCts = new CancellationTokenSource(timeout))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token))
                {
                    sw.Start();
                    // 这里传入 linkedCts.Token，点击停止时会立刻触发 TaskCanceledException
                    string result = await HttpHelper.SendAsync(fullUrl, linkedCts.Token);
                    sw.Stop();

                    // 如果等待期间停止，即使拿到了结果也不要了
                    if (cts.Token.IsCancellationRequested) return (-1, TestResultStatus.NetworkError);

                    if (string.IsNullOrEmpty(result) || sw.ElapsedMilliseconds >= timeout - 10)
                        return (-1, TestResultStatus.NetworkError);

                    if (result.IndexOf("\"Answer\":", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        result.IndexOf("\"answer\":", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (sw.ElapsedMilliseconds, TestResultStatus.Success);
                    }
                    return (sw.ElapsedMilliseconds, TestResultStatus.LogicError);
                }
            }
            catch (OperationCanceledException)
            {
                // 这是点击停止或超时会触发的异常
                return (-1, TestResultStatus.NetworkError);
            }
            catch
            {
                return (-1, TestResultStatus.NetworkError);
            }
        }

        //修改测试UI开关
        private void ToggleUI(bool v)
        {
            comboLocalEnd.Enabled = v;
            comboTLD.Enabled = v;
            txtTimeout.Enabled = v;
            comboServer1.Enabled = v;
            comboServer2.Enabled = v;
            comboServer3.Enabled = v;
            comboServer4.Enabled = v;
            radioDNS1.Enabled = v;
            radioDNS2.Enabled = v;
            radioDNS3.Enabled = v;
            radioDNS4.Enabled = v;
            radioDOH1.Enabled = v;
            radioDOH2.Enabled = v;
            radioDOH3.Enabled = v;
            radioDOH4.Enabled = v;
        }

        private void StopTesting()
        {
            isTesting = false;
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            timer1.Stop();
            this.Text = "DNS真选 ✧ NICX (已停止)";
            CloudControl.ApplyDevTitle(this);
            btnStart.Text = "开测";
            ToggleUI(true); // 恢复输入框
        }

        //循环测试方法
        private async Task StartTestLoop(int index, string server, bool isDoh, string tld, string localIp, int timeout)
        {
            RichTextBox targetRtb = (RichTextBox)this.Controls.Find("richServer" + index, true)[0];
            //Random rnd = new Random();

            while (!cts.Token.IsCancellationRequested)
            {
                // 💡 使用类级别的 globalRnd
                string fullDomain = $"{GenerateRandomString(globalRnd.Next(4, 32), globalRnd)}.{tld}";
                Console.WriteLine(fullDomain);

                long elapsed;
                TestResultStatus status;

                if (isDoh)
                {
                    (elapsed, status) = await DohDnsTest(server, fullDomain, timeout);
                }
                else
                {
                    (elapsed, status) = await UdpDnsTest(server, fullDomain, localIp, timeout);
                }

                //如果已经停止了，直接退出循环，不再更新 UI
                if (cts.Token.IsCancellationRequested) break;

                string textToShow = (status == TestResultStatus.NetworkError) ? "失败" : elapsed.ToString();
                AppendResult(targetRtb, textToShow, elapsed, status);

                try { await Task.Delay(1000, cts.Token); } catch { break; }
            }
        }

        // 输出颜色文本到对应文本框
        private void AppendResult(RichTextBox rtb, string text, long ms, TestResultStatus status)
        {
            if (rtb.IsDisposed) return;
            rtb.Invoke(new Action(() =>
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;

                if (status == TestResultStatus.NetworkError) rtb.SelectionColor = Color.Red;
                else if (ms <= 25) rtb.SelectionColor = Color.Lime;
                else if (ms <= 50) rtb.SelectionColor = Color.MediumSpringGreen;
                else if (ms <= 100) rtb.SelectionColor = Color.FromArgb(185, 210, 50);
                else if (ms <= 200) rtb.SelectionColor = Color.Gold;
                else if (ms <= 500) rtb.SelectionColor = Color.Orange;
                else rtb.SelectionColor = Color.Tomato;

                rtb.SelectionFont = new Font(rtb.Font, status == TestResultStatus.Success ? FontStyle.Regular : FontStyle.Italic);

                rtb.AppendText(text + ", ");
                rtb.ScrollToCaret();
            }));
        }

        // 在指定的 RichTextBox 中绘制延迟颜色对比图
        private void DrawLatencyLegend(RichTextBox rtb)
        {
            if (rtb == null) return;

            rtb.Invoke(new Action(() =>
            {
                rtb.Clear();

                // 定义颜色和对应文字的数组
                var legendItems = new[]
                {
            new { Text = "延迟颜色对照 ", Color = Color.LightYellow },
            new { Text = "≤25", Color = Color.Lime },
            new { Text = "50", Color = Color.MediumSpringGreen },
            new { Text = "100", Color = Color.FromArgb(185, 210, 50) },
            new { Text = "200", Color = Color.Gold },
            new { Text = "500", Color = Color.Orange },
            new { Text = ">500ms", Color = Color.Tomato },
            new { Text = "超时/失败", Color = Color.Red }
        };

                for (int i = 0; i < legendItems.Length; i++)
                {
                    var item = legendItems[i];

                    // 设置颜色
                    rtb.SelectionColor = item.Color;
                    // 设置加粗（对照表加粗一点更好看）
                    rtb.SelectionFont = new Font(rtb.Font, FontStyle.Bold);

                    // 写入文字
                    rtb.AppendText(item.Text);

                    // 如果不是最后一项，加一个分隔符
                    if (i < legendItems.Length - 1)
                    {
                        rtb.SelectionColor = Color.Gray; // 分隔符用灰色
                        rtb.SelectionFont = new Font(rtb.Font, FontStyle.Regular);
                        rtb.AppendText(" | ");
                    }
                }

                rtb.ScrollToCaret();
            }));
        }
        // 查找系统预期的出口 IP
        private string GetRoutingLocalIp(AddressFamily family)
        {
            try
            {
                // 尝试连接一个公网 IP（并不真正发送数据）
                IPAddress target = (family == AddressFamily.InterNetwork)
                    ? IPAddress.Parse("8.8.8.8")
                    : IPAddress.Parse("2001:4860:4860::8888");

                using (var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(target, 53);
                    if (socket.LocalEndPoint is IPEndPoint localEndPoint)
                    {
                        return localEndPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                // 如果没有该协议族的出口（比如没开启IPv6），就返回空
            }
            return null;
        }
        private void timer1_Tick(object sender, EventArgs e)
        {
            if (Global.isUnlimitedTime)
            {
                remainingSeconds++;
                this.Text = $"DNS真选 ✧ NICX ({remainingSeconds})";
                CloudControl.ApplyDevTitle(this);
            }
            else
            {
                remainingSeconds--;
                if (remainingSeconds <= 0)
                {
                    StopTesting();
                }
                else
                {
                    this.Text = $"DNS真选 ✧ NICX ({remainingSeconds})";
                    CloudControl.ApplyDevTitle(this);
                }
            }
        }
        //随机字符串
        private string GenerateRandomString(int length, Random random)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            char[] result = new char[length];

            for (int i = 0; i < length; i++)
            {
                result[i] = chars[random.Next(chars.Length)];
            }

            // 确保不以数字开头（符合域名规范）
            if (char.IsDigit(result[0]))
            {
                // 如果以数字开头，替换为字母
                result[0] = chars[random.Next(26)]; // 只取字母部分
            }

            return new string(result);
        }
        private void comboLocalEnd_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void DNSSelect_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            timer1.Stop();
            timer1.Dispose();
            timer2.Stop();
            timer2.Dispose();
            StopTesting();
        }

        private void timer2_Tick(object sender, EventArgs e)
        {
            label2.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | 耗时(ms)";
        }

        private void lblTLD_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (isTesting)
                {
                    StopTesting();
                }

                Point currentPkgLocation = this.Location;

                DNSSelect newForm = new DNSSelect();

                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentPkgLocation;

                newForm.Show();

                this.Close();
                this.Dispose();
            }
        }
    }
}
