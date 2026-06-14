using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class DNSTest : Form
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "DNSTest";

        private void SaveSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(combo1.Text))
                    WritePrivateProfileString(IniSection, "Domain1", combo1.Text, IniPath);
                if (!string.IsNullOrEmpty(combo2.Text))
                    WritePrivateProfileString(IniSection, "Domain2", combo2.Text, IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var sb = new StringBuilder(256);
                GetPrivateProfileString(IniSection, "Domain1", "", sb, sb.Capacity, IniPath);
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
                GetPrivateProfileString(IniSection, "Domain2", "", sb, sb.Capacity, IniPath);
                string d2 = sb.ToString();
                if (!string.IsNullOrEmpty(d2))
                {
                    if (combo2.Items.Count > 0)
                    {
                        int idx = -1;
                        for (int i = 0; i < combo2.Items.Count; i++)
                            if (combo2.Items[i].ToString() == d2) { idx = i; break; }
                        if (idx >= 0) combo2.SelectedIndex = idx;
                        else combo2.Text = d2;
                    }
                    else combo2.Text = d2;
                }
            }
            catch { }
        }
        private CancellationTokenSource _cts1;
        private CancellationTokenSource _cts2;

        private static readonly Regex _dohIpRegex =
            new Regex(@"""data""\s*:\s*""(\d+\.\d+\.\d+\.\d+)""", RegexOptions.Compiled);

        private static readonly HttpClient _dohClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private struct DnsRow
        {
            public string Server;   // null = 系统DNS, IP地址 = UDP DNS, URL = DoH
            public Label Label1;     // 左列结果标签
            public Label Label2;     // 右列结果标签
            public bool IsDoh;       // 是否为 DoH 查询
        }

        private DnsRow[] _rows;

        public DNSTest()
        {
            InitializeComponent();
            InitDnsRows();
            WireupNewLabelEvents();
        }

        private void InitDnsRows()
        {
            _rows = new DnsRow[]
            {
                new DnsRow { Server = null,                                 Label1 = lblSys1,     Label2 = lblSys2,     IsDoh = false },
                new DnsRow { Server = "223.5.5.5",                         Label1 = lbl2231,     Label2 = lbl2232,     IsDoh = false },
                new DnsRow { Server = "119.29.29.29",                      Label1 = lbl1191,     Label2 = lbl1192,     IsDoh = false },
                new DnsRow { Server = "https://dns.alidns.com/resolve",    Label1 = lbl223DOH1,  Label2 = lbl223DOH2,  IsDoh = true },
                new DnsRow { Server = "https://doh.pub/resolve",           Label1 = lbl119DOH1,  Label2 = lbl119DOH2,  IsDoh = true },
                new DnsRow { Server = "114.114.114.114",                   Label1 = lbl1141,     Label2 = lbl1142,     IsDoh = false },
                new DnsRow { Server = "8.8.8.8",                           Label1 = lblGoogle1,  Label2 = lblGoogle2,  IsDoh = false },
                new DnsRow { Server = "4.2.2.1",                           Label1 = lblMS1,  Label2 = lblMS2,  IsDoh = false },
                new DnsRow { Server = "1.14.5.14",                        Label1 = lblWrong1,   Label2 = lblWrong2,   IsDoh = false },
            };
        }

        private void WireupNewLabelEvents()
        {
            lbl1191.TextChanged += (s, e) => toolTip1.SetToolTip(lbl1191, lbl1191.Text);
            lbl1192.TextChanged += (s, e) => toolTip1.SetToolTip(lbl1192, lbl1192.Text);
            lbl223DOH1.TextChanged += (s, e) => toolTip1.SetToolTip(lbl223DOH1, lbl223DOH1.Text);
            lbl223DOH2.TextChanged += (s, e) => toolTip1.SetToolTip(lbl223DOH2, lbl223DOH2.Text);
            lbl119DOH1.TextChanged += (s, e) => toolTip1.SetToolTip(lbl119DOH1, lbl119DOH1.Text);
            lbl119DOH2.TextChanged += (s, e) => toolTip1.SetToolTip(lbl119DOH2, lbl119DOH2.Text);
        }

        private void SafeInvoke(Control ctrl, Action action)
        {
            if (ctrl.IsDisposed || !ctrl.IsHandleCreated) return;
            ctrl.Invoke(action);
        }

        private async Task PerformSystemDnsTest(string domain, Label targetLabel)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain);

                var ips = addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Take(2)
                    .Select(a => a.ToString())
                    .ToList();

                string displayText = ips.Count > 0 ? string.Join(", ", ips) : "未找到IPv4记录";
                SafeInvoke(targetLabel, () => targetLabel.Text = displayText);
            }
            catch (Exception ex)
            {
                SafeInvoke(targetLabel, () => targetLabel.Text = $"DNS失败(?)\n{ex.Message}");
            }
        }

        private async Task PerformDnsTest(string dnsServer, string domain, Label targetLabel, CancellationToken token)
        {
            try
            {
                var ips = await Task.Run(() =>
                {
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        socket.ReceiveTimeout = 3000;
                        socket.SendTimeout = 3000;
                        socket.SendTo(BuildDnsQuery(domain), new IPEndPoint(IPAddress.Parse(dnsServer), 53));

                        byte[] response = new byte[512];
                        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                        int len = socket.ReceiveFrom(response, ref remoteEP);
                        return ParseDnsARecords(response, len);
                    }
                }, token);

                string displayText = ips.Count > 0 ? string.Join(", ", ips) : "未找到IPv4记录";
                SafeInvoke(targetLabel, () => targetLabel.Text = displayText);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SafeInvoke(targetLabel, () => targetLabel.Text = $"DNS失败(?)\n{ex.Message}");
            }
        }

        private async Task PerformDohDnsTest(string dohUrl, string domain, Label targetLabel, CancellationToken token)
        {
            try
            {
                var requestUrl = $"{dohUrl}?name={Uri.EscapeDataString(domain)}&type=A";
                var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

                var response = await _dohClient.SendAsync(request, token);
                var json = await response.Content.ReadAsStringAsync();

                var ips = _dohIpRegex.Matches(json)
                    .Cast<Match>()
                    .Take(2)
                    .Select(m => m.Groups[1].Value)
                    .ToList();

                string displayText = ips.Count > 0 ? string.Join(", ", ips) : "未找到IPv4记录";
                SafeInvoke(targetLabel, () => targetLabel.Text = displayText);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SafeInvoke(targetLabel, () => targetLabel.Text = $"DoH失败(?)\n{ex.Message}");
            }
        }

        private static byte[] BuildDnsQuery(string domain)
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
            packet.AddRange(new byte[] { 0x00, 0x01 }); // QTYPE  = A
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

            for (int i = 0; i < ancount && ips.Count < 2; i++)
            {
                // 跳过 NAME（可能是指针压缩）
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

        private async Task ApplyDNSTestThemeAsync()
        {
            await Task.Yield();

            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color controlBack = isLight ? SystemColors.Window : Color.FromArgb(45, 45, 48);
            Color buttonBack = isLight ? SystemColors.ButtonFace : Color.FromArgb(55, 55, 58);
            Color comboFore = isLight ? SystemColors.ControlText : Color.White;

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] titleLabels = {
                lblSystem, lbl223, lbl119, label6, label3,
                lbl114, lblGoogle, lblMS, lblWrong
            };
            foreach (var l in titleLabels)
                if (l != null) l.ForeColor = yumeyoColor;

            Control[] resultLabels = {
                lblSys1, lblSys2, lbl2231, lbl2232,
                lbl1191, lbl1192, lbl223DOH1, lbl223DOH2,
                lbl119DOH1, lbl119DOH2,
                lbl1141, lbl1142, lblGoogle1, lblGoogle2, lblMS1, lblMS2,
                lblWrong1, lblWrong2
            };
            foreach (var r in resultLabels)
                if (r != null) r.ForeColor = contrastColor;

            if (combo1 != null)
            {
                combo1.FlatStyle = FlatStyle.Flat;
                combo1.BackColor = controlBack;
                combo1.ForeColor = comboFore;
            }
            if (combo2 != null)
            {
                combo2.FlatStyle = FlatStyle.Flat;
                combo2.BackColor = controlBack;
                combo2.ForeColor = comboFore;
            }

            if (btnStart1 != null)
            {
                btnStart1.BackColor = buttonBack;
                btnStart1.ForeColor = contrastColor;
                btnStart1.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                if (!isLight) btnStart1.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
            }
            if (btnStart2 != null)
            {
                btnStart2.BackColor = buttonBack;
                btnStart2.ForeColor = contrastColor;
                btnStart2.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                if (!isLight) btnStart2.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
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
            _cts1?.Cancel();
            _cts1?.Dispose();
            _cts2?.Cancel();
            _cts2?.Dispose();
            timer1.Dispose();
        }

        private async void btnStart1_Click(object sender, EventArgs e)
        {
            _cts1?.Cancel();
            _cts1?.Dispose();
            _cts1 = new CancellationTokenSource();
            btnStart1.Enabled = false;
            try
            {
                await RunStaggeredTests(combo1, column: 1, _cts1.Token);
            }
            finally
            {
                SafeInvoke(btnStart1, () => btnStart1.Enabled = true);
            }
        }

        private async void btnStart2_Click(object sender, EventArgs e)
        {
            _cts2?.Cancel();
            _cts2?.Dispose();
            _cts2 = new CancellationTokenSource();
            btnStart2.Enabled = false;
            try
            {
                await RunStaggeredTests(combo2, column: 2, _cts2.Token);
            }
            finally
            {
                SafeInvoke(btnStart2, () => btnStart2.Enabled = true);
            }
        }

        private async Task RunStaggeredTests(ComboBox combo, int column, CancellationToken token)
        {
            string domain = combo.Text.Trim();
            if (string.IsNullOrEmpty(domain))
            {
                MessageBox.Show("请先输入或选择一个域名。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var tasks = new List<Task>();
            try
            {
                foreach (var row in _rows)
                {
                    token.ThrowIfCancellationRequested();

                    Label targetLabel = column == 1 ? row.Label1 : row.Label2;
                    SafeInvoke(targetLabel, () => targetLabel.Text = "查询中…");

                    if (row.Server == null)
                        tasks.Add(PerformSystemDnsTest(domain, targetLabel));
                    else if (row.IsDoh)
                        tasks.Add(PerformDohDnsTest(row.Server, domain, targetLabel, token));
                    else
                        tasks.Add(PerformDnsTest(row.Server, domain, targetLabel, token));

                    try { await Task.Delay(100, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { }

            await Task.WhenAll(tasks);
        }

        private void lblSysBaidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblSys1, lblSys1.Text);
        }
        private void lblSysQQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblSys2, lblSys2.Text);
        }
        private void lbl223Baidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl2231, lbl2231.Text);
        }
        private void lbl223QQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl2232, lbl2232.Text);
        }
        private void lbl114Baidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl1141, lbl1141.Text);
        }
        private void lbl114QQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lbl1142, lbl1142.Text);
        }
        private void lblGoogleBaidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblGoogle1, lblGoogle1.Text);
        }
        private void lblGoogleQQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblGoogle2, lblGoogle2.Text);
        }
        private void lblWrongBaidu_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblWrong1, lblWrong1.Text);
        }
        private void lblWrongQQ_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(lblWrong2, lblWrong2.Text);
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            lblVersion.Text = Global.exeName + " " + Global.Version + " | " + Others.GetCurrentTime();
        }
    }
}
