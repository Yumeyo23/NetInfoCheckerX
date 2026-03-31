using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

//===================================================================
//                  最大连接数测试 类
//===================================================================
namespace NetInfoCheckerX
{
    public partial class ConnectionTest : Form
    {
        private CancellationTokenSource cts; // 用于取消（停止）所有测试任务
        private long successCount = 0;        // 成功总数
        private long failCount = 0;           // 失败总数
        private long totalTried = 0;          // 已尝试的总数
        private bool isTesting = false;      // 是否正在测试的开关
        private long lastLoggedSuccess = 0; // 上次记录成功的次数
        private long lastLoggedFail = 0;    // 上次记录失败的次数
        private ConcurrentBag<Socket> socketPool = new ConcurrentBag<Socket>();  //尝试保持连接数
        private Stopwatch testStopwatch = new Stopwatch(); // 计时器

        public ConnectionTest()
        {
            InitializeComponent();
        }
        //窗口主题切换方法
        private async Task ApplyConnectionThemeAsync()
        {
            // 获取全局的主题配置
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            // 1. 窗口整体背景
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 2. 状态显示等特殊标签
            Control[] yumeyoControls = {
        label2, lblOK, lblFail, lblTime //lblTime2, lblFail2, lblOK2
    };
            foreach (var c in yumeyoControls) { if (c != null) c.ForeColor = yumeyoColor; }

            // 3. 普通设置标签 (黑/白文字)
            Control[] contrastControls = {
        lblMaxTry, lblMaxFail, lblThread, lblTimeRest, label1, lblNIC,
    };
            foreach (var c in contrastControls)
            {
                if (c != null) c.ForeColor = contrastColor;
            }

            // 4. 输入框与下拉框
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

                    // 如果是文本框，深色模式下给个边框感
                    if (c is TextBox txt)
                    {
                        txt.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                    }

                    // 深色模式下把 ComboBox 扁平化
                    if (c is ComboBox cb)
                    {
                        cb.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                    }
                }
            }

