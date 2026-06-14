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
        private CancellationTokenSource cts;
        private bool isRunning = false;
        private Random random = new Random();

        private bool isManualChanged = false;
        private bool initialFirewallOn;
        private bool initialRuleExisted;
        private string ruleName = "NICX_ICMP_Unlock";
        private System.Windows.Forms.Timer flashTimer;

        // 缓存防火墙状态，避免重复查询
        private bool _lastFwStatus;
        private bool _lastRuleStatus;

        // 当前窗口唯一标识符，防止多开窗口时 ICMP 串扰
        private ushort _instanceIdentifier;

        // INI 读写
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "Trace";

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

        private Searcher _ip2regionSearcherV4;
        private Searcher _ip2regionSearcherV6;

        public Trace()
        {
            InitializeComponent();
            _instanceIdentifier = (ushort)(DateTime.Now.Ticks % 60000 + new Random().Next(100, 5000));
        }
        private void UpdateWDFUI(bool useCache = false)
        {
            if (flashTimer != null) flashTimer.Stop();
            btnWDF.Font = new Font(btnWDF.Font, FontStyle.Regular);

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
            bool isOn = IsFirewallEnabled();
            bool hasRule = IsICMPRuleExisted();
            string wdfStatus = !isOn ? "防火关" : (hasRule ? "已放行" : "防火开");

            this.Text = $"Trace+ ✧ NetInfoCheckerX | 权限:{Global.UACLevel} {wdfStatus}";
        }

        private void StartBtnFlash()
        {
            if (flashTimer == null)
            {
                flashTimer = new System.Windows.Forms.Timer();
                flashTimer.Interval = 500;
                flashTimer.Tick += (s, e) =>
                {
                    if (btnWDF.IsDisposed) return;
                    btnWDF.Font = new Font(btnWDF.Font, btnWDF.Font.Bold ? FontStyle.Regular : FontStyle.Bold);
                };
            }
            flashTimer.Start();
        }
        private bool IsFirewallEnabled()
        {
            string output = GetNetshOutput("advfirewall show allprofiles state", Encoding.UTF8);
            if (!IsOutputValid(output))
                output = GetNetshOutput("advfirewall show allprofiles state", Encoding.GetEncoding(936));

            // 跳过 netsh 输出的前几行标题，只比对状态内容
            string[] lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            string cleanOutput = "";
            if (lines.Length > 4)
            {
                cleanOutput = string.Join("\n", lines.Skip(4));
            }
            else
            {
                cleanOutput = output;
            }

            return cleanOutput.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cleanOutput.Contains("启用") ||
                   cleanOutput.Contains("开启");
        }

        private bool IsICMPRuleExisted()
        {
            try
            {
                string output = GetNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"", Encoding.UTF8);

                // netsh 在某些系统上可能输出 GB2312 而非 UTF-8
                if (!output.Contains(ruleName))
                {
                    string legacyOutput = GetNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"", Encoding.GetEncoding(936));
                    if (legacyOutput.Contains(ruleName)) output = legacyOutput;
                }

                return output.Contains(ruleName);
            }
            catch { return false; }
        }

        private bool IsOutputValid(string text)
        {
            string[] keywords = { "ON", "OFF", "启用", "禁用", "开启", "关闭", "State", "状态" };
            return keywords.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

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
        private void EnsureSelectedNICValid()
        {
            string selectedText = comboLocalEnd.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.Contains("系统默认") ||
                selectedText.Contains("ICMP兼容模式") || selectedText.StartsWith("0.0.0.0") ||
                selectedText.StartsWith("::")) return;

            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses(includeIPv6: true))
                {
                    comboLocalEnd.Items.Add(nicAddress.DisplayText);
                }
            }
            catch { }

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
            this.MinimumSize = this.Size;
            timer1.Start();
            lblExeName.Text = $"{Global.exeName} {Global.Version} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;
            _ = CheckIpSearcherDependencies();
            // 高 DPI 下使用等宽字体改善可读性
            using (Graphics g = this.CreateGraphics())
            {
                if (g.DpiX > 96)
                {
                    Font modernFont = new Font("Cascadia Mono", 9.5F, FontStyle.Regular);
                    richTextBox1.Font = modernFont;
                }
            }
            AppendColorText("✧ 正在检查系统环境，请稍候... ✧\n", Color.White, true);

            await Task.Run(() =>
            {
                InitIp2Region();

                initialFirewallOn = _lastFwStatus = IsFirewallEnabled();
                initialRuleExisted = _lastRuleStatus = IsICMPRuleExisted();

                try
                {
                    foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses(includeIPv6: true))
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

            UpdateWDFUI(true);
            UpdateProtocolTip(null, null);
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

                if (File.Exists(v4Path))
                {
                    _ip2regionSearcherV4 = new Searcher(CachePolicy.Content, v4Path);
                }
                else
                {
                    AppendColorText("ip2region.v4.xdb 未找到\n", ColorTranslator.FromHtml("#a8a5ff"), false);
                }

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

            string reservedLabel = GetPrivateOrReservedLabel(ipAddr);
            if (!string.IsNullOrEmpty(reservedLabel))
                return reservedLabel;

            try
            {
                string region = "";

                if (ipAddr.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (_ip2regionSearcherV4 == null) return "IP2RG4数据库未加载";
                    region = _ip2regionSearcherV4.Search(ip);
                }
                else if (ipAddr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    if (_ip2regionSearcherV6 == null) return "IP2RG6数据库未加载";
                    region = _ip2regionSearcherV6.Search(ip);
                }
                else
                {
                    return "IP2RG协议未知";
                }

                if (string.IsNullOrWhiteSpace(region)) return "未知";

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

            if (s == "0" || s == "0.0" || s == "0/0" || s == "-")
                return "";
            if (s.Equals("Reserved", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("保留", StringComparison.OrdinalIgnoreCase))
                return "";

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
                AppendColorText("    🔰 此处归属地仅供参考，有疑惑可复制IP用「手动查询-IP地址」确认属地 ❤", Color.LightSkyBlue, true);
                AppendColorText("    🔰 Trace+ 已更新支持IPv4/IPv6, 指定网卡测试时使用Raw Socket实现", Color.LightGreen, true);
                AppendColorText("          可切换为[兼容模式], 原生更稳定, 但无法识别/指定网卡 \n", Color.LightGreen, true);
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
                AppendColorText("    🔰 此处归属地仅供参考，有疑惑可复制IP用「手动查询-IP地址」确认属地 ❤", Color.LightSkyBlue, true);
                AppendColorText("    🔰 Trace+ 已更新支持IPv4/IPv6, 指定网卡测试时使用Raw Socket实现", Color.LightGreen, true);
                AppendColorText("          可切换为[兼容模式], 原生更稳定, 但无法识别/指定网卡 \n", Color.LightGreen, true);
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
            EnsureSelectedNICValid();

            if (isRunning)
            {
                if (cts != null) cts.Cancel();
                return;
            }
            bool isNotAdmin = this.Text.Contains("User");

            if ((radioTCP.Checked || radioUDP.Checked) && comboLocalEnd.Text.Contains("ICMP兼容模式"))
            {
                richTextBox1.Clear();
                AppendColorText("\n\nTCP/UDP Trace 需绑定指定网卡，ICMP兼容模式下不支持。\n请选择本机 IP 网卡或切换到 ICMP 协议。\n", Color.Yellow, true);
                return;
            }

            if ((radioTCP.Checked || radioUDP.Checked) && isNotAdmin)
            {
                DialogResult drUac = MessageBox.Show(
                    "查询器X的 TCP/UDP Trace 需【以管理员身份运行】。\n\n" +
                    "【确认】立刻以管理员身份重启（当前输入的内容不会保留）\n" +
                    "【取消】稍后自行操作\n\n" +
                    "也可通过右键窗口左上角「网卡」白字尝试提权",
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
                return;
            }

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
                    return;
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
                        List<IPAddress> addrList = hostEntry.AddressList
                            .OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0)
                            .ToList();

                        if (addrList.Count == 0) throw new Exception("未解析到任何 IP 地址");

                        comboTargetIP.Items.Clear();
                        comboTargetIP.Items.Add(inputTarget);
                        foreach (var ip in addrList) comboTargetIP.Items.Add(ip.ToString());

                        comboTargetIP.DroppedDown = true;
                        if (comboTargetIP.Items.Count == 2)
                        {
                            comboTargetIP.SelectedIndex = 1;
                            AppendColorText($"\n[DNS]✨ 解析到 {addrList.Count} 个目标 IP。", Color.Yellow, true);
                            AppendColorText($"[DNS]✨ 已自动选择，再次点击「开测」。\n", Color.Yellow, true);
                        }
                        else
                        {
                            AppendColorText($"\n[DNS]✨ 解析到 {addrList.Count} 个目标 IP。", Color.Yellow, true);
                            AppendColorText($"[DNS]✨ 请选择一个IP后，点击「开测」。\n", Color.Yellow, true);
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
                IPAddress localExportIp;
                string userSelectIp = "";
                this.Invoke(new Action(() => { userSelectIp = comboLocalEnd.Text; }));

                if (userSelectIp.Contains(" ")) userSelectIp = userSelectIp.Split(' ')[0];

                bool isV6Target = finalTargetIp.AddressFamily == AddressFamily.InterNetworkV6;
                if (userSelectIp == "0.0.0.0" || userSelectIp == "::")
                {
                    if ((userSelectIp == "0.0.0.0" && isV6Target) || (userSelectIp == "::" && !isV6Target))
                    {
                        string protoLabel = selectedMethod == "ICMP" ? "ICMP错误" : "Socket错误";
                        AppendColorText($"\n {protoLabel}: 本机网卡IP({userSelectIp})与目标IP({finalTargetIp})地址族不匹配\n", Color.Orange, true);
                        return;
                    }
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

        // 使用 .NET Ping 类的原生 ICMP 模式，不绑定特定网卡
        private async Task RunNativeIcmpTrace(IPAddress targetIp, int maxHops, int timeout, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool isV6 = targetIp.AddressFamily == AddressFamily.InterNetworkV6;
            var results = new ConcurrentDictionary<int, HopResult>();

            async Task ProbeHop(int ttl, CancellationToken hopToken)
            {
                var result = new HopResult(ttl);
                try
                {
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
                }
                catch (OperationCanceledException) { }
                results[ttl] = result;
            }

            var tasks = new List<Task>();
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                int capturedTTL = ttl;
                tasks.Add(Task.Run(() => ProbeHop(capturedTTL, token), token));
            }

            var allDone = Task.WhenAll(tasks);
            int maxWaitMs = timeout * 4 + 150;
            bool seenTimeout = false;
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                if (this.IsDisposed || token.IsCancellationRequested) break;
                if (!seenTimeout)
                {
                    var waited = Stopwatch.StartNew();
                    while (!results.TryGetValue(ttl, out _) && !allDone.IsCompleted && !token.IsCancellationRequested)
                    {
                        await Task.Delay(33);
                        if (waited.ElapsedMilliseconds > maxWaitMs)
                        {
                            bool futureDone = false;
                            for (int f = ttl + 1; f <= Math.Min(ttl + 6, maxHops); f++)
                                if (results.ContainsKey(f)) { futureDone = true; break; }
                            if (futureDone) { seenTimeout = true; break; }
                        }
                    }
                }
                else
                {
                    var waited = Stopwatch.StartNew();
                    while (!results.TryGetValue(ttl, out _) && waited.ElapsedMilliseconds < 200 && !allDone.IsCompleted && !token.IsCancellationRequested)
                        await Task.Delay(33);
                }
                HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                DisplaySingleHop(hop, geoChecked, isV6);
                if (hop.TargetReached) break;
                await Task.Delay(33);
            }
            try { await allDone; } catch { }
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

        private byte[] CreateIcmpPacket(ushort seqNum, bool isV6 = false)
        {
            byte[] packet = new byte[32];
            packet[0] = isV6 ? (byte)128 : (byte)8; // ICMPv6 Echo Request = 128, ICMPv4 = 8
            packet[1] = 0;

            Buffer.BlockCopy(BitConverter.GetBytes(_instanceIdentifier), 0, packet, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(seqNum), 0, packet, 6, 2);

            byte[] payload = Encoding.ASCII.GetBytes("YumeyoTraceX-" + seqNum.ToString("X4"));
            Buffer.BlockCopy(payload, 0, packet, 8, Math.Min(payload.Length, 24));

            // ICMPv6 checksum is computed by the OS; ICMPv4 needs manual calculation
            if (!isV6)
            {
                ushort checksum = ComputeChecksum(packet);
                byte[] checkBytes = BitConverter.GetBytes(checksum);
                packet[2] = checkBytes[0];
                packet[3] = checkBytes[1];
            }
            return packet;
        }

        private async Task RunIcmpTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool isV6 = targetIp.AddressFamily == AddressFamily.InterNetworkV6;
            var results = new ConcurrentDictionary<int, HopResult>();
            var seqTcsStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();

            var af = targetIp.AddressFamily;
            var proto = isV6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp;

            using (Socket receiver = new Socket(af, SocketType.Raw, proto))
            {
                try
                {
                    receiver.Bind(new IPEndPoint(localIp, 0));
                }
                catch (Exception ex)
                {
                    AppendColorText($" 绑定失败: {ex.Message}\n", Color.Orange, true);
                    return;
                }

                var receiveLoopTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[1024];
                    EndPoint remoteEP = new IPEndPoint(isV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    while (!token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            if (receiver.Available > 0)
                            {
                                int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                                int icmpStart = isV6 ? 0 : 20;
                                if (len >= icmpStart + 8)
                                {
                                    byte type = rcvBuffer[icmpStart];
                                    ushort rcvId = 0, rcvSeq = 0;

                                    if (type == (isV6 ? 129 : 0)) // Echo Reply
                                    {
                                        rcvId = BitConverter.ToUInt16(rcvBuffer, icmpStart + 4);
                                        rcvSeq = BitConverter.ToUInt16(rcvBuffer, icmpStart + 6);
                                    }
                                    else if (type == (isV6 ? 3 : 11)) // Time Exceeded
                                    {
                                        int embedOff = icmpStart + 8 + (isV6 ? 40 : 20); // ICMP + IP header of embedded packet
                                        if (len <= embedOff + 6) continue;
                                        rcvId = BitConverter.ToUInt16(rcvBuffer, embedOff + 4);
                                        rcvSeq = BitConverter.ToUInt16(rcvBuffer, embedOff + 6);
                                    }
                                    else continue;

                                    if (rcvId == _instanceIdentifier)
                                    {
                                        if (seqTcsStore.TryRemove(rcvSeq, out var tcs))
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
                    int baseSeq = (ttl - 1) * 4;

                    try
                    {
                        using (Socket sendSocket = new Socket(af, SocketType.Raw, proto))
                        {
                            sendSocket.Bind(new IPEndPoint(localIp, 0));
                            sendSocket.Ttl = (short)ttl;

                            for (int i = 0; i < 4; i++)
                            {
                                if (hopToken.IsCancellationRequested) break;

                                int seq = baseSeq + i + 1;
                                byte[] requestPacket = CreateIcmpPacket((ushort)seq, isV6);
                                Stopwatch sw = Stopwatch.StartNew();

                                try
                                {
                                    sendSocket.SendTo(requestPacket, new IPEndPoint(targetIp, 0));

                                    var tcs = new TaskCompletionSource<IPAddress>();
                                    seqTcsStore[seq] = tcs;

                                    var finishedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout, hopToken));
                                    sw.Stop();

                                    if (finishedTask == tcs.Task)
                                    {
                                        IPAddress addr = await tcs.Task;
                                        result.ReplyAddress = addr;
                                        result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                        if (addr.Equals(targetIp)) result.TargetReached = true;
                                    }
                                    seqTcsStore.TryRemove(seq, out _);
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

                var tasks = new List<Task>();
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    int capturedTTL = ttl;
                    tasks.Add(Task.Run(() => ProbeHop(capturedTTL, token), token));
                }

                var allDone = Task.WhenAll(tasks);
                int maxWaitMs = timeout * 4 + 150;
                bool seenTimeout = false;
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    if (this.IsDisposed || token.IsCancellationRequested) break;
                    if (!seenTimeout)
                    {
                        var waited = Stopwatch.StartNew();
                        while (!results.TryGetValue(ttl, out _) && !allDone.IsCompleted && !token.IsCancellationRequested)
                        {
                            await Task.Delay(33);
                            if (waited.ElapsedMilliseconds > maxWaitMs)
                            {
                                bool futureDone = false;
                                for (int f = ttl + 1; f <= Math.Min(ttl + 6, maxHops); f++)
                                    if (results.ContainsKey(f)) { futureDone = true; break; }
                                if (futureDone) { seenTimeout = true; break; }
                            }
                        }
                    }
                    else
                    {
                        var waited = Stopwatch.StartNew();
                        while (!results.TryGetValue(ttl, out _) && waited.ElapsedMilliseconds < 200 && !allDone.IsCompleted && !token.IsCancellationRequested)
                            await Task.Delay(33);
                    }
                    HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                    DisplaySingleHop(hop, geoChecked, isV6);
                    if (hop.TargetReached) break;
                    await Task.Delay(33);
                }
                try { await allDone; } catch { }
            }
        }

        private async Task RunSocketTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int customPort, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool isV6 = targetIp.AddressFamily == AddressFamily.InterNetworkV6;
            var results = new ConcurrentDictionary<int, HopResult>();
            var packetTcsStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();

            var af = targetIp.AddressFamily;
            var icmpProto = isV6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp;

            using (Socket receiver = new Socket(af, SocketType.Raw, icmpProto))
            {
                try
                {
                    receiver.Bind(new IPEndPoint(localIp, 0));
                    if (!isV6) receiver.IOControl(IOControlCode.ReceiveAll, new byte[] { 1, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 });
                }
                catch { }

                var receiveLoopTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[8192];
                    EndPoint remoteEP = new IPEndPoint(isV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    while (!token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            if (receiver.Available > 0)
                            {
                                int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                                int icmpOff = isV6 ? 0 : 20;
                                if (len >= icmpOff + 8)
                                {
                                    int icmpType = rcvBuffer[icmpOff];
                                    int teType = isV6 ? 3 : 11;  // Time Exceeded
                                    int duType = isV6 ? 1 : 3;   // Dest Unreachable
                                    if (icmpType == teType || icmpType == duType)
                                    {
                                        int portOff = icmpOff + 8 + (isV6 ? 40 : 20); // ICMP hdr(8) + embedded IP hdr
                                        if (len <= portOff + 1) continue;
                                        int originalSrcPort = (rcvBuffer[portOff] << 8) + rcvBuffer[portOff + 1];
                                        if (packetTcsStore.TryRemove(originalSrcPort, out var tcs))
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

                            using (Socket senderSocket = new Socket(af,
                                protocol == "TCP" ? SocketType.Stream : SocketType.Dgram,
                                protocol == "TCP" ? ProtocolType.Tcp : ProtocolType.Udp))
                            {
                                try
                                {
                                    senderSocket.Bind(new IPEndPoint(localIp, 0));
                                    int myLocalPort = ((IPEndPoint)senderSocket.LocalEndPoint).Port;
                                    senderSocket.Ttl = (short)ttl;

                                    var tcs = new TaskCompletionSource<IPAddress>();
                                    packetTcsStore[myLocalPort] = tcs;

                                    Stopwatch sw = Stopwatch.StartNew();
                                    Task tcpSuccessTask = new TaskCompletionSource<bool>().Task; // never completes unless TCP succeeds
                                    if (protocol == "TCP")
                                    {
                                        var rawConnect = senderSocket.ConnectAsync(new IPEndPoint(targetIp, customPort));
                                        var successTcs = new TaskCompletionSource<bool>();
                                        var _ = rawConnect.ContinueWith(__ =>
                                        {
                                            if (rawConnect.Status == TaskStatus.RanToCompletion && senderSocket.Connected)
                                                successTcs.TrySetResult(true);
                                        });
                                        tcpSuccessTask = successTcs.Task;
                                    }
                                    else
                                    {
                                        senderSocket.SendTo(Encoding.ASCII.GetBytes("Yume"), new IPEndPoint(targetIp, customPort));
                                    }

                                    // tcpSuccessTask: 仅在 TCP 直连成功时完成，fault 时静默忽略 → 不干扰 ICMP 回复竞争
                                    var finishedTask = await Task.WhenAny(tcs.Task, tcpSuccessTask, Task.Delay(timeout, hopToken));
                                    sw.Stop();
                                    packetTcsStore.TryRemove(myLocalPort, out _);

                                    if (finishedTask == tcs.Task)
                                    {
                                        IPAddress addr = await tcs.Task;
                                        result.ReplyAddress = addr;
                                        result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                        if (addr.Equals(targetIp)) result.TargetReached = true;
                                    }
                                    else if (finishedTask == tcpSuccessTask)
                                    {
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

                var tasks = new List<Task>();
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    int capturedTTL = ttl;
                    tasks.Add(Task.Run(() => ProbeHop(capturedTTL, token), token));
                }

                var allDone = Task.WhenAll(tasks);
                int maxWaitMs = timeout * 4 + 150;
                bool seenTimeout = false;
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    if (this.IsDisposed || token.IsCancellationRequested) break;
                    if (!seenTimeout)
                    {
                        var waited = Stopwatch.StartNew();
                        while (!results.TryGetValue(ttl, out _) && !allDone.IsCompleted && !token.IsCancellationRequested)
                        {
                            await Task.Delay(33);
                            if (waited.ElapsedMilliseconds > maxWaitMs)
                            {
                                bool futureDone = false;
                                for (int f = ttl + 1; f <= Math.Min(ttl + 6, maxHops); f++)
                                    if (results.ContainsKey(f)) { futureDone = true; break; }
                                if (futureDone) { seenTimeout = true; break; }
                            }
                        }
                    }
                    else
                    {
                        var waited = Stopwatch.StartNew();
                        while (!results.TryGetValue(ttl, out _) && waited.ElapsedMilliseconds < 200 && !allDone.IsCompleted && !token.IsCancellationRequested)
                            await Task.Delay(33);
                    }
                    HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                    DisplaySingleHop(hop, geoChecked, isV6);
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
                var af = targetIp.AddressFamily;
                using (Socket socket = new Socket(af, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(targetIp, 65530);
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            }
            catch { return targetIp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback; }
        }

        private void Trace_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            if (isRunning)
            {
                isRunning = false;
                SetUIState(false);
                cts?.Cancel();
            }
            cts?.Dispose();
            cts = null;

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
                    string fwCommand = initialFirewallOn ? "advfirewall set allprofiles state on" : "advfirewall set allprofiles state off";
                    RunNetshSync(fwCommand);

                    if (initialRuleExisted)
                    {
                        // 先删后加以确保规则正确存在
                        RunNetshSync($"advfirewall firewall delete rule name=\"{ruleName}\"");
                        RunNetshSync($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv4");
                    }
                    else
                    {
                        RunNetshSync($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    }
                }
                else if (dr == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
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
                timer1.Stop();
                timer1.Dispose();
            }
            catch { }
        }

        private void RunNetshSync(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(psi))
                {
                    p?.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                // UAC 被用户拒绝时会进入此处
                Debug.WriteLine(ex.Message);
            }
        }

        private void lblTarget_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (isRunning && cts != null) cts.Cancel();
                Point currentLocation = this.Location;
                Trace newForm = new Trace();
                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentLocation;
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
                sfd.FileName = $"NICX_Trace_{pingType}_{comboTargetIP.Text}_{saveTime}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder();

                        sb.AppendLine($"=== 欢迎使用 Trace+ ❤ 网络综合查询器X by Yumeyo ===");
                        sb.AppendLine($"🔥 本次 Trace+ 输出详情: \n");
                        sb.AppendLine(richTextBox1.Text);
                        sb.AppendLine($"=== 感谢使用 Trace+ ❤ 网络综合查询器X by Yumeyo ===");
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
        private async void btnWDF_Click(object sender, EventArgs e)
        {
            bool isOn = IsFirewallEnabled();
            bool hasRule = IsICMPRuleExisted();

            if (!isOn)
            {
                if (MessageBox.Show("当前防火墙【关闭】。开启防火墙吗？", "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await RunNetshCmd("advfirewall set allprofiles state on");
                    isManualChanged = true;
                }
            }
            else if (hasRule)
            {
                if (MessageBox.Show("当前防火墙【开启】且【已放行】查询器X入站。删除放行规则吗？", "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await RunNetshCmd($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    isManualChanged = true;
                }
            }
            else
            {
                DialogResult dr = MessageBox.Show("当前防火墙【开启】且【未放行】查询器X，\n只可使用系统默认网卡(ICMP兼容模式)\n\n要解锁网卡选择功能, 请选择一个操作：\n【是】 关闭 防火墙\n【否】 添加 放行规则\n【取消】暂不修改",
                    "解锁方法选择", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    await RunNetshCmd("advfirewall set allprofiles state off");
                    isManualChanged = true;
                }
                else if (dr == DialogResult.No)
                {
                    await RunNetshCmd($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    await RunNetshCmd($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv4");
                    isManualChanged = true;
                }
            }
            UpdateWDFUI();
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
                AppendColorText($"\nTrace 完成.\n", Color.Lime, false);
            }
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            lblExeName.Text = $"{Global.exeName} {Global.Version} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
    }
}
