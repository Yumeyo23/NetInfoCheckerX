using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

        // 定义统计用的变量
        double minDelay = 9999, maxDelay = 0, totalDelay = 0;
        int successCount = 0, lossCount = 0;
        // 增加记录序号的变量
        int minCountIndex = 0, maxCountIndex = 0;
        // 定义全局的ICMP序号，初始值为0
        private ushort _globalIcmpSequence = 0;

        private CancellationTokenSource _cts;
        private bool isRunning = false; // 记录当前是不是正在测试
        private bool isSettingsPrinted = false; // 增加一个开关，保证每次测试只打印一次

        private void UpdateDelay(double rtt)
        {
            int currentTotal = successCount + lossCount; // 获取当前是第几次

            if (rtt < minDelay)
            {
                minDelay = rtt;
                minCountIndex = currentTotal; // 记下最小值的序号
            }
            if (rtt > maxDelay)
            {
                maxDelay = rtt;
                maxCountIndex = currentTotal; // 记下最大值的序号
            }
            totalDelay += rtt;
        }

        private void ResetStats()
        {
            minDelay = 9999; maxDelay = 0; totalDelay = 0;
            successCount = 0; lossCount = 0;
            minCountIndex = 0; maxCountIndex = 0; // 重置序号
            isSettingsPrinted = false; // 准备好下次测试打印
            UpdateStats();
        }

        // 获取用户选中的本地出口端点
        private IPEndPoint GetLocalEndPoint()
        {
            string selected = comboLocalEnd.Text;

            // Any 逻辑保持不变
            if (selected.Contains("0.0.0.0")) return new IPEndPoint(IPAddress.Any, 0);
            if (selected.Contains("::")) return new IPEndPoint(IPAddress.IPv6Any, 0);

            // 如果字符串里有空格（说明后面跟着网卡名），只取前面的IP部分
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

        //UDP Ping方法
        private async Task ExecuteUdpPing(string targetIp, int port, int timeout, CancellationToken token)
        {
            IPAddress targetAddr = IPAddress.Parse(targetIp);
            IPEndPoint remoteEP = new IPEndPoint(targetAddr, port);

            using (Socket socket = new Socket(targetAddr.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                try
                {
                    // 绑定到用户选择的本地端点（可能是 Any）
                    socket.Bind(GetLocalEndPoint());
                    // 设置底层超时，作为最后的防线
                    socket.ReceiveTimeout = timeout;

                    // 如果用户选择的是 Any (0.0.0.0 / ::)，我们想知道实际系统将使用哪一个本地地址用于发包
                    // 于是尝试通过一个临时 UDP connect 来获取真实的出口地址（不改变当前 socket 的绑定）
                    try
                    {
                        var localEp = (IPEndPoint)socket.LocalEndPoint;
                        if (localEp != null && (localEp.Address.Equals(IPAddress.Any) || localEp.Address.Equals(IPAddress.IPv6Any)))
                        {
                            string outbound = GetActualLocalIp(targetIp); // 换成这行喵！
                            if (outbound != null)
                            {
                                // 更新界面（如果当前下拉选中 Any，则替换为实际使用的 IP）
                                //UpdateRealLocalIp(new IPEndPoint(outbound, 0));
                                // 重点：在这里就直接打印设置！不要等到收到回包再打印
                                PrintTestSettings(outbound.ToString());
                            }
                        }
                    }
                    catch { }

                    // 1. 准备数据包
                    byte[] sendData;
                    // 如果模拟DNS，构造一个最小DNS查询，每次使用随机域名避免缓存
                    if (port == 53)
                    {
                        // 生成随机域名，避免DNS缓存
                        string randomDomain = GenerateRandomDomain();
                        sendData = BuildSimpleDnsQuery(randomDomain);
                        // 记录使用的域名用于调试
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] DNS查询域名: {randomDomain}");
                    }
                    // 模拟NTP服务器同步时间
                    else if (port == 123)
                    {
                        sendData = new byte[48];
                        sendData[0] = 0x1B;
                    }
                    //模拟STUN服务器回显IP
                    else if (port == 3478 || port == 3489 || port == 19302)
                    {
                        // 使用最标准的 STUN 结构
                        sendData = new byte[20];

                        // 1. Message Type: 0x0001 (Binding Request)
                        sendData[0] = 0x00;
                        sendData[1] = 0x01;

                        // 2. Message Length: 0x0000 (没有附加属性)
                        sendData[2] = 0x00;
                        sendData[3] = 0x00;

                        // 3. Magic Cookie: 0x2112A442
                        sendData[4] = 0x21;
                        sendData[5] = 0x12;
                        sendData[6] = 0xA4;
                        sendData[7] = 0x42;

                        // 4. Transaction ID: 12字节。
                        // 把当前时间戳塞进去，保证每次请求都不一样
                        byte[] ts = BitConverter.GetBytes(DateTime.Now.Ticks);
                        Array.Copy(ts, 0, sendData, 8, Math.Min(ts.Length, 12));
                    }
                    else
                    {
                        sendData = new byte[int.TryParse(txtPackage.Text, out int b) ? b : 32];
                        new Random().NextBytes(sendData);
                    }

                    byte[] receiveBuffer = new byte[4096];
                    EndPoint receiveEP = new IPEndPoint(targetAddr.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

                    string timeStr = DateTime.Now.ToString("HH:mm:ss");
                    var sw = new Stopwatch();

                    // 预设接收任务
                    var receiveTask = Task.Factory.FromAsync(
                        (callback, state) => socket.BeginReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref receiveEP, callback, state),
                        (ar) => socket.EndReceiveFrom(ar, ref receiveEP),
                        null);

                    // 计时并发送
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

                        // 获取真实的本地出口 IP
                        string localIpToPrint = "系统默认";
                        try
                        {
                            // 从 socket 获取系统实际分配的本地端点
                            var localEp = (IPEndPoint)socket.LocalEndPoint;
                            if (localEp != null)
                            {
                                localIpToPrint = localEp.Address.ToString();
                            }
                        }
                        catch { }

                        PrintTestSettings(localIpToPrint);

                        // 3. 获取回包的目标 IP
                        string remoteIp = ((IPEndPoint)receiveEP).Address.ToString();

                        Color rowColor = GetRttColor(rtt);
                        AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);
                        AppendColorText($"UDP成功: {remoteIp} ={rtt:F1}ms", rowColor, true);

                    }
                    else
                    {
                        // 超时或取消
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

        // 生成随机域名
        private string GenerateRandomDomain()
        {
            // 创建随机数生成器，使用时间相关的种子确保每次不同
            Random random = new Random(Guid.NewGuid().GetHashCode());

            // 常见TLD列表
            // string[] tlds = { ".ipleak.net" ".dns4.browserleaks.org" ".dns6.browserleaks.org" ".ipv4.surfsharkdns.com" ".ipv6.surfsharkdns.com"   };
            string[] tlds = { ".nstool.netease.com" };

            // 生成随机长度的域名主体
            int nameLength = random.Next(4, 16);
            string domainName = GenerateRandomString(nameLength, random);

            // 选择随机TLD
            string tld = tlds[random.Next(tlds.Length)];

            return domainName + tld;
        }

        // 生成随机字符串
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

        // 修改BuildSimpleDnsQuery方法，使其接受域名参数
        private byte[] BuildSimpleDnsQuery(string domain)
        {
            // DNS头部：事务ID(2字节) + 标志(2字节) + 问题计数(2字节) + 回答/权威/附加记录计数(各2字节)
            byte[] header = new byte[] {
        0x00, 0x01,             // 事务ID - 随机生成（可以固定为0x0001）
        0x01, 0x00,             // 标志：标准查询，递归期望
        0x00, 0x01,             // 问题计数：1
        0x00, 0x00,             // 回答记录计数：0
        0x00, 0x00,             // 权威记录计数：0
        0x00, 0x00              // 附加记录计数：0
    };

            // 生成随机的Transaction ID（可选，但更符合实际情况）
            Random rand = new Random();
            byte[] transId = BitConverter.GetBytes((ushort)rand.Next(0, 65535));
            if (BitConverter.IsLittleEndian)
                Array.Reverse(transId);

            header[0] = transId[0];
            header[1] = transId[1];

            // 编码域名：每个标签前加长度字节
            List<byte> queryBytes = new List<byte>();
            string[] labels = domain.Split('.');

            foreach (string label in labels)
            {
                queryBytes.Add((byte)label.Length); // 标签长度
                queryBytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); // 标签内容
            }

            queryBytes.Add(0x00); // 域名结束标记

            // 查询类型：A记录 (0x0001)
            queryBytes.AddRange(new byte[] { 0x00, 0x01 });

            // 查询类：IN (0x0001)
            queryBytes.AddRange(new byte[] { 0x00, 0x01 });

            // 合并头部和查询部分
            byte[] fullQuery = new byte[header.Length + queryBytes.Count];
            Buffer.BlockCopy(header, 0, fullQuery, 0, header.Length);
            Buffer.BlockCopy(queryBytes.ToArray(), 0, fullQuery, header.Length, queryBytes.Count);

            return fullQuery;
        }

        //TCP Ping方法
        private async Task ExecuteTcpPing(string targetIp, int port, int timeout, CancellationToken token)
        {
            //预先解析好IP地址
            IPAddress ipAddr = IPAddress.Parse(targetIp);
            IPEndPoint remoteEP = new IPEndPoint(ipAddr, port);

            Socket socket = null;
            try
            {
                socket = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                // 保持优化设置
                socket.LingerState = new LingerOption(false, 0);
                socket.ExclusiveAddressUse = false;
                socket.NoDelay = true; // 禁用Nagle算法，让发包更干脆

                socket.Bind(GetLocalEndPoint());
                string timeStr = DateTime.Now.ToString("HH:mm:ss");

                //精确计时器
                var sw = new Stopwatch();

                // 使用 .NET 4.7.2 支持的 Task.Factory.FromAsync，这比 Task.Run 更直接
                // 它会直接调用底层的 I/O 完成端口，不经过线程池排队
                var connectTask = Task.Factory.FromAsync(
                    socket.BeginConnect,
                    socket.EndConnect,
                    remoteEP,
                    null
                );
                int currentTotal = successCount + lossCount + 1;
                sw.Start();

                // 等待连接完成、超时或被用户取消
                var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeout, token));

                if (completedTask == connectTask && socket.Connected)
                {
                    sw.Stop();
                    double rtt = sw.Elapsed.TotalMilliseconds;

                    if (rtt < 0.1) rtt = 0.1;
                    successCount++;
                    UpdateDelay(rtt);

                    // 更新 UI 逻辑
                    string actualIp = ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
                    PrintTestSettings(actualIp);

                    Color rowColor = GetRttColor(rtt);
                    AppendColorText($"[{timeStr}]({currentTotal}) ", ColorTranslator.FromHtml("#a8a5ff"), false);

                    // ✨ 梦酱看这里：判断如果是 IPv6，就给 targetIp 套上中括号
                    string displayTarget = ipAddr.AddressFamily == AddressFamily.InterNetworkV6
                        ? $"[{targetIp}]:{port}"
                        : $"{targetIp}:{port}";

                    AppendColorText($"TCP成功: {displayTarget} ={rtt:F1}ms", rowColor, true);
                }
                else
                {
                    // 处理失败逻辑
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
                // 彻底释放 Socket 资源
                if (socket != null)
                {
                    try { socket.Close(0); socket.Dispose(); } catch { }
                }
                UpdateStats();
            }
        }

        // 切换协议提示
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
                ResetStats(); //切换协议时顺便把标签还原
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
                    AppendColorText("          ICMP 为无端口测试，最大单包大小为 1472 字节\n", Color.White, true);
                    AppendColorText("    ❤ 延迟颜色对照表", Color.LightSkyBlue, true);
                    AppendColorMap(); // 调用色卡生成
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
                    AppendColorMap(); // 调用色卡生成
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
                    AppendColorMap(); // 调用色卡生成
                    txtPort.Text = "80";
                }
                comboLocalEnd.Enabled = true;
                txtPort.Enabled = true;
                txtPackage.Enabled = false;
            }
        }

        private void PingPP_Load(object sender, EventArgs e)
        {
            AppendColorText("✧ 正在检查系统环境，请稍候 ✧\n", Color.White, true);
            // 字体优化逻辑
            using (Graphics g = this.CreateGraphics())
            {
                if (g.DpiX > 96)
                {
                    Font modernFont = new Font("Cascadia Mono", 9.5F, FontStyle.Regular);
                    richTextBox1.Font = modernFont;
                }
                else
                {
                    // 100%缩放保持默认或指定为新宋体
                    //richTextBox1.Font = new Font("NSimSun", 10.5F, FontStyle.Regular);
                }
            }

            // 默认选中ICMP
            RadioProtocol_CheckedChanged(radioICMP, null);
            Task.Run(() => PingPPLoadAll());
        }

        private void PingPPLoadAll()
        {
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;

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

            // 后台预热逻辑
            _ = Task.Run(async () => await WarmUpAllProtocols());
            // 开发调试服务器列表
            CloudControl.LoadTraceServers(comboTarget);
            CloudControl.ApplyDevTitle(this);
        }

        // 新增：全局预热方法
        private async Task WarmUpAllProtocols()
        {
            try
            {
                // 阶段1：JIT预热 - 让.NET提前编译所有可能用到的代码
                await Task.Run(() => PreJitWarmUp());

                // 阶段2：网络预热 - 分别预热三种协议
                await Task.WhenAll(
                    WarmUpIcmpProtocol(),
                    WarmUpTcpProtocol(),
                    WarmUpUdpProtocol()
                );

                // 阶段3：让系统稳定一下
                await Task.Delay(200);
            }
            catch { /* 静默处理，不影响用户 */ }
        }
        // 新增：JIT预热 - 提前编译所有可能的方法
        private void PreJitWarmUp()
        {
            try
            {
                // 1. 预热所有统计方法
                ResetStats();
                UpdateStats();

                // 2. 预热颜色方法
                GetRttColor(0);
                GetRttColor(100);
                GetRttColor(300);

                // 4. 预热网络地址解析
                IPAddress.Parse("127.0.0.1");
                IPAddress.Parse("::1");

                // 5. 预热Socket对象创建
                var socket1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var socket2 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket1.Close();
                socket2.Close();

                // 6. 预热Ping对象
                using (var ping = new Ping())
                {
                    // 预热同步方法
                }

                // 7. 预热DNS解析
                Dns.GetHostAddresses("localhost");

                // 8. 预热Stopwatch（高频使用）
                var sw = Stopwatch.StartNew();
                sw.Stop();
            }
            catch { }
        }
        // 新增：ICMP协议预热
        private async Task WarmUpIcmpProtocol()
        {
            try
            {
                using (var ping = new Ping())
                {
                    // 预热本地回环，快速完成
                    var task = ping.SendPingAsync("127.0.0.1", 100);
                    // 等待但不超过200ms
                    if (await Task.WhenAny(task, Task.Delay(200)) == task)
                    {
                        await task;
                    }

                    // 额外预热一次IPv6
                    task = ping.SendPingAsync("::1", 100);
                    if (await Task.WhenAny(task, Task.Delay(200)) == task)
                    {
                        await task;
                    }
                }
            }
            catch { }
        }

        // 新增：TCP协议预热
        private async Task WarmUpTcpProtocol()
        {
            try
            {
                // 预热常用的几个地址族
                var addresses = new[] { "127.0.0.1", "::1" };

                foreach (var addr in addresses)
                {
                    using (var socket = new Socket(IPAddress.Parse(addr).AddressFamily,
                                                  SocketType.Stream, ProtocolType.Tcp))
                    {
                        // 设置超时非常短，只是为了触发JIT编译
                        socket.ReceiveTimeout = 10;
                        socket.SendTimeout = 10;

                        try
                        {
                            // 尝试连接一个不存在的高端口（应该会快速失败）
                            var connectTask = Task.Run(() => socket.Connect(addr, 65500));
                            if (await Task.WhenAny(connectTask, Task.Delay(50)) == connectTask)
                            {
                                await connectTask;
                            }
                        }
                        catch { }

                        // 确保Socket被正确关闭
                        try
                        {
                            if (socket.Connected)
                            {
                                socket.Shutdown(SocketShutdown.Both);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // 新增：UDP协议预热
        private async Task WarmUpUdpProtocol()
        {
            try
            {
                var addresses = new[] { "127.0.0.1", "::1" };

                foreach (var addr in addresses)
                {
                    using (var socket = new Socket(IPAddress.Parse(addr).AddressFamily,
                                                  SocketType.Dgram, ProtocolType.Udp))
                    {
                        // 绑定到随机端口
                        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                        // 发送一个小包到本地（通常会被丢弃）
                        var remoteEP = new IPEndPoint(IPAddress.Parse(addr), 53);
                        byte[] buffer = new byte[1];
                        socket.SendTo(buffer, remoteEP);

                        // 尝试接收（应该会超时）
                        try
                        {
                            socket.ReceiveTimeout = 10;
                            var receiveTask = Task.Run(() =>
                            {
                                byte[] recvBuffer = new byte[1024];
                                return socket.Receive(recvBuffer);
                            });

                            if (await Task.WhenAny(receiveTask, Task.Delay(20)) == receiveTask)
                            {
                                await receiveTask;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void PingPP_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (isRunning)
            {
                _cts?.Cancel(); // 停止测试信号
                isRunning = false;
            }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            // 修改停止按钮逻辑
            if (isRunning)
            {
                AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
                AppendColorText("正在停止上次测试", ColorTranslator.FromHtml("#a8a5ff"), true);
                _cts?.Cancel();
                await Task.Delay(10);
                _cts?.Cancel();
                return;
            }

            // 1. 先把输入全部转为小写（方便后面匹配协议头）并去除前后空格
            string input = comboTarget.Text.Trim().ToLower();

            // 2. 剔除协议头http:// 或 https://
            if (input.StartsWith("http://")) input = input.Substring(7);
            else if (input.StartsWith("https://")) input = input.Substring(8);

            // 3. 剔除斜杠及其后面的内容：比如 "www.baidu.com/index.php" 变成 "www.baidu.com"
            if (input.Contains("/"))
            {
                input = input.Split('/')[0];
            }

            // 4. 正则表达式只保留 字母、数字、点(.)、冒号(:)、中划线(-)、下划线(_)
            // 这里我们在括号里加了 _ 
            input = Regex.Replace(input, @"[^a-z0-9\.\:\-_]", "");

            // 检查清洗后是否还剩下内容
            if (string.IsNullOrEmpty(input))
            {
                SystemSounds.Beep.Play(); // 播放系统提示音
                return;
            }

            // 把清洗干净的地址放回输入框
            comboTarget.Text = input;

            // 接下来的逻辑保持不变
            bool isDirectIp = IPAddress.TryParse(input, out _);
            bool isSelectedIp = input.Contains(" (来自域名:");

            // 修改 btnStart_Click 中的解析逻辑部分
            if (!isDirectIp && !isSelectedIp)
            {
                try
                {
                    richTextBox1.Clear();
                    AppendColorText($"[DNS] 正在解析域名: {input} \n", Color.Yellow, true);

                    // 直接清空，然后把用户刚输入的域名再加回去
                    comboTarget.Items.Clear();
                    comboTarget.Items.Add(input);

                    // 异步解析，不卡 UI
                    IPAddress[] addresses = await Task.Run(() => Dns.GetHostAddresses(input));

                    AppendColorText($"域名 [{input}] 解析出以下 IP：", Color.Yellow, true);
                    foreach (var ip in addresses)
                    {
                        string ipStr = ip.ToString();
                        // 2. 关键点：直接添加 IP 字符串，不再带括号后缀
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

            // 准备测试参数
            string finalIP = input;
            comboTarget.Text = finalIP;

            // 1. 初始化统计和状态
            ResetStats();
            richTextBox1.Clear();
            _cts = new CancellationTokenSource();
            isRunning = true;
            btnStart.Text = "停止";
            SetControlsEnabled(false);

            // 2. 立刻记录并显示开测时间
            string startTimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            AppendColorText($"[开测时间] {startTimeStr}", Color.Yellow, true);
            AppendColorText($"[测试目标] {input}", Color.Cyan, true);

            // 3. 异步准备参数 (避免卡顿)
            int timeout = int.TryParse(txtMaxDelay.Text, out int t) ? t : 2000;
            int port = int.TryParse(txtPort.Text, out int p) ? p : 80;
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;

            // 开始测试，禁用所有设置 UI
            SetControlsEnabled(false);

            // 热身逻辑
            try
            {
                // 创建专门的热身超时Token
                using (var warmupCts = new CancellationTokenSource(2000)) // 最多2秒热身
                {
                    if (radioICMP.Checked)
                    {
                        // ICMP热身：连续热身2次，模拟真实场景
                        using (Ping warmUpPing = new Ping())
                        {
                            // 第一次热身
                            var firstTask = warmUpPing.SendPingAsync(finalIP, 100);
                            if (await Task.WhenAny(firstTask, Task.Delay(100, warmupCts.Token)) == firstTask)
                            {
                                await firstTask;
                            }

                            // 短暂延迟，模拟真实间隔
                            await Task.Delay(10, warmupCts.Token);

                            // 第二次热身
                            var secondTask = warmUpPing.SendPingAsync(finalIP, timeout);
                            if (await Task.WhenAny(secondTask, Task.Delay(200, warmupCts.Token)) == secondTask)
                            {
                                await secondTask;
                            }
                        }
                    }
                    else if (radioTCP.Checked)
                    {
                        // TCP深度热身：创建真实的连接并断开
                        using (Socket s = new Socket(IPAddress.Parse(finalIP).AddressFamily,
                                                    SocketType.Stream, ProtocolType.Tcp))
                        {
                            // 设置热身专用参数
                            s.LingerState = new LingerOption(false, 0);
                            s.ReceiveTimeout = 50;
                            s.SendTimeout = 50;

                            try
                            {
                                // 绑定并连接（快速失败也没关系）
                                s.Bind(GetLocalEndPoint());

                                var connectTask = s.ConnectAsync(finalIP, port);
                                // 快速热身：最多等100ms
                                if (await Task.WhenAny(connectTask, Task.Delay(100, warmupCts.Token)) == connectTask)
                                {
                                    await connectTask;

                                    // 如果连接成功，发送一点数据再断开
                                    if (s.Connected)
                                    {
                                        byte[] warmupData = new byte[1] { 0x00 };
                                        var sendTask = s.SendAsync(new ArraySegment<byte>(warmupData), SocketFlags.None);
                                        if (await Task.WhenAny(sendTask, Task.Delay(50, warmupCts.Token)) == sendTask)
                                        {
                                            await sendTask;
                                        }

                                        // 优雅关闭
                                        s.Shutdown(SocketShutdown.Both);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    else if (radioUDP.Checked)
                    {
                        // UDP深度热身：发送几种不同大小的包
                        using (Socket s = new Socket(IPAddress.Parse(finalIP).AddressFamily,
                                                    SocketType.Dgram, ProtocolType.Udp))
                        {
                            s.Bind(GetLocalEndPoint());
                            s.ReceiveTimeout = 50;

                            // 热身不同大小的包
                            var packetSizes = new[] { 1, 32, 1024 };
                            foreach (var size in packetSizes)
                            {
                                try
                                {
                                    byte[] warmupData = new byte[size];
                                    new Random().NextBytes(warmupData); // 填充随机数据

                                    var sendTask = s.SendToAsync(new ArraySegment<byte>(warmupData),
                                                                SocketFlags.None,
                                                                new IPEndPoint(IPAddress.Parse(finalIP), port));

                                    if (await Task.WhenAny(sendTask, Task.Delay(50, warmupCts.Token)) == sendTask)
                                    {
                                        await sendTask;
                                    }

                                    // 短暂间隔
                                    await Task.Delay(5, warmupCts.Token);
                                }
                                catch { }
                            }
                        }
                    }

                    // 给系统留时间释放资源
                    await Task.Delay(30, warmupCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch { }

            // 动态拼接前置提示
            string protocolName = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");
            string localDisplay = radioICMP.Checked ? "系统默认" : comboLocalEnd.Text;
            if (localDisplay.Contains("Any") && !radioICMP.Checked)
            {
                // 这里简单处理：先显示 Any，等第一笔测试出来后，UpdateRealLocalIp 会修正它
                localDisplay = "自动选择中";
            }

            // 构造设置行单独出一个方法，方便识别网卡输出好看
            // 1. 判断是 IPv4 还是 IPv6
            string version = radioICMP.Checked ? "IP" : (IPAddress.Parse(finalIP).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6");
            string checkTarget = comboLocalEnd.Text.Contains("::") ? "IPv6" : (comboLocalEnd.Text.Contains("0.0.0.0") ? "IPv4" : "");

            // 2. 选择系统默认网卡，检测系统实际出口IP
            if (comboLocalEnd.Text.Contains("Any"))
            {
                AppendColorText($"[检测网卡] 未指定出口网卡, 开始测试[{checkTarget}]实际出口网卡", Color.LightGreen, true);
            }

            // 开始异步循环测试
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    if (this.IsDisposed) break; // 梦酱看这里：如果窗口关了，直接跳出循环

                    if (radioICMP.Checked)
                    {
                        // 判断用户是否选择了“兼容模式”
                        if (comboLocalEnd.Text.Contains("ICMP兼容模式"))
                        {
                            // 兼容模式调用原生方法，不走Socket
                            await ExecuteNativeIcmpPing(finalIP, timeout, _cts.Token);
                        }
                        else
                        {
                            // 否则使用改好的模拟Socket
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
                // 在这里更新UI前，检查窗口是否还活着
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    string stopTimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    isRunning = false;
                    btnStart.Text = "开测";
                    AppendColorText($"[停止时间] {stopTimeStr}", Color.Yellow, true);
                    AppendColorText(" ■ 用户手动停止测试", Color.Yellow, true);
                    SetControlsEnabled(true);
                }
            }
            //测试结束，恢复UI
            SetControlsEnabled(true);
        }

        private void comboTarget_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        //原生ICMP PING
        //原生ICMP PING（增加超时后验证）
        private async Task ExecuteNativeIcmpPing(string targetIp, int timeout, CancellationToken token)
        {
            // 准备数据包大小
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;
            byte[] buffer = new byte[bufferSize];
            new Random().NextBytes(buffer);

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

                            // ✨ 关键修复：如果实际延迟超过设定的超时，视为失败
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
        // Socket ICMP Ping方法
        private async Task ExecuteIcmpPing(string targetIp, int timeout, CancellationToken token)
        {
            // 1. 准备设置
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;
            byte[] payload = new byte[bufferSize];
            new Random().NextBytes(payload);
            ushort identifier = (ushort)(Process.GetCurrentProcess().Id & 0xFFFF);
            unchecked { _globalIcmpSequence++; }

            IPAddress ipAddr = IPAddress.Parse(targetIp);
            var addrFamily = ipAddr.AddressFamily;
            IPEndPoint localEndPoint = GetLocalEndPoint();

            // 地址族匹配检查
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
                    // 不设置 ReceiveTimeout，完全由外部超时控制

                    // 自动识别出口 IP（用于显示）
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

                    // 构造并发送 ICMP 包
                    byte[] icmpPacket = BuildIcmpEchoPacket(8, 0, identifier, _globalIcmpSequence, payload);
                    raw.SendTo(icmpPacket, new IPEndPoint(ipAddr, 0));

                    string timeStr = DateTime.Now.ToString("HH:mm:ss");
                    int currentTotal = successCount + lossCount + 1;
                    Stopwatch sw = Stopwatch.StartNew();

                    byte[] recvBuffer = new byte[4096];
                    EndPoint receiveEP = new IPEndPoint(IPAddress.Any, 0);

                    // 异步接收循环（直到超时或取消）
                    bool receivedMatch = false;
                    double rtt = 0;
                    int ttl = 0;
                    IPAddress replyAddress = null;

                    while (!token.IsCancellationRequested && sw.ElapsedMilliseconds < timeout)
                    {
                        // 计算剩余超时时间（确保总超时精确）
                        int remaining = (int)(timeout - sw.ElapsedMilliseconds);
                        if (remaining <= 0) break;

                        // 异步接收，超时 = min(剩余时间, 200ms) 避免死循环
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
                                    rtt = sw.Elapsed.TotalMilliseconds; // 高精度
                                    ttl = recvBuffer[8];
                                    replyAddress = ((IPEndPoint)receiveEP).Address;
                                    receivedMatch = true;
                                    break;
                                }
                                // 不匹配则继续循环
                            }
                        }
                        // 超时则继续循环（总超时控制在外层）
                    }

                    if (receivedMatch)
                    {
                        // ✨ 关键修复：如果实际延迟超过设定的超时，视为失败
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
                    // 不设置 ReceiveTimeout

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
                                    rtt = sw.Elapsed.TotalMilliseconds; // 高精度
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

            // 未知地址族
            lossCount++;
            AppendColorText($"[{DateTime.Now:HH:mm:ss}] ", ColorTranslator.FromHtml("#a8a5ff"), false);
            AppendColorText($"ICMP错误: 未知地址族 {addrFamily}", Color.Red, true);
            UpdateStats();
        }

        // 辅助方法：构造 ICMP Echo 包 & 计算校验和 
        private static byte[] BuildIcmpEchoPacket(byte type, byte code, ushort identifier, ushort sequence, byte[] payload)
        {
            int headerLen = 8;
            byte[] packet = new byte[headerLen + payload.Length];

            packet[0] = type; // 8 = Echo Request ; 0 = Echo Reply
            packet[1] = code;
            // checksum 占 [2..3]，先置 0
            packet[2] = 0;
            packet[3] = 0;
            // identifier (网络字节序)
            packet[4] = (byte)(identifier >> 8);
            packet[5] = (byte)(identifier & 0xFF);
            // sequence (网络字节序)
            packet[6] = (byte)(sequence >> 8);
            packet[7] = (byte)(sequence & 0xFF);
            // payload
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
            // fold 32-bit to 16-bit
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            return (ushort)~sum;
        }
        //  辅助：构造 ICMPv6 报文（不含校验和） 
        private static byte[] BuildIcmpv6PacketWithoutChecksum(byte type, byte code, ushort identifier, ushort sequence, byte[] payload)
        {
            int headerLen = 8; // type(1)+code(1)+checksum(2)+id(2)+seq(2)
            byte[] packet = new byte[headerLen + payload.Length];
            packet[0] = type;
            packet[1] = code;
            // checksum 两字节先置0
            packet[2] = 0;
            packet[3] = 0;
            packet[4] = (byte)(identifier >> 8);
            packet[5] = (byte)(identifier & 0xFF);
            packet[6] = (byte)(sequence >> 8);
            packet[7] = (byte)(sequence & 0xFF);
            Buffer.BlockCopy(payload, 0, packet, headerLen, payload.Length);
            return packet;
        }

        //  辅助：根据 src/dst 构造伪首部并返回完整 icmpv6 包（含校验和） 
        private static byte[] BuildIcmpv6WithChecksum(IPAddress src, IPAddress dst, byte[] icmpWithoutChecksum)
        {
            // 伪首部: src(16) dst(16) upper-layer-pkt-len(4) zeros(3) next-header(1)
            int pseudoLen = 16 + 16 + 4 + 4; // 后面3个0 + next header = 4 bytes
            int totalLen = pseudoLen + icmpWithoutChecksum.Length;
            byte[] buf = new byte[totalLen];

            // src (16)
            Buffer.BlockCopy(src.GetAddressBytes(), 0, buf, 0, 16);
            // dst (16)
            Buffer.BlockCopy(dst.GetAddressBytes(), 0, buf, 16, 16);
            // upper-layer length (4 bytes) 网络字节序
            uint upperLen = (uint)icmpWithoutChecksum.Length;
            buf[32] = (byte)((upperLen >> 24) & 0xFF);
            buf[33] = (byte)((upperLen >> 16) & 0xFF);
            buf[34] = (byte)((upperLen >> 8) & 0xFF);
            buf[35] = (byte)(upperLen & 0xFF);
            // zeros(3) + next header(1) (58 for ICMPv6)
            buf[36] = 0;
            buf[37] = 0;
            buf[38] = 0;
            buf[39] = 58; // Next header = 58 (ICMPv6)

            // copy icmp packet after pseudo header
            Buffer.BlockCopy(icmpWithoutChecksum, 0, buf, 40, icmpWithoutChecksum.Length);

            // 计算校验和（伪首部 + icmp）
            ushort csum = ComputeChecksum(buf);

            // 构造最终 icmp 数据（把校验和写回到 icmp 报文的 [2..3]）
            byte[] icmpWithChecksum = new byte[icmpWithoutChecksum.Length];
            Buffer.BlockCopy(icmpWithoutChecksum, 0, icmpWithChecksum, 0, icmpWithoutChecksum.Length);
            icmpWithChecksum[2] = (byte)(csum >> 8);
            icmpWithChecksum[3] = (byte)(csum & 0xFF);

            return icmpWithChecksum;
        }


        // 更新 UI 上的那几个 lbl 标签
        private void UpdateStats()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateStats));
                return;
            }

            // 格式化最小/最大延迟的显示
            string minStr = (successCount > 0) ? $"{minDelay:F1}ms({minCountIndex})" : "-";
            string maxStr = (successCount > 0) ? $"{maxDelay:F1}ms({maxCountIndex})" : "-";

            double avgDelay = successCount > 0 ? totalDelay / successCount : 0;
            double lossRate = (successCount + lossCount) > 0 ? (double)lossCount / (successCount + lossCount) * 100 : 0;

            // 把文字更新到对应的 Label 上
            lblMin2.Text = minStr; // 比如显示：8.5(2)
            lblMax2.Text = maxStr; // 比如显示：120.4(15)
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
                // 关键修复：调用加强版函数，并传入 false (不清空内容)
                UpdateProtocolUI(
                    radioICMP.Checked ? radioICMP : (radioTCP.Checked ? radioTCP : radioUDP),
                    false
                );
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            // 1. 如果文本框是空的，就没必要保存啦
            if (string.IsNullOrEmpty(richTextBox1.Text))
            {
                MessageBox.Show("当前没有测试结果可以保存喵", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 2. 创建保存文件对话框
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

                // 默认文件名
                string saveTime = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                sfd.FileName = $"NICX_Ping_{pingType}_{comboTarget.Text}_{saveTime}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // 3. 准备要保存的内容
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

                        // 4. 写入文件
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
                // 1. 停止当前的测试逻辑（防止窗口关了后台还在跑）
                if (isRunning)
                {
                    _cts?.Cancel();
                }

                // 2. 记录当前窗口的位置，这样重启后窗口还在原来的地方，不会乱跳
                Point currentPkgLocation = this.Location;

                // 3. 创建一个新的窗口实例
                PingPP newForm = new PingPP();

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

        // 根据延迟返回夢酱设计的阶梯颜色
        private Color GetRttColor(double rtt)
        {
            if (rtt <= 15) return Color.Lime;             // 15ms以内：亮绿色
            if (rtt <= 30) return Color.MediumSpringGreen;// 16-30ms：青绿色
            if (rtt <= 50) return Color.FromArgb(185, 210, 50);      // 30-50ms：青色
            if (rtt <= 100) return Color.Gold;            // 50-100ms：黄色
            if (rtt <= 200) return Color.Orange;          // 100-200ms：橙色
            if (rtt <= 500) return Color.Tomato;       // 200-500ms：橙红色
            return Color.Red;                       // 超过500ms：红色
        }

        private void txtPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        private void txtMaxDelay_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        private void txtPackage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
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
            // 定义夢酱设计的阶梯和颜色
            var colors = new[] {
        Color.Lime, Color.MediumSpringGreen, Color.FromArgb(185,210,50),
        Color.Gold, Color.Orange, Color.Tomato, Color.Red
    };
            // 统一标签长度，方便箭头对齐（每个标签占 8 位宽度）
            var labels = new[] { "     ≤15ms ", " 30ms  ", " 50ms  ", " 100ms ", " 200ms ", " 500ms ", " >错误" };
            // 对应每一段标签下方的箭头（数量可以根据视觉微调）
            var arrows = new[] { "     >>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>" };

            // 1. 画顶部的分割线
            AppendColorText("    ===========================================================", ColorTranslator.FromHtml("#a8a5ff"), true);

            // 2. 第2排：打印数值行
            for (int i = 0; i < labels.Length; i++)
            {
                AppendColorText(labels[i], colors[i], false);
                if (i < labels.Length - 1) AppendColorText("|", Color.Gray, false);
            }
            richTextBox1.AppendText("\n");

            // 3. 第3排：打印彩色箭头行
            for (int i = 0; i < arrows.Length; i++)
            {
                AppendColorText(arrows[i], colors[i], false);
                // 在箭头之间也加一个小空格或者保持连贯
                if (i < arrows.Length - 1) AppendColorText(" ", Color.Black, false);
            }
            richTextBox1.AppendText("\n");

            // 4. 画底部的分割线
            AppendColorText("    ===========================================================", ColorTranslator.FromHtml("#a8a5ff"), true);
        }
        // 【全能版】出口 IP 探测器：统一处理 IPv4 和 IPv6
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
                        return ip.Contains("%") ? ip.Split('%')[0] : ip; // 去掉 IPv6 的作用域 ID
                    }
                }
            }
            catch { }
            return targetIp.Contains(":") ? "::" : "0.0.0.0";
        }

        //识别当前网卡后打印的方法
        private void PrintTestSettings(string actualIp)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => PrintTestSettings(actualIp)));
                return;
            }

            // 如果传进来的是空地址，我们再等等，直到拿到真 IP
            if (actualIp == "0.0.0.0" || actualIp == "::") return;

            if (isSettingsPrinted) return; // 保证每次测试只打印一次喵
            isSettingsPrinted = true;

            // 梦酱看这里：这里是找回括号的关键逻辑
            string displayName = actualIp;
            // 梦酱看这里：遍历下拉框，让它自动选中带括号的那一项
            for (int i = 0; i < comboLocalEnd.Items.Count; i++)
            {
                string itemStr = comboLocalEnd.Items[i].ToString();
                // 如果发现某一项是以当前真实的 IP 开头，并且后面跟着括号
                if (itemStr.StartsWith(actualIp + " ("))
                {
                    displayName = itemStr;
                    // 把下拉框的选中索引改到这一项，这样编辑框里就会显示完整名字了喵！
                    comboLocalEnd.SelectedIndex = i;
                    break;
                }
            }
            // 我们遍历一下下拉框里的所有项，看看能不能把网卡名字“认领”回来
            foreach (var item in comboLocalEnd.Items)
            {
                string itemStr = item.ToString();
                // 比如 itemStr 是 "192.168.1.5 (以太网)"，而 actualIp 是 "192.168.1.5"
                // 我们检查 itemStr 是不是以 "IP + 空格 + 括号" 开头的
                if (itemStr.StartsWith(actualIp + " ("))
                {
                    displayName = itemStr; // 找到了！把带有括号的名字给 displayName
                    break;
                }
            }

            if (displayName == "0.0.0.0" || displayName == "::") displayName = "系统默认";
            //-------------------------------------------

            string protocolName = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");
            int timeout = int.TryParse(txtMaxDelay.Text, out int t) ? t : 500;
            int port = int.TryParse(txtPort.Text, out int p) ? p : 80;
            int bufferSize = int.TryParse(txtPackage.Text, out int b) ? b : 32;

            string settingsLine = $"[测试设置] 网卡 {displayName} / 协议 {protocolName}";

            if (radioICMP.Checked) settingsLine += $" / 超时 {timeout}ms / 字节 {bufferSize}";
            else if (radioTCP.Checked) settingsLine += $" / 端口 {port} / 超时 {timeout}ms";
            else settingsLine += $" / 端口 {port} / 超时 {timeout}ms"; // UDP 逻辑

            AppendColorText(settingsLine + "\n", Color.LightPink, true);
        }
    }
}
