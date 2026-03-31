using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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
        // 这里的变量用来控制测试的开关
        private bool isTesting = false; // 是否正在测试中
        private int remainingSeconds = 300; // 剩余秒数
        private CancellationTokenSource cts; // 用于一键停止所有异步任务

        // 在类顶部定义，这样整个程序运行期间只初始化一次
        private static readonly Random globalRnd = new Random();

        // 定义三种测试状态
        public enum TestResultStatus
        {
            Success,      // 成功：有回包且解析出了 IP
            LogicError,   // 逻辑错误：有回包但告诉我们解析失败（比如 NXDOMAIN）
            NetworkError  // 网络错误：完全没回包（超时或断网）
        }
        //自由拖拽
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hwnd, int wMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        private const int HTCAPTION = 0x0002;

        // 这是一个通用的拖动处理函数
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
            //随意拖拽
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            this.MinimumSize = this.Size;
            timer2.Start();
            Task.Run(() => DNSSelectLoadALL());
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
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // 状态过滤：只看正在运行的
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    // 关键字黑名单：排除常见的纯虚拟网卡环境
                    string desc = ni.Description.ToLower();
                    if (desc.Contains("vmware") || desc.Contains("virtual") || desc.Contains("vbox") || desc.Contains("hyper-v") || desc.Contains("wsl") || desc.Contains("pseudo") || desc.Contains("tap") || desc.Contains("tun") || desc.Contains("loopback") || desc.Contains("vpn") || desc.Contains("teredo"))
                        continue;

                    // 获取 IP 属性
                    var ipProps = ni.GetIPProperties();

                    // 保留物理网卡+有网关的虚拟网卡
                    bool isPhysical = (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                       ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                    bool hasGateway = ipProps.GatewayAddresses.Count > 0;

                    if (!isPhysical && !hasGateway) continue;

                    // 遍历该网卡下的所有 IP
                    foreach (UnicastIPAddressInformation ipInfo in ipProps.UnicastAddresses)
                    {
                        IPAddress ip = ipInfo.Address;

                        // 排除回环地址、链路本地地址
                        if (IPAddress.IsLoopback(ip)) continue;
                        if (ip.IsIPv6LinkLocal) continue;
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] bytes = ip.GetAddressBytes();
                            if (bytes[0] == 169 && bytes[1] == 254) continue;
                        }

                        // 去掉IPv6的%ID
                        string ipStr = ip.ToString();
                        if (ipStr.Contains("%")) ipStr = ipStr.Split('%')[0];

                        //显示IP的同时也显示网卡名称
                        string displayName = string.Format("{0} ({1})", ipStr, ni.Name);

                        comboLocalEnd.Items.Add(displayName);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取网卡列表失败: " + ex.Message);
            }
            // 开发调试服务器列表
            CloudControl.LoadDNSTLD(comboTLD);
            CloudControl.LoadDNSServers(comboServer1);
            CloudControl.LoadDNSServers(comboServer2);
            CloudControl.LoadDNSServers(comboServer3);
            CloudControl.LoadDNSServers(comboServer4);
            CloudControl.ApplyDevTitle(this);
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (!isTesting)
            {
                // --- 1. 获取基础参数 ---
                string tld = comboTLD.Text;
                if (string.IsNullOrEmpty(tld)) { MessageBox.Show("夢酱，记得填入根域名哦！"); return; }

                int timeout;
                if (!int.TryParse(txtTimeout.Text, out timeout)) timeout = 2000;

                // --- 2. 核心：出口 IP 自动识别逻辑 ---
                string selectedIpInfo = comboLocalEnd.Text;
                string finalIp = selectedIpInfo.Split(' ')[0]; // 拿到原始填写的 IP 部分（如 0.0.0.0）

                if (finalIp == "0.0.0.0" || finalIp == "::")
                {
                    AddressFamily family = (finalIp == "0.0.0.0") ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                    string realIp = GetRoutingLocalIp(family); // 调用刚才定义的寻路方法

                    if (!string.IsNullOrEmpty(realIp))
                    {
                        // 在 ComboBox 中寻找匹配的项并切换过去
                        for (int i = 0; i < comboLocalEnd.Items.Count; i++)
                        {
                            if (comboLocalEnd.Items[i].ToString().StartsWith(realIp))
                            {
                                comboLocalEnd.SelectedIndex = i;
                                finalIp = realIp; // 更新为真实的出口 IP
                                break;
                            }
                        }
                    }
                }

                // --- 3. 准备开始测试状态 ---
                isTesting = true;
                btnStart.Text = "停止";
                ToggleUI(false); // 禁用输入框
                this.Text = "DNS真选 ✧ NICX (300)";
                CloudControl.ApplyDevTitle(this);
                cts = new CancellationTokenSource();
                remainingSeconds = 300;
                richServer1.Text = String.Empty;
                richServer2.Text = String.Empty;
                richServer3.Text = String.Empty;
                richServer4.Text = String.Empty;
                timer1.Start();
                CloudControl.UsedTimesCounter("DNS真选");
                // --- 4. 启动 4 个错峰测试任务 ---
                for (int i = 1; i <= 4; i++)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    // 动态获取 UI 控件
                    ComboBox cb = (ComboBox)this.Controls.Find("comboServer" + i, true)[0];
                    RadioButton rbDoh = (RadioButton)this.Controls.Find("radioDOH" + i, true)[0];
                    RichTextBox rtb = (RichTextBox)this.Controls.Find("richServer" + i, true)[0];

                    rtb.Clear();
                    if (!string.IsNullOrEmpty(cb.Text))
                    {
                        string serverAddr = cb.Text;
                        bool isDohMode = rbDoh.Checked;
                        int index = i;

                        // 强制在后台线程运行循环，彻底告别卡顿！
                        _ = Task.Run(async () =>
                        {
                            await StartTestLoop(index, serverAddr, isDohMode, tld, finalIp, timeout);
                        }, cts.Token);
                    }

                    await Task.Delay(250); // 错峰 250ms
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
                var dnsAddr = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(nic => nic.GetIPProperties().DnsAddresses)
                    .FirstOrDefault(addr => addr.AddressFamily == AddressFamily.InterNetwork);
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

                // 1. 设置颜色
                if (status == TestResultStatus.NetworkError) rtb.SelectionColor = Color.Red;
                else if (ms <= 25) rtb.SelectionColor = Color.Lime;
                else if (ms <= 50) rtb.SelectionColor = Color.MediumSpringGreen;
                else if (ms <= 100) rtb.SelectionColor = Color.FromArgb(185, 210, 50);
                else if (ms <= 200) rtb.SelectionColor = Color.Gold;
                else if (ms <= 500) rtb.SelectionColor = Color.Orange;
                else rtb.SelectionColor = Color.Tomato;

                // 2. 设置字体倾斜：只有解析成功是不倾斜的
                rtb.SelectionFont = new Font(rtb.Font, status == TestResultStatus.Success ? FontStyle.Regular : FontStyle.Italic);

                // 3. 追加文字
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
            timer2.Stop();
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
                // 1. 停止当前的测试逻辑（防止窗口关了后台还在跑）
                if (isTesting)
                {
                    StopTesting();
                }

                // 2. 记录当前窗口的位置，这样重启后窗口还在原来的地方，不会乱跳
                Point currentPkgLocation = this.Location;

                // 3. 创建一个新的窗口实例
                DNSSelect newForm = new DNSSelect();

                // 让新窗口在老窗口的位置显示
                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentPkgLocation;

                // 4. 显示新窗口
                newForm.Show();

                // 5. 彻底关闭并释放当前窗口
                this.Close();
                this.Dispose();
            }
        }
    }
}
