using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class DNSTest : Form
    {
        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);
        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "DNSTest";
        private const string MissingSettingValue = "__NICX_SETTING_NOT_FOUND__";

        private void SaveSettings()
        {
            SaveSettings(IniPath);
        }

        private void SaveSettings(string iniPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(combo1.Text))
                    WritePrivateProfileString(IniSection, "Domain1", combo1.Text, iniPath);

                // Always write all five values, including an intentionally empty value.
                WritePrivateProfileString(IniSection, "DnsServer1", comboBox1.Text ?? string.Empty, iniPath);
                WritePrivateProfileString(IniSection, "DnsServer2", comboBox2.Text ?? string.Empty, iniPath);
                WritePrivateProfileString(IniSection, "DnsServer3", comboBox3.Text ?? string.Empty, iniPath);
                WritePrivateProfileString(IniSection, "DnsServer4", comboBox4.Text ?? string.Empty, iniPath);
                WritePrivateProfileString(IniSection, "DnsServer5", comboBox5.Text ?? string.Empty, iniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            LoadSettings(IniPath);
        }

        private void LoadSettings(string iniPath)
        {
            try
            {
                var sb = new StringBuilder(256);
                GetPrivateProfileString(IniSection, "Domain1", "", sb, sb.Capacity, iniPath);
                string d1 = sb.ToString();
                if (!string.IsNullOrEmpty(d1))
                {
                    if (combo1.Items.Count > 0)
                    {
                        int idx = -1;
                        for (int i = 0; i < combo1.Items.Count; i++)
                            if (combo1.Items[i].ToString() == d1) { idx = i; break; }
                        if (idx >= 0) combo1.SelectedIndex = idx;
                        else combo1.Text = d1;
                    }
                    else combo1.Text = d1;
                }

                LoadComboBoxText("DnsServer1", comboBox1, sb, iniPath);
                LoadComboBoxText("DnsServer2", comboBox2, sb, iniPath);
                LoadComboBoxText("DnsServer3", comboBox3, sb, iniPath);
                LoadComboBoxText("DnsServer4", comboBox4, sb, iniPath);
                LoadComboBoxText("DnsServer5", comboBox5, sb, iniPath);
            }
            catch { }
        }

        private void LoadComboBoxText(string key, ComboBox comboBox, StringBuilder buffer, string iniPath)
        {
            buffer.Clear();
            GetPrivateProfileString(IniSection, key, MissingSettingValue,
                buffer, buffer.Capacity, iniPath);

            string value = buffer.ToString();
            if (!string.Equals(value, MissingSettingValue, StringComparison.Ordinal))
                comboBox.Text = value;
        }

        private void SetDnsComboBoxesEnabled(bool enabled)
        {
            if (comboBox1 != null && !comboBox1.IsDisposed) comboBox1.Enabled = enabled;
            if (comboBox2 != null && !comboBox2.IsDisposed) comboBox2.Enabled = enabled;
            if (comboBox3 != null && !comboBox3.IsDisposed) comboBox3.Enabled = enabled;
            if (comboBox4 != null && !comboBox4.IsDisposed) comboBox4.Enabled = enabled;
            if (comboBox5 != null && !comboBox5.IsDisposed) comboBox5.Enabled = enabled;
        }

        private CancellationTokenSource _testCts;
        private bool _isRunning;
        private const string QueryingText = "挠头中…";

        private static readonly Regex _dohIpRegex =
            new Regex(@"""data""\s*:\s*""([0-9a-fA-F.:]+)""", RegexOptions.Compiled);

        private static readonly HttpClient _dohClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private struct DnsRow
        {
            public string Server;          // null = 系统DNS, IP地址 = UDP DNS, URL = DoH
            public ComboBox ServerCombo;   // 非 null 时，在测试开始时读取用户选择的 DNS
            public Label ResultLabel;
            public bool IsDoh;
        }

        private DnsRow[] _rows;

        public DNSTest()
        {
            InitializeComponent();
            InitDnsRows();
        }

        private void InitDnsRows()
        {
            _rows = new DnsRow[]
            {
                new DnsRow { Server = null,                              ResultLabel = lblSys1 },
                new DnsRow { Server = "https://dns.alidns.com/resolve", ResultLabel = lbl223DOH1, IsDoh = true },
                new DnsRow { Server = "https://doh.pub/resolve",        ResultLabel = lbl119DOH1, IsDoh = true },
                new DnsRow { ServerCombo = comboBox1,                    ResultLabel = lbl2231 },
                new DnsRow { ServerCombo = comboBox2,                    ResultLabel = lbl1191 },
                new DnsRow { ServerCombo = comboBox3,                    ResultLabel = lbl1141 },
                new DnsRow { ServerCombo = comboBox4,                    ResultLabel = lblGoogle1 },
                new DnsRow { ServerCombo = comboBox5,                    ResultLabel = lblMS1 },
                new DnsRow { Server = "1.14.5.14",                     ResultLabel = lblWrong1 },
            };
        }

        private void SafeInvoke(Control ctrl, Action action)
        {
            if (ctrl.IsDisposed || !ctrl.IsHandleCreated) return;
            if (ctrl.InvokeRequired)
                ctrl.Invoke(action);
            else
                action();
        }

        private static List<string> ChunkLines(List<string> items, int perLine)
        {
            var lines = new List<string>();
            for (int i = 0; i < items.Count; i += perLine)
                lines.Add(string.Join(", ", items.Skip(i).Take(perLine)));
            return lines;
        }

        private static (string display, string full) FormatResults(
            List<string> ipv4, List<string> ipv6, string dnsServer)
        {
            // Some system DNS implementations return the same address more than once.
            // Keep the original response order, but only display/count each address once.
            ipv4 = ipv4
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ipv6 = ipv6
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string displayText = ipv4.Count > 0
                ? string.Join(", ", ipv4.Take(2))
                : "未找到IPv4记录";

            var parts = new List<string> { "DNS服务器: " + dnsServer };
            if (ipv4.Count > 0)
                parts.Add("IPv4 (" + ipv4.Count + "):\r\n    " +
                    string.Join("\r\n    ", ChunkLines(ipv4, 4)));
            if (ipv6.Count > 0)
                parts.Add("IPv6 (" + ipv6.Count + "):\r\n    " +
                    string.Join("\r\n    ", ChunkLines(ipv6, 2)));
            if (ipv4.Count == 0 && ipv6.Count == 0)
                parts.Add("无记录");
            string fullText = string.Join("\r\n", parts);

            return (displayText, fullText);
        }

        private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (task.IsCompleted)
                return await task;

            // .NET Framework 4.7.2's Dns.GetHostAddressesAsync has no CancellationToken
            // overload. Complete this wrapper immediately when cancellation is requested;
            // the continuation still observes any later exception from the DNS task.
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => completion.TrySetCanceled()))
            {
                _ = task.ContinueWith(completed =>
                {
                    if (completed.IsCanceled)
                        completion.TrySetCanceled();
                    else if (completed.IsFaulted)
                        completion.TrySetException(completed.Exception.InnerExceptions);
                    else
                        completion.TrySetResult(completed.Result);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                return await completion.Task;
            }
        }

        private void UpdateLabelResult(Label targetLabel, string display, string full, CancellationToken token)
        {
            SafeInvoke(targetLabel, () =>
            {
                if (token.IsCancellationRequested) return;
                targetLabel.Text = display;
                toolTip1.SetToolTip(targetLabel, full);
            });
        }

        private async Task PerformSystemDnsTest(string domain, Label targetLabel, CancellationToken token)
        {
            try
            {
                var addresses = await WaitWithCancellationAsync(
                    Dns.GetHostAddressesAsync(domain), token);
                token.ThrowIfCancellationRequested();

                var ipv4 = addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
                var ipv6 = addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(a => a.ToString())
                    .ToList();

                var (display, full) = FormatResults(ipv4, ipv6, "系统默认");
                UpdateLabelResult(targetLabel, display, full, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    UpdateLabelResult(targetLabel, $"DNS失败(?)\n{ex.Message}",
                        "DNS服务器: 系统默认\r\n" + ex.Message, token);
            }
        }

        private async Task PerformDnsTest(string dnsServer, string domain, Label targetLabel, CancellationToken token)
        {
            try
            {
                var (ipv4, ipv6) = await Task.Run(() =>
                {
                    var v4 = new List<string>();
                    var v6 = new List<string>();

                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        socket.ReceiveTimeout = 3000;
                        socket.SendTimeout = 3000;

                        using (token.Register(() => { try { socket.Close(); } catch { } }))
                        {
                            // A record query
                            try
                            {
                                socket.SendTo(BuildDnsQuery(domain, aaaa: false),
                                    new IPEndPoint(IPAddress.Parse(dnsServer), 53));
                                byte[] response = new byte[512];
                                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                                int len = socket.ReceiveFrom(response, ref remoteEP);
                                v4 = ParseDnsARecords(response, len);
                            }
                            catch (SocketException) { }
                            catch (ObjectDisposedException) { }

                            token.ThrowIfCancellationRequested();

                            // AAAA record query
                            try
                            {
                                socket.SendTo(BuildDnsQuery(domain, aaaa: true),
                                    new IPEndPoint(IPAddress.Parse(dnsServer), 53));
                                byte[] response = new byte[512];
                                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                                int len = socket.ReceiveFrom(response, ref remoteEP);
                                v6 = ParseDnsAAAARecords(response, len);
                            }
                            catch (SocketException) { }
                            catch (ObjectDisposedException) { }
                        }
                    }

                    return (v4, v6);
                }, token);

                token.ThrowIfCancellationRequested();
                var (display, full) = FormatResults(ipv4, ipv6, dnsServer);
                UpdateLabelResult(targetLabel, display, full, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    UpdateLabelResult(targetLabel, $"DNS失败(?)\n{ex.Message}",
                        "DNS服务器: " + dnsServer + "\r\n" + ex.Message, token);
            }
        }

        private async Task PerformDohDnsTest(string dohUrl, string domain, Label targetLabel, CancellationToken token)
        {
            try
            {
                var requestA = $"{dohUrl}?name={Uri.EscapeDataString(domain)}&type=A";
                var requestAAAA = $"{dohUrl}?name={Uri.EscapeDataString(domain)}&type=AAAA";

                var taskA = DoHQueryAsync(requestA, token);
                var taskAAAA = DoHQueryAsync(requestAAAA, token);

                try
                {
                    await Task.WhenAll(taskA, taskAAAA);
                }
                catch
                {
                    // At least one query failed; collect what we got
                }

                var ipv4 = taskA.Status == TaskStatus.RanToCompletion ? taskA.Result : new List<string>();
                var ipv6 = taskAAAA.Status == TaskStatus.RanToCompletion ? taskAAAA.Result : new List<string>();

                token.ThrowIfCancellationRequested();

                var (display, full) = FormatResults(ipv4, ipv6, dohUrl);
                UpdateLabelResult(targetLabel, display, full, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    UpdateLabelResult(targetLabel, $"DoH失败(?)\n{ex.Message}",
                        "DNS服务器: " + dohUrl + "\r\n" + ex.Message, token);
            }
        }

        private async Task<List<string>> DoHQueryAsync(string requestUrl, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

            var response = await _dohClient.SendAsync(request, token);
            var json = await response.Content.ReadAsStringAsync();

            return _dohIpRegex.Matches(json)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();
        }

        private static byte[] BuildDnsQuery(string domain, bool aaaa = false)
        {
            var packet = new List<byte>();
            // Header: ID(2), Flags(2)=标准查询, QDCOUNT(2)=1, ANCOUNT/NSCOUNT/ARCOUNT = 0
            packet.AddRange(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            // Question: QNAME (长度前缀格式) + QTYPE + QCLASS
            foreach (string part in domain.Split('.'))
            {
                packet.Add((byte)part.Length);
                packet.AddRange(Encoding.ASCII.GetBytes(part));
            }
            packet.Add(0x00);

            if (aaaa)
                packet.AddRange(new byte[] { 0x00, 0x1C }); // QTYPE = AAAA (28)
            else
                packet.AddRange(new byte[] { 0x00, 0x01 }); // QTYPE = A

            packet.AddRange(new byte[] { 0x00, 0x01 }); // QCLASS = IN
            return packet.ToArray();
        }

        private static List<string> ParseDnsARecords(byte[] response, int length)
        {
            var ips = new List<string>();
            if (length < 12) return ips;

            int rcode = response[3] & 0x0F;
            if (rcode != 0) return ips;

            int ancount = (response[6] << 8) | response[7];
            if (ancount == 0) return ips;

            int pos = 12;

            while (pos < length && response[pos] != 0)
            {
                if ((response[pos] & 0xC0) == 0xC0) { pos += 2; break; }
                pos += response[pos] + 1;
            }
            if (pos < length && response[pos] == 0) pos++;
            pos += 4;

            for (int i = 0; i < ancount; i++)
            {
                if (pos < length && (response[pos] & 0xC0) == 0xC0) { pos += 2; }
                else
                {
                    while (pos < length && response[pos] != 0) pos += response[pos] + 1;
                    if (pos < length && response[pos] == 0) pos++;
                }

                if (pos + 10 > length) break;

                int type = (response[pos] << 8) | response[pos + 1];
                int rdlength = (response[pos + 8] << 8) | response[pos + 9];
                pos += 10;

                if (type == 1 && rdlength == 4 && pos + 4 <= length)
                {
                    ips.Add($"{response[pos]}.{response[pos + 1]}.{response[pos + 2]}.{response[pos + 3]}");
                }
                pos += rdlength;
            }

            return ips;
        }

        private static List<string> ParseDnsAAAARecords(byte[] response, int length)
        {
            var ips = new List<string>();
            if (length < 12) return ips;

            int rcode = response[3] & 0x0F;
            if (rcode != 0) return ips;

            int ancount = (response[6] << 8) | response[7];
            if (ancount == 0) return ips;

            int pos = 12;

            while (pos < length && response[pos] != 0)
            {
                if ((response[pos] & 0xC0) == 0xC0) { pos += 2; break; }
                pos += response[pos] + 1;
            }
            if (pos < length && response[pos] == 0) pos++;
            pos += 4;

            for (int i = 0; i < ancount; i++)
            {
                if (pos < length && (response[pos] & 0xC0) == 0xC0) { pos += 2; }
                else
                {
                    while (pos < length && response[pos] != 0) pos += response[pos] + 1;
                    if (pos < length && response[pos] == 0) pos++;
                }

                if (pos + 10 > length) break;

                int type = (response[pos] << 8) | response[pos + 1];
                int rdlength = (response[pos + 8] << 8) | response[pos + 9];
                pos += 10;

                if (type == 28 && rdlength == 16 && pos + 16 <= length)
                {
                    var sb = new StringBuilder();
                    for (int j = 0; j < 16; j += 2)
                    {
                        if (j > 0) sb.Append(':');
                        sb.Append(((response[pos + j] << 8) | response[pos + j + 1]).ToString("x"));
                    }
                    ips.Add(sb.ToString());
                }
                pos += rdlength;
            }

            return ips;
        }

        private async Task ApplyDNSTestThemeAsync()
        {
            await Task.Yield();

            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color controlBack = isLight ? SystemColors.Window : Color.FromArgb(45, 45, 48);
            Color buttonBack = isLight ? SystemColors.ButtonFace : Color.FromArgb(55, 55, 58);
            Color comboFore = isLight ? SystemColors.ControlText : Color.White;

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] titleLabels = {
                lblSystem, label6, label3, lblWrong, label1
            };
            foreach (var l in titleLabels)
                if (l != null) l.ForeColor = yumeyoColor;

            Control[] resultLabels = {
                lblSys1, lbl223DOH1, lbl119DOH1,
                lbl2231, lbl1191, lbl1141, lblGoogle1, lblMS1, lblWrong1
            };
            foreach (var r in resultLabels)
                if (r != null) r.ForeColor = contrastColor;

            ComboBox[] comboBoxes = {
                combo1, comboBox1, comboBox2, comboBox3, comboBox4, comboBox5
            };
            foreach (var combo in comboBoxes)
            {
                if (combo == null) continue;
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = controlBack;
                combo.ForeColor = comboFore;
            }

            if (btnStart1 != null)
            {
                btnStart1.BackColor = buttonBack;
                btnStart1.ForeColor = contrastColor;
                btnStart1.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                if (!isLight) btnStart1.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
            }
            if (lblVersion != null) lblVersion.ForeColor = yumeyoColor;
            if (pictureBox1 != null) pictureBox1.BackColor = Color.Transparent;
        }

        private void DNSTest_Load(object sender, EventArgs e)
        {
            _ = ApplyDNSTestThemeAsync();
            string NowTime = Others.GetCurrentTime();
            lblVersion.Text = Global.exeName + " " + Global.Version + " | " + NowTime;
            timer1.Start();
            CloudControl.UsedTimesCounter("DNS劫持");
            LoadSettings();
        }

        private void DNSTest_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            timer1.Stop();
            _testCts?.Cancel();
            _testCts?.Dispose();
            timer1.Dispose();
        }

        private async void btnStart1_Click(object sender, EventArgs e)
        {
            if (_isRunning)
            {
                _testCts?.Cancel();
                ClearInterruptedResults();
                return;
            }

            _isRunning = true;
            _testCts?.Dispose();
            var currentCts = new CancellationTokenSource();
            _testCts = currentCts;
            btnStart1.Text = "取消";
            SetDnsComboBoxesEnabled(false);
            try
            {
                await RunStaggeredTests(currentCts.Token);
            }
            finally
            {
                currentCts.Dispose();
                if (ReferenceEquals(_testCts, currentCts))
                {
                    _testCts = null;
                    _isRunning = false;
                    SetDnsComboBoxesEnabled(true);
                    SafeInvoke(btnStart1, () => btnStart1.Text = "开测");
                }
            }
        }

        private void ClearResults()
        {
            foreach (var row in _rows)
            {
                Label target = row.ResultLabel;
                SafeInvoke(target, () =>
                {
                    target.Text = "-";
                    toolTip1.SetToolTip(target, null);
                });
            }
        }

        private void ClearInterruptedResults()
        {
            foreach (var row in _rows)
            {
                Label target = row.ResultLabel;
                SafeInvoke(target, () =>
                {
                    if (target.Text == QueryingText)
                    {
                        target.Text = "-";
                        toolTip1.SetToolTip(target, null);
                    }
                });
            }
        }

        private async Task RunStaggeredTests(CancellationToken token)
        {
            string domain = combo1.Text.Trim();
            if (string.IsNullOrEmpty(domain))
            {
                MessageBox.Show("请先输入或选择一个域名。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Snapshot all editable DNS selections so one run uses a consistent setup.
            var testRows = _rows.Select(row => new DnsRow
            {
                Server = row.ServerCombo == null ? row.Server : row.ServerCombo.Text.Trim(),
                ResultLabel = row.ResultLabel,
                IsDoh = row.IsDoh
            }).ToArray();

            ClearResults();

            var tasks = new List<Task>();
            try
            {
                foreach (var row in testRows)
                {
                    token.ThrowIfCancellationRequested();

                    Label targetLabel = row.ResultLabel;
                    SafeInvoke(targetLabel, () =>
                    {
                        targetLabel.Text = QueryingText;
                        toolTip1.SetToolTip(targetLabel, null);
                    });

                    if (row.Server == null)
                        tasks.Add(PerformSystemDnsTest(domain, targetLabel, token));
                    else if (row.IsDoh)
                        tasks.Add(PerformDohDnsTest(row.Server, domain, targetLabel, token));
                    else
                        tasks.Add(PerformDnsTest(row.Server, domain, targetLabel, token));

                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException) { }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (token.IsCancellationRequested)
                    ClearInterruptedResults();
            }
        }

        // TextChanged handlers — kept for Designer.cs compatibility.
        // Tooltips are now set directly in UpdateLabelResult(), so these are no-ops.
        private void lblSysBaidu_TextChanged(object sender, EventArgs e) { }
        private void lbl223Baidu_TextChanged(object sender, EventArgs e) { }
        private void lbl114Baidu_TextChanged(object sender, EventArgs e) { }
        private void lblGoogleBaidu_TextChanged(object sender, EventArgs e) { }
        private void lblWrongBaidu_TextChanged(object sender, EventArgs e) { }

        private void timer1_Tick(object sender, EventArgs e)
        {
            lblVersion.Text = Global.exeName + " " + Global.Version + " | " + Others.GetCurrentTime();
        }
    }
}
