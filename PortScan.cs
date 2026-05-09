using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
    public partial class PortScan : Form
    {
        // 常用端口字符串
        private readonly string commonPorts = "21-23,53,80,110,123,143,443,445,465,587,1433,1900,3306,3389,4000,5000,5201,5900,6000,7890-7895,8000,8080,8888,8989,9000,9090,9833,9987,9999";
        private CancellationTokenSource _cts; // 控制扫描停止的“红绿灯”
        private bool _isScanning = false;      // 记录当前是否正在扫描

        public PortScan()
        {
            InitializeComponent();
        }
        // 自动刷新网卡：当系统网卡变化导致选中网卡不存在时，刷新列表并恢复默认
        private void EnsureSelectedNICValid()
        {
            string selectedText = comboLocalEnd.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.StartsWith("0.0.0.0") || selectedText.StartsWith("::")) return;

            InitNetworkInterfaces();

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

        //获取本机网卡的方法
        private void InitNetworkInterfaces()
        {
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // 按照夢酱提供的“聪明”筛选逻辑
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    string desc = ni.Description.ToLower();
                    if (desc.Contains("vmware") || desc.Contains("virtual") || desc.Contains("vbox") || desc.Contains("hyper-v") || desc.Contains("wsl") || desc.Contains("pseudo") || desc.Contains("tap") || desc.Contains("tun") || desc.Contains("loopback") || desc.Contains("vpn") || desc.Contains("teredo"))
                        continue;

                    var ipProps = ni.GetIPProperties();
                    bool isPhysical = (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                       ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                    bool hasGateway = ipProps.GatewayAddresses.Count > 0;

                    if (!isPhysical && !hasGateway) continue;

                    foreach (UnicastIPAddressInformation ipInfo in ipProps.UnicastAddresses)
                    {
                        IPAddress ip = ipInfo.Address;
                        if (IPAddress.IsLoopback(ip)) continue;
                        if (ip.IsIPv6LinkLocal) continue;

                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] bytes = ip.GetAddressBytes();
                            if (bytes[0] == 169 && bytes[1] == 254) continue;
                        }

                        string ipStr = ip.ToString();
                        if (ipStr.Contains("%")) ipStr = ipStr.Split('%')[0];

                        string displayName = $"{ipStr} ({ni.Name})";
                        comboLocalEnd.Items.Add(displayName);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取网卡信息失败: " + ex.Message);
            }

            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;
        }
        private async Task ApplyPortScanThemeAsync()
        {
            // 1. 获取全局颜色配置
            bool isLight = Global.isThemelight;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;

            // --- 夢酱指定的色彩细化 ---
            // 基础文字颜色：浅色下是黑色，深色下是白色
            Color baseContrastColor = isLight ? Color.Black : Color.White;
            // lblExeName 专属颜色：浅色 #8e8cd8 (Global.Yumeyo)，深色 #a8a5ff (Global.Yumeyo2)
            Color exeNameColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            // 60 灰按钮背景
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            // --- 2. 窗口整体背景 ---
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // --- 3. 标签分类处理 ---
            // A组：必须严格遵循 黑/白 切换的标签
            Control[] blackWhiteLabels = {
        lblTarget, lblPort, lblThreads, lblTimeout, lbl5780LocalEnd, lblThreads, lblTimeout
    };
            foreach (var lbl in blackWhiteLabels)
            {
                if (lbl != null)
                {
                    lbl.ForeColor = baseContrastColor;
                    lbl.BackColor = Color.Transparent;
                }
            }

            // B组：夢酱指定的特殊主题色标签
            if (lblExeName != null)
            {
                lblExeName.ForeColor = exeNameColor;
                lblExeName.BackColor = Color.Transparent;
            }

            // --- 4. 输入框处理 (保持与背景色同步) ---
            Control[] editControls = {
        txtTarget, txtPort, txtThreads, txtTimeout, comboLocalEnd
    };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = baseContrastColor;
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

            // --- 5. 结果框处理 ---
            if (richResult != null)
            {
                richResult.BackColor = textBack;
                richResult.ForeColor = baseContrastColor;
                richResult.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

            // --- 6. 按钮组 ---
            Control[] buttons = { btnOK, btnPaste, btnSave, btnMinimum, btnFull };
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
                        // 悬停时变梦酱紫
                        btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                    }
                }
            }
        }
        private void PortScan_Load(object sender, EventArgs e)
        {
            _ = ApplyPortScanThemeAsync();
            lblExeName.Text = Global.exeName + " " + Global.Version;
            InitNetworkInterfaces();
        }

        private void btnPaste_Click(object sender, EventArgs e)
        {
            string clipText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipText)) return;

            // 正则清洗：只保留字母、数字、点、冒号
            string cleaned = Regex.Replace(clipText, @"[^a-zA-Z0-9\.\:\-]", "");

            if (!string.IsNullOrEmpty(cleaned))
            {
                txtTarget.Text = cleaned;
            }
            else
            {
                SystemSounds.Beep.Play();
            }
        }

        private void btnMinimum_Click(object sender, EventArgs e)
        {
            txtPort.Text = commonPorts;
        }

        private void btnFull_Click(object sender, EventArgs e)
        {
            txtPort.Text = "0-65535";
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(richResult.Text))
            {
                MessageBox.Show("还没有扫描结果可以保存哦~", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "请选择保存测试结果的位置";
                sfd.Filter = "文本文件(*.txt)|*.txt";
                // 生成默认文件名：NICX_PortScan_目标地址_yyyyMMdd_HHmmss.txt
                string safeTarget = Regex.Replace(txtTarget.Text, @"[^\w\.]", "_");
                sfd.FileName = $"NICX_PortScan_{safeTarget}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"=== 欢迎使用 端口扫描 ❤ 网络综合查询器X by Yumeyo ===");
                    sb.AppendLine($"🔰 本次端口扫描数据 🔥");
                    sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine("--------------------------------------------------");
                    sb.AppendLine(richResult.Text);
                    sb.AppendLine("--------------------------------------------------");
                    sb.AppendLine($"=== 感谢使用 端口扫描 ❤ 网络综合查询器X by Yumeyo ===");
                    sb.AppendLine($"======== 导出于 NetInfoCheckerX by Yumeyo ========\n");

                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show($"保存成功，夢酱辛苦了！", "保存成功了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        //清洗端口号
        private List<int> ParsePorts(string input)
        {
            // 1. 简单清洗：替换中文逗号，过滤掉非法字符
            string cleaned = Regex.Replace(input.Replace("，", ","), @"[^0-9,\-]", "");
            List<int> portList = new List<int>();

            try
            {
                string[] parts = cleaned.Split(',');
                foreach (var part in parts)
                {
                    if (part.Contains("-")) // 处理 100-200 这种范围
                    {
                        var range = part.Split('-');
                        if (range.Length == 2 && int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
                        {
                            for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++)
                                if (i >= 0 && i <= 65535) portList.Add(i);
                        }
                    }
                    else if (int.TryParse(part, out int port)) // 处理单个端口
                    {
                        if (port >= 0 && port <= 65535) portList.Add(port);
                    }
                }
            }
            catch { /* 忽略解析错误 */ }
            return portList.Distinct().ToList(); // 去重后返回
        }

        // --- 工具方法：格式化 IP 文本 ---
        private string GetFormattedIP()
        {
            string raw = txtTarget.Text.Trim();

            //✨ 正则表达式大扫除：只保留 字母、数字、点(.)、冒号(:)
            raw = Regex.Replace(raw, @"[^a-zA-Z0-9\.\:\-]", "");

            // 检查清洗后是否还剩下内容
            if (string.IsNullOrEmpty(raw))
            {
                SystemSounds.Beep.Play(); // 播放系统提示音
                return String.Empty;
            }

            // 把清洗干净的地址放回输入框
            return raw;
        }

        //开始按钮方法
        private async void btnOK_Click(object sender, EventArgs e)
        {
            // 自动刷新网卡（若当前选中的网卡已不存在）
            EnsureSelectedNICValid();

            // --- 1. 状态切换逻辑 ---
            if (_isScanning) { _cts?.Cancel(); return; }

            // --- 2. 参数读取与目标解析 ---
            string target = GetFormattedIP();
            if (string.IsNullOrEmpty(target)) return;

            IPAddress targetIp;
            try
            {
                targetIp = Dns.GetHostAddresses(target).FirstOrDefault();
                if (targetIp == null) throw new Exception();
            }
            catch
            {
                MessageBox.Show("无法解析目标地址");
                return;
            }

            // --- 3. 出口 IP 探测与协议检查 ---
            IPAddress selectedIp = GetSelectedLocalIP();
            IPAddress actualLocalIp = GetActualLocalIP(target, selectedIp);

            if (actualLocalIp.AddressFamily != targetIp.AddressFamily)
            {
                string localVer = actualLocalIp.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                string targetVer = targetIp.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                MessageBox.Show($"本地 {localVer}, 目标 {targetVer}。", "协议不太对");
                return;
            }

            // --- 4. 智能选中网卡（防重复逻辑） ---
            if (selectedIp.Equals(IPAddress.Any) || selectedIp.Equals(IPAddress.IPv6Any))
            {
                string targetPrefix = actualLocalIp.ToString() + " (";
                bool found = false;
                for (int i = 0; i < comboLocalEnd.Items.Count; i++)
                {
                    if (comboLocalEnd.Items[i].ToString().StartsWith(targetPrefix))
                    {
                        comboLocalEnd.SelectedIndex = i;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    string newEntry = $"{actualLocalIp}";
                    comboLocalEnd.Items.Add(newEntry);
                    comboLocalEnd.SelectedItem = newEntry;
                }
            }

            // --- 5. 准备扫描参数 ---
            string finalLocalInfo = comboLocalEnd.SelectedItem.ToString();
            List<int> ports = ParsePorts(txtPort.Text);
            if (!int.TryParse(txtThreads.Text, out int threadCount)) threadCount = 100;
            if (!int.TryParse(txtTimeout.Text, out int timeout)) timeout = 30;

            _isScanning = true;
            _cts = new CancellationTokenSource();
            btnOK.Text = "停止";
            SetControlsEnabled(false);
            richResult.Clear();

            // --- 6. 打印梦酱要求的抬头格式 ---
            richResult.AppendText($"[扫描目标] {target} 的 {txtPort.Text.Trim()} 端口\n");
            richResult.AppendText($"[使用网卡] {finalLocalInfo}\n");
            richResult.AppendText($"[扫描设置] 线程 {threadCount} / 超时 {timeout}ms\n");
            richResult.AppendText($"[开始时间] {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            richResult.AppendText("[TCP端口] ");
            richResult.ScrollToCaret();

            // --- 7. 排序收集与进度报告逻辑 ---
            List<int> foundPortsList = new List<int>();
            var progress = new Progress<int>(port =>
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    foundPortsList.Add(port);
                    foundPortsList.Sort(); // 实时排序

                    StringBuilder sb = new StringBuilder();
                    sb.Append("[TCP端口] ");
                    foreach (int p in foundPortsList)
                    {
                        string note = portNotes.ContainsKey(p) ? $"({portNotes[p]})" : "";
                        sb.Append($"{p}{note}, ");
                    }
                    UpdateTcpPortLine(sb.ToString()); // 替换旧行实现整齐输出
                }
            });

            // --- 8. 核心后台扫描引擎 ---
            await Task.Run(async () =>
            {
                try
                {
                    using (var semaphore = new SemaphoreSlim(threadCount))
                    {
                        var tasks = new List<Task>();
                        foreach (int port in ports)
                        {
                            if (_cts.Token.IsCancellationRequested) break;
                            await semaphore.WaitAsync(_cts.Token);

                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    int result = await ScanPortFast(target, port, actualLocalIp, timeout, _cts.Token);
                                    if (result != -1)
                                    {
                                        ((IProgress<int>)progress).Report(result);
                                    }

                                    // 更新按钮进度提示
                                    if (port % 100 == 0)
                                    {
                                        this.BeginInvoke(new Action(() => { if (!this.IsDisposed) btnOK.Text = $"P:{port}"; }));
                                    }
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }));
                        }
                        await Task.WhenAll(tasks);
                    }
                }
                catch (OperationCanceledException)
                {
                    this.BeginInvoke(new Action(() => { richResult.AppendText("\n[用户手动停止扫描]"); }));
                }
                finally
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        _isScanning = false;
                        btnOK.Text = "开扫";
                        SetControlsEnabled(true);
                        richResult.AppendText($"\n[扫描完成] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        richResult.ScrollToCaret();
                    }));
                }
            });
        }

        // 辅助方法：精准替换最后一行扫描结果，防止刷屏
        private void UpdateTcpPortLine(string newLineText)
        {
            int start = richResult.Find("[TCP端口]");
            if (start >= 0)
            {
                // 选中从“[TCP端口]”开始到结尾的所有文本
                richResult.Select(start, richResult.TextLength - start);
                // 替换为排序后的新文本
                richResult.SelectedText = newLineText;
            }
        }
        private IPAddress GetSelectedLocalIP()
        {
            string selectedItem = "";
            // 跨线程访问 UI 加上 Invoke
            this.Invoke(new Action(() => { selectedItem = comboLocalEnd.SelectedItem?.ToString() ?? ""; }));

            if (selectedItem.Contains("0.0.0.0")) return IPAddress.Any;
            if (selectedItem.Contains("::")) return IPAddress.IPv6Any;

            string ipPart = selectedItem.Split(' ')[0];
            if (IPAddress.TryParse(ipPart, out IPAddress ip)) return ip;

            return IPAddress.Any;
        }
        // 辅助方法：禁用/启用界面组件
        private void SetControlsEnabled(bool enabled)
        {
            txtTarget.Enabled = enabled;
            txtPort.Enabled = enabled;
            txtThreads.Enabled = enabled;
            txtTimeout.Enabled = enabled;
            btnPaste.Enabled = enabled;
            btnMinimum.Enabled = enabled;
            btnFull.Enabled = enabled;
            btnSave.Enabled = enabled;
            comboLocalEnd.Enabled = enabled;
        }

        // 预定义的端口备注字典，让结果更专业
        private Dictionary<int, string> portNotes = new Dictionary<int, string>
        {
            {20, "FTP Data"},
            {21, "FTP"},
            {22, "SSH"},
            {23, "Telnet"},
            {25, "SMTP"},
            {37, "Time"},
            {42, "WINS"},
            {43, "WHOIS"},
            {53, "DNS"},
            {67, "DHCP Server"},
            {68, "DHCP Client"},
            {69, "TFTP"},
            {70, "Gopher"},
            {79, "Finger"},
            {80, "HTTP"},
            {88, "Kerberos"},
            {110, "POP3"},
            {113, "Ident"},
            {119, "NNTP"},
            {123, "NTP"},
            {135, "RPC"},
            {137, "NetBIOS Name"},
            {138, "NetBIOS Datagram"},
            {139, "NetBIOS Session"},
            {143, "IMAP"},
            {161, "SNMP"},
            {162, "SNMP Trap"},
            {179, "BGP"},
            {389, "LDAP"},
            {443, "HTTPS"},
            {445, "SMB"},
            {458, "QuickTime"},
            {465, "SMTPS"},
            {514, "Syslog"},
            {546, "DHCPv6 Client"},
            {547, "DHCPv6 Server"},
            {554, "RTSP"},
            {569, "MSN"},
            {587, "SMTP"},
            {990, "FTPS"},
            {993, "IMAPS"},
            {995, "POP3S"},
            {1080, "Socks Proxy"},
            {1433, "SQL Server"},
            {1503, "NetMeeting"},
            {1688, "KMS"},
            {1723, "PPTP VPN"},
            {1900, "UPnP"},
            {2049, "NFS"},
            {3306, "MySQL"},
            {3389, "RDP"},
            {4899, "Radmin"},
            {5000, "UPnP"},
            {5201, "iPerf"},
            {5631, "pcAnywhere"},
            {5900, "VNC"},
            {6129, "Dameware"},
            {7890, "HTTP(Clash)"},
            {7891, "SOCKS5(Clash)"},
            {7892, "Forward(Clash)"},
            {7893, "Mix(Clash)"},
            {7894, "DNS(Clash)"},
            {7895, "TProxy(Clash)"},
            {8080, "HTTP-Proxy"},
            {9090, "Prometheus/WebUI"}
        };

        // 极速扫描核心方法
        private async Task<int> ScanPortFast(string host, int port, IPAddress localIp, int timeout, CancellationToken ct)
        {
            using (Socket socket = new Socket(localIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                try
                {
                    // 优化：让 Socket 关闭后立即释放端口，不进入 TIME_WAIT 状态
                    socket.LingerState = new LingerOption(true, 0);
                    socket.NoDelay = true; // 禁用 Nagle 算法，减少延迟

                    socket.Bind(new IPEndPoint(localIp, 0));

                    // 使用 TaskCompletionSource 把回调转为 await，性能更强
                    var tcs = new TaskCompletionSource<bool>();
                    using (ct.Register(() => tcs.TrySetCanceled()))
                    {
                        var args = new SocketAsyncEventArgs { RemoteEndPoint = new IPEndPoint(IPAddress.Parse(host), port) };
                        args.Completed += (s, e) => tcs.TrySetResult(e.SocketError == SocketError.Success);

                        if (!socket.ConnectAsync(args)) tcs.TrySetResult(args.SocketError == SocketError.Success);

                        var timeoutTask = Task.Delay(timeout, ct);
                        if (await Task.WhenAny(tcs.Task, timeoutTask) == tcs.Task && await tcs.Task)
                        {
                            return port;
                        }
                    }
                }
                catch { }
                return -1;
            }
        }

        //获取出口IP的方法
        private IPAddress GetActualLocalIP(string targetHost, IPAddress selectedIp)
        {
            // 如果梦酱选的是明确的 IP，直接返回
            if (!selectedIp.Equals(IPAddress.Any) && !selectedIp.Equals(IPAddress.IPv6Any))
            {
                return selectedIp;
            }

            try
            {
                // 尝试解析目标地址（处理域名或IP）
                IPAddress targetIp = Dns.GetHostAddresses(targetHost).FirstOrDefault();
                if (targetIp == null) return selectedIp;

                // 利用 UDP 的 Connect 不产生流量的特性，探测系统路由表
                using (Socket socket = new Socket(targetIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(targetIp, 1); // 端口是多少不重要
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            }
            catch { return selectedIp; }
        }


        private void PortScan_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_isScanning)
            {
                _cts?.Cancel(); // 告诉扫描任务赶紧停下
            }
        }

        private void txtTarget_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }

        private void txtPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }

        private void txtThreads_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }

        private void txtTimeout_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }

        private void btnFull_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }

        private void btnMinimum_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }
    }
}
