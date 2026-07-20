using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class PortScan : Form
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "PortScan";

        private static string CleanPortsText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            return string.Join(",",
                raw.Replace("\r\n", ",").Replace("\n", ",").Replace("\r", ",")
                   .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(p => p.Trim())
                   .Where(p => !string.IsNullOrEmpty(p)));
        }

        private void SaveSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(txtTarget.Text))
                    WritePrivateProfileString(IniSection, "Target", txtTarget.Text.Replace("\r\n", "").Replace("\n", ""), IniPath);
                WritePrivateProfileString(IniSection, "Port", CleanPortsText(txtPort.Text), IniPath);
                WritePrivateProfileString(IniSection, "Threads", txtThreads.Text.Replace("\r\n", "").Replace("\n", ""), IniPath);
                WritePrivateProfileString(IniSection, "Timeout", txtTimeout.Text.Replace("\r\n", "").Replace("\n", ""), IniPath);
                WritePrivateProfileString(IniSection, "TuningVersion", "2", IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var sb = new StringBuilder(256);
                string val;
                GetPrivateProfileString(IniSection, "TuningVersion", "", sb, sb.Capacity, IniPath);
                bool needsTuningMigration = sb.ToString() != "2";
                GetPrivateProfileString(IniSection, "Target", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtTarget.Text = val;
                GetPrivateProfileString(IniSection, "Port", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtPort.Text = val;
                GetPrivateProfileString(IniSection, "Threads", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString()))
                    txtThreads.Text = needsTuningMigration && (val == "256" || val == "100") ? "64" : val;
                GetPrivateProfileString(IniSection, "Timeout", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString()))
                    txtTimeout.Text = needsTuningMigration && (val == "300" || val == "30") ? "500" : val;
            }
            catch { }
        }
        private readonly string commonPorts = "21-23,53,80,110,123,143,443,445,465,587,1433,1900,3306,3389,4000,5000,5201,5900,6000,7890-7895,8000,8080,8888,8989,9000,9090,9833,9987,9999";
        private CancellationTokenSource _cts;
        private bool _isScanning = false;

        private enum PortScanState
        {
            Open,
            Closed,
            TimedOut,
            Cancelled,
            Error
        }

        private sealed class ScanProgressInfo
        {
            public int Completed { get; set; }
            public int? OpenPort { get; set; }
        }

        private sealed class ScanSummary
        {
            public int Completed;
            public int Open;
            public int Closed;
            public int TimedOut;
            public int Errors;
            public readonly ConcurrentBag<int> OpenPorts = new ConcurrentBag<int>();
        }

        public PortScan()
        {
            InitializeComponent();
        }
        // 自动刷新网卡：当系统网卡变化导致选中网卡不存在时，刷新列表并恢复默认
        private void EnsureSelectedNICValid()
        {
            string selectedText = comboLocalEnd.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.StartsWith("0.0.0.0", StringComparison.Ordinal)) return;

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
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                {
                    comboLocalEnd.Items.Add(nicAddress.DisplayText);
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

            Color baseContrastColor = isLight ? Color.Black : Color.White;
            Color exeNameColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

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

            if (lblExeName != null)
            {
                lblExeName.ForeColor = exeNameColor;
                lblExeName.BackColor = Color.Transparent;
            }

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

            if (richResult != null)
            {
                richResult.BackColor = textBack;
                richResult.ForeColor = baseContrastColor;
                richResult.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

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
            LoadSettings();
        }

        private void btnPaste_Click(object sender, EventArgs e)
        {
            if (!ClipboardHelper.TryGetText(out string clipText) || string.IsNullOrWhiteSpace(clipText)) return;

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
            txtPort.Text = "1-65535";
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
        private bool TryParsePorts(string input, out List<int> ports, out string error)
        {
            ports = new List<int>();
            error = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                error = "请输入要扫描的端口。";
                return false;
            }

            var uniquePorts = new SortedSet<int>();
            string normalized = input.Replace("，", ",")
                .Replace("\r\n", ",").Replace("\n", ",").Replace("\r", ",");

            foreach (string rawPart in normalized.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = rawPart.Trim();
                int separator = part.IndexOf('-');
                if (separator >= 0)
                {
                    if (separator != part.LastIndexOf('-') ||
                        !int.TryParse(part.Substring(0, separator).Trim(), out int start) ||
                        !int.TryParse(part.Substring(separator + 1).Trim(), out int end))
                    {
                        error = $"无法识别端口范围：{part}";
                        return false;
                    }

                    int first = Math.Min(start, end);
                    int last = Math.Max(start, end);
                    if (first < 1 || last > 65535)
                    {
                        error = $"端口必须在 1-65535 之间：{part}";
                        return false;
                    }

                    for (int port = first; port <= last; port++) uniquePorts.Add(port);
                }
                else
                {
                    if (!int.TryParse(part, out int port) || port < 1 || port > 65535)
                    {
                        error = $"端口必须在 1-65535 之间：{part}";
                        return false;
                    }
                    uniquePorts.Add(port);
                }
            }

            ports = uniquePorts.ToList();
            if (ports.Count == 0)
            {
                error = "没有找到有效的端口。";
                return false;
            }
            return true;
        }

        private string GetFormattedTarget()
        {
            string raw = txtTarget.Text.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                SystemSounds.Beep.Play();
                return String.Empty;
            }

            // 兼容从浏览器复制的 URL，以及带方括号的 IPv6 地址。
            if (Uri.TryCreate(raw, UriKind.Absolute, out Uri uri) && !string.IsNullOrEmpty(uri.Host))
                raw = uri.Host;
            if (raw.Length > 2 && raw[0] == '[' && raw[raw.Length - 1] == ']')
                raw = raw.Substring(1, raw.Length - 2);

            if (raw.Any(char.IsWhiteSpace) || raw.IndexOfAny(new[] { '/', '\\', '?', '#', '@' }) >= 0)
            {
                MessageBox.Show("目标地址格式不正确。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return String.Empty;
            }

            return raw;
        }

        private async void btnOK_Click(object sender, EventArgs e)
        {
            if (_isScanning)
            {
                btnOK.Text = "正在停止";
                _cts?.Cancel();
                return;
            }

            EnsureSelectedNICValid();
            string target = GetFormattedTarget();
            if (string.IsNullOrEmpty(target)) return;

            if (!TryParsePorts(txtPort.Text, out List<int> ports, out string portError))
            {
                MessageBox.Show(portError, "端口格式不正确", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!int.TryParse(txtThreads.Text, out int concurrency) || concurrency < 1)
            {
                MessageBox.Show("并发数请输入大于 0 的整数。", "扫描设置不正确", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (concurrency > 1000)
            {
                concurrency = 1000;
                txtThreads.Text = "1000";
            }
            if (!int.TryParse(txtTimeout.Text, out int timeout) || timeout < 10 || timeout > 60000)
            {
                MessageBox.Show("超时时间请输入 10-60000 毫秒。", "扫描设置不正确", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            IPAddress selectedIp = GetSelectedLocalIP();
            IPAddress targetIp;
            try
            {
                targetIp = await ResolveTargetAsync(target, selectedIp.AddressFamily);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法解析目标地址：{ex.Message}", "解析失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            IPAddress actualLocalIp = GetActualLocalIP(targetIp, selectedIp);

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

            string finalLocalInfo = comboLocalEnd.SelectedItem?.ToString() ?? actualLocalIp.ToString();
            var scanCts = new CancellationTokenSource();
            _cts = scanCts;
            _isScanning = true;
            btnOK.Text = "停止";
            SetControlsEnabled(false);
            richResult.Clear();

            richResult.AppendText($"[扫描目标] {target} ({targetIp}) / {ports.Count} 个端口\n");
            richResult.AppendText($"[使用网卡] {finalLocalInfo}\n");
            richResult.AppendText($"[扫描设置] 并发 {concurrency} / 超时 {timeout}ms\n");
            richResult.AppendText($"[开始时间] {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            richResult.AppendText("[TCP端口] ");
            richResult.ScrollToCaret();

            List<int> foundPortsList = new List<int>();
            int displayedCompleted = 0;
            var progress = new Progress<ScanProgressInfo>(info =>
            {
                if (IsDisposed || !IsHandleCreated) return;

                displayedCompleted = Math.Max(displayedCompleted, info.Completed);
                if (_isScanning) btnOK.Text = $"{displayedCompleted}/{ports.Count}";

                if (info.OpenPort.HasValue && !foundPortsList.Contains(info.OpenPort.Value))
                {
                    foundPortsList.Add(info.OpenPort.Value);
                    UpdateTcpPortLine(BuildOpenPortsLine(foundPortsList));
                }
            });

            var watch = Stopwatch.StartNew();
            ScanSummary summary = null;
            try
            {
                summary = await Task.Run(() => ScanPortsAsync(targetIp, actualLocalIp, ports, concurrency,
                    timeout, progress, scanCts.Token));
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    richResult.AppendText($"\n[扫描异常] {ex.Message}");
            }
            finally
            {
                watch.Stop();
                if (!IsDisposed)
                {
                    if (summary != null)
                    {
                        UpdateTcpPortLine(BuildOpenPortsLine(summary.OpenPorts));
                        richResult.AppendText($"\n[扫描统计] 本次扫描 {summary.Completed}/{ports.Count}，扫到{summary.Open}，" +
                            $"超时{summary.TimedOut}，错误{summary.Errors}");
                    }
                    richResult.AppendText(scanCts.IsCancellationRequested
                        ? $"\n[停止扫描] 用时 {watch.Elapsed.TotalSeconds:F1} 秒"
                        : $"\n[扫描完成] {DateTime.Now:yyyy-MM-dd HH:mm:ss} / 用时 {watch.Elapsed.TotalSeconds:F1} 秒");
                    btnOK.Text = "开扫";
                    SetControlsEnabled(true);
                    richResult.ScrollToCaret();
                }

                _isScanning = false;
                if (ReferenceEquals(_cts, scanCts)) _cts = null;
                scanCts.Dispose();
            }
        }

        // 辅助方法：精准替换最后一行扫描结果，防止刷屏
        private void UpdateTcpPortLine(string newLineText)
        {
            int start = richResult.Find("[TCP端口]");
            if (start >= 0)
            {
                int lineEnd = richResult.Text.IndexOf('\n', start);
                int length = (lineEnd < 0 ? richResult.TextLength : lineEnd) - start;
                richResult.Select(start, length);
                richResult.SelectedText = newLineText;
            }
        }

        private string BuildOpenPortsLine(IEnumerable<int> openPorts)
        {
            var sb = new StringBuilder("[TCP端口] ");
            foreach (int port in openPorts.Distinct().OrderBy(p => p))
            {
                string note = portNotes.TryGetValue(port, out string value) ? $"({value})" : "";
                sb.Append($"{port}{note}, ");
            }
            return sb.ToString();
        }

        private IPAddress GetSelectedLocalIP()
        {
            string selectedItem = comboLocalEnd.SelectedItem?.ToString() ?? "";

            if (selectedItem.StartsWith("0.0.0.0", StringComparison.Ordinal)) return IPAddress.Any;
            if (selectedItem.StartsWith(":: (IPv6 Any)", StringComparison.Ordinal) || selectedItem == "::")
                return IPAddress.IPv6Any;

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

        private async Task<IPAddress> ResolveTargetAsync(string target, AddressFamily addressFamily)
        {
            if (IPAddress.TryParse(target, out IPAddress literalAddress))
            {
                if (literalAddress.AddressFamily != addressFamily)
                    throw new InvalidOperationException($"当前出口网卡使用 {(addressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6")}，与目标地址不匹配");
                return literalAddress;
            }

            IPAddress[] addresses = await Task.Run(() => Dns.GetHostAddresses(target)).ConfigureAwait(false);
            IPAddress result = addresses.FirstOrDefault(ip => ip.AddressFamily == addressFamily);
            if (result == null)
                throw new InvalidOperationException($"域名没有可用的 {(addressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} 地址");
            return result;
        }

        private async Task<ScanSummary> ScanPortsAsync(IPAddress targetIp, IPAddress localIp,
            IList<int> ports, int concurrency, int timeout, IProgress<ScanProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            var summary = new ScanSummary();
            int nextIndex = -1;
            int workerCount = Math.Min(concurrency, ports.Count);
            var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (cancellationToken.Register(() => cancellationSignal.TrySetResult(true)))
            {
                Func<Task> worker = async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int index = Interlocked.Increment(ref nextIndex);
                        if (index >= ports.Count) break;

                        int port = ports[index];
                        PortScanState firstState = await ScanPortAsync(targetIp, port, localIp, timeout,
                            cancellationSignal.Task).ConfigureAwait(false);
                        if (firstState == PortScanState.Cancelled) break;

                        // 每个端口首轮结束后立即重新建立一次独立连接，不区分端口号和扫描规模。
                        PortScanState secondState = await ScanPortAsync(targetIp, port, localIp, timeout,
                            cancellationSignal.Task).ConfigureAwait(false);
                        if (secondState == PortScanState.Cancelled) break;

                        PortScanState finalState = CombineScanStates(firstState, secondState);
                        IncrementSummaryState(summary, finalState);
                        int? openPort = null;
                        if (finalState == PortScanState.Open)
                        {
                            summary.OpenPorts.Add(port);
                            openPort = port;
                        }
                        int completed = Interlocked.Increment(ref summary.Completed);
                        if (openPort.HasValue || completed % 128 == 0 || completed == ports.Count)
                            progress?.Report(new ScanProgressInfo { Completed = completed, OpenPort = openPort });
                    }
                };

                var workers = new Task[workerCount];
                for (int i = 0; i < workers.Length; i++) workers[i] = worker();
                await Task.WhenAll(workers).ConfigureAwait(false);
            }

            return summary;
        }

        private static PortScanState CombineScanStates(PortScanState first, PortScanState second)
        {
            if (first == PortScanState.Open || second == PortScanState.Open) return PortScanState.Open;
            if (first == PortScanState.Closed || second == PortScanState.Closed) return PortScanState.Closed;
            if (first == PortScanState.TimedOut || second == PortScanState.TimedOut) return PortScanState.TimedOut;
            return PortScanState.Error;
        }

        private static void IncrementSummaryState(ScanSummary summary, PortScanState state)
        {
            switch (state)
            {
                case PortScanState.Open:
                    Interlocked.Increment(ref summary.Open);
                    break;
                case PortScanState.Closed:
                    Interlocked.Increment(ref summary.Closed);
                    break;
                case PortScanState.TimedOut:
                    Interlocked.Increment(ref summary.TimedOut);
                    break;
                default:
                    Interlocked.Increment(ref summary.Errors);
                    break;
            }
        }

        private async Task<PortScanState> ScanPortAsync(IPAddress targetIp, int port,
            IPAddress localIp, int timeout, Task cancellationTask)
        {
            using (var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            using (var args = new SocketAsyncEventArgs())
            using (var timeoutCts = new CancellationTokenSource())
            {
                var completion = new TaskCompletionSource<SocketError>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<SocketAsyncEventArgs> completedHandler = (sender, eventArgs) =>
                    completion.TrySetResult(eventArgs.SocketError);
                args.Completed += completedHandler;
                args.RemoteEndPoint = new IPEndPoint(targetIp, port);

                try
                {
                    // RST 关闭已连接的探测 Socket，避免全端口扫描积累大量 TIME_WAIT。
                    socket.LingerState = new LingerOption(true, 0);
                    socket.Bind(new IPEndPoint(localIp, 0));

                    if (!socket.ConnectAsync(args)) completion.TrySetResult(args.SocketError);

                    Task timeoutTask = Task.Delay(timeout, timeoutCts.Token);
                    Task winner = await Task.WhenAny(completion.Task, timeoutTask, cancellationTask).ConfigureAwait(false);
                    if (winner != completion.Task)
                    {
                        try { socket.Close(); } catch { }
                        try { await completion.Task.ConfigureAwait(false); } catch { }
                        if (winner == cancellationTask) return PortScanState.Cancelled;
                        return PortScanState.TimedOut;
                    }

                    timeoutCts.Cancel();
                    SocketError socketError = await completion.Task.ConfigureAwait(false);
                    return ClassifySocketError(socketError);
                }
                catch (SocketException ex)
                {
                    return ClassifySocketError(ex.SocketErrorCode);
                }
                catch (ObjectDisposedException)
                {
                    return cancellationTask.IsCompleted ? PortScanState.Cancelled : PortScanState.Error;
                }
                catch
                {
                    return PortScanState.Error;
                }
                finally
                {
                    args.Completed -= completedHandler;
                }
            }
        }

        private static PortScanState ClassifySocketError(SocketError socketError)
        {
            switch (socketError)
            {
                case SocketError.Success:
                    return PortScanState.Open;
                case SocketError.ConnectionRefused:
                case SocketError.ConnectionReset:
                    return PortScanState.Closed;
                case SocketError.TimedOut:
                    return PortScanState.TimedOut;
                default:
                    return PortScanState.Error;
            }
        }

        // 获取系统路由到目标地址时实际使用的本地 IP。
        private IPAddress GetActualLocalIP(IPAddress targetIp, IPAddress selectedIp)
        {
            if (!selectedIp.Equals(IPAddress.Any) && !selectedIp.Equals(IPAddress.IPv6Any))
            {
                return selectedIp;
            }

            try
            {
                using (Socket socket = new Socket(targetIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(targetIp, 1);
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            }
            catch { return selectedIp; }
        }


        private void PortScan_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            if (_isScanning)
            {
                _cts?.Cancel();
            }
        }

        private void txtTarget_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }

        private void txtPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }

        private void txtThreads_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }

        private void txtTimeout_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }

        private void btnFull_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }

        private void btnMinimum_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }
    }
}
