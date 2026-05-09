using System;
using System.Collections.Generic;
using System.Drawing;
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
    public partial class NATTest : Form
    {
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
        // 首先修改成员变量类型
        private List<IPEndPoint> _publicEndPoints5780 = new List<IPEndPoint>();
        private List<IPEndPoint> _publicEndPoints3489 = new List<IPEndPoint>();

        private CancellationTokenSource _cts3489;
        private CancellationTokenSource _cts5780;
        private Socket _activeSocket3489;
        private Socket _activeSocket5780;

        //严格模式
        //bool strictMode = true;

        public NATTest()
        {
            InitializeComponent();
        }
        // 这是一个“间谍”方法，用来偷看系统到底想用哪个 IP 出去
        // 现在支持 IPv4/IPv6 目标地址
        private IPAddress GetLocalRoutingIp(IPEndPoint targetServer)
        {
            try
            {
                // 根据目标地址的地址族 (V4 或 V6) 来创建对应的 Socket
                // 这样才能正确地连接到目标服务器
                using (Socket socket = new Socket(targetServer.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    // 假装连接一下服务器 (UDP 不需要真握手，所以很快)
                    socket.Connect(targetServer);

                    // 连接后，系统就已经分配好出口 IP 了，读出来！
                    IPAddress localAddress = ((IPEndPoint)socket.LocalEndPoint).Address;

                    // 增加一步检查：如果是 IPv6 Any 地址 (::) 或 IPv4 Any 地址 (0.0.0.0)，说明系统没有分配特定的出口 IP
                    // 这种情况通常发生在路由失败或未配置网络的情况下，我们返回该地址的 Any IP
                    if (localAddress.Equals(IPAddress.IPv6Any) || localAddress.Equals(IPAddress.Any))
                    {
                        // 路由失败，只好返回通配符地址
                        return localAddress;
                    }

                    return localAddress;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                // 路由失败，返回对应地址族的 Any 地址
                if (targetServer.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    return IPAddress.IPv6Any;
                }
                else
                {
                    return IPAddress.Any; // IPv4
                }
            }
        }
        private async Task ApplyNATThemeAsync()
        {
            // 异步等待 UI 准备就绪
            //await Task.Yield();

            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60); // 梦酱要求的 60 灰

            // 1. 窗口背景
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 2. 标题标签组 (Yumeyo色)
            Control[] yumeyoControls = {
        lbl5780, lbl3489, lbl5780StartTime, lbl3489StartTime, lblExeName
    };
            foreach (var c in yumeyoControls) { if (c != null) c.ForeColor = yumeyoColor; }

            // 3. 普通标签与勾选框 (黑/白文字)
            Control[] contrastControls = {
        lbl5780Binding, lbl5780Mapping, lbl5780Filtering, lbl5780LocalEnd,
        lbl5780PublicEnd, lbl3489Type, lbl3489LocalEnd, lbl3489PublicEnd,
        checkPortRandom, checkPortMode, checkPortRange, radioTCP, radioUDP, radioTLS
    };
            foreach (var c in contrastControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    if (c is CheckBox) c.BackColor = Color.Transparent;
                }
            }

            // 4. 文本框与下拉框 (背景与文字智能切换)
            Control[] editControls = {
    txt5780Debug, txt3489Debug, txt5780Binding, txt5780Mapping,
    txt5780Filtering, combo5780LocalEnd, txt5780PublicEnd,
    txt3489Type, combo3489LocalEnd, txt3489PublicEnd, comboServer
};

            foreach (var c in editControls)
            {
                if (c != null)
                {
                    // 特殊处理：如果文本框有 [!] 标记，保持橙色
                    if (c == txt5780PublicEnd && txt5780PublicEnd.Text.StartsWith("[!]"))
                    {
                        txt5780PublicEnd.ForeColor = Color.DarkOrange;
                    }
                    else if (c == txt3489PublicEnd && txt3489PublicEnd.Text.StartsWith("[!]"))
                    {
                        txt3489PublicEnd.ForeColor = Color.DarkOrange;
                    }
                    else
                    {
                        c.ForeColor = contrastColor;
                    }

                    c.BackColor = textBack;

                    // 核心优化：只在深色模式下扁平化
                    if (c is ComboBox cb)
                    {
                        if (isLight)
                        {
                            // 浅色模式：恢复系统默认 3D 样式
                            cb.FlatStyle = FlatStyle.Standard;
                        }
                        else
                        {
                            // 深色模式：开启扁平化，消灭白边
                            cb.FlatStyle = FlatStyle.Flat;
                        }
                    }
                }
            }

            // 5. 按钮组 (深色模式下为 60 灰)
            // 请根据你设计器里的实际按钮名修改数组内容
            Control[] buttons = { btnCheck5780, btnCheck3489, btnRFCCompare, btnTrace, btnReset, btnSettings };
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
                        btn.FlatAppearance.BorderColor = Color.DimGray; // 给个暗色边框
                        btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                    }
                }
            }
        }
        private async void NATTest_Load(object sender, EventArgs e)      // 窗口加载
        {
            lblExeName.Text = Global.exeName + " " + Global.Version;
            _ = ApplyNATThemeAsync();

            //随意拖拽
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            pictureBox2.MouseDown += MyMouseDown;

            // 先清除可能重复绑定的事件
            btnCheck5780.Click -= btnCheck5780_Click;
            btnCheck3489.Click -= btnCheck3489_Click;

            // 1. 加载本地 IP 地址到下拉框
            LoadCheckStates();
            LoadLocalIPs();

            // 开发调试服务器列表
            CloudControl.LoadStunServers(comboServer);
            CloudControl.ApplyDevTitle(this);

            if (comboServer.Items.Count > 0)
            {
                comboServer.SelectedIndex = 0;
            }

            SetupButtonEvents5780();
            SetupButtonEvents3489();
        }
        private void SetupButtonEvents5780()
        {
            // 移除所有现有的事件处理程序，防止重复绑定
            btnCheck5780.Click -= btnCheck5780_Click;
            btnCheck5780.MouseDown -= Button_MouseDown5780;
            btnCheck5780.MouseWheel -= btnCheck5780_MouseWheel;

            // 1. 正常点击（左键）
            btnCheck5780.Click += btnCheck5780_Click;

            // 2. 右键点击
            btnCheck5780.MouseDown += Button_MouseDown5780;

            // 3. 鼠标悬停并滚动滚轮
            btnCheck5780.MouseEnter -= Button_MouseEnter5780;
            btnCheck5780.MouseLeave -= Button_MouseLeave5780;

            btnCheck5780.MouseEnter += Button_MouseEnter5780;
            btnCheck5780.MouseLeave += Button_MouseLeave5780;
        }

        // 为每个事件处理创建单独的方法，避免使用匿名方法
        private void Button_MouseDown5780(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && btnCheck5780.Enabled)
            {
                btnCheck5780_Click(sender, e);
            }
        }

        private void Button_MouseEnter5780(object sender, EventArgs e)
        {
            // 当鼠标进入按钮区域时，绑定滚轮事件
            btnCheck5780.MouseWheel += btnCheck5780_MouseWheel;
        }

        private void Button_MouseLeave5780(object sender, EventArgs e)
        {
            // 当鼠标离开按钮区域时，移除滚轮事件绑定
            btnCheck5780.MouseWheel -= btnCheck5780_MouseWheel;
        }

        // 修改滚轮事件处理，避免重复触发
        private void btnCheck5780_MouseWheel(object sender, MouseEventArgs e)
        {
            // 防止短时间内重复触发：检查按钮是否已禁用（表示测试正在进行）
            if (e.Delta != 0 && btnCheck5780.Enabled)
            {
                btnCheck5780_Click(sender, e);
            }
        }

        private void SetupButtonEvents3489()
        {
            // 移除所有现有的事件处理程序，防止重复绑定
            btnCheck3489.Click -= btnCheck3489_Click;
            btnCheck3489.MouseDown -= Button_MouseDown3489;
            btnCheck3489.MouseWheel -= btnCheck3489_MouseWheel;

            // 1. 正常点击（左键）
            btnCheck3489.Click += btnCheck3489_Click;

            // 2. 右键点击
            btnCheck3489.MouseDown += Button_MouseDown3489;

            // 3. 鼠标悬停并滚动滚轮
            btnCheck3489.MouseEnter -= Button_MouseEnter3489;
            btnCheck3489.MouseLeave -= Button_MouseLeave3489;

            btnCheck3489.MouseEnter += Button_MouseEnter3489;
            btnCheck3489.MouseLeave += Button_MouseLeave3489;
        }

        private void Button_MouseDown3489(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && btnCheck3489.Enabled)
            {
                btnCheck3489_Click(sender, e);
            }
        }

        private void Button_MouseEnter3489(object sender, EventArgs e)
        {
            // 当鼠标进入按钮区域时，将鼠标滚轮事件绑定到当前控件
            btnCheck3489.MouseWheel += btnCheck3489_MouseWheel;
        }

        private void Button_MouseLeave3489(object sender, EventArgs e)
        {
            // 当鼠标离开按钮区域时，移除滚轮事件绑定
            btnCheck3489.MouseWheel -= btnCheck3489_MouseWheel;
        }
        private void btnCheck3489_MouseWheel(object sender, MouseEventArgs e)
        {
            // 防止短时间内重复触发：检查按钮是否已禁用（表示测试正在进行）
            if (e.Delta != 0 && btnCheck3489.Enabled)
            {
                btnCheck3489_Click(sender, e);
            }
        }

        // 全局记忆变量
        // [修复]：拆分两个变量，防止3489和5780互相干扰端口记忆
        private int _lastPort3489 = 0;
        private int _lastPort5780 = 0;
        private bool _stopRequested = false; // 停止标志

        // 辅助工具：获取当前时间
        private string GetCurrentTime()
        {
            return DateTime.Now.ToString("HH:mm:ss");
        }

        // 辅助工具：更新日志 3489
        // 接收一个可选的 titleStatus 字符串，用来更新标题
        private void Log(string msg, string titleStatus = null)
        {
            if (_stopRequested || (_cts3489?.Token.IsCancellationRequested == true))
                return;

            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => Log(msg, titleStatus)));
                return;
            }

            if (_stopRequested || (_cts3489?.Token.IsCancellationRequested == true))
                return;

            // 只有调试框可见时才追加文本
            if (!string.IsNullOrEmpty(msg) && txt3489Debug.Visible)
            {
                txt3489Debug.Text += msg + "\r\n";
                txt3489Debug.SelectionStart = txt3489Debug.Text.Length;
                txt3489Debug.ScrollToCaret();
            }

            if (!string.IsNullOrEmpty(titleStatus))
            {
                lbl3489.Text = $"RFC3489: {titleStatus}";
            }

            Application.DoEvents();
        }

        // 辅助工具：更新 5780 日志
        private void Log5780(string msg, string titleStatus = null)
        {
            if (_stopRequested || (_cts5780?.Token.IsCancellationRequested == true))
                return;

            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => Log5780(msg, titleStatus)));
                return;
            }

            if (_stopRequested || (_cts5780?.Token.IsCancellationRequested == true))
                return;

            if (!string.IsNullOrEmpty(msg) && txt5780Debug.Visible)
            {
                txt5780Debug.Text += msg + "\r\n";
                txt5780Debug.SelectionStart = txt5780Debug.Text.Length;
                txt5780Debug.ScrollToCaret();
            }

            if (!string.IsNullOrEmpty(titleStatus))
            {
                lbl5780.Text = $"RFC5780: {titleStatus}";
            }

            Application.DoEvents();
        }

        // 核心方法：根据设置生成本次测试要使用的端口
        // is5780: 是否为 5780 测试，用来决定日志输出到哪个 Debug 框
        private int GetPortToUse(bool is5780)
        {
            // 定义日志委托
            Action<string> logger = is5780
                ? (msg => Log5780(msg, null))
                : (Action<string>)(msg => Log(msg, null));

            int minPort = 1;
            int maxPort = 65535;

            if (checkPortRange.Checked) minPort = 49152;

            string input = is5780 ? combo5780LocalEnd.Text.Trim() : combo3489LocalEnd.Text.Trim();

            // 清理掉可能存在的描述文本 (比如 "192.168.1.1 (描述)")
            if (input.Contains(" ")) input = input.Split(' ')[0];

            int currentDisplayPort = 0;
            bool hasPortInDisplay = false;

            // 判断是否包含端口的逻辑
            if (input.StartsWith("[") && input.Contains("]:")) // [IPv6]:Port
            {
                string portPart = input.Split(new string[] { "]:" }, StringSplitOptions.None)[1];
                if (int.TryParse(portPart, out currentDisplayPort)) hasPortInDisplay = true;
            }
            else if (input.Contains(":") && input.Split(':').Length == 2) // IPv4:Port
            {
                string portPart = input.Split(':')[1];
                if (int.TryParse(portPart, out currentDisplayPort)) hasPortInDisplay = true;
            }

            // 如果检测到手动输入的端口是0，视为无效
            if (hasPortInDisplay && currentDisplayPort == 0)
            {
                logger($"[端口] 检测到手动输入端口为0，视为无效，将重新生成端口");
                hasPortInDisplay = false;
            }

            // [修复逻辑开始]：根据协议类型获取对应的"上一次端口"
            int lastPortRecord = is5780 ? _lastPort5780 : _lastPort3489;

            // 只有当 UI 上的端口 存在 且 与该协议上一次程序生成的端口 不一致 时，
            // 才认为是用户"手动输入"或"手动保留"了特定端口。
            // 这样如果是程序上次填进去的旧端口，会被视为"非手动"，从而继续执行下面的随机/递增逻辑。
            if (hasPortInDisplay && currentDisplayPort != lastPortRecord)
            {
                // 更新记录，视为本次使用了该端口
                if (is5780) _lastPort5780 = currentDisplayPort;
                else _lastPort3489 = currentDisplayPort;

                logger($"[端口] 检测到手动指定端口，强制使用: {currentDisplayPort}");
                return currentDisplayPort;
            }

            // 下面保持原有的随机/递增逻辑，但使用分开的变量
            if (!checkPortRandom.Checked && lastPortRecord != 0)
            {
                // 连续固定模式：检查是否与另一测试的端口冲突
                int otherLastPort = is5780 ? _lastPort3489 : _lastPort5780;
                if (lastPortRecord == otherLastPort && otherLastPort != 0)
                {
                    logger($"[端口] 连续固定模式检测到端口冲突({lastPortRecord})，按设置生成新端口");
                    int resolvedPort;
                    if (checkPortMode.Checked)
                        resolvedPort = new Random().Next(minPort, maxPort + 1);
                    else
                    {
                        resolvedPort = lastPortRecord + 1;
                        if (resolvedPort > maxPort) resolvedPort = minPort;
                    }
                    if (is5780) _lastPort5780 = resolvedPort;
                    else _lastPort3489 = resolvedPort;
                    logger($"[端口] 冲突解决，新固定端口: {resolvedPort}");
                    return resolvedPort;
                }
                logger($"[端口] 连续固定模式，复用端口: {lastPortRecord}");
                return lastPortRecord;
            }

            int newPort;
            if (checkPortMode.Checked)
            {
                Random rnd = new Random();
                newPort = rnd.Next(minPort, maxPort + 1);
                logger($"[端口] 生成随机端口: {newPort}");
            }
            else
            {
                if (lastPortRecord == 0)
                {
                    Random rnd = new Random();
                    newPort = rnd.Next(minPort, maxPort + 1);
                }
                else
                {
                    newPort = lastPortRecord + 1;
                    if (newPort > maxPort) newPort = minPort;
                }
                logger($"[端口] 递增模式端口: {newPort}");
            }

            // 连续固定模式首次生成：检查是否与另一测试的端口冲突
            if (!checkPortRandom.Checked)
            {
                int otherLastPort = is5780 ? _lastPort3489 : _lastPort5780;
                if (newPort == otherLastPort && otherLastPort != 0)
                {
                    logger($"[端口] 连续固定模式检测到端口冲突({newPort})，按设置生成新端口");
                    if (checkPortMode.Checked)
                    {
                        int attempts = 0;
                        do
                        {
                            newPort = new Random().Next(minPort, maxPort + 1);
                            attempts++;
                        } while (newPort == otherLastPort && attempts < 100);
                    }
                    else
                    {
                        newPort = otherLastPort + 1;
                        if (newPort > maxPort) newPort = minPort;
                    }
                    logger($"[端口] 冲突解决，新固定端口: {newPort}");
                }
            }

            // 保存回对应的变量
            if (is5780) _lastPort5780 = newPort;
            else _lastPort3489 = newPort;

            return newPort;
        }

        // 自动刷新网卡：当系统网卡变化导致选中网卡不存在时，刷新列表并恢复默认
        private void EnsureSelectedNICValid(bool is5780)
        {
            ComboBox combo = is5780 ? combo5780LocalEnd : combo3489LocalEnd;
            string selectedText = combo.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.StartsWith("0.0.0.0") || selectedText.StartsWith("::")) return;
            string selectedIP = selectedText.Split(' ')[0];

            LoadLocalIPs();

            bool found = false;
            foreach (var item in combo.Items)
            {
                if (item.ToString() == selectedText)
                {
                    combo.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found && combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        // 本机网卡IP列表
        private async void LoadLocalIPs()
        {
            combo5780LocalEnd.Items.Clear();
            combo3489LocalEnd.Items.Clear();

            // 手动添加 Any 选项
            string anyItemV4 = "0.0.0.0 (Any)";
            string anyItemV6 = ":: (IPv6 Any)";
            combo5780LocalEnd.Items.Add(anyItemV4);
            combo5780LocalEnd.Items.Add(anyItemV6);
            combo3489LocalEnd.Items.Add(anyItemV4);

            try
            {
                // 遍历所有网卡
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // A. 基本状态过滤：跳过未启用的网卡
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    // B. 关键字屏蔽：屏蔽常见的纯内部虚拟网卡 (VMware/VirtualBox)
                    string desc = ni.Description.ToLower();
                    string name = ni.Name.ToLower();
                    if (desc.Contains("vmware") || desc.Contains("virtual") || desc.Contains("vbox") || desc.Contains("hyper-v") || desc.Contains("wsl") || desc.Contains("pseudo") || desc.Contains("tap") || desc.Contains("tun") || desc.Contains("loopback") || desc.Contains("vpn") || desc.Contains("teredo"))
                        continue;

                    // C. 核心逻辑：获取 IP 属性
                    var ipProps = ni.GetIPProperties();

                    // D. 智能判断：是否是我们要找的“有效”网卡？
                    // 满足以下任一条件即可：
                    // 1. 是物理网卡 (Ethernet/Wireless80211)
                    // 2. 或者是拥有网关的虚拟网卡 (比如网卡聚合、Hyper-V 桥接、OpenWrt 虚拟网卡)
                    bool isPhysical = (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                       ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                    bool hasGateway = ipProps.GatewayAddresses.Count > 0;

                    if (!isPhysical && !hasGateway) continue;

                    // E. 遍历该网卡下的所有 IP 地址
                    foreach (UnicastIPAddressInformation ipInfo in ipProps.UnicastAddresses)
                    {
                        IPAddress ip = ipInfo.Address;

                        // 排除回环地址 (127.0.0.1 / ::1)
                        if (IPAddress.IsLoopback(ip)) continue;

                        // 排除 IPv6 链路本地地址 (fe80:...)
                        if (ip.IsIPv6LinkLocal) continue;

                        // ✨ 新增：排除 169.254.x.x (APIPA 无效地址)
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] bytes = ip.GetAddressBytes();
                            if (bytes[0] == 169 && bytes[1] == 254) continue;
                        }

                        string ipStr = ip.ToString();
                        // 去掉 IPv6 后面带的 % 区域 ID
                        if (ipStr.Contains("%")) ipStr = ipStr.Split('%')[0];

                        // 格式化：加上网卡名称，方便梦酱一眼看出是哪个网卡
                        string displayName = string.Format("{0} ({1})", ipStr, ni.Name);

                        if (ip.AddressFamily == AddressFamily.InterNetwork) // IPv4
                        {
                            combo5780LocalEnd.Items.Add(displayName);
                            combo3489LocalEnd.Items.Add(displayName);
                        }
                        else if (ip.AddressFamily == AddressFamily.InterNetworkV6) // IPv6
                        {
                            combo5780LocalEnd.Items.Add(displayName);
                        }
                    }
                }

                // 默认选中第一个
                if (combo5780LocalEnd.Items.Count > 0) combo5780LocalEnd.SelectedIndex = 0;
                if (combo3489LocalEnd.Items.Count > 0) combo3489LocalEnd.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取IP失败: " + ex.Message);
            }
        }

        private async void btnCheck5780_Click(object sender, EventArgs e)
        {
            // 1. 初始化和锁定
            btnCheck5780.Enabled = false;
            combo5780LocalEnd.Enabled = false;
            radioTCP.Enabled = false;
            radioUDP.Enabled = false;
            radioTLS.Enabled = false;

            // 重置IP检测
            ResetIPDetection5780();  // <-- 添加这一行

            string current5780Server = comboServer.Text;
            txt5780Binding.ForeColor = Color.Black;
            txt5780Mapping.ForeColor = ColorTranslator.FromHtml("#8e8cd8");
            txt5780Filtering.ForeColor = ColorTranslator.FromHtml("#8e8cd8");

            // 创建取消令牌
            _cts5780 = new CancellationTokenSource();
            var cancellationToken = _cts5780.Token;

            _stopRequested = false;
            txt5780Debug.Clear();
            txt5780Mapping.Text = "......";
            txt5780Filtering.Text = "......";
            txt5780PublicEnd.Text = "";

            // === 协议选择 ===
            string protocol = "UDP"; // 默认
            if (radioTCP.Checked) protocol = "TCP";
            else if (radioTLS.Checked) protocol = "TLS";

            lbl5780StartTime.Text = string.Format("开测: {0} 服务器:[{2}]{1}", GetCurrentTime(), current5780Server, protocol);
            txt5780Binding.Text = "";

            Socket socket = null;
            _activeSocket5780 = null;

            try
            {
                Log5780(string.Format("开始时间: " + Others.GetCurrentTime()));
                Log5780(string.Format("=== 开始 RFC5780 {0} 协议测试 ===", protocol), string.Format("{0} 测试初始化...", protocol));

                // 2. 准备服务器和绑定 IP/Port
                string serverHost = comboServer.Text.Trim();
                if (string.IsNullOrEmpty(serverHost)) throw new Exception("请选择服务器");

                IPAddress[] serverIps = await Task.Run(() => Dns.GetHostAddresses(serverHost), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // 解析本地IP选择 (修复版)
                EnsureSelectedNICValid(true);
                string inputRaw = combo5780LocalEnd.Text.Trim();

                // 1. 剥离掉 IP 后的描述或空格 (例如 "192.168.1.1 (描述)")
                string ipPartToParse = inputRaw.Split(' ')[0];
                IPAddress selectedLocalIP = IPAddress.Any;
                bool parseSuccess = false;

                // 2. 优先尝试直接解析 (完美支持纯 IPv4 和 纯 IPv6)这样像 2001:da8::1 这样的地址就不会被错误切割了
                if (IPAddress.TryParse(ipPartToParse, out selectedLocalIP))
                {
                    parseSuccess = true;
                }
                // 3. 如果直接解析失败，可能是 [IPv6]:Port 格式
                else if (ipPartToParse.StartsWith("[") && ipPartToParse.Contains("]:"))
                {
                    int closeBracketIndex = ipPartToParse.IndexOf(']');
                    string ipOnly = ipPartToParse.Substring(1, closeBracketIndex - 1);
                    if (IPAddress.TryParse(ipOnly, out selectedLocalIP))
                    {
                        parseSuccess = true;
                    }
                }
                // 4. 最后尝试 IPv4:Port 格式 (特征是只有一个冒号)
                else if (ipPartToParse.Contains(":") && ipPartToParse.Split(':').Length == 2)
                {
                    string ipOnly = ipPartToParse.Split(':')[0];
                    if (IPAddress.TryParse(ipOnly, out selectedLocalIP))
                    {
                        parseSuccess = true;
                    }
                }

                // 如果所有尝试都失败，或者解析到了 Any (0.0.0.0 / ::)，做个标记或默认处理
                if (!parseSuccess)
                {
                    // 如果原本就是 Any 选项，尝试根据输入判断类型
                    if (ipPartToParse.Contains("IPv6") || ipPartToParse.Contains("::"))
                    {
                        selectedLocalIP = IPAddress.IPv6Any;
                    }
                    else
                    {
                        selectedLocalIP = IPAddress.Any; // 默认 V4 Any
                    }

                    if (!ipPartToParse.Contains("Any") && !ipPartToParse.Contains("0.0.0.0") && !ipPartToParse.Contains("::"))
                    {
                        Log5780(string.Format("[Warning] 无法解析地址 '{0}'，默认为 Any。", ipPartToParse));
                    }
                }

                // 确定测试是 V4 还是 V6
                AddressFamily testFamily = AddressFamily.InterNetwork;
                if (selectedLocalIP.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    testFamily = AddressFamily.InterNetworkV6;
                }

                // 找到第一个匹配地址族的服务器 IP
                IPAddress serverIp = null;
                foreach (var ip in serverIps)
                {
                    if (ip.AddressFamily == testFamily)
                    {
                        serverIp = ip;
                        break;
                    }
                }

                if (serverIp == null)
                {
                    throw new Exception(string.Format("服务器未提供 {0} 地址", testFamily));
                }

                // 根据协议类型确定服务器端口，注意3478那边也有一句一模一样的端口话，不要弄错了
                int serverPort = 3478; // 默认 UDP/TCP 端口
                if (protocol == "TLS")
                {
                    serverPort = 5349; // TLS STUN 默认端口
                    Log5780("使用 TLS 默认端口 5349", "端口配置");
                }

                IPEndPoint serverEp1 = new IPEndPoint(serverIp, serverPort);

                // === 根据协议类型分别处理 ===
                if (protocol == "UDP")
                {
                    await RunUdpTest5780(serverEp1, selectedLocalIP, testFamily, cancellationToken);
                }
                else if (protocol == "TCP")
                {
                    await RunTcpTest5780(serverEp1, selectedLocalIP, testFamily, protocol, cancellationToken);
                }
                else if (protocol == "TLS")
                {
                    await RunTlsTest5780(serverEp1, selectedLocalIP, testFamily, protocol, cancellationToken, serverHost);
                }

            }
            catch (OperationCanceledException)
            {
                Log5780("测试已被用户取消", "测试取消");
            }
            catch (Exception ex)
            {
                Log5780(string.Format("[Error] {0}", ex.Message));
                if (!cancellationToken.IsCancellationRequested)
                {
                    // 错误处理逻辑保持不变
                    string errorMessage = ex.Message.ToLower();
                    if (errorMessage.Contains("tcp") || errorMessage.Contains("连接") ||
                        errorMessage.Contains("timeout") || errorMessage.Contains("超时"))
                    {
                        txt5780Binding.Text = "Fail";
                        txt5780Binding.ForeColor = Color.Red;
                        txt5780Mapping.Text = errorMessage.Contains("timeout") || errorMessage.Contains("超时") ? "Timeout" : "Connection Fail";
                        txt5780Filtering.Text = txt5780Mapping.Text;
                        txt5780Mapping.ForeColor = Color.DarkOrange;
                        txt5780Filtering.ForeColor = Color.DarkOrange;
                        txt5780PublicEnd.Text = "";
                        Log5780(null, string.Format("测试失败: {0}", ex.Message));
                    }
                    else
                    {
                        MessageBox.Show("测试出错: " + ex.Message);
                        Log5780(null, string.Format("测试出错: {0}", ex.Message));
                    }
                }
            }
            finally
            {
                // 清理逻辑保持不变
                if (socket != null)
                {
                    try
                    {
                        socket.Close();
                        socket.Dispose();
                    }
                    catch { }
                }
                _activeSocket5780 = null;

                if (!cancellationToken.IsCancellationRequested)
                {
                    btnCheck5780.Enabled = true;
                    combo5780LocalEnd.Enabled = true;
                    radioTCP.Enabled = true;
                    radioUDP.Enabled = true;
                    radioTLS.Enabled = true;
                    if (!_stopRequested && !lbl5780.Text.Contains("结束"))
                    {
                        lbl5780.Text = "RFC5780 (完成)";
                    }
                }
            }
        }
        private async Task RunUdpTest5780(IPEndPoint serverEp1, IPAddress selectedLocalIP, AddressFamily testFamily, CancellationToken cancellationToken)
        {
            Socket socket = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                Log5780($"[UDP] 目标服务器: {serverEp1}");
                Log5780($"[UDP] 测试族: {testFamily}");

                // 获取绑定端口
                int bindPort = GetPortToUse(true);
                Log5780($"[UDP] 获取绑定端口: {bindPort}");

                // 创建 UDP Socket
                socket = new Socket(testFamily, SocketType.Dgram, ProtocolType.Udp);
                _activeSocket5780 = socket;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                Log5780($"[UDP] Socket 创建完成");

                if (testFamily == AddressFamily.InterNetworkV6 && selectedLocalIP.Equals(IPAddress.IPv6Any))
                {
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                    Log5780($"[UDP] IPv6Only 已启用");
                }

                // 确定最终的绑定 IP
                IPAddress finalBindIp;
                if (selectedLocalIP.Equals(IPAddress.Any) || selectedLocalIP.Equals(IPAddress.IPv6Any))
                {
                    Log5780($"[UDP] 检测系统路由出口...");
                    finalBindIp = await Task.Run(() => GetLocalRoutingIp(serverEp1), cancellationToken);
                    Log5780($"[UDP] 检测系统实际出口: {finalBindIp}");
                }
                else
                {
                    finalBindIp = selectedLocalIP;
                    Log5780($"[UDP] 使用手动指定出口 IP: {finalBindIp}");
                }

                IPEndPoint localBindEp = new IPEndPoint(finalBindIp, bindPort);
                socket.Bind(localBindEp);
                combo5780LocalEnd.Text = localBindEp.ToString();
                Log5780($"[UDP] 本地绑定完成: {localBindEp}");

                cancellationToken.ThrowIfCancellationRequested();

                // ============================================================
                // Mapping Test I (标准 Binding Request)
                // ============================================================
                Log5780(">>> [UDP] Mapping Test I: Binding Request", "Mapping Test I");
                Log5780($"[UDP] 接收超时: 3000ms");

                Log5780($"[UDP] 请求地址: {serverEp1}");
                var resultA = await Task.Run(() => StunClient.Query(socket, serverEp1, false, false, 3000), cancellationToken);

                if (resultA?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultA.PublicEndPoint);
                    Log5780($"[UDP] 成功 -> {resultA.PublicEndPoint}");
                    if (resultA.ChangedEndPoint != null)
                        Log5780($"[UDP] 备用服务器地址: {resultA.ChangedEndPoint}");
                }
                else
                {
                    Log5780($"[UDP] 失败: 请求超时或无响应");
                    txt5780Binding.Text = "Fail";
                    txt5780Binding.ForeColor = Color.Red;
                    txt5780Mapping.Text = "Unknown";
                    txt5780Filtering.Text = "Unknown";
                    txt5780Mapping.ForeColor = Color.DarkOrange;
                    txt5780Filtering.ForeColor = Color.DarkOrange;
                    txt5780PublicEnd.Text = "";
                    Log5780("Mapping Test I 失败");
                    return;
                }

                txt5780PublicEnd.Text = resultA.PublicEndPoint.ToString();
                txt5780Binding.Text = "Success";
                txt5780Binding.ForeColor = Color.LimeGreen;
                Log5780($"[UDP] Binding 成功，外部地址 {resultA.PublicEndPoint.ToString()}");

                bool isDirectMapping = resultA.PublicEndPoint.Equals(resultA.LocalEndPoint);
                Log5780($"[UDP] 是否公网: {isDirectMapping}");

                var changedEp = resultA.ChangedEndPoint;
                // 检查服务器返回的 ChangedAddress 是否有效
                if (changedEp == null || !IsValidServerAddress(serverEp1, changedEp,
                    (msg, title) => Log5780(msg, title), "RFC5780"))
                {
                    txt5780Mapping.Text = "Test Failed";
                    txt5780Mapping.ForeColor = Color.Red;
                    txt5780Filtering.Text = "Unsupported Server";
                    txt5780Filtering.ForeColor = Color.DarkOrange;
                    Log5780("Mapping Test I 失败 (服务器配置有误)");
                    return;
                }

                Log5780($"[UDP] Mapping Test I 完成");
                Log5780($"[UDP] 服务器备用地址: {changedEp}");

                // RFC5780 Mapping 行为测试固定目标：
                // Test II -> (otherIP, primaryPort)
                // Test III -> (otherIP, otherPort)
                IPEndPoint mappingTest2Server = new IPEndPoint(changedEp.Address, serverEp1.Port);
                IPEndPoint mappingTest3Server = changedEp;

                cancellationToken.ThrowIfCancellationRequested();

                // ============================================================
                // Filtering Test II (Change IP + Port)
                // ============================================================
                Log5780(">>> [UDP] Filtering Test II: Change IP & Port", "Filtering Test II");
                Log5780($"[UDP] 接收超时: 2000ms");

                var filteringII = await Task.Run(() => StunClient.Query(socket, serverEp1, true, true, 2000), cancellationToken);
                Log5780($"[UDP] 请求地址: {serverEp1}");
                if (filteringII?.ResponseEndPoint != null)
                    Log5780($"[UDP] 成功: 响应来源 {filteringII.ResponseEndPoint}");
                else
                    Log5780($"[UDP] 失败: 无响应");

                cancellationToken.ThrowIfCancellationRequested();

                // ============================================================
                // Filtering Test III (Change Port Only)
                // ============================================================
                Log5780(">>> [UDP] Filtering Test III: Change Port", "Filtering Test III");
                Log5780($"[UDP] 接收超时: 2000ms");

                var filteringIII = await Task.Run(() => StunClient.Query(socket, serverEp1, false, true, 2000), cancellationToken);
                Log5780($"[UDP] 请求地址: {serverEp1}");
                if (filteringIII?.ResponseEndPoint != null)
                    Log5780($"[UDP] 成功: 响应来源 {filteringIII.ResponseEndPoint}");
                else
                    Log5780($"[UDP] 失败: 无响应");

                cancellationToken.ThrowIfCancellationRequested();

                // ============================================================
                // 最终 Filtering 判定 (RFC5780)
                // ============================================================
                Log5780(">>> [UDP] 计算最终 Filtering 行为");
                string filteringType = CalculateFilteringType(filteringII, filteringIII, serverEp1, changedEp);
                txt5780Filtering.Text = filteringType;
                txt5780Filtering.ForeColor = GetFilteringColor(filteringType);
                Log5780($"[UDP] Filtering 判定结果: {filteringType}");

                if (filteringType == "Unsupported Server")
                {
                    txt5780Mapping.Text = "Unsupported Server";
                    txt5780Mapping.ForeColor = Color.DarkOrange;
                    Log5780("[UDP] Filtering 为 Unsupported Server，按 RFC5780 流程停止后续 Mapping 测试。", "(完成)");
                    return;
                }


                // ============================================================
                // Mapping Test II: (otherIP, primaryPort)
                // ============================================================
                Log5780(">>> [UDP] Mapping Test II: otherIP + primaryPort", "Mapping Test II");
                Log5780($"[UDP] 接收超时: 1500ms");

                StunResult resultB;
                resultB = await Task.Run(() => StunClient.Query(socket, mappingTest2Server, false, false, 1500), cancellationToken);

                Log5780($"[UDP] 请求地址: {mappingTest2Server}");
                if (resultB != null && resultB.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultB.PublicEndPoint);
                    Log5780($"[UDP] 成功 -> {resultB.PublicEndPoint}");
                }
                else
                {
                    Log5780($"[UDP] 失败: 无响应");
                }

                cancellationToken.ThrowIfCancellationRequested();


                // ============================================================
                // Mapping Test III: (otherIP, otherPort)
                // ============================================================
                Log5780(">>> [UDP] Mapping Test III: otherIP + otherPort", "Mapping Test III");
                Log5780($"[UDP] 接收超时: 1000ms");
                Log5780($"[UDP] 请求地址: {mappingTest3Server}");
                var resultC = await Task.Run(() => StunClient.Query(socket, mappingTest3Server, false, false, 1000), cancellationToken);

                if (resultC != null && resultC.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultC.PublicEndPoint);
                    Log5780($"[UDP] 成功 -> {resultC.PublicEndPoint}");
                }

                else
                {
                    Log5780($"[UDP] 失败: 无响应");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // ============================================================
                // 最终 Mapping 判定 (RFC5780)
                // ============================================================
                Log5780(">>> [UDP] 计算最终 Mapping 行为");
                string mappingType = CalculateMappingType(isDirectMapping, resultA, resultB, resultC);
                txt5780Mapping.Text = mappingType;
                txt5780Mapping.ForeColor = GetMappingColor(mappingType);
                Log5780($"[UDP] Mapping 判定结果: {mappingType}");

                CheckAndMarkIPChange5780();
                cancellationToken.ThrowIfCancellationRequested();



                Log5780($"最终检测结果: \nMapping={mappingType}, \nFiltering={filteringType}", "(完成)");
                CheckAndMarkIPChange5780();

                Log5780($"=== UDP 测试结束 ===", "UDP完成");
            }
            catch (OperationCanceledException)
            {
                Log5780("UDP 测试已被用户取消", "测试取消");
                return;
            }
            finally
            {
                socket?.Close();
                socket?.Dispose();
                _activeSocket5780 = null;
            }
        }
        // 2. 修复后的 TCP 测试流程
        private async Task RunTcpTest5780(IPEndPoint serverEp1, IPAddress selectedLocalIP, AddressFamily testFamily, string protocol, CancellationToken cancellationToken)
        {
            try
            {
                int serverPort = (protocol == "TLS") ? 5349 : 3478;
                if (serverEp1.Port != serverPort) serverEp1 = new IPEndPoint(serverEp1.Address, serverPort);

                Log5780($"[TCP] 目标服务器: {serverEp1}");
                Log5780($"[TCP] 协议: {protocol}, 测试族: {testFamily}");

                int tcpBindPort = GetPortToUse(true);
                Log5780($"[TCP] 获取绑定端口: {tcpBindPort}");

                IPAddress finalBindIp = (selectedLocalIP.Equals(IPAddress.Any) || selectedLocalIP.Equals(IPAddress.IPv6Any))
                                        ? await Task.Run(() => GetLocalRoutingIp(serverEp1), cancellationToken)
                                        : selectedLocalIP;
                Log5780($"[TCP] 最终出口IP: {finalBindIp}");

                IPEndPoint tcpLocalEndPoint = new IPEndPoint(finalBindIp, tcpBindPort);
                combo5780LocalEnd.Text = tcpLocalEndPoint.ToString();
                Log5780($"[TCP] 本地绑定: {tcpLocalEndPoint}");

                // === Mapping Test I ===
                Log5780(">>> [TCP] Mapping Test I: Binding Request", "Mapping Test I");
                var resultA = await StunClient.QueryTcpAsync(serverEp1, false, false, tcpLocalEndPoint, cancellationToken);

                Log5780($"[TCP] TcpClient超时更改为 4000ms");
                if (resultA?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultA.PublicEndPoint);
                    txt5780PublicEnd.Text = resultA.PublicEndPoint.ToString();
                    txt5780Binding.Text = "Success";
                    txt5780Binding.ForeColor = Color.LimeGreen;
                    Log5780($"[TCP] Mapping Test I 成功 -> {resultA.PublicEndPoint}");
                    if (resultA.ChangedEndPoint != null)
                        Log5780($"[TCP] 备用服务器地址: {resultA.ChangedEndPoint}");
                }
                else
                {
                    Log5780($"[TCP] Mapping Test I 失败: 无响应或超时");
                    throw new Exception("Mapping Test I 无响应");
                }

                await Task.Delay(250, cancellationToken);
                Log5780($"[TCP] 等待 250ms 后继续...");

                IPEndPoint currentLocalEndPoint = tcpLocalEndPoint;
                if (resultA.LocalEndPoint != null)
                {
                    currentLocalEndPoint = resultA.LocalEndPoint;
                    Log5780($"[TCP] 实际本地端点: {currentLocalEndPoint}");
                }

                // === Mapping Test II: (otherIP, primaryPort) ===
                Log5780(">>> [TCP] Mapping Test II: otherIP + primaryPort", "Mapping Test II");
                var changedEp = resultA.ChangedEndPoint;
                Log5780($"[TCP] 备用服务器地址: {resultA.ChangedEndPoint}");
                IPEndPoint mappingTest2Server = null;
                IPEndPoint mappingTest3Server = null;
                StunResult resultB = null;
                if (changedEp != null && IsValidServerAddress(serverEp1, changedEp, (m, t) => Log5780(m, t), "RFC5780"))
                {
                    mappingTest2Server = new IPEndPoint(changedEp.Address, serverEp1.Port);
                    mappingTest3Server = changedEp;

                    resultB = await StunClient.QueryTcpAsync(mappingTest2Server, false, false, currentLocalEndPoint, cancellationToken);
                    if (resultB?.PublicEndPoint != null)
                    {
                        RecordEndPoint5780(resultB.PublicEndPoint);
                        Log5780($"[TCP] Mapping Test II 成功 -> {resultB.PublicEndPoint}");
                        if (resultB.LocalEndPoint != null)
                        {
                            currentLocalEndPoint = resultB.LocalEndPoint;
                            Log5780($"[TCP] Test II 实际本地端点: {currentLocalEndPoint}");
                        }
                    }
                    else
                    {
                        Log5780($"[TCP] Mapping Test II 无响应");
                    }
                }
                else
                {
                    txt5780Mapping.Text = "Unsupported Server";
                    txt5780Mapping.ForeColor = Color.DarkOrange;
                    txt5780Filtering.Text = "--";
                    Log5780($"[TCP] 备用地址无效或服务器不支持", "(完成)");
                    return;
                }

                await Task.Delay(250, cancellationToken);

                // === Mapping Test III: (otherIP, otherPort) ===
                Log5780(">>> [TCP] Mapping Test III: otherIP + otherPort", "Mapping Test III");
                StunResult resultC = null;
                if (mappingTest3Server != null)
                {
                    resultC = await StunClient.QueryTcpAsync(mappingTest3Server, false, false, currentLocalEndPoint, cancellationToken);
                }
                if (resultC?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultC.PublicEndPoint);
                    Log5780($"[TCP] Mapping Test III 成功 -> {resultC.PublicEndPoint}");
                    if (resultC.LocalEndPoint != null)
                    {
                        currentLocalEndPoint = resultC.LocalEndPoint;
                        Log5780($"[TCP] Test III 实际本地端点: {currentLocalEndPoint}");
                    }
                }
                else
                {
                    Log5780($"[TCP] Mapping Test III 无响应");
                }

                // === 判定结果 ===
                bool isDirect = resultA.PublicEndPoint.Equals(resultA.LocalEndPoint);
                Log5780($"[TCP] 是否公网: {isDirect}");
                string mappingType = CalculateMappingType(isDirect, resultA, resultB, resultC);
                Log5780($"[TCP] 计算映射行为: {mappingType}");

                txt5780Mapping.Text = mappingType;
                txt5780Mapping.ForeColor = GetMappingColor(mappingType);
                txt5780Filtering.Text = "--"; // TCP跳过过滤测试
                Log5780($"最终 Mapping 结果: {mappingType}");
                CheckAndMarkIPChange5780();
                Log5780($"=== TCP 测试结束 ===", "TCP完成");
            }
            catch (Exception ex)
            {
                Log5780($"[TCP错误] {ex.Message}");
                txt5780Binding.Text = "Fail";
                txt5780Binding.ForeColor = Color.Red;
                txt5780Mapping.Text = "Error";
                txt5780Mapping.ForeColor = Color.Red;
                txt5780Filtering.Text = "";
            }
        }

        private string CalculateMappingType(bool isDirectMapping, StunResult resultA, StunResult resultB, StunResult resultC)
        {
            if (isDirectMapping) return "Direct";
            if (resultA?.PublicEndPoint == null) return "Fail";

            // RFC5780 Mapping:
            // Test II 与 Test I 相同 => Endpoint-Independent
            // Test III 与 Test II 相同 => Address-Dependent
            // 否则 => Address-and-Port-Dependent
            if (resultB?.PublicEndPoint == null) return "Fail";

            if (resultA.PublicEndPoint.Equals(resultB.PublicEndPoint))
            {
                return "Endpoint-Independent";
            }

            if (resultC?.PublicEndPoint == null) return "Fail";

            if (resultC.PublicEndPoint.Equals(resultB.PublicEndPoint))
            {
                return "Address-Dependent";
            }

            return "Address-and-Port-Dependent";
        }

        private string CalculateFilteringType(StunResult filteringII, StunResult filteringIII, IPEndPoint serverEp1, IPEndPoint changedEp)
        {
            // RFC5780 Filtering:
            // Test II(change IP+port) 成功且响应来自 other-address => Endpoint-Independent
            // 否则 Test III(change port) 无响应 => Address-and-Port-Dependent
            // 否则若响应来自 same-IP/different-port => Address-Dependent
            // 其他情况 => Unsupported Server

            if (filteringII?.ResponseEndPoint != null)
            {
                // 验证响应来自 alternate IP（不强制要求端口也匹配，因为部分 NAT 网关可能重写源端口）
                return filteringII.ResponseEndPoint.Address.Equals(changedEp.Address)
                    ? "Endpoint-Independent"
                    : "Unsupported Server";
            }

            if (filteringIII?.ResponseEndPoint == null)
            {
                return "Address-and-Port-Dependent";
            }

            if (filteringIII.ResponseEndPoint.Address.Equals(serverEp1.Address) &&
                filteringIII.ResponseEndPoint.Port != serverEp1.Port)
            {
                return "Address-Dependent";
            }

            return "Unsupported Server";
        }
        // 3. 修复后的 TLS 测试流程 (与 TCP 逻辑一致)
        private async Task RunTlsTest5780(
            IPEndPoint serverEp1,
            IPAddress selectedLocalIP,
            AddressFamily testFamily,
            string protocol,
            CancellationToken cancellationToken,
            string tlsServerName)
        {
            try
            {
                int serverPort = 5349;
                if (serverEp1.Port != serverPort) serverEp1 = new IPEndPoint(serverEp1.Address, serverPort);

                Log5780($"[TLS] 目标服务器: {serverEp1}");
                Log5780($"[TLS] 协议: {protocol}, 测试族: {testFamily}");

                int tlsBindPort = GetPortToUse(true);
                Log5780($"[TLS] 获取绑定端口: {tlsBindPort}");

                IPAddress finalBindIp = (selectedLocalIP.Equals(IPAddress.Any) || selectedLocalIP.Equals(IPAddress.IPv6Any))
                                        ? await Task.Run(() => GetLocalRoutingIp(serverEp1), cancellationToken)
                                        : selectedLocalIP;
                Log5780($"[TLS] 最终出口IP: {finalBindIp}");

                IPEndPoint tlsLocalEndPoint = new IPEndPoint(finalBindIp, tlsBindPort);
                combo5780LocalEnd.Text = tlsLocalEndPoint.ToString();
                Log5780($"[TLS] 本地绑定: {tlsLocalEndPoint}");

                // === Mapping Test I ===
                Log5780(">>> [TLS] Mapping Test I: Binding Request", "Mapping Test I");
                var resultA = await StunClient.QueryTlsAsync(serverEp1, false, false, tlsLocalEndPoint, cancellationToken, tlsServerName);

                Log5780($"[TLS] TcpClient超时更改为 4000ms");
                if (resultA?.PublicEndPoint == null)
                {
                    Log5780($"[TLS] Mapping Test I 失败: 无响应或超时");
                    throw new Exception("Mapping Test I (TLS) 无响应");
                }

                RecordEndPoint5780(resultA.PublicEndPoint);
                txt5780PublicEnd.Text = resultA.PublicEndPoint.ToString();
                txt5780Binding.Text = "Success";
                txt5780Binding.ForeColor = Color.LimeGreen;
                Log5780($"[TLS] Mapping Test I 成功 -> {resultA.PublicEndPoint}");
                if (resultA.ChangedEndPoint != null)
                    Log5780($"[TLS] 备用服务器地址: {resultA.ChangedEndPoint}");

                IPEndPoint currentLocalEndPoint = tlsLocalEndPoint;
                if (resultA.LocalEndPoint != null)
                {
                    currentLocalEndPoint = resultA.LocalEndPoint;
                    Log5780($"[TLS] 实际本地端点: {currentLocalEndPoint}");
                }

                await Task.Delay(250, cancellationToken);
                Log5780($"[TLS] 等待 250ms 后继续...");

                // === Mapping Test II ===
                Log5780(">>> [TLS] Mapping Test II: otherIP + primaryPort", "Mapping Test II");
                var changedEp = resultA.ChangedEndPoint;
                IPEndPoint mappingTest2Server = null;
                IPEndPoint mappingTest3Server = null;
                StunResult resultB = null;
                if (changedEp != null && IsValidServerAddress(serverEp1, changedEp, (m, t) => Log5780(m, t), "RFC5780"))
                {
                    Log5780($"[TLS] 备用服务器地址: {changedEp}");
                    mappingTest2Server = new IPEndPoint(changedEp.Address, serverEp1.Port);
                    mappingTest3Server = changedEp;

                    resultB = await StunClient.QueryTlsAsync(mappingTest2Server, false, false, currentLocalEndPoint, cancellationToken, tlsServerName);
                    if (resultB?.PublicEndPoint != null)
                    {
                        RecordEndPoint5780(resultB.PublicEndPoint);
                        Log5780($"[TLS] Mapping Test II 成功 -> {resultB.PublicEndPoint}");
                        if (resultB.LocalEndPoint != null)
                        {
                            currentLocalEndPoint = resultB.LocalEndPoint;
                            Log5780($"[TLS] Test II 实际本地端点: {currentLocalEndPoint}");
                        }
                    }
                    else
                    {
                        Log5780($"[TLS] Mapping Test II 无响应");
                    }
                }
                else
                {
                    txt5780Mapping.Text = "Unsupported Server";
                    txt5780Mapping.ForeColor = Color.DarkOrange;
                    txt5780Filtering.Text = "--";
                    Log5780($"[TLS] 备用地址无效或服务器不支持", "(完成)");
                    return;
                }

                await Task.Delay(250, cancellationToken);

                // === Mapping Test III ===
                Log5780(">>> [TLS] Mapping Test III: otherIP + otherPort", "Mapping Test III");
                StunResult resultC = null;
                if (mappingTest3Server != null)
                {
                    resultC = await StunClient.QueryTlsAsync(mappingTest3Server, false, false, currentLocalEndPoint, cancellationToken, tlsServerName);
                }
                if (resultC?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultC.PublicEndPoint);
                    Log5780($"[TLS] Mapping Test III 成功 -> {resultC.PublicEndPoint}");
                    if (resultC.LocalEndPoint != null)
                    {
                        currentLocalEndPoint = resultC.LocalEndPoint;
                        Log5780($"[TLS] Test III 实际本地端点: {currentLocalEndPoint}");
                    }
                }
                else
                {
                    Log5780($"[TLS] Mapping Test III 无响应");
                }

                // === 判定结果 ===
                bool isDirect = resultA.PublicEndPoint.Equals(resultA.LocalEndPoint);
                Log5780($"[TLS] 是否公网: {isDirect}");
                string mappingType = CalculateMappingType(isDirect, resultA, resultB, resultC);
                Log5780($"[TLS] 计算映射行为: {mappingType}");

                txt5780Mapping.Text = mappingType;
                txt5780Mapping.ForeColor = GetMappingColor(mappingType);
                txt5780Filtering.Text = "--";
                Log5780($"最终 TLS Mapping 结果: {mappingType}");
                CheckAndMarkIPChange5780();
                Log5780($"=== TLS 测试结束 ===", "TLS完成");
            }
            catch (Exception ex)
            {
                Log5780($"[TLS错误] {ex.Message}");
                txt5780Binding.Text = "Fail";
                txt5780Binding.ForeColor = Color.Red;
                txt5780Mapping.Text = "Error";
                txt5780Mapping.ForeColor = Color.Red;
                txt5780Filtering.Text = "";
            }
        }

        //===========================================================================
        //===========================================================================

        private Color GetMappingColor(string mappingType)
        {
            return (mappingType == "Endpoint-Independent" || mappingType == "Direct")
                ? Color.LimeGreen : (mappingType == "Address-Dependent")
                ? Color.Orange : Color.Red;
        }

        private Color GetFilteringColor(string filteringType)
        {
            if (filteringType == "Unsupported Server") return Color.DarkOrange;
            return filteringType == "Endpoint-Independent" ? Color.LimeGreen :
                   filteringType == "Address-Dependent" ? Color.Orange : Color.Red;
        }
        private async void btnCheck3489_Click(object sender, EventArgs e)
        {
            // 1. 锁定按钮与初始化
            btnCheck3489.Enabled = false;
            combo3489LocalEnd.Enabled = false;
            // 重置IP检测
            ResetIPDetection3489();  // <-- 添加这一行
            string current3489Server = comboServer.Text;
            txt3489Type.ForeColor = ColorTranslator.FromHtml("#8e8cd8");

            // 创建取消令牌
            _cts3489 = new CancellationTokenSource();
            var cancellationToken = _cts3489.Token;

            _stopRequested = false;
            txt3489Debug.Clear();
            txt3489Type.Text = "...";
            txt3489Type.ForeColor = Color.Gray;
            txt3489PublicEnd.Text = "";
            lbl3489StartTime.Text = "开测: " + GetCurrentTime() + " 服务器:" + current3489Server;

            Socket socket = null;
            _activeSocket3489 = null;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 2. 准备服务器
                string serverHost = comboServer.Text.Trim();
                if (string.IsNullOrEmpty(serverHost)) throw new Exception("请选择服务器");

                Log(string.Format("开始时间: " + Others.GetCurrentTime()));
                Log("=== 开始 RFC3489 测试 ===", "测试初始化...");
                Log("正在解析服务器...", "解析服务器 IP...");

                IPAddress[] serverIps = await Task.Run(() => Dns.GetHostAddresses(serverHost), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // 强制选择IPv4，3489测IPV6没什么意义
                IPAddress serverIp = null;
                foreach (var ip in serverIps)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) // 仅选择 IPv4
                    {
                        serverIp = ip;
                        break;
                    }
                }
                Log("RFC3489对IPV6测试不完善, 查询器X只实现了IPV4. \n\n已为你选择服务器IPv4地址. ");
                if (serverIp == null)
                {
                    throw new Exception($"服务器未提供IPv4地址，无法进行 RFC3489 测试");
                }

                IPEndPoint serverEp1 = new IPEndPoint(serverIp, 3478);

                Log($"Server Original Address: {serverEp1}");

                // 检查取消请求
                cancellationToken.ThrowIfCancellationRequested();

                // 3. 智能 IP 和 端口计算逻辑
                EnsureSelectedNICValid(false);
                string inputRaw = combo3489LocalEnd.Text.Trim();
                string ipPartString = inputRaw;
                if (inputRaw.Contains(":"))
                {
                    var parts = inputRaw.Split(':');
                    ipPartString = parts[0];
                }

                IPAddress finalBindIp;

                if (ipPartString.Contains("0.0.0.0") || ipPartString.Contains("Any"))
                {
                    Log("检测系统路由出口...");
                    finalBindIp = await Task.Run(() => GetLocalRoutingIp(serverEp1), cancellationToken);
                    Log(string.Format("检测系统实际出口: {0}", finalBindIp), "路由检测");
                }
                else
                {
                    finalBindIp = IPAddress.Parse(ipPartString.Split(' ')[0]);
                }

                int bindPort = GetPortToUse(false);

                // 绑定 Socket
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _activeSocket3489 = socket; // 保存引用以便Reset时关闭
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                IPEndPoint localBindEp = new IPEndPoint(finalBindIp, bindPort);
                socket.Bind(localBindEp);

                // 4. 更新 UI 显示真实 IP:Port
                combo3489LocalEnd.Text = localBindEp.ToString();
                Log($"Local Bind (Actual): {localBindEp}", "读取当前IP:端口...");

                cancellationToken.ThrowIfCancellationRequested();

                // ============================================================
                // Test 1 (Binding Request)
                // ============================================================
                cancellationToken.ThrowIfCancellationRequested();
                Log(">>> Test 1: 正在发送(Binding Request)...", "Test1 (Binding Request) ");

                var result1 = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StunClient.Query3489(socket, serverEp1, false, false);
                }, cancellationToken);

                if (result1?.PublicEndPoint != null)
                {
                    RecordEndPoint3489(result1.PublicEndPoint);
                }

                if (result1 == null || result1.PublicEndPoint == null)
                {
                    txt3489Type.Text = "UdpBlocked";
                    txt3489Type.ForeColor = Color.Red;
                    Log("Test 1 Failed: 接收超时。", "(完成)");
                    return; // 结束本次测试，不往下跑了
                }
                txt3489PublicEnd.Text = result1.PublicEndPoint.ToString();
                Log($"Test 1 Success. Public: {result1.PublicEndPoint}");
                Log("Test1 (Binding Request) 成功", "(完成)");

                cancellationToken.ThrowIfCancellationRequested();

                // ============================================================
                // 测试服务器是否支持3489测试
                // ============================================================
                cancellationToken.ThrowIfCancellationRequested();
                var changedEp = result1.ChangedEndPoint;
                var primaryRemoteEp = result1.ResponseEndPoint ?? serverEp1;

                // 检查服务器返回的 ChangedAddress 是否有效（使用 Log 委托）
                if (changedEp == null || !IsValidServerAddress(primaryRemoteEp, changedEp,
                    (msg, title) => Log(msg, title), "RFC3489"))
                {
                    txt3489Type.Text = "Unsupported Server";
                    txt3489Type.ForeColor = Color.DarkOrange;

                    Log("Test 2 Failed: 服务器不支持测试。", "(完成)");
                    return;
                }

                Log($"Server Changed Address: {changedEp}");

                bool isDirect = result1.PublicEndPoint.Equals(result1.LocalEndPoint);

                // 如果备用地址端口与原始不同，记录这个信息
                if (changedEp.Port != primaryRemoteEp.Port)
                {
                    Log($"注意：备用服务器端口不同 ({primaryRemoteEp.Port} -> {changedEp.Port})", "端口差异");
                }
                cancellationToken.ThrowIfCancellationRequested();
                // ============================================================
                // Test 2 (Change IP&Port)
                // ============================================================
                cancellationToken.ThrowIfCancellationRequested();
                Log(">>> Test 2: 正在发送 Change IP&Port ...", "Test2 (Symmetric) ");

                var result2 = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StunClient.Query3489(socket, serverEp1, true, true);
                }, cancellationToken);

                if (result2 != null)
                {
                    // Test2 响应必须来自 changed 地址 (不同 IP 且不同端口)，否则视为不支持的服务器
                    if (result2.ResponseEndPoint != null && !result2.ResponseEndPoint.Equals(changedEp))
                    {
                        txt3489Type.Text = "Unsupported Server";
                        txt3489Type.ForeColor = Color.DarkOrange;
                        Log($"Test 2 响应来源异常（期望 {changedEp}，实际 {result2.ResponseEndPoint}），判定为 Unsupported Server。", "(完成)");
                        return;
                    }

                    if (result2.PublicEndPoint != null)
                    {
                        RecordEndPoint3489(result2.PublicEndPoint);
                        txt3489PublicEnd.Text = result2.PublicEndPoint.ToString();
                    }

                    if (isDirect)
                    {
                        txt3489Type.Text = "OpenInternet";
                        txt3489Type.ForeColor = Color.LimeGreen;
                        Log("Test 2 Success: 公网直连且收到 Change IP&Port 响应。", "(完成)");
                        Log("检测结束: OpenInternet");
                        return;
                    }

                    txt3489Type.Text = "FullCone";
                    txt3489Type.ForeColor = Color.LimeGreen;
                    Log("Test 2 Success: NAT 环境下收到 Change IP&Port 响应。", "(完成)");
                    Log("检测结束: FullCone (全锥型)");
                    return;
                }

                if (isDirect)
                {
                    txt3489Type.Text = "SymmetricUdpFirewall";
                    txt3489Type.ForeColor = Color.OrangeRed;
                    Log("Test 2 Failed: 公网直连但未收到 Change IP&Port 响应。", "(完成)");
                    Log("检测结束: SymmetricUdpFirewall");
                    return;
                }
                Log("Test 2 Failed: 未收到回复 (非 FullCone，继续 Test1#2)。", "(完成)");
                cancellationToken.ThrowIfCancellationRequested();
                // ============================================================
                // Test 1#2 (Send to changed address)
                // ============================================================
                cancellationToken.ThrowIfCancellationRequested();
                Log(">>> Test 1#2: 正在检测 Symmetric (Server IP Change)...", "Test1#2 (Symmetric) ");

                if (result1.ChangedEndPoint != null)
                {
                    // RFC 3489 Section 10.1: Test I#2 must send to alternate IP + primary port
                    IPEndPoint testI2Server = new IPEndPoint(result1.ChangedEndPoint.Address, serverEp1.Port);
                    Log($"Test 1#2 目标: {testI2Server} (alternateIP + primaryPort)");
                    var result3 = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return StunClient.Query3489(socket, testI2Server, false, false);
                    }, cancellationToken);

                    if (result3 == null || result3.PublicEndPoint == null)
                    {
                        txt3489Type.Text = "Unknown";
                        txt3489Type.ForeColor = Color.DarkOrange;
                        Log("Test 1#2 Failed: 备用地址无响应，判定 Unknown。", "(完成)");
                        return;
                    }

                    Log($"Test 1#2 Public: {result3.PublicEndPoint}");
                    RecordEndPoint3489(result3.PublicEndPoint);

                    if (!result3.PublicEndPoint.Equals(result1.PublicEndPoint))
                    {
                        txt3489Type.Text = "Symmetric";
                        txt3489Type.ForeColor = Color.Red;
                        Log($"映射地址改变! \r\n[{result1.PublicEndPoint}] -> [{result3.PublicEndPoint}]");
                        Log("检测结束: Symmetric (对称型)", "(完成)");
                        return;
                    }
                }
                Log(null, "(完成)");

                cancellationToken.ThrowIfCancellationRequested();
                // ============================================================
                // Test 4 (Restricted Type)
                // ============================================================
                cancellationToken.ThrowIfCancellationRequested();
                Log(">>> Test 4: 正在判断 Restricted / Port Restricted...", "Test4 (Restricted) ");

                var result4 = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StunClient.Query3489(socket, serverEp1, false, true);
                }, cancellationToken);

                if (result4 != null &&
                    result4.PublicEndPoint != null &&
                    result4.ResponseEndPoint != null &&
                    result4.ResponseEndPoint.Address.Equals(primaryRemoteEp.Address) &&
                    result4.ResponseEndPoint.Port != primaryRemoteEp.Port)
                {
                    txt3489Type.Text = "RestrictedCone";
                    txt3489Type.ForeColor = Color.Orange;
                    Log("Test 4 Success: 收到回复 (仅限制IP)。");
                    Log("检测结束: Restricted Cone (地址限制型, NAT2)", "(完成)");
                }
                else
                {
                    txt3489Type.Text = "PortRestrictedCone";
                    txt3489Type.ForeColor = Color.DarkOrange;
                    Log("Test 4 Failed: 未收到回复 (限制IP和端口)。");
                    Log("检测结束: Port Restricted Cone (端口限制型, NAT3)", "(完成)");
                }

            }
            catch (OperationCanceledException)
            {
                return;//Log("测试已被用户取消", "测试取消");
            }
            catch (Exception ex)
            {
                Log($"[Error] {ex.Message}");
                if (!cancellationToken.IsCancellationRequested)
                {
                    MessageBox.Show("测试出错: " + ex.Message);
                    Log(null, $"测试出错: {ex.Message}");
                }
            }
            finally
            {
                // 在最后返回之前检查IP变化
                CheckAndMarkIPChange3489();  // <-- 添加这一行
                if (socket != null)
                {
                    try
                    {
                        socket.Close();
                        socket.Dispose();
                    }
                    catch { }
                }
                _activeSocket3489 = null;

                // 只有在测试正常完成或出错时才恢复按钮状态
                if (!cancellationToken.IsCancellationRequested)
                {
                    btnCheck3489.Enabled = true;
                    combo3489LocalEnd.Enabled = true;
                    if (!_stopRequested && !lbl3489.Text.Contains("结束"))
                    {
                        lbl3489.Text = "RFC3489 (完成)";
                    }
                }
            }
        }


        private async void btnReset_Click(object sender, EventArgs e)//重置，用两次
        {
            await PerformResetAsync();
            await PerformResetAsync();
            LoadLocalIPs();
        }
        private async Task PerformResetAsync()  // 改为异步方法
        {
            // 1. 设置停止标志，阻止所有后续操作
            _stopRequested = true;

            // 2. 禁用UI按钮，防止重复点击
            btnCheck3489.Enabled = false;
            btnCheck5780.Enabled = false;
            btnReset.Enabled = false;

            // 3. 取消所有测试任务
            if (_cts3489 != null)
            {
                _cts3489.Cancel();
                //await Task.Delay(200);
                _cts3489.Dispose();
                _cts3489 = null;
            }

            if (_cts5780 != null)
            {
                _cts5780.Cancel();
                //await Task.Delay(150);
                _cts5780.Dispose();
                _cts5780 = null;
            }

            // 4. 暴力关闭活动的 Socket
            try
            {
                _activeSocket3489?.Close();
                _activeSocket3489?.Dispose();
                _activeSocket3489 = null;
            }
            catch { }

            try
            {
                _activeSocket5780?.Close();
                _activeSocket5780?.Dispose();
                _activeSocket5780 = null;
            }
            catch { }

            // 5. 等待一小段时间确保所有异步操作完成
            await Task.Delay(50);  // 给TCP/TLS连接足够时间关闭

            // 6. 重置内部逻辑标志和端口记忆
            _lastPort3489 = 0;
            _lastPort5780 = 0;

            // 9. 重新启用按钮
            btnReset.Enabled = true;

            // 10. 清除停止标志
            _stopRequested = false;

            // 强制垃圾回收，释放所有网络资源
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // 7. 重置 UI 状态
            ResetUIState();

            // 8. 重新应用主题
            await ApplyNATThemeAsync();
        }
        // 改进后的 UI 重置方法：考虑主题颜色
        private void ResetUIState()
        {
            bool isLight = Global.isThemelight;
            // 获取当前模式下应该显示的默认文字颜色
            Color defaultTextColor = isLight ? Color.Black : Color.White;
            // 夢酱最喜欢的二次元紫，用于某些提示性文字
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");

            // 重置IP检测
            ResetIPDetection5780();
            ResetIPDetection3489();

            // 3489 部分
            txt3489Type.Text = "--";
            txt3489Type.ForeColor = defaultTextColor; // 还原为基础色
            txt3489PublicEnd.Text = "--";
            txt3489PublicEnd.ForeColor = defaultTextColor;
            txt3489Debug.Text = "DeBugRFC3489 等待中";
            lbl3489StartTime.Text = "开测时间";
            lbl3489.Text = "RFC3489";

            // 5780 部分
            txt5780Mapping.Text = "--";
            txt5780Mapping.ForeColor = defaultTextColor;
            txt5780Filtering.Text = "--";
            txt5780Filtering.ForeColor = defaultTextColor;
            txt5780PublicEnd.Text = "--";
            txt5780PublicEnd.ForeColor = defaultTextColor;
            txt5780Binding.Text = "--";
            txt5780Binding.ForeColor = defaultTextColor;
            txt5780Debug.Text = "DeBugRFC5780 等待中";
            lbl5780StartTime.Text = "开测时间";
            lbl5780.Text = "RFC5780";

            // 下拉框恢复（去除端口号，只留 IP）
            // 这里夢酱之前的逻辑是对的，我们保留
            if (combo3489LocalEnd.Text.Contains(":"))
            {
                string[] parts = combo3489LocalEnd.Text.Split(':');
                // 兼容 [IPv6]:Port 格式
                if (combo3489LocalEnd.Text.Contains("]"))
                    combo3489LocalEnd.Text = parts[0] + ":" + parts[1].Split(' ')[0];
                else
                    combo3489LocalEnd.Text = parts[0].Split(' ')[0];
            }

            // 恢复所有交互按钮
            btnCheck3489.Enabled = true;
            btnCheck5780.Enabled = true;
            combo5780LocalEnd.Enabled = true;
            combo3489LocalEnd.Enabled = true;
            radioTCP.Enabled = true;
            radioUDP.Enabled = true;
            radioTLS.Enabled = true;
        }

        private void checkPortRandom_CheckedChanged(object sender, EventArgs e)//连续模式设置
        {
            if (checkPortRandom.Checked == true)
            {
                checkPortRandom.Text = "连续更换 (连续测试时, 按下面设置换一个端口)";
            }
            else
            {
                checkPortRandom.Text = "连续固定 (连续测试不换端口, 重置/手动设置更换)";
            }
        }

        private void checkPortMode_CheckedChanged(object sender, EventArgs e)//随机端口设置
        { //
            if (checkPortMode.Checked == true)
            {
                checkPortMode.Text = "随机端口 (每次开测都随机一个新端口)";
            }
            else
            {
                checkPortMode.Text = "顺序端口 (每次开测用上次的端口号+1)";
            }
        }

        private void checkPortRange_CheckedChanged(object sender, EventArgs e)//范围设置
        {//
            if (checkPortRange.Checked == true)
            {
                checkPortRange.Text = "推荐范围 (49152-65535) 按规范范围, 尽量避免占用";
            }
            else
            {
                checkPortRange.Text = "完全范围 (1-65535) 不考虑占用, 测就完事了";
            }
        }

        private void NATTest_SizeChanged(object sender, EventArgs e)
        {

        }

        private void btnSettings_Click(object sender, EventArgs e)
        {
            float dpiScale = GetDpiScale();
            int normalWidth = (int)(432 * dpiScale);
            int expandedWidth = (int)(778 * dpiScale);
            int threshold = (normalWidth + expandedWidth) / 2;

            if (this.Width < threshold)
            {
                btnSettings.Text = "隐藏";
                this.Width = expandedWidth;
                this.Text = "NAT类型测试 ✦ NetInfoCheckerX ✧ 设置将在关闭窗口时自动记忆";
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.MinimumSize = this.Size;
                // 显示调试框
                txt5780Debug.Visible = true;
                txt3489Debug.Visible = true;
            }
            else
            {
                btnSettings.Text = "设置?";
                this.MinimumSize = new Size(this.Height, normalWidth);
                this.Width = normalWidth;
                this.Text = "NAT类型测试 ✧ NetInfoCheckerX";
                this.FormBorderStyle = FormBorderStyle.FixedSingle;
                // 隐藏调试框
                txt5780Debug.Visible = false;
                txt3489Debug.Visible = false;
            }
            CloudControl.ApplyDevTitle(this);
        }

        // 获取当前窗体的DPI缩放比例
        private float GetDpiScale()
        {
            using (Graphics g = this.CreateGraphics())
            {
                return g.DpiX / 96f; // 96是100%缩放的基准DPI
            }
        }

        private void txt5780Binding_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true; // 阻止所有键盘输入
        }

        private void txt5780Mapping_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true; // 阻止所有键盘输入
        }

        private void txt5780Filtering_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true; // 阻止所有键盘输入
        }

        private void txt5780PublicEnd_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true; // 阻止所有键盘输入
        }

        private void txt3489Type_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true; // 阻止所有键盘输入
        }

        private void txt3489PublicEnd_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true; // 阻止所有键盘输入
        }

        private void btnRFCCompare_Click(object sender, EventArgs e)
        {
            NATRFCCompare secondForm = new NATRFCCompare();
            secondForm.Show();
        }

        private void btnTrace_Click(object sender, EventArgs e)
        {
            Trace secondForm = new Trace();
            secondForm.Show();
        }

        // 在 NATTest 类内部的最下方添加这两个系统调用
        [DllImport("kernel32")]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);
        [DllImport("kernel32")]
        private static extern int GetPrivateProfileString(string section, string key, string def, System.Text.StringBuilder retVal, int size, string filePath);

        // 程序旁边的 ini 文件完整路径
        private string iniPath = System.IO.Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

        private void LoadCheckStates()
        {
            // 准备一个“小篮子”来装读取到的字符串
            System.Text.StringBuilder temp = new System.Text.StringBuilder(255);

            // 定义一个简单的内部读取小方法，方便复用
            string ReadIni(string key)
            {
                GetPrivateProfileString("NATTest", key, "true", temp, 255, iniPath);
                return temp.ToString().ToLower();
            }

            // 设置勾选框状态
            checkPortRandom.Checked = ReadIni("checkPortRandom") == "true";
            checkPortMode.Checked = ReadIni("checkPortMode") == "true";
            checkPortRange.Checked = ReadIni("checkPortRange") == "true";

            // 触发一下 CheckedChanged 事件，确保按钮上的文字也被更新
            checkPortRandom_CheckedChanged(null, null);
            checkPortMode_CheckedChanged(null, null);
            checkPortRange_CheckedChanged(null, null);
        }
        private void NATTest_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // 写入配置项
                WritePrivateProfileString("NATTest", "checkPortRandom", checkPortRandom.Checked.ToString().ToLower(), iniPath);
                WritePrivateProfileString("NATTest", "checkPortMode", checkPortMode.Checked.ToString().ToLower(), iniPath);
                WritePrivateProfileString("NATTest", "checkPortRange", checkPortRange.Checked.ToString().ToLower(), iniPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"记录当前NAT测试设置失败，但问题不大，\n下次打开NAT测试时会自动使用默认设置喵。\n错误信息：{ex.Message}", "别急");
            }
        }

        // 辅助方法：判断是否为内网IP地址
        private bool IsPrivateIP(IPAddress ip)
        {
            if (ip == null) return false;

            // IPv4 内网地址检查
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = ip.GetAddressBytes();

                // 10.0.0.0/8
                if (bytes[0] == 10) return true;

                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;

                // 127.0.0.0/8 (回环地址)
                if (bytes[0] == 127) return true;

                // 169.254.0.0/16 (链路本地)
                if (bytes[0] == 169 && bytes[1] == 254) return true;

                return false;
            }
            // IPv6 内网地址检查
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // ::1 (IPv6回环)
                if (ip.Equals(IPAddress.IPv6Loopback)) return true;

                // fe80::/10 (链路本地地址)
                if (ip.IsIPv6LinkLocal) return true;

                // fc00::/7 (唯一本地地址 ULA)
                byte[] bytes = ip.GetAddressBytes();
                if (bytes[0] == 0xFC || bytes[0] == 0xFD) return true;

                // fec0::/10 (站点本地地址 - 已废弃，但仍可能是内网)
                if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) return true;

                return false;
            }

            return false;
        }

        // 添加获取IP类型描述的辅助方法
        private string GetIPType(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = ip.GetAddressBytes();

                if (bytes[0] == 10) return "10.0.0.0/8 (私有A类)";
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return "172.16.0.0/12 (私有B类)";
                if (bytes[0] == 192 && bytes[1] == 168) return "192.168.0.0/16 (私有C类)";
                if (bytes[0] == 127) return "127.0.0.0/8 (回环地址)";
                if (bytes[0] == 169 && bytes[1] == 254) return "169.254.0.0/16 (链路本地)";

                return "公网IPv4";
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Loopback)) return "::1 (IPv6回环)";
                if (ip.IsIPv6LinkLocal) return "fe80::/10 (链路本地)";

                byte[] bytes = ip.GetAddressBytes();
                if (bytes[0] == 0xFC || bytes[0] == 0xFD) return "fc00::/7 (唯一本地地址)";
                if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) return "fec0::/10 (站点本地-已废弃)";

                return "公网IPv6";
            }

            return "未知类型";
        }

        // 辅助方法：检查服务器地址是否有效
        private bool IsValidServerAddress(IPEndPoint originalEp, IPEndPoint changedEp, Action<string, string> logger, string testType = "RFC3489")
        {
            // 1. ChangedAddress 不能为 null
            if (changedEp == null)
            {
                logger?.Invoke($"错误：服务器未提供 {testType} 所需备用地址", "服务器不支持");
                return false;
            }

            // 2. ChangedAddress 的 IP 不能与原始地址的 IP 相同
            if (changedEp.Address.Equals(originalEp.Address))
            {
                logger?.Invoke($"错误：服务器提供的备用地址与原始地址相同: \n{originalEp} → {changedEp}", "服务器配置错误");
                return false;
            }

            // 3. ChangedAddress 不能与原始地址完全相同（IP和端口都相同）
            if (changedEp.Equals(originalEp))
            {
                logger?.Invoke($"错误：服务器提供的备用地址与原始地址相同: \n{originalEp} → {changedEp}", "服务器配置错误");
                return false;
            }

            // 4. ChangedAddress 的 IP 不能是内网地址
            if (IsPrivateIP(changedEp.Address))
            {
                logger?.Invoke($"错误：服务器提供的备用地址是内网地址 ({GetIPType(changedEp.Address)}): {changedEp.Address}", "服务器配置错误");
                return false;
            }

            // 5. 原始地址也不应该是内网地址（警告）
            if (IsPrivateIP(originalEp.Address))
            {
                logger?.Invoke($"警告：原始服务器地址可能是内网地址 ({GetIPType(originalEp.Address)}): {originalEp.Address}", "服务器配置错误");
            }

            bool requireDifferentPort = testType.Equals("RFC5780", StringComparison.OrdinalIgnoreCase) ||
                                        testType.Equals("RFC5389", StringComparison.OrdinalIgnoreCase) ||
                                        testType.Equals("RFC3489", StringComparison.OrdinalIgnoreCase);

            // 6. RFC5780 的 OTHER-ADDRESS 需要不同端口
            if (requireDifferentPort && changedEp.Port == originalEp.Port)
            {
                logger?.Invoke($"错误：{testType} 备用地址端口必须不同: {originalEp.Port} == {changedEp.Port}", "服务器配置错误");
                return false;
            }

            // 7. 记录端口差异信息
            if (changedEp.Port != originalEp.Port)
            {
                logger?.Invoke($"提示：备用服务器使用不同端口 ({originalEp.Port} → {changedEp.Port})", "");
            }

            // 8. 服务器配置正确，可以开始测试
            logger?.Invoke($"服务器配置正确: {originalEp} → {changedEp}", "");
            return true;
        }

        // 在NATTest类中添加以下辅助方法
        // 检查并标记 RFC5780 的 IP 变化
        private void CheckAndMarkIPChange5780()
        {
            // 梦酱看这里：我们要先统计到底出现了多少个不同的 IP
            List<IPAddress> uniqueIPs = new List<IPAddress>();
            foreach (var ep in _publicEndPoints5780)
            {
                if (!uniqueIPs.Contains(ep.Address))
                {
                    uniqueIPs.Add(ep.Address);
                }
            }

            // 只有当检测到不同的 IP 地址超过 1 个时，才标记橙色
            if (uniqueIPs.Count > 1)
            {
                StringBuilder tipText = new StringBuilder();
                tipText.AppendLine("公网IP变动, 测试结果不可靠! [设置?]查看debug详情");
                for (int i = 0; i < _publicEndPoints5780.Count; i++)
                {
                    tipText.AppendLine($" → PublicEnd{i + 1}: {_publicEndPoints5780[i]}");
                }

                string currentText = txt5780PublicEnd.Text;
                if (!currentText.StartsWith("[!]"))
                {
                    txt5780PublicEnd.Text = "[!]" + currentText;
                }
                txt5780PublicEnd.ForeColor = Color.DarkOrange;
                toolTip1.SetToolTip(txt5780PublicEnd, tipText.ToString());
            }
        }
        // 检查并标记 RFC3489 的 IP 变化
        private void CheckAndMarkIPChange3489()
        {
            // 同样的操作，只提取不同的 IP 地址
            List<IPAddress> uniqueIPs = new List<IPAddress>();
            foreach (var ep in _publicEndPoints3489)
            {
                if (!uniqueIPs.Contains(ep.Address))
                {
                    uniqueIPs.Add(ep.Address);
                }
            }

            // 只有 IP 变了才标橙色，单纯端口变了就放过它~
            if (uniqueIPs.Count > 1)
            {
                StringBuilder tipText = new StringBuilder();
                tipText.AppendLine("公网IP变动, 测试结果不可靠! [设置?]查看debug详情");
                for (int i = 0; i < _publicEndPoints3489.Count; i++)
                {
                    tipText.AppendLine($" → PublicEnd{i + 1}: {_publicEndPoints3489[i]}");
                }

                string currentText = txt3489PublicEnd.Text;
                if (!currentText.StartsWith("[!]"))
                {
                    txt3489PublicEnd.Text = "[!]" + currentText;
                }
                txt3489PublicEnd.ForeColor = Color.DarkOrange;
                toolTip1.SetToolTip(txt3489PublicEnd, tipText.ToString());
            }
        }

        // 重置IP记录和UI状态
        private void ResetIPDetection5780()
        {
            _publicEndPoints5780.Clear();

            // 恢复文本框状态
            if (txt5780PublicEnd.Text.StartsWith("[!]"))
            {
                txt5780PublicEnd.Text = txt5780PublicEnd.Text.Substring(4);
            }

            // 根据主题恢复颜色
            bool isLight = Global.isThemelight;
            txt5780PublicEnd.ForeColor = isLight ? Color.Black : Color.White;

            // 清除ToolTip
            toolTip1.SetToolTip(txt5780PublicEnd, null);
        }

        private void ResetIPDetection3489()
        {
            _publicEndPoints3489.Clear();

            // 恢复文本框状态
            if (txt3489PublicEnd.Text.StartsWith("[!]"))
            {
                txt3489PublicEnd.Text = txt3489PublicEnd.Text.Substring(4);
            }

            // 根据主题恢复颜色
            bool isLight = Global.isThemelight;
            txt3489PublicEnd.ForeColor = isLight ? Color.Black : Color.White;

            // 清除ToolTip
            toolTip1.SetToolTip(txt3489PublicEnd, null);
        }

        // 辅助方法：记录完整的IPEndPoint
        private void RecordEndPoint5780(IPEndPoint endPoint)
        {
            if (endPoint == null || endPoint.Address == null) return;

            // 只记录不重复的端点（比较IP和端口）
            bool exists = false;
            foreach (var existingEndPoint in _publicEndPoints5780)
            {
                if (existingEndPoint.Equals(endPoint))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                _publicEndPoints5780.Add(endPoint);
            }
        }

        private void RecordEndPoint3489(IPEndPoint endPoint)
        {
            if (endPoint == null || endPoint.Address == null) return;

            // 只记录不重复的端点（比较IP和端口）
            bool exists = false;
            foreach (var existingEndPoint in _publicEndPoints3489)
            {
                if (existingEndPoint.Equals(endPoint))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                _publicEndPoints3489.Add(endPoint);
            }
        }

        private async void comboServer_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnCheck5780.PerformClick();
                await Task.Delay(1);
                btnCheck3489.PerformClick();
            }
        }

    }
}
