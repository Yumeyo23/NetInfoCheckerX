using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class DNSExit : Form
    {
        private const string AnyV4Text = "0.0.0.0 (Any)";
        private const string AnyV6Text = ":: (IPv6 Any)";
        private const string SystemDefaultText = "系统默认 (兼容模式)";
        private const string IniSection = "DNSExit";
        private const string BaseTitle = "DNS出口/泄露测试 ✧ NetInfoCheckerX";

        private readonly Dictionary<string, DataGridViewRow> _rows =
            new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Task> _geoTasks = new List<Task>();
        private readonly SemaphoreSlim _geoSemaphore = new SemaphoreSlim(3, 3);
        private readonly System.Windows.Forms.Timer _titleTimer;
        private readonly Stopwatch _testStopwatch = new Stopwatch();
        private CancellationTokenSource _testCts;
        private bool _isRunning;
        private bool _isStopping;
        private bool _unlimitedTime;
        private int _totalSeconds;
        private int _requestTimeoutMs;
        private int _geoProviderIndex;
        private IPAddress _bindIp;
        private bool _forceIPv4;
        private bool _forceIPv6;

        private string IniPath => System.IO.Path.Combine(
            Application.StartupPath, "NetInfoCheckerX.ini");

        public DNSExit()
        {
            InitializeComponent();
            MinimumSize = Size;
            comboNIC.DropDownStyle = ComboBoxStyle.DropDown;
            dataGridView1.AllowUserToAddRows = false;
            ConfigureResultGrid();
            _titleTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _titleTimer.Tick += (sender, e) => UpdateRunningTitle();
            FormClosing += DNSExit_FormClosing;
            btnStart.Click += btnStart_Click;
            lblNIC.MouseDown += lblNIC_MouseDown;
        }
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hwnd, int wMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        private const int HTCAPTION = 0x0002;

        private void MyMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_SYSCOMMAND, SC_MOVE + HTCAPTION, 0);
            }
        }
        private void DNSExit_Load(object sender, EventArgs e)
        {
            RefreshNicList();
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            _geoProviderIndex = ReadGeoProviderIndex();
            Text = BaseTitle;
            ApplyDNSExitTheme();
            lblVersion.Text = Global.exeName + " " + Global.Version;
            CloudControl.UsedTimesCounter("DNS泄露");
        }

        private void ConfigureResultGrid()
        {
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dataGridView1.ScrollBars = ScrollBars.Both;
            dataGridView1.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;

            colNum.AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader;
            coIP.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colGEO.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colFrom.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            coIP.FillWeight = 35F;
            colGEO.FillWeight = 40F;
            colFrom.FillWeight = 30F;
            colNum.Resizable = DataGridViewTriState.False;
            colNum.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            colNum.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            coIP.Resizable = DataGridViewTriState.True;
            colGEO.Resizable = DataGridViewTriState.True;
            colFrom.Resizable = DataGridViewTriState.True;
            colNum.MinimumWidth = 41;
            float dpiScale = dataGridView1.DeviceDpi / 96F;
            int ipv4Width = TextRenderer.MeasureText("255.255.255.255",
                dataGridView1.Font).Width;
            int partialIpv6Width = TextRenderer.MeasureText("8888:8888:8888",
                dataGridView1.Font).Width;
            coIP.MinimumWidth = Math.Max((int)Math.Ceiling(145F * dpiScale),
                Math.Max(ipv4Width, partialIpv6Width) +
                (int)Math.Ceiling(24F * dpiScale));
            colGEO.MinimumWidth = 160;
            colFrom.MinimumWidth = 90;
        }

        private void ApplyDNSExitTheme()
        {
            bool isLight = Global.isThemelight;
            Color foreground = isLight ? Global.colorBlack : Global.colorWhite;
            Color formBackground = isLight ? Global.themeLight : Global.themeBlack;
            Color controlBackground = isLight ? SystemColors.Window : Color.FromArgb(32, 32, 32);
            Color headerBackground = isLight ? SystemColors.Control : Color.FromArgb(45, 45, 48);
            Color alternateBackground = isLight ? Color.FromArgb(248, 248, 248) : Color.FromArgb(25, 25, 25);
            Color accent = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color accentText = GetReadableTextColor(accent);

            BackColor = formBackground;
            ForeColor = foreground;
            pictureBox1.BackColor = Color.Transparent;

            foreach (Control control in new Control[] { lblNIC, lblTime, lblTimeout })
            {
                control.BackColor = Color.Transparent;
                control.ForeColor = foreground;
            }

            // lblVersion 的文字由设计器/调用方维护，这里仅应用主题颜色。
            lblVersion.BackColor = Color.Transparent;
            lblVersion.ForeColor = accent;

            foreach (TextBox textBox in new[] { txtTime, txtTimeout })
            {
                textBox.BackColor = controlBackground;
                textBox.ForeColor = foreground;
                textBox.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

            comboNIC.BackColor = controlBackground;
            comboNIC.ForeColor = foreground;
            comboNIC.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;

            lnkProxy.BackColor = Color.Transparent;
            lnkProxy.LinkColor = accent;
            lnkProxy.ActiveLinkColor = foreground;
            lnkProxy.VisitedLinkColor = accent;
            ApplyButtonTheme(btnStart);

            dataGridView1.EnableHeadersVisualStyles = false;
            dataGridView1.BackgroundColor = controlBackground;
            dataGridView1.GridColor = isLight ? Color.FromArgb(210, 210, 210) : Color.FromArgb(65, 65, 65);
            dataGridView1.ColumnHeadersDefaultCellStyle.BackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionBackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
            dataGridView1.DefaultCellStyle.BackColor = controlBackground;
            dataGridView1.DefaultCellStyle.ForeColor = foreground;
            dataGridView1.DefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.DefaultCellStyle.SelectionForeColor = accentText;
            dataGridView1.RowsDefaultCellStyle.BackColor = controlBackground;
            dataGridView1.RowsDefaultCellStyle.ForeColor = foreground;
            dataGridView1.RowsDefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.RowsDefaultCellStyle.SelectionForeColor = accentText;
            dataGridView1.AlternatingRowsDefaultCellStyle.BackColor = alternateBackground;
            dataGridView1.AlternatingRowsDefaultCellStyle.ForeColor = foreground;
            dataGridView1.AlternatingRowsDefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.AlternatingRowsDefaultCellStyle.SelectionForeColor = accentText;
        }

        private static Color GetReadableTextColor(Color background)
        {
            int brightness = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
            return brightness >= 145 ? Color.Black : Color.White;
        }

        private static void ApplyButtonTheme(Button button)
        {
            if (Global.isThemelight)
            {
                button.ForeColor = SystemColors.ControlText;
                button.BackColor = SystemColors.Control;
                button.FlatStyle = FlatStyle.Standard;
                button.UseVisualStyleBackColor = true;
                return;
            }

            button.ForeColor = Global.colorWhite;
            button.BackColor = Color.FromArgb(60, 60, 60);
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
            button.FlatAppearance.MouseOverBackColor = Global.Yumeyo2;
        }

        private bool RefreshNicList(string preferredText = null)
        {
            string selected = preferredText ?? (comboNIC.SelectedItem == null
                ? comboNIC.Text
                : comboNIC.SelectedItem.ToString());
            return NicHelper.RefreshAddressCombo(comboNIC,
                new[] { AnyV4Text, AnyV6Text, SystemDefaultText },
                true, true, selected, AnyV4Text);
        }

        private void lblNIC_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || _isRunning) return;

            string selected = comboNIC.Text;
            bool retained = RefreshNicList(selected);
            string message = retained
                ? "网卡列表已刷新"
                : "网卡列表已刷新，原网卡不存在，已回退到 0.0.0.0";
            toolTip1.Show(message, lblNIC, 0, lblNIC.Height, retained ? 800 : 1500);
        }

        private void ResolveAndValidateSelectedNic()
        {
            _bindIp = null;
            _forceIPv4 = false;
            _forceIPv6 = false;

            string selected = comboNIC.Text ?? string.Empty;
            AddressFamily? requestedFamily = null;
            if (selected.StartsWith("0.0.0.0", StringComparison.Ordinal))
                requestedFamily = AddressFamily.InterNetwork;
            else if (selected.StartsWith("::", StringComparison.Ordinal))
                requestedFamily = AddressFamily.InterNetworkV6;

            if (requestedFamily.HasValue)
            {
                IPAddress actual = NicHelper.GetDefaultLocalAddress(requestedFamily.Value);
                if (actual != null && SelectNicAddress(actual))
                    selected = comboNIC.Text;
                else
                {
                    _forceIPv4 = requestedFamily.Value == AddressFamily.InterNetwork;
                    _forceIPv6 = requestedFamily.Value == AddressFamily.InterNetworkV6;
                    return;
                }
            }

            if (selected.Contains("系统默认")) return;

            string ipText = selected.Split(' ')[0];
            if (!IPAddress.TryParse(ipText, out IPAddress selectedIp) ||
                !NicHelper.IsUsableLocalAddress(selectedIp))
            {
                RefreshNicList(AnyV4Text);
                IPAddress actual = NicHelper.GetDefaultLocalAddress(AddressFamily.InterNetwork);
                if (actual == null || !SelectNicAddress(actual))
                {
                    _forceIPv4 = true;
                    return;
                }
                selectedIp = actual;
            }

            _bindIp = selectedIp;
            _forceIPv4 = selectedIp.AddressFamily == AddressFamily.InterNetwork;
            _forceIPv6 = selectedIp.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private bool SelectNicAddress(IPAddress address)
        {
            if (address == null) return false;
            string addressText = address.ToString();
            int scopeIndex = addressText.IndexOf('%');
            if (scopeIndex >= 0) addressText = addressText.Substring(0, scopeIndex);

            foreach (object item in comboNIC.Items)
            {
                string itemText = Convert.ToString(item);
                if (itemText.StartsWith(addressText + " (", StringComparison.OrdinalIgnoreCase))
                {
                    comboNIC.SelectedItem = item;
                    return true;
                }
            }

            RefreshNicList(comboNIC.Text);
            foreach (object item in comboNIC.Items)
            {
                string itemText = Convert.ToString(item);
                if (itemText.StartsWith(addressText + " (", StringComparison.OrdinalIgnoreCase))
                {
                    comboNIC.SelectedItem = item;
                    return true;
                }
            }
            return false;
        }

        private int ReadGeoProviderIndex()
        {
            try
            {
                var value = new StringBuilder(16);
                IniFileHelper.GetPrivateProfileString(IniSection, "DNSExitGEO", "0",
                    value, value.Capacity, IniPath);
                if (int.TryParse(value.ToString(), out int index) && index >= 0 &&
                    index < Api2.GeoCN_Providers.Count)
                    return index;
            }
            catch { }
            return 0;
        }

        private bool TryReadSettings(out int totalSeconds)
        {
            totalSeconds = 0;
            _unlimitedTime = Global.isYumeyo && Global.isUnlimitedTime;
            if ((!int.TryParse(txtTime.Text.Trim(), out totalSeconds) ||
                totalSeconds < 1 || totalSeconds > 999) && !_unlimitedTime)
            {
                MessageBox.Show("总测试时长请输入 1 - 999 秒", "DNS出口测试",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtTime.Focus();
                return false;
            }
            if (_unlimitedTime && totalSeconds < 1) totalSeconds = 0;

            if (!int.TryParse(txtTimeout.Text.Trim(), out _requestTimeoutMs) ||
                _requestTimeoutMs < 100 || _requestTimeoutMs > 9999)
            {
                MessageBox.Show("单次访问超时请输入 100 - 9999 毫秒", "DNS出口测试",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtTimeout.Focus();
                return false;
            }

            ResolveAndValidateSelectedNic();
            return true;
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (_isRunning)
            {
                _isStopping = true;
                _testCts?.Cancel();
                UpdateRunningTitle();
                return;
            }
            if (!TryReadSettings(out int totalSeconds)) return;

            _isRunning = true;
            btnStart.Text = "停止";
            comboNIC.Enabled = false;
            txtTime.Enabled = false;
            txtTimeout.Enabled = false;
            dataGridView1.Rows.Clear();
            _rows.Clear();
            _geoTasks.Clear();
            _geoProviderIndex = ReadGeoProviderIndex();
            _testCts = new CancellationTokenSource();
            _totalSeconds = totalSeconds;
            if (!_unlimitedTime)
                _testCts.CancelAfter(TimeSpan.FromSeconds(totalSeconds));
            CancellationToken token = _testCts.Token;
            _isStopping = false;
            _testStopwatch.Restart();
            _titleTimer.Start();
            UpdateRunningTitle();
            IDisposable geoUsageCountScope = GeoProvider.BeginUsageCountScope();

            try
            {
                await Task.WhenAll(CreateProbeTasks(token));
                await Task.WhenAll(_geoTasks.ToArray());
            }
            catch (OperationCanceledException) { }
            finally
            {
                geoUsageCountScope.Dispose();
                _titleTimer.Stop();
                _testStopwatch.Stop();
                _testCts.Dispose();
                _testCts = null;
                _isRunning = false;
                _isStopping = false;
                btnStart.Text = "开测";
                comboNIC.Enabled = true;
                txtTime.Enabled = true;
                txtTimeout.Enabled = true;
                Text = string.Format(BaseTitle + " | 完成 ({0}个出口)",
                    _rows.Count, _testStopwatch.Elapsed.TotalSeconds);
            }
        }

        private void UpdateRunningTitle()
        {
            if (!_isRunning) return;
            string state = _isStopping ? "正在停止" : "测试中";
            if (_unlimitedTime)
            {
                Text = string.Format(BaseTitle + " | {0} ({1}个出口/{2:0}s)",
                    state, _rows.Count, _testStopwatch.Elapsed.TotalSeconds);
                return;
            }

            int remainingSeconds = Math.Max(0, (int)Math.Ceiling(
                _totalSeconds - _testStopwatch.Elapsed.TotalSeconds));
            Text = string.Format(BaseTitle + " | {0} ({1}个出口/{2}s)",
                state, _rows.Count, remainingSeconds);
        }

        private IEnumerable<Task> CreateProbeTasks(CancellationToken token)
        {
            var tasks = new List<Task>
            {
                RunProbeLoopAsync("网易", 8, ProbeNetEaseAsync, token),
                RunProbeLoopAsync("阿里云", 4, ProbeAlibabaAsync, token),
                RunProbeLoopAsync("IP-API", 4, ProbeIpApiAsync, token),
                RunProbeLoopAsync("Fastly", 4, ProbeFastlyAsync, token),
                RunProbeLoopAsync("IPLeak", 8, ProbeIpLeakAsync, token)
            };

            if (!_forceIPv6)
            {
                tasks.Add(RunProbeLoopAsync("Surfshark", 4,
                    ct => ProbeSurfsharkAsync(4, ct), token));
                tasks.Add(RunProbeLoopAsync("BrowserLeaks", 2,
                    ct => ProbeBrowserLeaksAsync(4, "net", ct), token));
                tasks.Add(RunProbeLoopAsync("BrowserLeaks", 4,
                    ct => ProbeBrowserLeaksAsync(4, "org", ct), token));
            }
            if (!_forceIPv4)
            {
                tasks.Add(RunProbeLoopAsync("Surfshark", 4,
                    ct => ProbeSurfsharkAsync(6, ct), token));
                tasks.Add(RunProbeLoopAsync("BrowserLeaks", 2,
                    ct => ProbeBrowserLeaksAsync(6, "net", ct), token));
                tasks.Add(RunProbeLoopAsync("BrowserLeaks", 4,
                    ct => ProbeBrowserLeaksAsync(6, "org", ct), token));
            }
            return tasks;
        }

        private async Task RunProbeLoopAsync(string source, int count,
            Func<CancellationToken, Task<IEnumerable<string>>> probe,
            CancellationToken totalToken)
        {
            while (!totalToken.IsCancellationRequested)
            {
                for (int i = 0; i < count && !totalToken.IsCancellationRequested; i++)
                {
                    using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(totalToken))
                    {
                        requestCts.CancelAfter(_requestTimeoutMs);
                        try
                        {
                            IEnumerable<string> addresses = await probe(requestCts.Token)
                                .ConfigureAwait(false);
                            foreach (string address in addresses ?? Enumerable.Empty<string>())
                                PostResult(address, source, totalToken);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("[DNSExit] " + source + ": " + ex.Message);
                        }
                    }

                    if (i + 1 < count && !totalToken.IsCancellationRequested)
                    {
                        try { await Task.Delay(100, totalToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }

                if (!totalToken.IsCancellationRequested)
                {
                    // 每批之间稍作停顿，避免响应很快时对检测服务造成过高请求压力。
                    try { await Task.Delay(500, totalToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private Task<string> FetchAsync(string url, CancellationToken token)
        {
            return HttpHelper.SendAsync(url, token, forceIPv4: _forceIPv4,
                forceIPv6: _forceIPv6, bindIP: _bindIp);
        }

        private async Task<IEnumerable<string>> ProbeNetEaseAsync(CancellationToken token)
        {
            string text = await FetchAsync("https://nstool.netease.com/info.js?_=" +
                DateTime.UtcNow.Ticks, token).ConfigureAwait(false);
            Match match = Regex.Match(text ?? string.Empty,
                "\\bdns\\s*=\\s*['\\\"](?<ip>[^'\\\"]+)['\\\"]",
                RegexOptions.IgnoreCase);
            return NormalizeAddresses(match.Success ? new[] { match.Groups["ip"].Value } : null);
        }

        private async Task<IEnumerable<string>> ProbeAlibabaAsync(CancellationToken token)
        {
            string callback = "nicx" + RandomToken(12);
            string url = "https://" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" +
                RandomToken(16) + ".dns-detect.alicdn.com/api/detect/DescribeDNSLookup?cb=" + callback;
            string text = await FetchAsync(url, token).ConfigureAwait(false);
            int start = text == null ? -1 : text.IndexOf('(');
            int end = text == null ? -1 : text.LastIndexOf(')');
            if (start >= 0 && end > start) text = text.Substring(start + 1, end - start - 1);
            return NormalizeAddresses(new[] { GetString(Deserialize(text), "content", "ldns") });
        }

        private async Task<IEnumerable<string>> ProbeIpApiAsync(CancellationToken token)
        {
            string text = await FetchAsync("https://" + RandomToken(32) +
                ".edns.ip-api.com/json?lang=zh-CN", token).ConfigureAwait(false);
            return NormalizeAddresses(new[] { GetString(Deserialize(text), "dns", "ip") });
        }

        private async Task<IEnumerable<string>> ProbeFastlyAsync(CancellationToken token)
        {
            string text = await FetchAsync("https://" +
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + RandomToken(8) +
                ".u.fastly-analytics.com/debug_resolver", token).ConfigureAwait(false);
            return NormalizeAddresses(new[] {
                GetString(Deserialize(text), "dns_resolver_info", "ip") });
        }

        private async Task<IEnumerable<string>> ProbeSurfsharkAsync(int version,
            CancellationToken token)
        {
            string text = await FetchAsync("https://" +
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + RandomToken(8) +
                ".ipv" + version + ".surfsharkdns.com", token).ConfigureAwait(false);
            return DictionaryAddressKeys(Deserialize(text));
        }

        private async Task<IEnumerable<string>> ProbeBrowserLeaksAsync(int version,
            string suffix, CancellationToken token)
        {
            string text = await FetchAsync("https://" + RandomToken(12) + ".dns" +
                version + ".browserleaks." + suffix, token).ConfigureAwait(false);
            return DictionaryAddressKeys(Deserialize(text));
        }

        private async Task<IEnumerable<string>> ProbeIpLeakAsync(CancellationToken token)
        {
            string text = await FetchAsync("https://" + RandomToken(40) +
                "-1.ipleak.net/dnsdetection/", token).ConfigureAwait(false);
            object root = Deserialize(text);
            var dictionary = root as Dictionary<string, object>;
            if (dictionary == null || !dictionary.TryGetValue("ip", out object value))
                return Enumerable.Empty<string>();
            return DictionaryAddressKeys(value);
        }

        private static object Deserialize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return new JavaScriptSerializer().DeserializeObject(text); }
            catch { return null; }
        }

        private static string GetString(object value, params string[] path)
        {
            object current = value;
            foreach (string part in path)
            {
                var dictionary = current as Dictionary<string, object>;
                if (dictionary == null || !dictionary.TryGetValue(part, out current))
                    return null;
            }
            return current == null ? null : Convert.ToString(current);
        }

        private static IEnumerable<string> DictionaryAddressKeys(object value)
        {
            var dictionary = value as Dictionary<string, object>;
            return dictionary == null
                ? Enumerable.Empty<string>()
                : NormalizeAddresses(dictionary.Keys);
        }

        private static IEnumerable<string> NormalizeAddresses(IEnumerable<string> values)
        {
            if (values == null) return Enumerable.Empty<string>();
            var result = new List<string>();
            foreach (string value in values)
            {
                if (!IPAddress.TryParse((value ?? string.Empty).Trim(), out IPAddress ip))
                    continue;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                string normalized = ip.ToString();
                if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    result.Add(normalized);
            }
            return result;
        }

        private static string RandomToken(int length)
        {
            var builder = new StringBuilder(length);
            while (builder.Length < length)
                builder.Append(Guid.NewGuid().ToString("N"));
            return builder.ToString(0, length);
        }

        private void PostResult(string ip, string source, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(ip) || IsDisposed || !IsHandleCreated) return;
            try { Invoke((Action)(() => AddOrUpdateResult(ip, source, token))); }
            catch (InvalidOperationException) { }
        }

        private void AddOrUpdateResult(string ip, string source, CancellationToken token)
        {
            if (_rows.TryGetValue(ip, out DataGridViewRow existing))
            {
                string sources = Convert.ToString(existing.Cells[colFrom.Name].Value);
                var items = sources.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                if (!items.Contains(source))
                {
                    items.Add(source);
                    existing.Cells[colFrom.Name].Value = string.Join(", ", items);
                }
                return;
            }

            int index = dataGridView1.Rows.Add(_rows.Count + 1, ip, "查询中...", source);
            DataGridViewRow row = dataGridView1.Rows[index];
            _rows[ip] = row;
            _geoTasks.Add(UpdateGeoAsync(ip, row, token));
            UpdateRunningTitle();
        }

        private async Task UpdateGeoAsync(string ip, DataGridViewRow row,
            CancellationToken token)
        {
            bool entered = false;
            try
            {
                await _geoSemaphore.WaitAsync(token);
                entered = true;
                string location = await Trace.ResolveSharedGeoAsync(
                    _geoProviderIndex, ip, token);
                if (!IsDisposed && row.DataGridView != null)
                    row.Cells[colGEO.Name].Value = string.IsNullOrWhiteSpace(location)
                        ? "未知" : location;
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && row.DataGridView != null)
                    row.Cells[colGEO.Name].Value = "已取消";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DNSExit-GEO] " + ex.Message);
                if (!IsDisposed && row.DataGridView != null)
                    row.Cells[colGEO.Name].Value = "查询失败";
            }
            finally
            {
                if (entered) _geoSemaphore.Release();
            }
        }

        private void lnkProxy_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            MessageBox.Show(
                "查询器X设置中的“是否走系统代理”设置项【优先于】系统设置，\r\n可在“设置关于”窗口查看当前是否走代理状态\r\n\r\n" +
                "默认代理参数：\r\n" +
                "【0.0.0.0】【::】或【具体网卡IP】时，【不走】系统代理，使用指定网卡IP出口；\r\n" +
                "【系统默认】或【设置-使用系统代理-选中】时，【走】系统代理，不指定网卡IP出口，系统代理决定实际出口\r\n\r\n" +
                "查询器X设置不影响 TUN/VPN/路由器透明代理 等路由层代理，仅涉及系统代理。",
                "系统代理说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DNSExit_FormClosing(object sender, FormClosingEventArgs e)
        {
            _titleTimer.Stop();
            _testCts?.Cancel();
        }
    }
}
