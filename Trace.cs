using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IP2Region.Net.XDB;


namespace NetInfoCheckerX
{
    public partial class Trace : Form
    {
        private CancellationTokenSource cts; // 取消令牌
        private bool isRunning = false;      // 运行状态标识
        private Random random = new Random();

        // 防火墙逻辑变量
        private bool isManualChanged = false;     // 标记本次运行是否手动改过状态
        private bool initialFirewallOn;           // 记录进入窗口时的初始防火墙状态
        private bool initialRuleExisted;          // 记录进入窗口时的初始规则状态
        private string ruleName = "NICX_ICMP_Unlock";
        private System.Windows.Forms.Timer flashTimer; // 闪烁计时器

        // 增加两个布尔值，用于缓存状态，避免重复弹窗/查询
        private bool _lastFwStatus;
        private bool _lastRuleStatus;

        // 新增：当前窗口的唯一标识符，防止多开窗口时串扰
        private ushort _instanceIdentifier;
        private ConcurrentDictionary<string, string> _geoCache = new ConcurrentDictionary<string, string>();
        private int _activeGeoOnlineIndex = 0;
        private const int GeoOnlineTimeoutMs = 3000;
        private SemaphoreSlim _geoEnrichSemaphore = new SemaphoreSlim(3, 3);
        private ConcurrentDictionary<string, byte> _enrichPending = new ConcurrentDictionary<string, byte>();
        private Dictionary<int, string> _hopGeoOriginal = new Dictionary<int, string>();
        private Dictionary<string, int> _ipToHop = new Dictionary<string, int>();


        // INI 读写
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;

        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "Trace";

        // ip2region v4 搜索器（仅 IPv4）
        private Searcher _ip2regionSearcherV4;
        private Searcher _ip2regionSearcherV6;

        public Trace()
        {
            InitializeComponent();
            this.MinimumSize = this.Size;
            // 初始化一个随机的标识符 (使用时间戳和随机数混合)
            _instanceIdentifier = (ushort)(DateTime.Now.Ticks % 60000 + new Random().Next(100, 5000));
        }

