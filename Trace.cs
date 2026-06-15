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

        // INI 读写
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "Trace";

        // ip2region v4 搜索器（仅 IPv4）
        private Searcher _ip2regionSearcherV4;
        private Searcher _ip2regionSearcherV6;

        public Trace()
        {
            InitializeComponent();
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
            // 1. 先做那些“秒开”的基础 UI 初始化
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
                    Font modernFont = new Font("Cascadia Mono", 9.5F, FontStyle.Regular);

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

            // 2. 开启异步任务，不阻塞窗口显示
            await Task.Run(() =>
            {
                // 在后台线程初始化 IP 数据库
                InitIp2Region();

                // 在后台线程检查防火墙和规则
                initialFirewallOn = _lastFwStatus = IsFirewallEnabled();
                initialRuleExisted = _lastRuleStatus = IsICMPRuleExisted();

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

            });

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
                AppendColorText($"开测时间: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}\n", Color.LightPink, false);
                AppendColorText($"开始 {selectedMethod} Tracert 到 {finalTargetIp} {finalPort} ...\n", Color.Lime, false);
                if (comboLocalEnd.Text.Contains("ICMP兼容模式"))
                {
                    AppendColorText($"使用接口: {comboLocalEnd.Text} 跳数:{maxHops} 超时:{maxDelayMs}ms\n\n", Color.LightSkyBlue, false);
                }
                else
                {
                    AppendColorText($"使用接口: {localExportIp} 跳数:{maxHops} 超时:{maxDelayMs}ms\n\n", Color.LightSkyBlue, false);
                }

                if (selectedMethod == "ICMP")
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
                AppendColorText("\n\n ■ 用户手动停止测试 \n", Color.Yellow, true);
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
                        PingOptions options = new PingOptions(ttl, true);
                        byte[] buffer = Encoding.ASCII.GetBytes("YumeyoNICX_Trace_Packet");
                        Stopwatch sw = Stopwatch.StartNew();
                        try
                        {
                            PingReply reply = await pingSender.SendPingAsync(targetIp, timeout, buffer, options);
                            sw.Stop();
                            if (reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired)
                            {
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
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                if (this.IsDisposed || token.IsCancellationRequested) break;
                var w = Stopwatch.StartNew();
                while (!results.TryGetValue(ttl, out _) && !allDone.IsCompleted && !token.IsCancellationRequested
                    && w.ElapsedMilliseconds < timeout * 4 + 150) await Task.Delay(50);
                HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                DisplaySingleHop(hop, geoChecked);
                if (hop.TargetReached) break;
            }
            try { await allDone; } catch { }
        }

        // ==========================================
        // 第一部分：校验和计算
        // ==========================================
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
                try { receiver.Bind(new IPEndPoint(localIp, 0)); receiver.IOControl(IOControlCode.ReceiveAll, new byte[] { 1, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 }); } catch { }

                var receiveLoopTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[1024];
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    while (!token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            if (receiver.Poll(5000, SelectMode.SelectRead))
                            {
                                int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                                int icmpStart = 20;
                                if (len >= icmpStart + 8)
                                {
                                    byte type = rcvBuffer[icmpStart];
                                    ushort rcvId = 0, rcvSeq = 0;
                                    if (type == 0) { rcvId = BitConverter.ToUInt16(rcvBuffer, icmpStart + 4); rcvSeq = BitConverter.ToUInt16(rcvBuffer, icmpStart + 6); }
                                    else if (type == 11) { int eOff = icmpStart + 8 + 20; if (len > eOff + 6) { rcvId = BitConverter.ToUInt16(rcvBuffer, eOff + 4); rcvSeq = BitConverter.ToUInt16(rcvBuffer, eOff + 6); } }
                                    else continue;
                                    if (rcvId == _instanceIdentifier && seqTcsStore.TryRemove(rcvSeq, out var tcs))
                                        tcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                                }
                            }
                        }
                        catch { break; }
                    }
                }, token);

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
                                    if (done == tcs.Task) { IPAddress addr = await tcs.Task; result.ReplyAddress = addr; result.RTTs[i] = sw.Elapsed.TotalMilliseconds; if (addr.Equals(targetIp)) result.TargetReached = true; }
                                }
                                catch (Exception ex) when (!(ex is OperationCanceledException)) { sw.Stop(); result.RTTs[i] = -2; seqTcsStore.TryRemove(seq, out _); }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    results[ttl] = result;
                }

                var tasks = new List<Task>();
                for (int ttl = 1; ttl <= maxHops; ttl++) { int ct = ttl; tasks.Add(Task.Run(() => ProbeHop(ct, token), token)); }
                var allDone = Task.WhenAll(tasks);
                int maxWaitMs = timeout * 4 + 150; bool seenTimeout = false;
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    if (this.IsDisposed || token.IsCancellationRequested) break;
                    if (!seenTimeout) { var waited = Stopwatch.StartNew(); while (!results.TryGetValue(ttl, out _) && !allDone.IsCompleted && !token.IsCancellationRequested) { await Task.Delay(33); if (waited.ElapsedMilliseconds > maxWaitMs) { bool fd = false; for (int f = ttl + 1; f <= Math.Min(ttl + 6, maxHops); f++) if (results.ContainsKey(f)) { fd = true; break; } if (fd) { seenTimeout = true; break; } } } }
                    else { var waited = Stopwatch.StartNew(); while (!results.TryGetValue(ttl, out _) && waited.ElapsedMilliseconds < 200 && !allDone.IsCompleted && !token.IsCancellationRequested) await Task.Delay(33); }
                    HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                    DisplaySingleHop(hop, geoChecked);
                    if (hop.TargetReached) break;
                    await Task.Delay(33);
                }
                try { await allDone; } catch { }
            }
        }

        // ==========================================
        // TCP/UDP Trace实现
        // ==========================================
        private async Task RunSocketTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int customPort, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            var results = new ConcurrentDictionary<int, HopResult>();
            var packetTcsStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();

            using (Socket receiver = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            {
                try { receiver.Bind(new IPEndPoint(localIp, 0)); receiver.IOControl(IOControlCode.ReceiveAll, new byte[] { 1, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 }); } catch { }

                var receiveLoopTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[8192];
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    while (!token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            if (receiver.Poll(5000, SelectMode.SelectRead))
                            {
                                int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                                int icmpOff = 20;
                                if (len >= icmpOff + 8)
                                {
                                    int icmpType = rcvBuffer[icmpOff];
                                    if (icmpType == 11 || icmpType == 3)
                                    {
                                        int portOff = icmpOff + 8 + 20;
                                        if (len <= portOff + 1) continue;
                                        int sport = (rcvBuffer[portOff] << 8) + rcvBuffer[portOff + 1];
                                        if (packetTcsStore.TryRemove(sport, out var tcs))
                                            tcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                                    }
                                }
                            }
                        }
                        catch { break; }
                    }
                }, token);

                async Task ProbeHop(int ttl, CancellationToken hopToken)
                {
                    var result = new HopResult(ttl);
                    try
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            if (hopToken.IsCancellationRequested) break;
                            using (Socket senderSocket = new Socket(AddressFamily.InterNetwork,
                                protocol == "TCP" ? SocketType.Stream : SocketType.Dgram,
                                protocol == "TCP" ? ProtocolType.Tcp : ProtocolType.Udp))
                            {
                                try
                                {
                                    senderSocket.Bind(new IPEndPoint(localIp, 0));
                                    int myPort = ((IPEndPoint)senderSocket.LocalEndPoint).Port;
                                    senderSocket.Ttl = (short)ttl;
                                    var tcs = new TaskCompletionSource<IPAddress>();
                                    packetTcsStore[myPort] = tcs;
                                    Stopwatch sw = Stopwatch.StartNew();
                                    Task tcpOk = new TaskCompletionSource<bool>().Task;
                                    if (protocol == "TCP")
                                    {
                                        var stcs = new TaskCompletionSource<bool>();
                                        var rc = senderSocket.ConnectAsync(new IPEndPoint(targetIp, customPort));
                                        var _ = rc.ContinueWith(__ => { if (rc.Status == TaskStatus.RanToCompletion && senderSocket.Connected) stcs.TrySetResult(true); });
                                        tcpOk = stcs.Task;
                                    }
                                    else senderSocket.SendTo(Encoding.ASCII.GetBytes("Yume"), new IPEndPoint(targetIp, customPort));
                                    var done = await Task.WhenAny(tcs.Task, tcpOk, Task.Delay(timeout, hopToken));
                                    sw.Stop();
                                    packetTcsStore.TryRemove(myPort, out _);
                                    if (done == tcs.Task) { IPAddress addr = await tcs.Task; result.ReplyAddress = addr; result.RTTs[i] = sw.Elapsed.TotalMilliseconds; if (addr.Equals(targetIp)) result.TargetReached = true; }
                                    else if (done == tcpOk) { result.ReplyAddress = targetIp; result.RTTs[i] = sw.Elapsed.TotalMilliseconds; result.TargetReached = true; }
                                }
                                catch (Exception ex) when (!(ex is OperationCanceledException)) { result.RTTs[i] = -2; }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    results[ttl] = result;
                }

                var tasks = new List<Task>();
                for (int ttl = 1; ttl <= maxHops; ttl++) { int ct = ttl; tasks.Add(Task.Run(() => ProbeHop(ct, token), token)); }
                var allDone = Task.WhenAll(tasks);
                int maxWaitMs = timeout * 4 + 150; bool seenTimeout = false;
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    if (this.IsDisposed || token.IsCancellationRequested) break;
                    if (!seenTimeout) { var waited = Stopwatch.StartNew(); while (!results.TryGetValue(ttl, out _) && !allDone.IsCompleted && !token.IsCancellationRequested) { await Task.Delay(33); if (waited.ElapsedMilliseconds > maxWaitMs) { bool fd = false; for (int f = ttl + 1; f <= Math.Min(ttl + 6, maxHops); f++) if (results.ContainsKey(f)) { fd = true; break; } if (fd) { seenTimeout = true; break; } } } }
                    else { var waited = Stopwatch.StartNew(); while (!results.TryGetValue(ttl, out _) && waited.ElapsedMilliseconds < 200 && !allDone.IsCompleted && !token.IsCancellationRequested) await Task.Delay(33); }
                    HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                    DisplaySingleHop(hop, geoChecked);
                    if (hop.TargetReached) break;
                    await Task.Delay(33);
                }
                try { await allDone; } catch { }
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

            public bool HasAnyResponse => ReplyAddress != null;

            public HopResult(int ttl)
            {
                TTL = ttl;
                for (int i = 0; i < 4; i++) RTTs[i] = -1;
            }
        }

        private void DisplaySingleHop(HopResult result, bool geoChecked, bool isV6 = false)
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
                AppendColorText("\n               ", Color.White, false);
            }
            else
            {
                AppendColorText("                 ", Color.White, false);
            }

            for (int i = 0; i < 4; i++)
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
                    string geo = GetIpLocationString(result.ReplyAddress.ToString());
                    string geoCN = Api2.GetGeoCNLocationQuick(result.ReplyAddress.ToString());
                    string combined = string.IsNullOrEmpty(geoCN) ? geo : $"{geoCN} | {geo}";
                    AppendColorText("             └─ " + combined + "\n", ColorTranslator.FromHtml("#a8a5ff"), false);
                }
            }
            else
            {
                AppendColorText("   请求超时.\n", Color.Orange, false);
            }

            richTextBox1.ScrollToCaret();

            if (result.TargetReached)
            {
                AppendColorText("\nTrace 完成.\n", Color.Lime, false);
            }
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
                sfd.FileName = $"NICX_Trace_{pingType}_{comboTargetIP.Text}_{saveTime}.txt";

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