            // 5. 按钮组 (深色模式下为 60 灰)
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
                    }
                }
            }
        }
        private void ConnectionTest_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            _ = ApplyConnectionThemeAsync();
            var portStatus = GetSystemDynamicPortRange();
            InitNICList();
            this.Text = $"最大连接数测试(TCP) ✧ NetInfoCheckerX (mP:{portStatus.num}({portStatus.start}))";
            CloudControl.LoadConnectionServers(comboServer);
            CloudControl.ApplyDevTitle(this);
        }

        private void InitNICList()
        {
            comboNIC.Items.Clear();

            // 1. 添加默认选项
            var defaultItem = new { Text = "0.0.0.0 (Any)", Value = "0.0.0.0" };
            var defaultItemV6 = new { Text = ":: (IPv6 Any)", Value = "::" };

            comboNIC.DisplayMember = "Text";
            comboNIC.ValueMember = "Value";
            comboNIC.Items.Add(defaultItem);
            comboNIC.Items.Add(defaultItemV6);

            try
            {
                // 2. 获取并筛选网卡
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // A. 状态过滤：只看正在运行的
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    // B. 关键字屏蔽：排除 VMware/VirtualBox
                    string desc = ni.Description.ToLower();
                    if (desc.Contains("vmware") || desc.Contains("virtual") || desc.Contains("vbox") || desc.Contains("hyper-v") || desc.Contains("wsl") || desc.Contains("pseudo") || desc.Contains("tap") || desc.Contains("tun") || desc.Contains("loopback") || desc.Contains("vpn") || desc.Contains("teredo"))
                        continue;

                    // C. 获取 IP 属性
                    var ipProps = ni.GetIPProperties();

                    // D. 智能判断：保留物理网卡 或 有网关的网卡（支持聚合网卡和 OpenWrt 桥接）
                    bool isPhysical = (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                       ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                    bool hasGateway = ipProps.GatewayAddresses.Count > 0;

                    if (!isPhysical && !hasGateway) continue;

                    // E. 遍历该网卡下的 IP
                    foreach (UnicastIPAddressInformation ipInfo in ipProps.UnicastAddresses)
                    {
                        IPAddress ip = ipInfo.Address;

                        // 排除回环地址 (127.0.0.1 / ::1)
                        if (IPAddress.IsLoopback(ip)) continue;

                        // 排除 IPv6 链路本地地址 (fe80开头的，这种不能跨网段)
                        if (ip.IsIPv6LinkLocal) continue;

                        // 排除 169.254.x.x (无效 APIPA)
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] bytes = ip.GetAddressBytes();
                            if (bytes[0] == 169 && bytes[1] == 254) continue;
                        }

                        // 处理 IPv6 的显示格式（去掉末尾的 % 区域 ID）
                        string ipStr = ip.ToString();
                        if (ipStr.Contains("%")) ipStr = ipStr.Split('%')[0];

                        // 将网卡信息加入列表
                        // 这里依然使用匿名对象，保证和夢酱原本的 ValueMember("Value") 逻辑兼容
                        comboNIC.Items.Add(new
                        {
                            Text = $"{ipStr} ({ni.Name})",
                            Value = ipStr
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("初始化网卡失败: " + ex.Message, "获取失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }

            // 默认选中第一个
            if (comboNIC.Items.Count > 0) comboNIC.SelectedIndex = 0;
        }

        private async Task<string> HandleDNSAsync(string input)
        {
            if (IPAddress.TryParse(input, out _)) return input;

            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(input);
                // 夢酱，我们把过滤条件去掉，或者同时包含两种
                var allIPs = addresses.Where(a =>
                    a.AddressFamily == AddressFamily.InterNetwork ||
                    a.AddressFamily == AddressFamily.InterNetworkV6).ToList();

                if (allIPs.Count == 0) return null;

                comboServer.Items.Clear();
                comboServer.Items.Add(input);
                foreach (var ip in allIPs)
                {
                    // 如果是 IPv6，加上方括号显示会更专业哦！
                    string display = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
                    comboServer.Items.Add(ip.ToString()); // 实际查询还是用原始字符串
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
        // 改变 UI 状态，state 为 true 表示可以编辑，false 表示锁定
        private void SetUIState(bool state)
        {
            comboNIC.Enabled = state;
            comboServer.Enabled = state;
            txtServerPort.Enabled = state;
            txtMaxTry.Enabled = state;
            txtMaxFail.Enabled = state;
            txtThread.Enabled = state;
            txtTimeReset.Enabled = state;

            btnStart.Text = state ? "开测" : "停止";
            //btnStart.BackColor = state ? Color.Transparent : Color.MistyRose; // 停止时变个颜色提醒
        }
        private async void btnStart_Click(object sender, EventArgs e)
        {
            // 1. 如果正在测试，点击就是“停止”
            if (isTesting)
            {
                StopTest("用户手动停止");
                return;
            }

            // ✨ 夢酱看这里：提前搬到这里的清洗逻辑
            // 先把输入全部转为小写并去除空格
            string input = comboServer.Text.Trim().ToLower();

            // 剔除协议头
            if (input.StartsWith("http://")) input = input.Substring(7);
            else if (input.StartsWith("https://")) input = input.Substring(8);

            // 提取斜杠后的端口数字
            if (input.Contains("/"))
            {
                int slashIndex = input.IndexOf('/');
                string afterSlash = input.Substring(slashIndex + 1);
                string portDigits = new string(afterSlash.Where(char.IsDigit).ToArray());

                if (!string.IsNullOrEmpty(portDigits))
                {
                    txtServerPort.Text = portDigits; // 此时更新 UI 上的端口框
                }
                input = input.Substring(0, slashIndex); // 此时 input 变成了域名/IP
            }

            // 正则清洗其余杂质
            input = Regex.Replace(input, @"[^a-z0-9\.\:\-_]", "");
            if (string.IsNullOrEmpty(input))
            {
                SystemSounds.Beep.Play();
                return;
            }

            // 把清洗干净的地址放回 ComboBox，这样后续逻辑拿到的就是干净的地址
            comboServer.Text = input;

            // ✨ 关键：现在再执行校验，拿到的是更新后的端口和地址
            if (!ValidateInputs(out int port, out int maxTry, out int maxFail, out int threads, out int interval))
            {
                return;
            }

            txtSuccess.Text = "成功次数统计";
            txtFail.Text = "失败次数统计";

            // 4. DNS 处理逻辑 (这里的 targetIP 会拿到已经去掉斜杠的地址)
            string targetIP = comboServer.Text.Trim();

            if (!IPAddress.TryParse(targetIP, out _))
            {
                // 如果输入的不是 IP，去解析它
                string resolvedIP = await HandleDNSAsync(targetIP);
                if (string.IsNullOrEmpty(resolvedIP))
                {
                    // 如果解析中或者解析失败，就停在这里让用户选
                    return;
                }
                targetIP = resolvedIP;
            }

            // 协议匹配检查逻辑
            string localIPText = "";
            this.Invoke(new Action(() => { localIPText = comboNIC.Text.Trim(); }));

            // 1. ✨ 關鍵點：清洗本地 IP 字符串
            // 有些项是 ":: (IPv6 Any)" 或 "0.0.0.0 (Any)"，我们只取空格前的地址部分
            string cleanLocalIP = localIPText.Split(' ')[0]
                                             .Replace("[", "")
                                             .Replace("]", "");

            // 2. 分别解析目标和本地地址
            IPAddress.TryParse(targetIP, out IPAddress targetAddr);
            IPAddress.TryParse(cleanLocalIP, out IPAddress localAddr);

            if (targetAddr != null && localAddr != null)
            {
                bool isTargetV6 = targetAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                bool isLocalV6 = localAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

                // ✨ 梦酱看这里：判断是不是“自动选择”模式 (Any)
                bool isAny = (cleanLocalIP == "0.0.0.0" || cleanLocalIP == "::");

                // 如果两个协议族不一致，就提示并停止
                if (isTargetV6 != isLocalV6)
                {
                    string errorMsg = isLocalV6
                                ? "协议错误(本地IPv6, 目标IPv4)"
                                : "协议错误(本地IPv4, 目标IPv6)";

                    // ✨ 夢酱看這裡：直接手動輸出到文本框，而不是調用 StopTest
                    txtFail.AppendText($"\r\n[启动失败] {errorMsg}");
                    txtFail.SelectionStart = txtFail.Text.Length;
                    txtFail.ScrollToCaret();

                    SystemSounds.Beep.Play(); // 叮一聲提醒夢酱
                    return; // 直接中斷，不往下走啟動流程了启动流程
                }
            }

            //清理旧数据，准备新测试
            ResetStatistics();

            // 5. 锁定 UI
            isTesting = true;
            SetUIState(false);

            // startTime = DateTime.Now; // 这一行可以退休啦
            testStopwatch.Restart(); // 开始计时
            timerUpdate.Start();

            // 7. 启动测试引擎
            cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(10)); // 10分钟自动停止
            CloudControl.UsedTimesCounter("连接数测试");

            // 直接调用我们封装好的引擎函数
            StartTestingEngine(targetIP, port, maxTry, maxFail, threads, interval, cleanLocalIP);
        }
        private void StopTest(string reason)
        {
            // 如果已经停止了，就不重复执行
            if (!isTesting) return;

            isTesting = false;

            // 1. 发出强力取消信号
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            // 2. 停止计时器
            testStopwatch.Stop();
            timerUpdate.Stop();

            // 关键改动：清空连接池
            Task.Run(() =>
            {
                foreach (var s in socketPool)
                {
                    try { s.Shutdown(SocketShutdown.Both); s.Close(); } catch { }
                }
                // 清空列表，为下次测试准备
                while (!socketPool.IsEmpty) socketPool.TryTake(out _);
            });

            // 3. 统一处理 UI 更新（合并成一个 Invoke）
            if (!this.IsDisposed && !this.Disposing)
            {
                this.BeginInvoke(new Action(() =>
                {
                    SetUIState(true); // 恢复按钮和输入框
                    var portStatus = GetSystemDynamicPortRange();
                    this.Text = $"最大连接数测试(TCP) ✧ NetInfoCheckerX (mP:{portStatus.num}({portStatus.start}))";
                    CloudControl.ApplyDevTitle(this);
                    // 记录停止信息
                    string stopLog = $"\r\n==========================\r\n" +
                                     $" [测试停止] {reason}\r\n" +
                                     $" ❤ 最终成功：{successCount}\r\n" +
                                     $" ❤ 最终失败：{failCount}\r\n" +
                                     $"==========================";
                    txtFail.AppendText(stopLog);

                    // 滚动到底部
                    txtFail.SelectionStart = txtFail.Text.Length;
                    txtFail.ScrollToCaret();
                }));
            }
        }

        private bool ValidateInputs(out int port, out int maxTry, out int maxFail, out int threads, out int interval)
        {
            // 初始化输出参数
            port = 0; maxTry = 0; maxFail = 0; threads = 0; interval = 0;

            // 校验端口
            if (!int.TryParse(txtServerPort.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("端口号需 1-65535 之间");
                return false;
            }
            // 校验线程数 (1-9999)
            if (!int.TryParse(txtThread.Text, out threads) || threads < 1 || threads > 999)
            {
                MessageBox.Show("线程数需 1-999 之间");
                return false;
            }
            // 校验时间间隔 (10-9999ms)
            if (!int.TryParse(txtTimeReset.Text, out interval) || interval < 10 || interval > 999)
            {
                MessageBox.Show("间隔时间需 10-999ms 之间");
                return false;
            }
            // 校验失败上限 (>100)
            if (!int.TryParse(txtMaxFail.Text, out maxFail) || maxFail < 10 || maxFail > 99999)
            {
                MessageBox.Show("失败上限需 10-99999 之间");
                return false;
            }
            // 校验尝试上限 (>100)
            if (!int.TryParse(txtMaxTry.Text, out maxTry) || maxTry < 10 || maxTry > 99999)
            {
                MessageBox.Show("尝试上限需 10-99999 之间");
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

            lblOK2.Text = "-";
            lblFail2.Text = "-";
            lblTime2.Text = "-";

            txtSuccess.Text = "成功次数统计\n";
            txtFail.Text = "失败次数统计\n";
        }

        // 这是一个异步任务，代表一次 TCP 连接尝试
        private async Task DoConnectAsync(string targetIP, int port, string localIP, int interval, int maxTry, int maxFail, CancellationToken token)
        {
            // 1. 先解析出目标 IP 对象，确定它是 V4 还是 V6
            if (!IPAddress.TryParse(targetIP, out IPAddress targetAddr)) return;
            AddressFamily family = targetAddr.AddressFamily;

            while (!token.IsCancellationRequested && isTesting)
            {
                if (Interlocked.Read(ref totalTried) >= maxTry) break;
                Interlocked.Increment(ref totalTried);

                // 2. 这里的 AddressFamily 改为变量 family，实现自动切换！
                Socket socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // 绑定网卡逻辑
                    // 1. 如果是 IPv4 模式 (family 是 InterNetwork)
                    if (family == AddressFamily.InterNetwork)
                    {
                        // 如果用户选的是 IPv6 的 Any，但在测 IPv4 目标，这就是不合法的
                        if (localIP == "::") return;

                        // 只要不是 "::"，无论是具体的 IPv4 还是 "0.0.0.0"，我们都执行绑定
                        // 这样 Socket 就会被锁定在 IPv4 协议栈
                        socket.Bind(new IPEndPoint(IPAddress.Parse(localIP), 0));
                    }
                    // 2. 如果是 IPv6 模式 (family 是 InterNetworkV6)
                    else if (family == AddressFamily.InterNetworkV6)
                    {
                        // 如果用户选的是 IPv4 的 Any，但在测 IPv6 目标，直接拦截
                        if (localIP == "0.0.0.0") return;
                        // 绑定具体的 IPv6 地址或 "::"
                        socket.Bind(new IPEndPoint(IPAddress.Parse(localIP), 0));
                    }

                    var result = socket.BeginConnect(targetAddr, port, null, null);

                    using (token.Register(() => { try { socket.Close(); } catch { } }))
                    {
                        bool success = result.AsyncWaitHandle.WaitOne(2000, true);

                        if (success && socket.Connected && isTesting)
                        {
                            socketPool.Add(socket);
                            Interlocked.Increment(ref successCount);
                            // 自动回填本地 IP 的逻辑
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
            // 根据线程数，启动多个任务
            for (int i = 0; i < threads; i++)
            {
                Task.Run(() => DoConnectAsync(targetIP, port, localIP, interval, maxTry, maxFail, cts.Token));
            }
        }

        // 更新界面的统计标签
        // 更新界面的统计标签
        private void UpdateStatsLabels()
        {
            // 1. 先从多线程变量中读取当前的最新数值
            long currentOK = Interlocked.Read(ref successCount);
            long currentFail = Interlocked.Read(ref failCount);

            // 2. 使用 BeginInvoke 切回到 UI 线程
            this.BeginInvoke(new Action(() =>
            {
                // ✨【关键修改】这两行放在 if 逻辑之外，确保每次被调用都会更新标签！
                lblOK2.Text = currentOK.ToString();
                lblFail2.Text = currentFail.ToString();

                // 成功日志逻辑：依然保持每增加 100 次输出一行
                if (currentOK >= lastLoggedSuccess + 100)
                {
                    lastLoggedSuccess = (currentOK / 100) * 100;
                    string nowTime = Others.GetCurrentTimeHMS();
                    txtSuccess.AppendText($"\r\n[{nowTime}]TCP={comboServer.Text} 成功={lastLoggedSuccess}");
                }

                // 失败日志逻辑：依然保持每增加 10 次输出一行
                if (currentFail >= lastLoggedFail + 10)
                {
                    lastLoggedFail = (currentFail / 10) * 10;
                    string nowTime = Others.GetCurrentTimeHMS();
                    txtFail.AppendText($"\r\n[{nowTime}]TCP={comboServer.Text} 失败={lastLoggedFail}");
                }
            }));
        }

        // 检查是否达到用户设定的上限
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

        // 如果用户选了 0.0.0.0，在第一次成功后把实际 IP 填回去
        // 如果用户选了 0.0.0.0，在第一次成功后把实际 IP 填回去
        private void UpdateLocalIP(string localEndPoint)
        {
            this.BeginInvoke(new Action(() =>
            {
                // 1. 提取 IP（处理 IPv6 的中括号和端口号）
                string actualIP = "";
                if (localEndPoint.Contains("]")) // IPv6 格式 [2001:db8::1]:12345
                {
                    actualIP = localEndPoint.Split(']')[0].Replace("[", "");
                }
                else // IPv4 格式 192.168.1.1:12345
                {
                    actualIP = localEndPoint.Split(':')[0];
                }

                // 2. 检查当前是否是 "Any" 模式
                if (comboNIC.Text.Contains("Any") || comboNIC.Text.Contains("0.0.0.0") || comboNIC.Text.Contains("::"))
                {
                    bool foundMatch = false;

                    // 3. 遍历下拉框里的所有项，寻找匹配的 IP
                    for (int i = 0; i < comboNIC.Items.Count; i++)
                    {
                        // 获取当前项（我们在 InitNICList 里存的是匿名对象，包含 Text 和 Value）
                        dynamic item = comboNIC.Items[i];

                        // 这里的 Value 就是存的纯 IP 字符串
                        if (item.Value.ToString() == actualIP)
                        {
                            comboNIC.SelectedIndex = i; // 找到了！直接选中这一项
                            foundMatch = true;
                            break;
                        }
                    }

                    // 4. 如果万一没在列表里找到（虽然概率很低），再保底直接填 IP
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
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        private void ConnectionTest_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 如果正在测试中，先强制停止
            if (isTesting)
            {
                // 夢酱注意：这里直接调用 StopTest 可能会触发 UI 更新
                // 但因为窗体要关闭了，我们主要执行取消信号和停止计时器
                StopTest("窗口关闭");
            }

            // 彻底释放取消令牌资源
            if (cts != null)
            {
                cts.Dispose();
            }
        }

        // 计时器事件（建议设置 Interval 为 1000ms）
        private async void timerUpdate_Tick(object sender, EventArgs e)
        {
            if (isTesting)
            {
                lblTime2.Text = testStopwatch.Elapsed.TotalSeconds.ToString("0"); // 取整显示

                // ✨ 优化点：删掉这里的 GetSystemDynamicPortRange() 调用！
                string memInfo = GetMemoryUsageString();

                // 标题栏只显示内存，端口信息我们可以从变量里读，或者不显示
                this.Text = $"最大连接数测试(TCP) ✧ NICX ({memInfo})";
                CloudControl.ApplyDevTitle(this);
            }
        }

        //获取内存使用率
        private string GetMemoryUsageString()
        {
            using (var proc = System.Diagnostics.Process.GetCurrentProcess())
            {
                // WorkingSet64 是程序占用的物理内存 (MB)
                double usedMs = proc.WorkingSet64 / 1024.0 / 1024.0;
                // PrivateMemorySize64 是程序申请的虚拟内存/提交大小 (MB)
                double commitMs = proc.PrivateMemorySize64 / 1024.0 / 1024.0;

                // 格式化为：物理/提交
                return $"Mem: {usedMs:F2}/{commitMs:F2}MB";
            }
        }

        //解锁系统TCP连接数
        private void btnUnlockTCPUDP_Click(object sender, EventArgs e)
        {
            // 1. 先获取当前的设置状态
            var (currentStart, currentNum) = GetSystemDynamicPortRange();
            bool isUnlocked = currentNum > 16384;

            if (isUnlocked)
            {
                //恢复默认逻辑
                string restoreMsg = "当前系统连接数大于默认，是否还原默认？\n（若当前未解锁到最大，请还原一次后再次解锁）";
                if (MessageBox.Show(restoreMsg, "操作确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    // 恢复默认值：起始 49152，数量 16384
                    ExecuteNetshCommand("int ipv4 set dynamicport tcp start=49152 num=16384 & netsh int ipv4 set dynamicport udp start=49152 num=16384");
                    MessageBox.Show("已恢复系统默认连接数设置", "操作成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else
            {
                //解锁最大逻辑
                if (MessageBox.Show("是否解锁系统默认TCP/UDP连接数到 64511 (最大值)？", "操作确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    // 设置最大值：起始 1025，数量 64511
                    ExecuteNetshCommand("int ipv4 set dynamicport tcp start=1025 num=64511 & netsh int ipv4 set dynamicport udp start=1025 num=64511");
                    MessageBox.Show("已解锁最大连接数设置", "操作成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            var portStatus = GetSystemDynamicPortRange();
            this.Text = $"最大连接数测试(TCP) ✧ NetInfoCheckerX (mP:{portStatus.num}({portStatus.start}))";
            CloudControl.ApplyDevTitle(this);
        }

        // 提取一个通用的执行命令方法，这样代码更整洁，夢酱看起来也舒服
        private void ExecuteNetshCommand(string netshArgs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c netsh " + netshArgs)
                {
                    Verb = "runas", // 触发 UAC 提权盾牌
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                Process p = Process.Start(psi);
                p?.WaitForExit(); // 等待执行完成，这样结果更准确
            }
            catch (Exception ex)
            {
                MessageBox.Show($"设置出错了: {ex}", "设置出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 获取系统当前动态端口范围
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
                    StandardOutputEncoding = System.Text.Encoding.Default // 自动适配系统编码
                };

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd(); // 一次性读完更稳妥

                    // 使用正则表达式匹配冒号后面的数字
                    // 第一处匹配通常是“起始端口”，第二处是“端口数”
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
                // 如果正在测试中，先强制停止
                if (isTesting)
                {
                    // 夢酱注意：这里直接调用 StopTest 可能会触发 UI 更新
                    // 但因为窗体要关闭了，我们主要执行取消信号和停止计时器
                    StopTest("重载窗口");
                }

                // 彻底释放取消令牌资源
                if (cts != null)
                {
                    cts.Dispose();
                }

                // 2. 记录当前窗口的位置，这样重启后窗口还在原来的地方，不会乱跳
                Point currentPkgLocation = this.Location;

                // 3. 创建一个新的窗口实例
                ConnectionTest newForm = new ConnectionTest();

                // ✨ 重点在这里：传递图片
                if (this.pictureBox1.Image != null)
                {
                    newForm.pictureBox1.Image = this.pictureBox1.Image;
                }

                // 让新窗口在老窗口的位置显示
                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentPkgLocation;
                // 4. 显示新窗口
                newForm.Show();
                // 5. 彻底关闭并释放当前窗口
                this.Close();
            }
        }

        private void txtServerPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        private void txtMaxTry_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        private void txtMaxFail_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        private void txtThread_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }

        private void txtTimeReset_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnStart_Click(sender, e);
            }
        }
    }
}