        private void SaveSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(comboTargetIP.Text))
                    WritePrivateProfileString(IniSection, "TargetIP", comboTargetIP.Text, IniPath);
                WritePrivateProfileString(IniSection, "Hops", txtHops.Text, IniPath);
                WritePrivateProfileString(IniSection, "Delay", txtDelay.Text, IniPath);
                WritePrivateProfileString(IniSection, "Port", txtTargetPort.Text, IniPath);
                WritePrivateProfileString(IniSection, "GEO", checkGEO.Checked.ToString().ToLower(), IniPath);
                WritePrivateProfileString(IniSection, "MTR", checkMTR.Checked.ToString().ToLower(), IniPath);
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
                GetPrivateProfileString(IniSection, "TargetIP", "", sb, sb.Capacity, IniPath);
                string target = sb.ToString();
                if (!string.IsNullOrEmpty(target) && comboTargetIP.Items.Count > 0)
                {
                    int idx = -1;
                    for (int i = 0; i < comboTargetIP.Items.Count; i++)
                        if (comboTargetIP.Items[i].ToString() == target) { idx = i; break; }
                    if (idx >= 0) comboTargetIP.SelectedIndex = idx;
                    else comboTargetIP.Text = target;
                }
                string val;
                GetPrivateProfileString(IniSection, "Hops", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtHops.Text = val;
                GetPrivateProfileString(IniSection, "Delay", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtDelay.Text = val;
                GetPrivateProfileString(IniSection, "Port", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtTargetPort.Text = val;
                GetPrivateProfileString(IniSection, "GEO", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkGEO.Checked = val.ToLower() == "true";
                GetPrivateProfileString(IniSection, "MTR", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkMTR.Checked = val.ToLower() == "true";
                GetPrivateProfileString(IniSection, "Protocol", "", sb, sb.Capacity, IniPath);
                string proto = sb.ToString();
                if (proto == "TCP") radioTCP.Checked = true;
                else if (proto == "UDP") radioUDP.Checked = true;
                else if (proto == "ICMP") radioICMP.Checked = true;
            }
            catch { }
        }

        // 修改后的 UpdateWDFUI
        private void UpdateWDFUI(bool useCache = false)
        {
            if (flashTimer != null) flashTimer.Stop();
            btnWDF.Font = new Font(btnWDF.Font, FontStyle.Regular);

            // 如果是初始化加载，直接用缓存；如果是点击后刷新，再实时查
            bool isFirewallOn = useCache ? _lastFwStatus : IsFirewallEnabled();
            bool hasRule = useCache ? _lastRuleStatus : IsICMPRuleExisted();

            if (!isFirewallOn)
            {
                btnWDF.Text = "防火关";
                btnWDF.ForeColor = Color.White;
            }
            else if (hasRule)
            {
                btnWDF.Text = "已放行";
                btnWDF.ForeColor = Color.Lime;
            }
            else
            {
                btnWDF.Text = "防火开";
                btnWDF.ForeColor = Color.Yellow;
                StartBtnFlash();
            }
            UpdateWindowTitleStatus();
        }
        private void UpdateWindowTitleStatus()
        {
            // 获取防火墙简要状态
            bool isOn = IsFirewallEnabled();
            bool hasRule = IsICMPRuleExisted();
            string wdfStatus = !isOn ? "防火关" : (hasRule ? "已放行" : "防火开");

            // 更新窗口标题文字
            this.Text = $"Trace+ ✧ NetInfoCheckerX | 权限:{Global.UACLevel} {wdfStatus}";
        }

        // 开启闪烁效果的方法
        private void StartBtnFlash()
        {
            if (flashTimer == null)
            {
                flashTimer = new System.Windows.Forms.Timer();
                flashTimer.Interval = 500;
                flashTimer.Tick += (s, e) =>
                {
                    if (btnWDF.IsDisposed) return;
                    // 来回切换粗体和常规体
                    btnWDF.Font = new Font(btnWDF.Font, btnWDF.Font.Bold ? FontStyle.Regular : FontStyle.Bold);
                };
            }
            flashTimer.Start();
        }
        private bool IsFirewallEnabled()
        {
            // 1. 获取输出（依然保留梦酱之前的双编码逻辑）
            string output = GetNetshOutput("advfirewall show allprofiles state", Encoding.UTF8);
            if (!IsOutputValid(output))
                output = GetNetshOutput("advfirewall show allprofiles state", Encoding.GetEncoding(936));

            // 2. 按照夢酱的思路：切除前三行
            // StringSplitOptions.None 保留空行，确保行数计算准确
            string[] lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // 如果行数够多，我们就跳过前3行，把剩下的拼回来
            string cleanOutput = "";
            if (lines.Length > 4)
            {
                cleanOutput = string.Join("\n", lines.Skip(4));
            }
            else
            {
                cleanOutput = output; // 行数太少就不切了
            }

            // 调试用（确认切完后是什么）：
            //MessageBox.Show("清理后的内容：\n" + cleanOutput);

            // 3. 在清理后的内容里进行比对
            return cleanOutput.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cleanOutput.Contains("启用") ||
                   cleanOutput.Contains("开启");
        }

        private bool IsICMPRuleExisted()
        {
            try
            {
                // 1. 同样先走 UTF-8 尝试
                string output = GetNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"", Encoding.UTF8);

                // 2. 如果 UTF-8 拿到的东西看起来完全不对（比如连 ruleName 都没有，或者全是问号）
                // 我们尝试 GB2312
                if (!output.Contains(ruleName))
                {
                    string legacyOutput = GetNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"", Encoding.GetEncoding(936));
                    if (legacyOutput.Contains(ruleName)) output = legacyOutput;
                }

                // 4. 调试输出（梦酱测试完可以关掉）
                //MessageBox.Show("规则内容：\n" + output);

                // 只要清理后的内容里包含我们的规则名英文，就认为存在！
                return output.Contains(ruleName);
            }
            catch { return false; }
        }

        // 辅助：判断输出是否有效（包含防火墙特有的状态词）
        private bool IsOutputValid(string text)
        {
            string[] keywords = { "ON", "OFF", "启用", "禁用", "开启", "关闭", "State", "状态" };
            return keywords.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // 辅助：统一获取命令行输出
        private string GetNetshOutput(string args, Encoding enc)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = enc
                };
                using (Process p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd();
                    // 如果返回的是空的或者是提示“未找到”，尝试用默认编码再扫一遍
                    if (string.IsNullOrWhiteSpace(s)) return "";
                    return s;
                }
            }
            catch { return ""; }
        }

        private async Task RunNetshCmd(string args)
        {
            await Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("netsh", args)
                    {
                        Verb = "runas",
                        CreateNoWindow = true,
                        UseShellExecute = true
                    };
                    Process.Start(psi)?.WaitForExit();
                }
                catch { }
            });
        }
        // 自动刷新网卡：当系统网卡变化导致选中网卡不存在时，刷新列表并恢复默认
        private void EnsureSelectedNICValid()
        {
            string selectedText = comboLocalEnd.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.Contains("系统默认") ||
                selectedText.Contains("ICMP兼容模式") || selectedText.StartsWith("0.0.0.0") ||
                selectedText.StartsWith("::")) return;

            // 刷新网卡列表（仅IPv4，与原有过滤逻辑一致）
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses(includeIPv6: false))
                {
                    comboLocalEnd.Items.Add(nicAddress.DisplayText);
                }
            }
            catch { }

            // 尝试恢复原选中项
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

        private async void Trace_Load(object sender, EventArgs e)
        {
            // 1. 先做那些”秒开”的基础 UI 初始化
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;
            // 检查依赖文件
            _ = CheckIpSearcherDependencies();
            // --- 字体优化逻辑开始 ---
            using (Graphics g = this.CreateGraphics())
            {
                // 96 DPI 是 Windows 的标准 100% 缩放
                // 如果大于 96，说明缩放比例超过了 100%
                if (g.DpiX > 96)
                {
                    // 定义夢酱喜欢的现代感字体
                    // 微软雅黑适合中文，Segoe UI 适合英文数字，Consolas 或者 Cascadia Mono，C# 会自动回退匹配
                    Font modernFont = new Font("Cascadia Mono", 9F, FontStyle.Regular);

                    // 应用到文本框和下拉框
                    richTextBox1.Font = modernFont;
                }
                else
                {
                    // 100% 缩放时保持默认，或者显式指定为新宋体
                    //richTextBox1.Font = new Font("NSimSun", 10.5F, FontStyle.Regular);
                }
            }
            AppendColorText("✧ 正在检查系统环境，请稍候... ✧\n", Color.White, true);

            // 2. 后台初始化 IP 数据库和防火墙状态
            await Task.Run(() =>
            {
                InitIp2Region();
                initialFirewallOn = _lastFwStatus = IsFirewallEnabled();
                initialRuleExisted = _lastRuleStatus = IsICMPRuleExisted();
            });

            // 3. UI 线程：填充网卡列表
            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses(includeIPv6: false))
                {
                    comboLocalEnd.Items.Add(nicAddress.DisplayText);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取网卡列表失败: " + ex.Message);
            }

            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;

            radioICMP.CheckedChanged += UpdateProtocolTip;
            radioTCP.CheckedChanged += UpdateProtocolTip;
            radioUDP.CheckedChanged += UpdateProtocolTip;

            // 3. 后台干完活了，回到 UI 线程更新界面
            UpdateWDFUI(true);
            UpdateProtocolTip(null, null); // 刷新协议提示文字
            // 开发调试服务器列表
            CloudControl.LoadTraceServers(comboTargetIP);
            CloudControl.ApplyDevTitle(this);
            LoadSettings();
            CloudControl.UsedTimesCounter("TracePP");
        }
        private void InitIp2Region()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string v4Path = Path.Combine(baseDir, "ip2region.v4.xdb");
                string v6Path = Path.Combine(baseDir, "ip2region.v6.xdb");

                // 初始化 IPv4 搜索器
                if (File.Exists(v4Path))
                {
                    _ip2regionSearcherV4 = new Searcher(CachePolicy.Content, v4Path);
                }
                else
                {
                    AppendColorText("ip2region.v4.xdb 未找到\n", ColorTranslator.FromHtml("#a8a5ff"), false);
                }

                // 新增：初始化 IPv6 搜索器
                if (File.Exists(v6Path))
                {
                    _ip2regionSearcherV6 = new Searcher(CachePolicy.Content, v6Path);
                }
                else
                {
                    AppendColorText("ip2region.v6.xdb 未找到\n", ColorTranslator.FromHtml("#a8a5ff"), false);
                }
            }
            catch (Exception ex)
            {
                AppendColorText("ip2region 初始化失敗：" + ex.Message + "\n", ColorTranslator.FromHtml("#a8a5ff"), false);
            }
        }
        /// <summary>
        /// 使用 ip2region 查询并返回格式化结果（Country/Province/City/Isp）
        /// 优化：不显示 "0" / "-" / "Reserved" 字段；若是内网/保留地址，返回中文友好提示。
        /// 若没有任何可用字段，返回 "未知"（你也可以改为空字符串以不显示）
        /// </summary>
        private string GetIpLocationString(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return string.Empty;

            if (!IPAddress.TryParse(ip, out var ipAddr))
                return "未知";

            // 1) 先處理私有/保留地址 (目前夢酱的邏輯主要是 V4，V6 的特殊地址通常數據庫會包含)
            string reservedLabel = GetPrivateOrReservedLabel(ipAddr);
            if (!string.IsNullOrEmpty(reservedLabel))
                return reservedLabel;

            try
            {
                string region = "";

                // 判斷 IP 類型並選擇對應的搜索器
                if (ipAddr.AddressFamily == AddressFamily.InterNetwork) // IPv4
                {
                    if (_ip2regionSearcherV4 == null) return "V4數據庫未加載";
                    region = _ip2regionSearcherV4.Search(ip);
                }
                else if (ipAddr.AddressFamily == AddressFamily.InterNetworkV6) // IPv6
                {
                    if (_ip2regionSearcherV6 == null) return "V6數據庫未加載";
                    region = _ip2regionSearcherV6.Search(ip);
                }
                else
                {
                    return "未知協議";
                }

                if (string.IsNullOrWhiteSpace(region)) return "未知";

                // 接下來的格式化邏輯（Split 和 Join）保持不變，因為 ip2region 的格式是一樣的
                var parts = region.Split('|');
                var fields = new List<string>();
                foreach (var part in parts)
                {
                    string clean = NormalizeField(part);
                    if (!string.IsNullOrEmpty(clean)) fields.Add(clean);
                }

                return fields.Count == 0 ? "未知" : string.Join("/", fields);
            }
            catch
            {
                return "查詢失敗";
            }
        }

        /// <summary>
        /// 规范化单个字段：去掉 "0", "0/0", "-", "Reserved", "reserved" 等占位值并 Trim。
        /// 若是保留/占位，返回空字符串。
        /// </summary>
        private string NormalizeField(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();

            // 常见无效占位值
            if (s == "0" || s == "0.0" || s == "0/0" || s == "-")
                return "";
            if (s.Equals("Reserved", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("保留", StringComparison.OrdinalIgnoreCase))
                return "";

            // 有些 ip2region 写成 "CN" 在 country；你可以保留或映射（这里保留原样）
            return s;
        }

        /// <summary>
        /// 判断是否是私有/保留/特殊地址并返回友好中文说明（如果匹配则返回非空字符串）
        /// 返回示例： "内网地址", "CGNAT(运营商私网)", "回环地址", "链路本地", "多播地址", "保留地址"
        /// 如果不是私有/保留地址，返回 null 或 空串。
        /// </summary>
        private string GetPrivateOrReservedLabel(IPAddress ipAddr)
        {
            if (ipAddr == null) return null;
            return IanaReservedIP.Check(ipAddr.ToString());
        }
        private void AppendColorText(string text, Color color, bool addNewLine = false)
        {
            if (this.IsDisposed || richTextBox1.IsDisposed) return;
            richTextBox1.SelectionStart = richTextBox1.Text.Length;
            richTextBox1.SelectionLength = 0;
            richTextBox1.SelectionColor = color;
            richTextBox1.AppendText(addNewLine ? text + Environment.NewLine : text);
            richTextBox1.ScrollToCaret();
        }

        private void UpdateProtocolTip(object sender, EventArgs e)
        {
            if (sender is RadioButton rb && !rb.Checked) return;

            string protocol = GetSelectedProtocol();
            richTextBox1.Clear();
            Color themeColor = Color.FromArgb(168, 165, 255);

            if (protocol == "ICMP")
            {
                AppendColorText("     ==== 欢迎使用 Trace + ❤ 网络综合查询器X by Yumeyo ====", themeColor, true);
                AppendColorText("当前选中 ICMP 协议，请先阅读下列提示：", Color.Lime, true);
                AppendColorText("    🔰 Trace+ 采用Socket模拟实现, 以实现网卡选择, \n", Color.Pink, true);
                AppendColorText("    1.需要 >>关闭防火墙/放行查询器X的ICMP<< 才能测试, 否则一跳也看不到 💦", Color.Yellow, true);
                AppendColorText("                  └─ 点击网卡右边[防火墙]按钮，快速操作", Color.Yellow, true);
                AppendColorText("    2.如有问题可 >>管理员权限运行<< 再尝试 💦", Color.Orange, true);
                AppendColorText("                 └─ 右键左上角[网卡]白字，快速操作\n", Color.Orange, true);
                AppendColorText("    🔰 此处归属地仅供参考，有疑惑可复制IP用“手动查询-IP地址”确认属地 ❤", Color.LightSkyBlue, true);
                AppendColorText("    🔰 Trace+ 专为IPv4优化, IPv6请选择[ICMP兼容模式]网卡, 不可指定网卡\n", Color.LightGreen, true);
                AppendColorText("🚀注意: 因测试原理，所有第三方Trace都可能互相干扰,", Color.Gold, true);
                AppendColorText("    建议同时只运行一个Trace测试，包括但不限于查询器X和同类软件! ", Color.Gold, true);
                comboLocalEnd.Enabled = true;
                txtTargetPort.Enabled = false;
            }
            else
            {
                AppendColorText("     ==== 欢迎使用 Trace + ❤ 网络综合查询器X by Yumeyo ====", themeColor, true);
                AppendColorText($"当前选中 {protocol} 协议，请先阅读下列提示：", Color.Lime, true);
                AppendColorText("    🔰 Trace+ 采用Socket模拟实现, 以实现网卡选择, \n", Color.Pink, true);
                AppendColorText($"    1. {protocol} 必须 >>管理员权限运行<< 💦", Color.Yellow, true);
                AppendColorText("                   └─ 右键左上角[网卡]白字，快速操作", Color.Yellow, true);
                AppendColorText("    2. 还要 >>关闭防火墙/放行查询器X的ICMP<< 才能测试 💦", Color.Yellow, true);
                AppendColorText("                  └─ 点击网卡右边[防火墙]按钮，快速操作\n", Color.Yellow, true);
                AppendColorText("    🔰 此处归属地仅供参考，有疑惑可复制IP用“手动查询-IP地址”确认属地 ❤", Color.LightSkyBlue, true);
                AppendColorText("    🔰 Trace+ 专为IPv4优化, IPv6请选择[ICMP兼容模式]网卡, 不可指定网卡\n", Color.LightGreen, true);
                AppendColorText("🚀注意: 因测试原理，所有第三方Trace都可能互相干扰,", Color.Gold, true);
                AppendColorText("    建议同时只运行一个Trace测试，包括但不限于查询器X和同类软件! ", Color.Gold, true);
                comboLocalEnd.Enabled = true;
                txtTargetPort.Enabled = true;
                // 切换协议时自动填入默认端口
                if (protocol == "TCP")
                    txtTargetPort.Text = "80";
                else if (protocol == "UDP")
                    txtTargetPort.Text = "53";
            }
        }

        private string GetSelectedProtocol()
        {
            if (radioTCP.Checked) return "TCP";
            if (radioUDP.Checked) return "UDP";
            return "ICMP";
        }

        private void SetUIState(bool running)
        {
            bool enableControls = !running;
            comboLocalEnd.Enabled = enableControls;
            txtHops.Enabled = enableControls;
            txtDelay.Enabled = enableControls;
            comboTargetIP.Enabled = enableControls;
            radioTCP.Enabled = enableControls;
            radioUDP.Enabled = enableControls;
            radioICMP.Enabled = enableControls;
            checkGEO.Enabled = enableControls;
            checkMTR.Enabled = enableControls;
            txtTargetPort.Enabled = !running && !radioICMP.Checked;
            btnWDF.Enabled = enableControls;
            btnSave.Enabled = enableControls;

            if (running) btnStartTrace.Text = "停止";
            else btnStartTrace.Text = "开测";
        }

        private async void btnStartTrace_Click(object sender, EventArgs e)
        {
            // 自动刷新网卡（若当前选中的网卡已不存在）
            EnsureSelectedNICValid();

            if (isRunning)
            {
                if (cts != null) cts.Cancel();
                return;
            }
            // 新增：协议与环境前置检查
            bool isNotAdmin = this.Text.Contains("User");

            // 情况 A: TCP/UDP 模式但非管理员
            if ((radioTCP.Checked || radioUDP.Checked) && isNotAdmin)
            {
                DialogResult drUac = MessageBox.Show(
                    "查询器X的 TCP/UDP Trace 需【以管理员身份运行】。\n\n" +
                    "【确认】立刻以管理员身份重启（当前输入的内容不会保留）\n" +
                    "【取消】稍后自行操作\n\n" +
                    "也可通过右键窗口左上角“网卡”白字尝试提权",
                    "权限不够了", MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation);

                if (drUac == DialogResult.OK)
                {
                    if (isRunning && cts != null) cts.Cancel();
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = Application.ExecutablePath;
                    startInfo.WorkingDirectory = Environment.CurrentDirectory;
                    startInfo.Verb = "runas";

                    try
                    {
                        Process.Start(startInfo);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("提权失败: " + ex.Message, "提权已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                return; // 终止本次开测
            }

            // 情况 B: 未关闭防火墙
            if (this.Text.Contains("防火开"))
            {
                bool fwOn = IsFirewallEnabled();
                bool ruleOk = IsICMPRuleExisted();
                if (fwOn && !ruleOk && !comboLocalEnd.Text.Contains("ICMP兼容模式"))
                {
                    MessageBox.Show(
                        "Trace+ 需【关闭防火墙】或【放行查询器X】才能正常使用，\n" +
                        "当前还未设置任意一种放行规则。\n\n" +
                        "请点击网卡右边【防火开】按钮, 选择放行方式\n" +
                        "或选择【ICMP兼容模式】网卡, 可发起测试, 但不支持识别/指定网卡",
                        "遇到问题了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    return; // 终止本次开测
                }
            }

            string input = comboTargetIP.Text.Trim().ToLower();
            if (input.StartsWith("http://")) input = input.Substring(7);
            else if (input.StartsWith("https://")) input = input.Substring(8);
            if (input.Contains("/")) input = input.Split('/')[0];
            input = Regex.Replace(input, @"[^a-z0-9\.\:\-_]", "");

            if (string.IsNullOrEmpty(input))
            {
                SystemSounds.Beep.Play();
                return;
            }

            comboTargetIP.Text = input;
            richTextBox1.Clear();
            string inputTarget = input;
            SetUIState(true);
            IPAddress finalTargetIp = null;

            try
            {
                if (IPAddress.TryParse(inputTarget, out IPAddress directIp))
                {
                    finalTargetIp = directIp;
                }
                else
                {
                    if (this.IsDisposed) return;
                    AppendColorText($"[DNS]正在解析域名 {inputTarget} ...\n", Color.Yellow, true);

                    try
                    {
                        IPHostEntry hostEntry = await Dns.GetHostEntryAsync(inputTarget);
                        List<IPAddress> ipv4List = new List<IPAddress>();
                        foreach (var ip in hostEntry.AddressList)
                        {
                            if (ip.AddressFamily == AddressFamily.InterNetwork) ipv4List.Add(ip);
                        }

                        if (ipv4List.Count == 0) throw new Exception("未解析到 IPv4 地址");

                        comboTargetIP.Items.Clear();
                        comboTargetIP.Items.Add(inputTarget);
                        foreach (var ip in ipv4List) comboTargetIP.Items.Add(ip.ToString());

                        comboTargetIP.DroppedDown = true;
                        if (comboTargetIP.Items.Count == 2)
                        {
                            comboTargetIP.SelectedIndex = 1;
                            AppendColorText($"\n[DNS]解析到 {ipv4List.Count} 个目标 IP。", Color.Yellow, true);
                            AppendColorText($"[DNS]已经选择了，再次点击“开测”。\n", Color.Yellow, true);
                        }
                        else
                        {
                            AppendColorText($"\n[DNS]解析到 {ipv4List.Count} 个目标 IP。", Color.Yellow, true);
                            AppendColorText($"[DNS]请选择一个IP后，点击“开测”。\n", Color.Yellow, true);
                        }

                        isRunning = false;
                        SetUIState(false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppendColorText($"[DNS]解析出错：{ex.Message}\n", Color.Yellow, true);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (this.IsDisposed) return;
                AppendColorText($"[DNS]解析出错：{ex.Message}\n", Color.Yellow, true);
                return;
            }
            finally
            {
                if (finalTargetIp == null) SetUIState(false);
            }

            if (finalTargetIp == null) return;

            isRunning = true;
            cts = new CancellationTokenSource();
            _activeGeoOnlineIndex = ReadTraceGEOIndexFromIni();
            _geoCache.Clear();
            _enrichPending.Clear();
            _hopGeoOriginal.Clear();
            _ipToHop.Clear();
            CancellationToken token = cts.Token;

            if (!int.TryParse(txtHops.Text, out int maxHops)) maxHops = 30;
            if (!int.TryParse(txtDelay.Text, out int maxDelayMs) || maxDelayMs <= 0) maxDelayMs = 500;

            string selectedMethod = GetSelectedProtocol();
            if (!int.TryParse(txtTargetPort.Text, out int targetPort))
            {
                targetPort = (selectedMethod == "TCP") ? 80 : random.Next(1025, 65535);
            }

            try
            {
                if (this.IsDisposed) return;
                // --- 网卡解析逻辑 ---
                IPAddress localExportIp;
                string userSelectIp = "";
                this.Invoke(new Action(() => { userSelectIp = comboLocalEnd.Text; }));

                if (userSelectIp.Contains(" ")) userSelectIp = userSelectIp.Split(' ')[0];

                if (userSelectIp == "0.0.0.0")
                {
                    localExportIp = GetLocalExportIP(finalTargetIp);
                    string detectedIpStr = localExportIp.ToString();
                    for (int i = 0; i < comboLocalEnd.Items.Count; i++)
                    {
                        if (comboLocalEnd.Items[i].ToString().StartsWith(detectedIpStr))
                        {
                            comboLocalEnd.SelectedIndex = i;
                            break;
                        }
                    }
                }
                else
                {
                    if (!IPAddress.TryParse(userSelectIp, out localExportIp)) localExportIp = GetLocalExportIP(finalTargetIp);
                }

                if (this.IsDisposed) return;

                string finalPort = String.Empty;
                if (selectedMethod == "TCP" || selectedMethod == "UDP")
                {
                    finalPort = $"端口 {txtTargetPort.Text}";
                }
                else
                {
                    finalPort = String.Empty;
                }
                // --- 统一输出提示信息 ---
                string portInfo = (selectedMethod == "TCP" || selectedMethod == "UDP") ? $" 端口:{txtTargetPort.Text}" : "";
                AppendColorText($">> [Trace] 目标: {finalTargetIp} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", Color.Lime, false);
                if (comboLocalEnd.Text.Contains("ICMP兼容模式"))
                {
                    AppendColorText($"   使用接口: {comboLocalEnd.Text} 跳数:{maxHops} 超时:{maxDelayMs}ms 协议:{selectedMethod}{portInfo} | NICX By Yumeyo\n\n", Color.LightSkyBlue, false);
                }
                else
                {
                    AppendColorText($"   使用接口: {localExportIp} 跳数:{maxHops} 超时:{maxDelayMs}ms 协议:{selectedMethod}{portInfo} | NICX By Yumeyo\n\n", Color.LightSkyBlue, false);
                }

                if (checkMTR.Checked)
                {
                    await RunMtrTrace(finalTargetIp, localExportIp, maxHops, maxDelayMs, selectedMethod, targetPort, token);
                }
                else if (selectedMethod == "ICMP")
                {
                    if (comboLocalEnd.Text.Contains("ICMP兼容模式"))
                    {
                        await RunNativeIcmpTrace(finalTargetIp, maxHops, maxDelayMs, token);
                    }
                    else
                    {
                        await RunIcmpTrace(finalTargetIp, localExportIp, maxHops, maxDelayMs, token);
                    }
                }
                else
                {
                    await RunSocketTrace(finalTargetIp, localExportIp, maxHops, maxDelayMs, selectedMethod, targetPort, token);
                }
            }
            catch (OperationCanceledException)
            {
                AppendColorText(" ■ 用户手动停止测试", Color.Yellow, true);
            }
            catch (Exception ex)
            {
                if (!this.IsDisposed) richTextBox1.AppendText($"\n执行过程出错: {ex.Message}");
            }
            finally
            {
                if (!this.IsDisposed)
                {
                    isRunning = false;
                    SetUIState(false);
                }
                if (cts != null) cts.Dispose();
            }
        }

        // 梦酱专属：原生 ICMP 兼容模式 Trace
        private async Task RunNativeIcmpTrace(IPAddress targetIp, int maxHops, int timeout, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            var results = new ConcurrentDictionary<int, HopResult>();

            async Task ProbeHop(int ttl, CancellationToken hopToken)
            {
                var result = new HopResult(ttl);
                using (Ping pingSender = new Ping())
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (hopToken.IsCancellationRequested) break;
                        if (i > 0) await Task.Delay(40, hopToken);
                        PingOptions options = new PingOptions(ttl, true);
                        byte[] buffer = Encoding.ASCII.GetBytes("YumeyoNICX_Trace_Packet");
                        Stopwatch sw = Stopwatch.StartNew();
                        try
                        {
                            PingReply reply = await pingSender.SendPingAsync(targetIp, timeout, buffer, options);
                            sw.Stop();
                            if (reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired)
                            {
                                if (result.ReplyAddress == null && geoChecked)
                                    result.GeoInfo = ResolveGeoInfo(reply.Address.ToString(), hopToken);
                                result.ReplyAddress = reply.Address;
                                result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                if (reply.Status == IPStatus.Success) result.TargetReached = true;
                            }
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            sw.Stop();
                            result.RTTs[i] = -2;
                        }
                    }
                }
                results[ttl] = result;
            }

            var tasks = new List<Task>();
            for (int ttl = 1; ttl <= maxHops; ttl++)
            { int ct = ttl; tasks.Add(Task.Run(() => ProbeHop(ct, token), token)); }
            var allDone = Task.WhenAll(tasks);
            var globalTimer = Stopwatch.StartNew();
            int hopTimeout = timeout * 4 + 200;//最长等待时间 原生
            int missingStreak = 0;
            const int minPacingMs = 50; //显示缓冲时间
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                if (this.IsDisposed || token.IsCancellationRequested) break;
                int effectiveWait;
                if (allDone.IsCompleted)
                    effectiveWait = 0;
                else if (missingStreak > 0)
                {
                    int futureDone = 0;
                    for (int f = ttl + 1; f <= Math.Min(ttl + 6, maxHops); f++)
                        if (results.ContainsKey(f)) futureDone++;
                    if (futureDone >= 3) effectiveWait = 400;
                    else if (futureDone >= 1) effectiveWait = Math.Min(700, hopTimeout);
                    else effectiveWait = Math.Max(400, hopTimeout - missingStreak * 250);
                }
                else effectiveWait = hopTimeout;
                effectiveWait = Math.Min(effectiveWait, Math.Max(0, hopTimeout - (int)globalTimer.ElapsedMilliseconds));
                var iterStart = Stopwatch.StartNew();
                var waited = Stopwatch.StartNew();
                while (!results.TryGetValue(ttl, out _) && waited.ElapsedMilliseconds < effectiveWait
                       && !allDone.IsCompleted && !token.IsCancellationRequested)
                    await Task.Delay(20);
                HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                DisplaySingleHop(hop, geoChecked);
                if (hop.TargetReached) break;
                missingStreak = hop.HasAnyResponse ? 0 : missingStreak + 1;
                int iterMs = (int)iterStart.ElapsedMilliseconds;
                if (iterMs < minPacingMs)
                    await Task.Delay(minPacingMs - iterMs);
            }
            try { await allDone; } catch { }
        }

        // ==========================================
        // 第一部分：校验和计算
        // ==========================================
        private string GetLocalGeoInfo(string ip)
        {
            try
            {
                string geo = GetIpLocationString(ip);
                string geoCN = Api2.GetGeoCNLocationQuick(ip);
                return string.IsNullOrEmpty(geoCN) ? geo : $"{geoCN} | {geo}";
            }
            catch { return null; }
        }

        private string ResolveGeoInfo(string ip, CancellationToken token)
        {
            string geo = GetLocalGeoInfo(ip);
            bool isReserved = !string.IsNullOrEmpty(IanaReservedIP.Check(ip));
            Debug.WriteLine($"[GEO-Trace] 本地 ip={ip} reserved={isReserved} geo={geo}");
            if (!isReserved)
                EnrichGeoCacheAsync(ip, token);
            return geo;
        }

        private void EnrichGeoCacheAsync(string ip, CancellationToken token)
        {
            if (_activeGeoOnlineIndex <= 0) return;
            if (!_enrichPending.TryAdd(ip, 0)) return;

            Debug.WriteLine($"[GEO-Trace] 发起查询 ip={ip}");
            _ = Task.Run(async () =>
            {
                try { await _geoEnrichSemaphore.WaitAsync(token); }
                catch { _enrichPending.TryRemove(ip, out _); return; }
                try
                {
                    if (_geoCache.ContainsKey(ip)) { Debug.WriteLine($"[GEO-Trace] 跳过(已缓存) ip={ip}"); return; }
                    var provider = Api2.GeoCN_Providers[_activeGeoOnlineIndex];
                    var sw = Stopwatch.StartNew();
                    var geoResult = await provider.GetGeoTask(ip, token);
                    sw.Stop();
                    if (geoResult != null && (!string.IsNullOrEmpty(geoResult.Loc) || !string.IsNullOrEmpty(geoResult.AS)))
                    {
                        string enriched = $"{geoResult.Loc} {geoResult.AS}".Trim();
                        _geoCache[ip] = enriched;
                        Debug.WriteLine($"[GEO-Trace] 完成 ip={ip} => {enriched} 耗时={sw.Elapsed.TotalSeconds:F1}s");
                    }
                    else
                    {
                        Debug.WriteLine($"[GEO-Trace] 空结果 ip={ip} 耗时={sw.Elapsed.TotalSeconds:F1}s");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GEO-Trace] 异常 ip={ip}: {ex.Message}");
                }
                finally
                {
                    _geoEnrichSemaphore.Release();
                    _enrichPending.TryRemove(ip, out _);
                    if (_geoCache.TryGetValue(ip, out string enriched) && _ipToHop.TryGetValue(ip, out int hop))
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (_hopGeoOriginal.TryGetValue(hop, out string oldGeo) && enriched != oldGeo)
                            {
                                try
                                {
                                    int pos = richTextBox1.Find(oldGeo, 0, RichTextBoxFinds.None);
                                    if (pos >= 0)
                                    {
                                        richTextBox1.Select(pos, oldGeo.Length);
                                        richTextBox1.SelectedText = enriched;
                                        _hopGeoOriginal[hop] = enriched;
                                    }
                                }
                                catch { }
                            }
                        }));
                    }
                }
            }, token);
        }

        private async Task WaitForEnrichmentsAsync(CancellationToken token)
        {
            if (_activeGeoOnlineIndex <= 0) return;

            // 第一次扫描：为所有仍显示本地库格式的IP启动在线查询
            bool startedAny = false;
            foreach (var kvp in _ipToHop)
            {
                string ip = kvp.Key;
                int hop = kvp.Value;
                if (_hopGeoOriginal.TryGetValue(hop, out string oldGeo)
                    && !string.IsNullOrEmpty(oldGeo)
                    && oldGeo.Contains(" | ")
                    && string.IsNullOrEmpty(IanaReservedIP.Check(ip)))
                {
                    EnrichGeoCacheAsync(ip, token);
                    startedAny = true;
                }
            }

            bool hasPending = _enrichPending.Count > 0;
            if (hasPending || startedAny)
            {
                if (hasPending)
                    AppendColorText("\n正在查询地理位置...", Color.FromArgb(168, 165, 255), false);
                using (var cts = new CancellationTokenSource(GeoOnlineTimeoutMs + 2000))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token))
                {
                    try
                    {
                        while (_enrichPending.Count > 0 && !linked.Token.IsCancellationRequested)
                            await Task.Delay(100, linked.Token);
                    }
                    catch { }
                }
            }

            // 同步应用所有已完成的在线查询结果到 RichTextBox
            foreach (var kvp in _ipToHop)
            {
                string ip = kvp.Key;
                int hop = kvp.Value;
                if (_geoCache.TryGetValue(ip, out string enriched)
                    && _hopGeoOriginal.TryGetValue(hop, out string oldGeo)
                    && enriched != oldGeo)
                {
                    try
                    {
                        int pos = richTextBox1.Find(oldGeo, 0, RichTextBoxFinds.None);
                        if (pos >= 0)
                        {
                            richTextBox1.Select(pos, oldGeo.Length);
                            richTextBox1.SelectedText = enriched;
                            _hopGeoOriginal[hop] = enriched;
                        }
                    }
                    catch { }
                }
            }
        }

        private static void ApplyEnrichedGeoToAllStats(string ip, string enriched,
            ConcurrentDictionary<int, MtrHopStats> stats)
        {
            foreach (var kvp in stats)
            {
                if (kvp.Value.ReplyAddress?.ToString() == ip)
                {
                    kvp.Value.GeoInfo = enriched;
                    if (kvp.Value.IpGeoCache.ContainsKey(ip))
                        kvp.Value.IpGeoCache[ip] = enriched;
                }
            }
        }

        private async Task EnrichGeoOnlineAsync(string ip, int ttl,
            ConcurrentDictionary<int, MtrHopStats> stats, CancellationToken token)
        {
            if (_activeGeoOnlineIndex <= 0) return;
            if (_geoCache.ContainsKey(ip)) return;

            Debug.WriteLine($"[GEO-MTR] 发起查询 ttl={ttl} ip={ip}");
            try { await _geoEnrichSemaphore.WaitAsync(token); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (_geoCache.ContainsKey(ip))
                {
                    ApplyEnrichedGeoToAllStats(ip, _geoCache[ip], stats);
                    Debug.WriteLine($"[GEO-MTR] 跳过(已缓存) ttl={ttl} ip={ip}");
                    return;
                }
                var provider = Api2.GeoCN_Providers[_activeGeoOnlineIndex];
                var sw = Stopwatch.StartNew();
                var geoResult = await provider.GetGeoTask(ip, token);
                sw.Stop();
                if (geoResult != null && (!string.IsNullOrEmpty(geoResult.Loc) || !string.IsNullOrEmpty(geoResult.AS)))
                {
                    string enriched = $"{geoResult.Loc} {geoResult.AS}".Trim();
                    _geoCache[ip] = enriched;
                    ApplyEnrichedGeoToAllStats(ip, enriched, stats);
                    Debug.WriteLine($"[GEO-MTR] 完成 ttl={ttl} ip={ip} => {enriched} 耗时={sw.Elapsed.TotalSeconds:F1}s");
                }
                else
                {
                    Debug.WriteLine($"[GEO-MTR] 空结果 ttl={ttl} ip={ip} 耗时={sw.Elapsed.TotalSeconds:F1}s");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GEO-MTR] 异常 ttl={ttl} ip={ip}: {ex.Message}");
            }
            finally { _geoEnrichSemaphore.Release(); _enrichPending.TryRemove(ip, out _); }
        }

        private int ReadTraceGEOIndexFromIni()
        {
            try
            {
                var sb = new StringBuilder(16);
                GetPrivateProfileString("Trace", "TraceGEO", "0", sb, sb.Capacity, IniPath);
                if (int.TryParse(sb.ToString(), out int idx) && idx > 0 && idx < Api2.GeoCN_Providers.Count)
                    return idx;
            }
            catch { }
            return 0;
        }

        private static ushort ComputeChecksum(byte[] data)
        {
            uint sum = 0;
            int index = 0;
            int count = data.Length;
            while (count > 1)
            {
                sum += BitConverter.ToUInt16(data, index);
                index += 2;
                count -= 2;
            }
            if (count > 0) sum += data[index];
            sum = (sum >> 16) + (sum & 0xffff);
            sum += (sum >> 16);
            return (ushort)(~sum);
        }

        private static ushort ComputeChecksumRange(byte[] data, int offset, int length)
        {
            uint sum = 0;
            int index = offset;
            int count = length;
            while (count > 1)
            {
                sum += (uint)((data[index] << 8) | data[index + 1]);
                index += 2;
                count -= 2;
            }
            if (count > 0) sum += (uint)(data[index] << 8);
            sum = (sum >> 16) + (sum & 0xffff);
            sum += (sum >> 16);
            return (ushort)(~sum);
        }

        // 构造完整 IP+UDP 包，用于 raw socket 发送，避免 Windows 将 ICMP 错误
        // 关联到 UDP socket 而导致 raw ICMP receiver 收不到响应
        // 当目标端口为 53(DNS) 时，构造合法 DNS 查询 payload 以通过 DPI 检测
        private static readonly byte[] DnsQueryPayload = new byte[] {
            0x00, 0x01, // Transaction ID
            0x01, 0x00, // Flags: standard query, RD
            0x00, 0x01, // Questions: 1
            0x00, 0x00, // Answer RRs
            0x00, 0x00, // Authority RRs
            0x00, 0x00, // Additional RRs
            // Query: "test.local" type A, class IN
            0x04, 0x74, 0x65, 0x73, 0x74, // "test"
            0x05, 0x6c, 0x6f, 0x63, 0x61, 0x6c, // "local"
            0x00, // root label
            0x00, 0x01, // Type A
            0x00, 0x01  // Class IN
        };

        private static byte[] GetUdpPayload(int dstPort)
        {
            if (dstPort == 53) return DnsQueryPayload;
            return new byte[0];
        }

        private static byte[] BuildUdpTracePacket(IPAddress srcIp, IPAddress dstIp,
            int srcPort, int dstPort, int ttl, Random rng)
        {
            byte[] packet = new byte[28]; // 20 IP header + 8 UDP header, no payload
            byte[] src = srcIp.GetAddressBytes();
            byte[] dst = dstIp.GetAddressBytes();

            // --- IP Header (20 bytes) ---
            packet[0] = 0x45; // Version=4, IHL=5 (20 bytes)
            packet[1] = 0x00; // DSCP/ECN = default
            packet[2] = 0x00; packet[3] = 28; // Total Length = 28 (big-endian)
            ushort id = (ushort)(rng != null ? rng.Next(1, 65535) : 1);
            packet[4] = (byte)(id >> 8); packet[5] = (byte)(id & 0xFF);
            packet[6] = 0x00; packet[7] = 0x00; // Flags=0, Fragment=0
            packet[8] = (byte)ttl;
            packet[9] = 17; // Protocol = UDP
            packet[10] = 0x00; packet[11] = 0x00; // Checksum placeholder
            Buffer.BlockCopy(src, 0, packet, 12, 4);
            Buffer.BlockCopy(dst, 0, packet, 16, 4);
            // Compute IP header checksum
            ushort ipCksum = ComputeChecksumRange(packet, 0, 20);
            packet[10] = (byte)(ipCksum >> 8);
            packet[11] = (byte)(ipCksum & 0xFF);

            // --- UDP Header (8 bytes) at offset 20 ---
            packet[20] = (byte)(srcPort >> 8);
            packet[21] = (byte)(srcPort & 0xFF);
            packet[22] = (byte)(dstPort >> 8);
            packet[23] = (byte)(dstPort & 0xFF);
            packet[24] = 0x00; packet[25] = 8; // UDP Length = 8 (no payload)

            // Compute UDP checksum over pseudo-header + UDP header
            // Pseudo-header: src IP (4) + dst IP (4) + zero (1) + protocol (1) + UDP len (2) = 12 bytes
            byte[] udpCksumData = new byte[12 + 8];
            Buffer.BlockCopy(src, 0, udpCksumData, 0, 4);
            Buffer.BlockCopy(dst, 0, udpCksumData, 4, 4);
            udpCksumData[8] = 0;
            udpCksumData[9] = 17; // Protocol = UDP
            udpCksumData[10] = 0x00; udpCksumData[11] = 8; // UDP length = 8
            // UDP header (with checksum field = 0)
            Buffer.BlockCopy(packet, 20, udpCksumData, 12, 8);
            ushort udpCksum = ComputeChecksum(udpCksumData);
            packet[26] = (byte)(udpCksum >> 8);
            packet[27] = (byte)(udpCksum & 0xFF);

            return packet;
        }

        // ==========================================
        // 第二部分：构造 ICMP 报文 (使用 InstanceID)
        // ==========================================
        private byte[] CreateIcmpPacket(ushort seqNum)
        {
            byte[] packet = new byte[32];
            packet[0] = 8; // Type: Echo Request
            packet[1] = 0; // Code: 0

            // 关键修改：使用当前窗口唯一的 _instanceIdentifier 作为 ID
            Buffer.BlockCopy(BitConverter.GetBytes(_instanceIdentifier), 0, packet, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(seqNum), 0, packet, 6, 2);

            byte[] payload = Encoding.ASCII.GetBytes("YumeyoTraceX-" + seqNum.ToString("X4"));
            Buffer.BlockCopy(payload, 0, packet, 8, Math.Min(payload.Length, 24));

            ushort checksum = ComputeChecksum(packet);
            byte[] checkBytes = BitConverter.GetBytes(checksum);
            packet[2] = checkBytes[0];
            packet[3] = checkBytes[1];
            return packet;
        }

        // ==========================================
        // 终极整合版 RunIcmpTrace (多窗口防串扰版)
        // ==========================================
        private async Task RunIcmpTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            var results = new ConcurrentDictionary<int, HopResult>();
            var seqTcsStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();

            using (Socket receiver = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            {
                receiver.Bind(new IPEndPoint(localIp, 0));
                receiver.ReceiveBufferSize = 65536;

                var receiveCts = new CancellationTokenSource();
                var receiveTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[1024];
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    receiver.ReceiveTimeout = 500;
                    while (!receiveCts.Token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                            int ipHdrLen = (rcvBuffer[0] & 0x0F) * 4;
                            if (len >= ipHdrLen + 8)
                            {
                                byte type = rcvBuffer[ipHdrLen];
                                ushort rcvId = 0, rcvSeq = 0;
                                if (type == 0) { rcvId = BitConverter.ToUInt16(rcvBuffer, ipHdrLen + 4); rcvSeq = BitConverter.ToUInt16(rcvBuffer, ipHdrLen + 6); }
                                else if (type == 11) { int innerIpHdrLen = (rcvBuffer[ipHdrLen + 8] & 0x0F) * 4; int eOff = ipHdrLen + 8 + innerIpHdrLen; if (len > eOff + 6) { rcvId = BitConverter.ToUInt16(rcvBuffer, eOff + 4); rcvSeq = BitConverter.ToUInt16(rcvBuffer, eOff + 6); } }
                                else continue;
                                if (rcvId == _instanceIdentifier && seqTcsStore.TryRemove(rcvSeq, out var tcs))
                                    tcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                            }
                        }
                        catch (SocketException) { continue; }
                        catch { break; }
                    }
                }, receiveCts.Token);

                async Task ProbeHop(int ttl, CancellationToken hopToken)
                {
                    var result = new HopResult(ttl);
                    try
                    {
                        using (Socket sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                        {
                            sendSocket.Bind(new IPEndPoint(localIp, 0));
                            sendSocket.Ttl = (short)ttl;
                            for (int i = 0; i < 4; i++)
                            {
                                if (hopToken.IsCancellationRequested) break;
                                if (i > 0) await Task.Delay(40, hopToken);
                                int seq = (ttl - 1) * 4 + i + 1;
                                byte[] req = CreateIcmpPacket((ushort)seq);
                                var tcs = new TaskCompletionSource<IPAddress>();
                                seqTcsStore[seq] = tcs;
                                Stopwatch sw = Stopwatch.StartNew();
                                try
                                {
                                    sendSocket.SendTo(req, new IPEndPoint(targetIp, 0));
                                    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, hopToken));
                                    sw.Stop();
                                    seqTcsStore.TryRemove(seq, out _);
                                    if (done == tcs.Task)
                                    {
                                        IPAddress addr = await tcs.Task;
                                        if (result.ReplyAddress == null && geoChecked)
                                            result.GeoInfo = ResolveGeoInfo(addr.ToString(), hopToken);
                                        result.ReplyAddress = addr;
                                        result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                        if (addr.Equals(targetIp))
                                            result.TargetReached = true;
                                    }
                                }
                                catch (Exception ex) when (!(ex is OperationCanceledException))
                                {
                                    sw.Stop();
                                    result.RTTs[i] = -2;
                                    seqTcsStore.TryRemove(seq, out _);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    results[ttl] = result;
                }

                // 并发启动所有跳
                var hopTasks = new Dictionary<int, Task>();
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    int ct = ttl;
                    hopTasks[ttl] = Task.Run(() => ProbeHop(ct, token), token);
                }

                // 顺序收集显示：等待每跳的Task完成，不会因为启发式超时而跳过未完成的跳
                int hopTimeout = timeout * 4 + 200;
                bool reachedTarget = false;
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    if (token.IsCancellationRequested || this.IsDisposed) break;

                    Task hopTask = hopTasks[ttl];
                    try { await Task.WhenAny(hopTask, Task.Delay(hopTimeout, token)); } catch { }

                    HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                    DisplaySingleHop(hop, geoChecked);
                    if (hop.TargetReached) { reachedTarget = true; break; }
                }

                receiveCts.Cancel();
                try { await receiveTask; } catch { }
                receiveCts.Dispose();

                if (geoChecked) await WaitForEnrichmentsAsync(token);
                if (reachedTarget)
                    AppendColorText("\nTrace 完成.\n", Color.Lime, false);
            }
        }

        // ==========================================
        // TCP/UDP Trace实现
        // ==========================================
        private async Task RunSocketTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int customPort, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool isTcp = (protocol == "TCP");
            var results = new ConcurrentDictionary<int, HopResult>();
            var portTcsStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();

            using (Socket receiver = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            {
                receiver.Bind(new IPEndPoint(localIp, 0));
                try { receiver.IOControl(IOControlCode.ReceiveAll, new byte[] { 1, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 }); } catch { }
                receiver.ReceiveBufferSize = 65536;

                // 固定源端口：避免ECMP哈希因端口变化导致路径不一致/跳数抖动
                int fixedSourcePort;
                using (var tmpSocket = new Socket(AddressFamily.InterNetwork,
                    isTcp ? SocketType.Stream : SocketType.Dgram,
                    isTcp ? ProtocolType.Tcp : ProtocolType.Udp))
                {
                    tmpSocket.Bind(new IPEndPoint(localIp, 0));
                    fixedSourcePort = ((IPEndPoint)tmpSocket.LocalEndPoint).Port;
                }

                var receiveCts = new CancellationTokenSource();
                int expectedProtocol = isTcp ? 6 : 17;
                var receiveTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[8192];
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    receiver.ReceiveTimeout = 500;
                    while (!receiveCts.Token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                            int ipHdrLen = (rcvBuffer[0] & 0x0F) * 4;
                            if (len >= ipHdrLen + 8)
                            {
                                int icmpType = rcvBuffer[ipHdrLen];
                                if (icmpType == 11 || icmpType == 3)
                                {
                                    // 验证嵌入包的IP头长度和协议类型，防止误匹配
                                    int innerIpHdrLen = (rcvBuffer[ipHdrLen + 8] & 0x0F) * 4;
                                    if (innerIpHdrLen < 20) continue;
                                    int innerProto = rcvBuffer[ipHdrLen + 8 + 9];
                                    if (innerProto != expectedProtocol) continue;
                                    int portOff = ipHdrLen + 8 + innerIpHdrLen;
                                    if (len <= portOff + 3) continue;
                                    int sport = (rcvBuffer[portOff] << 8) + rcvBuffer[portOff + 1];
                                    int dport = (rcvBuffer[portOff + 2] << 8) + rcvBuffer[portOff + 3];
                                    if (dport != customPort) continue;
                                    if (portTcsStore.TryRemove(sport, out var tcs))
                                        tcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                                }
                            }
                        }
                        catch (SocketException) { continue; }
                        catch { break; }
                    }
                }, receiveCts.Token);

                async Task ProbeHop(int ttl, CancellationToken hopToken)
                {
                    var result = new HopResult(ttl);
                    try
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            if (hopToken.IsCancellationRequested) break;
                            if (i > 0) await Task.Delay(40, hopToken);
                            using (Socket senderSocket = new Socket(AddressFamily.InterNetwork,
                                isTcp ? SocketType.Stream : SocketType.Dgram,
                                isTcp ? ProtocolType.Tcp : ProtocolType.Udp))
                            {
                                try
                                {
                                    senderSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                                    int myPort = fixedSourcePort + ttl;
                                    senderSocket.Bind(new IPEndPoint(localIp, myPort));
                                    senderSocket.Ttl = (short)ttl;
                                    var icmpTcs = new TaskCompletionSource<IPAddress>();
                                    portTcsStore[myPort] = icmpTcs;
                                    Stopwatch sw = Stopwatch.StartNew();

                                    Task<IPAddress> tcpResultTask = null;
                                    if (isTcp)
                                    {
                                        var tcpTcs = new TaskCompletionSource<IPAddress>();
                                        tcpResultTask = tcpTcs.Task;
                                        var _ = senderSocket.ConnectAsync(new IPEndPoint(targetIp, customPort))
                                            .ContinueWith(t =>
                                            {
                                                // 只有TCP握手成功才认为到达目标。
                                                // ConnectionRefused可能来自中间设备的RST，不可靠。
                                                if (t.Status == TaskStatus.RanToCompletion)
                                                    tcpTcs.TrySetResult(targetIp);
                                            }, TaskContinuationOptions.NotOnCanceled);
                                    }
                                    else
                                    {
                                        senderSocket.SendTo(GetUdpPayload(customPort), new IPEndPoint(targetIp, customPort));
                                        senderSocket.Close();
                                    }

                                    Task completed;
                                    if (tcpResultTask != null)
                                        completed = await Task.WhenAny(icmpTcs.Task, tcpResultTask, Task.Delay(timeout, hopToken));
                                    else
                                        completed = await Task.WhenAny(icmpTcs.Task, Task.Delay(timeout, hopToken));

                                    sw.Stop();
                                    portTcsStore.TryRemove(myPort, out _);

                                    if (completed == icmpTcs.Task)
                                    {
                                        IPAddress addr = ((Task<IPAddress>)completed).Result;
                                        if (result.ReplyAddress == null && geoChecked)
                                            result.GeoInfo = ResolveGeoInfo(addr.ToString(), hopToken);
                                        result.ReplyAddress = addr;
                                        result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                        if (addr.Equals(targetIp))
                                            result.TargetReached = true;
                                    }
                                    else if (tcpResultTask != null && completed == tcpResultTask)
                                    {
                                        if (result.ReplyAddress == null && geoChecked)
                                            result.GeoInfo = ResolveGeoInfo(targetIp.ToString(), hopToken);
                                        result.ReplyAddress = targetIp;
                                        result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                        result.TargetReached = true;
                                    }
                                }
                                catch (Exception ex) when (!(ex is OperationCanceledException))
                                {
                                    result.RTTs[i] = -2;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    results[ttl] = result;
                }

                // 逐跳探测：每跳发起→等待→立即显示，间隔100ms再发下一跳
                int hopTimeout = timeout * 4 + 200;
                bool reachedTarget = false;
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    if (token.IsCancellationRequested || this.IsDisposed) break;

                    var hopTask = Task.Run(() => ProbeHop(ttl, token), token);
                    try { await Task.WhenAny(hopTask, Task.Delay(hopTimeout, token)); } catch { }

                    HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                    DisplaySingleHop(hop, geoChecked, probeCount: 3);
                    if (hop.TargetReached) { reachedTarget = true; break; }

                    if (ttl < maxHops)
                        await Task.Delay(100, token);
                }

                receiveCts.Cancel();
                try { await receiveTask; } catch { }
                receiveCts.Dispose();

                if (geoChecked) await WaitForEnrichmentsAsync(token);
                if (reachedTarget)
                    AppendColorText("\nTrace 完成.\n", Color.Lime, false);
            }
        }

        // ==========================================
        // MTR 模式：持续多轮探测 + 累计统计 + 实时刷新表格
        // ==========================================
        private async Task RunMtrTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int targetPort, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            var stats = new ConcurrentDictionary<int, MtrHopStats>();
            for (int ttl = 1; ttl <= maxHops; ttl++)
                stats[ttl] = new MtrHopStats { TTL = ttl };

            string targetLabel = targetIp.ToString();
            int round = 0;
            int hopTimeout = timeout * 4 + 200;
            int effectiveMaxHops = maxHops;
            int confirmedTargetHop = 0;
            bool targetEverReached = false;
            bool isFirstRound = true;

            using (Socket receiver = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            {
                receiver.Bind(new IPEndPoint(localIp, 0));
                try { receiver.IOControl(IOControlCode.ReceiveAll, new byte[] { 1, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 }); } catch { }
                receiver.ReceiveBufferSize = 65536;

                // 固定源端口：避免ECMP哈希因端口变化导致路径不一致/跳数抖动（TCP/UDP）
                int mtrFixedPort = 0;
                if (protocol != "ICMP")
                {
                    using (var tmpSocket = new Socket(AddressFamily.InterNetwork,
                        protocol == "TCP" ? SocketType.Stream : SocketType.Dgram,
                        protocol == "TCP" ? ProtocolType.Tcp : ProtocolType.Udp))
                    {
                        tmpSocket.Bind(new IPEndPoint(localIp, 0));
                        mtrFixedPort = ((IPEndPoint)tmpSocket.LocalEndPoint).Port;
                    }
                }

                while (!token.IsCancellationRequested && !this.IsDisposed)
                {
                    round++;
                    var roundResults = new ConcurrentDictionary<int, HopResult>();
                    // 接收循环：同时支持 ICMP (ID+seq) 和 TCP/UDP (源端口) 匹配
                    var roundSeqStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();
                    var roundPortStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();
                    var roundCts = CancellationTokenSource.CreateLinkedTokenSource(token);

                    var receiveTask = Task.Run(() =>
                    {
                        byte[] buf = new byte[1024];
                        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                        receiver.ReceiveTimeout = 500;
                        while (!roundCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                int len = receiver.ReceiveFrom(buf, ref remoteEP);
                                int ipHdrLen = (buf[0] & 0x0F) * 4;
                                if (len >= ipHdrLen + 8)
                                {
                                    byte type = buf[ipHdrLen];
                                    // ICMP 匹配
                                    if (type == 0 || type == 11)
                                    {
                                        ushort rcvId = 0, rcvSeq = 0;
                                        if (type == 0) { rcvId = BitConverter.ToUInt16(buf, ipHdrLen + 4); rcvSeq = BitConverter.ToUInt16(buf, ipHdrLen + 6); }
                                        else { int innerIpHdrLen = (buf[ipHdrLen + 8] & 0x0F) * 4; int eOff = ipHdrLen + 8 + innerIpHdrLen; if (len > eOff + 6) { rcvId = BitConverter.ToUInt16(buf, eOff + 4); rcvSeq = BitConverter.ToUInt16(buf, eOff + 6); } }
                                        if (rcvId == _instanceIdentifier && roundSeqStore.TryRemove(rcvSeq, out var stcs))
                                            stcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                                    }
                                    // TCP/UDP 匹配（源端口在嵌入的 IP 头之后），增加协议和目标端口验证
                                    if (type == 11 || type == 3)
                                    {
                                        int innerIpHdrLen = (buf[ipHdrLen + 8] & 0x0F) * 4;
                                        if (innerIpHdrLen < 20) continue;
                                        int expectedProto = (protocol == "TCP") ? 6 : 17;
                                        int innerProto = buf[ipHdrLen + 8 + 9];
                                        if (innerProto != expectedProto) continue;
                                        int portOff = ipHdrLen + 8 + innerIpHdrLen;
                                        if (len <= portOff + 3) continue;
                                        int sport = (buf[portOff] << 8) + buf[portOff + 1];
                                        int dport = (buf[portOff + 2] << 8) + buf[portOff + 3];
                                        if (dport != targetPort) continue;
                                        if (roundPortStore.TryRemove(sport, out var ptcs))
                                            ptcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                                    }
                                }
                            }
                            catch (SocketException) { continue; }
                            catch { break; }
                        }
                    }, roundCts.Token);

                    // 逐跳探测（每轮每跳 1 个探针，间隔100ms错开发起）
                    bool isIcmpProto = (protocol == "ICMP");
                    var lastDraw = Stopwatch.StartNew();
                    int roundTargetHop = 0;

                    for (int ttl = 1; ttl <= effectiveMaxHops; ttl++)
                    {
                        if (token.IsCancellationRequested) break;
                        int ct = ttl;

                        await Task.Run(async () =>
                        {
                            var result = new HopResult(ct);
                            try
                            {
                                if (isIcmpProto)
                                {
                                    using (Socket sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                                    {
                                        sendSocket.Bind(new IPEndPoint(localIp, 0));
                                        sendSocket.Ttl = (short)ct;
                                        if (token.IsCancellationRequested) { roundResults[ct] = result; return; }
                                        int seq = ct;
                                        byte[] req = CreateIcmpPacket((ushort)seq);
                                        var tcs = new TaskCompletionSource<IPAddress>();
                                        roundSeqStore[seq] = tcs;
                                        Stopwatch sw = Stopwatch.StartNew();
                                        try
                                        {
                                            sendSocket.SendTo(req, new IPEndPoint(targetIp, 0));
                                            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, token));
                                            sw.Stop();
                                            roundSeqStore.TryRemove(seq, out _);
                                            if (done == tcs.Task)
                                            {
                                                IPAddress addr = await tcs.Task;
                                                if (geoChecked) result.GeoInfo = GetLocalGeoInfo(addr.ToString());
                                                result.ReplyAddress = addr;
                                                result.RTTs[0] = sw.Elapsed.TotalMilliseconds;
                                                if (addr.Equals(targetIp)) result.TargetReached = true;
                                            }
                                        }
                                        catch (Exception ex) when (!(ex is OperationCanceledException))
                                        {
                                            sw.Stop();
                                            result.RTTs[0] = -2;
                                            roundSeqStore.TryRemove(seq, out _);
                                        }
                                    }
                                }
                                else
                                {
                                    using (Socket sendSocket = new Socket(AddressFamily.InterNetwork,
                                        protocol == "TCP" ? SocketType.Stream : SocketType.Dgram,
                                        protocol == "TCP" ? ProtocolType.Tcp : ProtocolType.Udp))
                                    {
                                        sendSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                                        int myPort = mtrFixedPort + ct;
                                        sendSocket.Bind(new IPEndPoint(localIp, myPort));
                                        sendSocket.Ttl = (short)ct;
                                        if (token.IsCancellationRequested) { roundResults[ct] = result; return; }
                                        var tcs = new TaskCompletionSource<IPAddress>();
                                        roundPortStore[myPort] = tcs;
                                        Stopwatch sw = Stopwatch.StartNew();
                                        try
                                        {
                                            Task<IPAddress> tcpResultTask = null;
                                            if (protocol == "TCP")
                                            {
                                                var tcpTcs = new TaskCompletionSource<IPAddress>();
                                                tcpResultTask = tcpTcs.Task;
                                                var _ = sendSocket.ConnectAsync(new IPEndPoint(targetIp, targetPort))
                                                    .ContinueWith(t =>
                                                    {
                                                        // 只有TCP握手成功才认为到达目标
                                                        if (t.Status == TaskStatus.RanToCompletion)
                                                            tcpTcs.TrySetResult(targetIp);
                                                    }, TaskContinuationOptions.NotOnCanceled);
                                            }
                                            else
                                            {
                                                sendSocket.SendTo(GetUdpPayload(targetPort), new IPEndPoint(targetIp, targetPort));
                                                sendSocket.Close();
                                            }
                                            Task completed;
                                            if (tcpResultTask != null)
                                                completed = await Task.WhenAny(tcs.Task, tcpResultTask, Task.Delay(timeout, token));
                                            else
                                                completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, token));
                                            sw.Stop();
                                            roundPortStore.TryRemove(myPort, out _);
                                            if (completed == tcs.Task)
                                            {
                                                IPAddress addr = await tcs.Task;
                                                if (geoChecked) result.GeoInfo = GetLocalGeoInfo(addr.ToString());
                                                result.ReplyAddress = addr;
                                                result.RTTs[0] = sw.Elapsed.TotalMilliseconds;
                                                if (addr.Equals(targetIp)) result.TargetReached = true;
                                            }
                                            else if (tcpResultTask != null && completed == tcpResultTask)
                                            {
                                                if (geoChecked) result.GeoInfo = GetLocalGeoInfo(targetIp.ToString());
                                                result.ReplyAddress = targetIp;
                                                result.RTTs[0] = sw.Elapsed.TotalMilliseconds;
                                                result.TargetReached = true;
                                            }
                                        }
                                        catch (Exception ex) when (!(ex is OperationCanceledException))
                                        {
                                            sw.Stop();
                                            result.RTTs[0] = -2;
                                            roundPortStore.TryRemove(myPort, out _);
                                        }
                                    }
                                }
                            }
                            catch (OperationCanceledException) { }
                            roundResults[ct] = result;
                        }, token);

                        // 即时更新统计
                        if (roundResults.TryGetValue(ct, out var hop))
                        {
                            var stat = stats[ct];
                            stat.Sent += 1;
                            if (hop.HasAnyResponse)
                            {
                                var ip = hop.ReplyAddress;
                                stat.ReplyAddress = ip;
                                string ipStr = ip.ToString();
                                // 统计IP出现次数
                                if (!stat.IpAppearCount.ContainsKey(ipStr))
                                    stat.IpAppearCount[ipStr] = 0;
                                stat.IpAppearCount[ipStr]++;
                                if (!stat.AllIPs.Any(a => a.Equals(ip)))
                                {
                                    stat.AllIPs.Add(ip);
                                    if (!stat.FirstSeenRound.ContainsKey(ipStr))
                                        stat.FirstSeenRound[ipStr] = round;
                                    if (hop.GeoInfo != null)
                                        stat.IpGeoCache[ipStr] = hop.GeoInfo;
                                    if (geoChecked
                                        && _enrichPending.TryAdd(ipStr, 0)
                                        && string.IsNullOrEmpty(IanaReservedIP.Check(ipStr)))
                                    {
                                        int capTtl = ct;
                                        string capIp = ipStr;
                                        _ = Task.Run(() => EnrichGeoOnlineAsync(capIp, capTtl, stats, token), token);
                                    }
                                }
                                stat.GeoInfo = stat.IpGeoCache.ContainsKey(ipStr) ? stat.IpGeoCache[ipStr] : hop.GeoInfo;
                                for (int i = 0; i < 4; i++)
                                    if (hop.RTTs[i] >= 0) { stat.Received++; stat.RTTs.Add(hop.RTTs[i]); }
                                if (geoChecked && _enrichPending.TryAdd(ipStr, 0)
                                    && string.IsNullOrEmpty(IanaReservedIP.Check(ipStr)))
                                {
                                    int captureTtl = ct;
                                    string captureIp = ipStr;
                                    _ = Task.Run(() => EnrichGeoOnlineAsync(captureIp, captureTtl, stats, token), token);
                                }
                            }
                            if (hop.TargetReached && roundTargetHop == 0)
                                roundTargetHop = ct;
                        }

                        if (lastDraw.ElapsedMilliseconds >= 100)
                        {
                            int displayHops = isFirstRound ? ct : effectiveMaxHops;
                            DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout, protocol, round, geoChecked, targetEverReached, displayHops);
                            lastDraw.Restart();
                        }

                        if (roundTargetHop > 0) break;

                        if (ttl < effectiveMaxHops)
                            await Task.Delay(100, token);
                    }

                    if (roundTargetHop > 0 && confirmedTargetHop == 0)
                    {
                        confirmedTargetHop = roundTargetHop;
                        effectiveMaxHops = confirmedTargetHop;
                    }
                    if (roundTargetHop > 0) targetEverReached = true;
                    // 首轮必定显示最终表格；后续轮由实时刷新处理
                    if (isFirstRound || confirmedTargetHop > 0)
                        DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout, protocol, round, geoChecked, targetEverReached, effectiveMaxHops);
                    isFirstRound = false;

                    // 停止本轮接收循环
                    roundCts.Cancel();
                    try { await receiveTask; } catch { }

                    if (token.IsCancellationRequested) break;

                    // 轮间间隔
                    await Task.Delay(800, token);
                }
            }
        }

        private IPAddress GetLocalExportIP(IPAddress targetIp)
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(targetIp, 65530);
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            }
            catch { return IPAddress.Parse("127.0.0.1"); }
        }

        private class HopResult
        {
            public int TTL;
            public IPAddress ReplyAddress;
            public bool TargetReached;
            public double[] RTTs = new double[4]; // >=0=ms, -1=timeout, -2=error
            public string GeoInfo; // 预缓存的归属地信息（线程池线程计算，避免 UI 线程磁盘 I/O）

            public bool HasAnyResponse => ReplyAddress != null;

            public HopResult(int ttl)
            {
                TTL = ttl;
                for (int i = 0; i < 4; i++) RTTs[i] = -1;
            }
        }

        private class MtrHopStats
        {
            public int TTL;
            public IPAddress ReplyAddress;
            public string GeoInfo;
            public List<IPAddress> AllIPs = new List<IPAddress>();
            public Dictionary<string, string> IpGeoCache = new Dictionary<string, string>();
            public Dictionary<string, int> FirstSeenRound = new Dictionary<string, int>();
            public Dictionary<string, int> IpAppearCount = new Dictionary<string, int>();
            public int Sent;
            public int Received;
            public List<double> RTTs = new List<double>();
            public double LastRTT => RTTs.Count > 0 ? RTTs[RTTs.Count - 1] : double.NaN;

            public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
            public double AvgRTT => RTTs.Count == 0 ? double.NaN : RTTs.Average();
            public double BestRTT => RTTs.Count == 0 ? double.NaN : RTTs.Min();
            public double WorstRTT => RTTs.Count == 0 ? double.NaN : RTTs.Max();
        }

        /// <summary>
        /// 线程安全的归属地预计算（可在线程池线程调用，避免阻塞 UI）
        /// </summary>
        private string ComputeCachedGeoInfo(string ip)
        {
            try
            {
                string geo = GetIpLocationString(ip);
                string geoCN = Api2.GetGeoCNLocationQuick(ip);
                return string.IsNullOrEmpty(geoCN) ? geo : $"{geoCN} | {geo}";
            }
            catch { return null; }
        }

        private void DisplaySingleHop(HopResult result, bool geoChecked, bool isV6 = false, int probeCount = 4)
        {
            if (this.IsDisposed || richTextBox1.IsDisposed) return;

            AppendColorText(result.TTL.ToString().PadLeft(3), Color.Yellow, false);
            AppendColorText("   ", Color.White, false);

            if (result.HasAnyResponse)
            {
                if (isV6)
                {
                    AppendColorText("  " + result.ReplyAddress.ToString() + "\n", Color.Yellow, false);
                    AppendColorText("               ", Color.White, false);
                }
                else
                {
                    AppendColorText("  " + result.ReplyAddress.ToString().PadRight(15), Color.Yellow, false);
                }
            }
            else if (isV6)
            {
                AppendColorText("\n  -            ", Color.Orange, false);
            }
            else
            {
                AppendColorText("  -              ", Color.Orange, false);
            }

            for (int i = 0; i < probeCount; i++)
            {
                double rtt = result.RTTs[i];
                if (rtt >= 0)
                    AppendColorText($"{rtt:F1} ms".PadLeft(10), Color.White, false);
                else if (rtt <= -1.5)
                    AppendColorText("       ERR", Color.Orange, false);
                else
                    AppendColorText("         *", Color.Orange, false);
            }

            if (result.HasAnyResponse)
            {
                AppendColorText("\n", Color.White, false);
                if (geoChecked && !isV6)
                {
                    string combined = result.GeoInfo;
                    if (string.IsNullOrEmpty(combined))
                    {
                        string geo = GetIpLocationString(result.ReplyAddress.ToString());
                        string geoCN = Api2.GetGeoCNLocationQuick(result.ReplyAddress.ToString());
                        combined = string.IsNullOrEmpty(geoCN) ? geo : $"{geoCN} | {geo}";
                    }
                    var defaultFont2 = richTextBox1.Font;
                    using (var smallFont2 = new Font(defaultFont2.FontFamily, Math.Max(defaultFont2.Size - 1.5f, 7f)))
                    {
                        richTextBox1.SelectionFont = smallFont2;
                        AppendColorText("             -> " + combined + "\n", ColorTranslator.FromHtml("#a8a5ff"), false);
                        richTextBox1.SelectionFont = defaultFont2;
                        _hopGeoOriginal[result.TTL] = combined;
                        if (result.ReplyAddress != null)
                            _ipToHop[result.ReplyAddress.ToString()] = result.TTL;
                    }
                }
            }
            else
            {
                AppendColorText("   请求超时.\n", Color.Orange, false);
            }

            richTextBox1.ScrollToCaret();
        }

        private void DrawMtrTable(ConcurrentDictionary<int, MtrHopStats> stats, string targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int round, bool geoChecked, bool targetReached, int effectiveMaxHops)
        {
            if (this.IsDisposed || richTextBox1.IsDisposed) return;

            int firstLine = SendMessage(richTextBox1.Handle, EM_GETFIRSTVISIBLELINE, 0, 0);
            SendMessage(richTextBox1.Handle, WM_SETREDRAW, 0, 0);

            richTextBox1.Clear();

            AppendColorText($">> [MTR] 第 {round} 轮 | 目标: {targetIp} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", Color.Lime, false);
            AppendColorText($"   使用接口: {localIp} 跳数:{maxHops} 超时:{timeout}ms 协议:{protocol} | NICX By Yumeyo\n", Color.LightSkyBlue, false);

            AppendColorText("\n   # IP                    Loss%   Sent  Rcvd   Last   Avg   Best   Worst\n", Color.Cyan, false);
            //AppendColorText("──── ──────────────── ────── ──── ──── ───── ───── ───── ─────\n", Color.Gray, false);

            var sorted = stats.Where(kv => kv.Key <= effectiveMaxHops).OrderBy(kv => kv.Key);
            foreach (var kvp in sorted)
            {
                var s = kvp.Value;

                AppendColorText(kvp.Key.ToString().PadLeft(4) + " ", Color.Yellow, false);

                if (s.ReplyAddress != null)
                {
                    bool isTarget = s.ReplyAddress.ToString() == targetIp;
                    AppendColorText(s.ReplyAddress.ToString().PadRight(20) + " ", isTarget ? Color.LightGreen : Color.Yellow, false);
                }
                else
                    AppendColorText("-                    ", Color.Orange, false);

                double loss = s.LossPercent;
                Color lossColor = loss >= 50 ? Color.FromArgb(255, 160, 140) : (loss > 0 ? Color.FromArgb(255, 255, 190) : Color.White);
                AppendColorText($"{loss:F1}%".PadLeft(6) + " ", lossColor, false);

                AppendColorText(s.Sent.ToString().PadLeft(5) + " ", Color.White, false);
                AppendColorText(s.Received.ToString().PadLeft(5) + " ", Color.White, false);

                AppendRttCell(s.LastRTT);
                AppendRttCellAvg(s.AvgRTT);
                AppendRttCellBest(s.BestRTT);
                AppendRttCellWorst(s.WorstRTT);

                AppendColorText("\n", Color.White, false);

                // 归属地用更小字号
                var defaultFont = richTextBox1.Font;
                using (var smallFont = new Font(defaultFont.FontFamily, Math.Max(defaultFont.Size - 1.5f, 7f)))
                {
                    if (geoChecked && s.GeoInfo != null)
                    {
                        richTextBox1.SelectionFont = defaultFont;
                        richTextBox1.SelectionFont = smallFont;
                        AppendColorText("             -> " + s.GeoInfo, ColorTranslator.FromHtml("#a8a5ff"), false);
                        richTextBox1.SelectionFont = defaultFont;
                        AppendColorText("\n", Color.White, false);
                    }

                    // 同一跳的其他 IP（-> 箭头与主 IP 对齐）
                    var altIPs = s.AllIPs.Where(ip => !ip.Equals(s.ReplyAddress)).ToList();
                    foreach (var altIp in altIPs)
                    {
                        richTextBox1.SelectionFont = defaultFont;
                        string altIpStr = altIp.ToString();
                        s.FirstSeenRound.TryGetValue(altIpStr, out int firstRnd);
                        s.IpAppearCount.TryGetValue(altIpStr, out int appearCnt);
                        string roundTag = "";
                        if (appearCnt > 1 && firstRnd > 0)
                            roundTag = $" ({firstRnd}/{appearCnt})";
                        bool isTargetAlt = altIpStr == targetIp;
                        AppendColorText("      " + altIpStr.PadRight(20), isTargetAlt ? Color.LightGreen : Color.Yellow, false);
                        if (roundTag.Length > 0)
                            AppendColorText(roundTag, Color.Gray, false);
                        if (geoChecked && s.IpGeoCache.TryGetValue(altIp.ToString(), out var altGeo))
                        {
                            richTextBox1.SelectionFont = smallFont;
                            AppendColorText(" -> " + altGeo, ColorTranslator.FromHtml("#a8a5ff"), false);
                        }
                        richTextBox1.SelectionFont = defaultFont;
                        AppendColorText("\n", Color.White, false);
                    }
                }
            }

            string status = targetReached ? "(目标已达)" : "";
            AppendColorText($"\n>> 第 {round} 轮完成 {status}, 按[停止]结束 | {Global.exeName}", Color.Green, true);

            SendMessage(richTextBox1.Handle, WM_SETREDRAW, 1, 0);
            richTextBox1.Invalidate();

            // clamp 到有效行范围，避免截断后滚出空白
            SendMessage(richTextBox1.Handle, EM_LINESCROLL, 0, -9999);
            int lastChar = Math.Max(0, richTextBox1.TextLength - 1);
            int totalLines = richTextBox1.GetLineFromCharIndex(lastChar) + 1;
            int restoreLine = Math.Max(0, Math.Min(firstLine, totalLines - 1));
            if (restoreLine > 0)
                SendMessage(richTextBox1.Handle, EM_LINESCROLL, 0, restoreLine);
        }

        private void AppendRttCell(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.White, false);
        }

        private void AppendRttCellAvg(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.FromArgb(255, 255, 190), false);
        }

        private void AppendRttCellBest(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.Lime, false);
        }

        private void AppendRttCellWorst(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.Red, false);
        }

        private void Trace_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            // 1. 停止测试任务
            if (isRunning)
            {
                isRunning = false;
                SetUIState(false);
                cts?.Cancel();
            }
            cts?.Dispose();
            cts = null;

            // 2. 检查是否需要还原防火墙设置
            if (isManualChanged)
            {
                bool curOn = IsFirewallEnabled();
                bool curRule = IsICMPRuleExisted();
                string stateStr = !curOn ? "防火关" : (curRule ? "已放行" : "防火开");

                DialogResult dr = MessageBox.Show(
                    $"当前防火墙手动设为【{stateStr}】，正在退出Trace+。\n需要还原之前状态吗？\n\n" +
                    "【是】还原并退出 (可能会有多个UAC提示框, 请允许)\n" +
                    "【否】保持并退出\n" +
                    "【取消】手滑了，先不退出",
                    "需要还原吗",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    // 使用同步执行，确保命令跑完
                    // A. 还原防火墙总开关状态
                    string fwCommand = initialFirewallOn ? "advfirewall set allprofiles state on" : "advfirewall set allprofiles state off";
                    RunNetshSync(fwCommand);

                    // B. 还原规则状态 (不管防火墙开没开，规则都要对齐初始状态)
                    if (initialRuleExisted)
                    {
                        // 初始有规则 -> 确保现在也有 (先删再加，保底做法)
                        RunNetshSync($"advfirewall firewall delete rule name=\"{ruleName}\"");
                        RunNetshSync($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv4");
                    }
                    else
                    {
                        // 初始没规则 -> 确保现在删掉
                        RunNetshSync($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    }
                }
                else if (dr == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return; // 梦酱注意：如果是取消，直接返回，不要执行后面的 Dispose
                }
            }

            try
            {
                flashTimer?.Stop();
                flashTimer?.Dispose();
                flashTimer = null;
                _ip2regionSearcherV4?.Dispose();
                _ip2regionSearcherV6?.Dispose();
                _ip2regionSearcherV4 = null;
                _ip2regionSearcherV6 = null;
            }
            catch { }
        }

        // 梦酱专属辅助方法：同步运行 netsh
        private void RunNetshSync(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", arguments)
                {
                    // 关键点 1：必须为 true，否则下面的 runas 不起作用，也就不会弹 UAC
                    UseShellExecute = true,

                    // 关键点 2：申请管理员权限
                    Verb = "runas",

                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(psi))
                {
                    // 关键点 3：等它运行完。3000毫秒（3秒）足够 netsh 处理完了
                    p?.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                // 如果梦酱在弹出的 UAC 框点了“否”，会进到这里
                Debug.WriteLine(ex.Message);
            }
        }

        private void lblTarget_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (isRunning && cts != null) cts.Cancel();

                SaveSettings();

                Point currentLocation = this.Location;
                Size currentSize = this.Size;

                Trace newForm = new Trace();
                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentLocation;
                newForm.Size = currentSize;

                newForm.Show();
                this.Close();
                this.Dispose();
            }
        }
        private void lblLocalEnd_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                DialogResult result = MessageBox.Show(
                    "确定以管理员身份重启程序？\n(TCP/UDP Trace需管理员身份运行，如误点请取消)",
                    "提权确认框",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    if (isRunning && cts != null) cts.Cancel();
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = Application.ExecutablePath;
                    startInfo.WorkingDirectory = Environment.CurrentDirectory;
                    startInfo.Verb = "runas";

                    try
                    {
                        Process.Start(startInfo);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("提权失败: " + ex.Message, "提权已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }
        private async Task CheckIpSearcherDependencies()
        {
            string appPath = AppDomain.CurrentDomain.BaseDirectory;

            // 需要检查的文件清单：文件名 -> 用途说明
            Dictionary<string, string> requiredFiles = new Dictionary<string, string>
    {
        { "IP2Region.Net.dll", "用于 IP2RG 本地数据库核心库" },
        { "ip2region.v4.xdb", "用于 IP2RG IPv4 本地数据库" },
        { "ip2region.v6.xdb", "用于 IP2RG IPv6 本地数据库" },
        { "GeoCN.mmdb", "用于 MaxMind 本地数据库" },
        { "Microsoft.Bcl.AsyncInterfaces.dll", "用于 IP2RG 本地数据库依赖" },
        { "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Memory.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Numerics.Vectors.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Runtime.CompilerServices.Unsafe.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Threading.Tasks.Extensions.dll", "用于 IP2RG 本地数据库依赖" }
    };

            List<string> missingFiles = new List<string>();

            foreach (var item in requiredFiles)
            {
                string fullPath = Path.Combine(appPath, item.Key);
                if (!File.Exists(fullPath))
                {
                    missingFiles.Add($"{item.Key}（{item.Value}）");
                }
            }

            if (missingFiles.Count > 0)
            {
                checkGEO.Checked = false;
                checkGEO.Enabled = false;

                string msg =
                    "提示：缺少运行 IP 归属地查询所需的文件。\n\n" +
                    string.Join("\n", missingFiles.Select(f => "• " + f)) +
                    "\n\n你可正常进行 Trace 测试，但无法显示每一跳的 IP 归属地。\n" +
                    "如有需要，请重新解压程序、检查杀毒软件或检查相关数据库。";

                MessageBox.Show(
                    msg,
                    "IP2RG 归属地组件缺失",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
            else
            {
                checkGEO.Enabled = true;
            }

            await Task.CompletedTask;
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
                string mtrPrefix = checkMTR.Checked ? "_MTR" : "";
                sfd.FileName = $"NICX_Trace{mtrPrefix}_{pingType}_{comboTargetIP.Text}_{saveTime}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // 3. 准备要保存的内容
                        StringBuilder sb = new StringBuilder();

                        sb.AppendLine($"=== 欢迎使用 Trace+ ❤ 网络综合查询器X by Yumeyo ===");
                        sb.AppendLine($"🔥 本次 Trace+ 输出详情: \n");
                        sb.AppendLine(richTextBox1.Text);
                        sb.AppendLine($"=== 感谢使用 Trace+ ❤ 网络综合查询器X by Yumeyo ===");
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
        private async void btnWDF_Click(object sender, EventArgs e)
        {
            bool isOn = IsFirewallEnabled();
            bool hasRule = IsICMPRuleExisted();

            if (!isOn)
            {
                // 状态 1：当前关闭
                if (MessageBox.Show("当前防火墙【关闭】。开启防火墙吗？", "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await RunNetshCmd("advfirewall set allprofiles state on");
                    isManualChanged = true;
                }
            }
            else if (hasRule)
            {
                // 状态 2：已放行
                if (MessageBox.Show("当前防火墙【开启】且【已放行】查询器X入站。删除放行规则吗？", "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await RunNetshCmd($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    isManualChanged = true;
                }
            }
            else
            {
                // 状态 3：开启未放行
                DialogResult dr = MessageBox.Show("当前防火墙【开启】且【未放行】查询器X，\n只可使用系统默认网卡(ICMP兼容模式)\n\n要解锁网卡选择功能, 请选择一个操作：\n【是】 关闭 防火墙\n【否】 添加 放行规则\n【取消】暂不修改",
                    "解锁方法选择", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    await RunNetshCmd("advfirewall set allprofiles state off");
                    isManualChanged = true;
                }
                else if (dr == DialogResult.No)
                {
                    // 添加前先删一次确保不重复
                    await RunNetshCmd($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    await RunNetshCmd($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv4");
                    isManualChanged = true;
                }
            }
            UpdateWDFUI();
        }
    }
}
