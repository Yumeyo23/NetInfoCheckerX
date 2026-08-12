using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Net;
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
        private List<IPEndPoint> _publicEndPoints5780 = new List<IPEndPoint>();
        private List<IPEndPoint> _publicEndPoints3489 = new List<IPEndPoint>();

        private CancellationTokenSource _cts3489;
        private CancellationTokenSource _cts5780;
        private CancellationTokenSource _batchCts;
        private Socket _activeSocket3489;
        private Socket _activeSocket5780;
        private bool _isBatchTesting;
        private bool _isResolvingServer;
        private bool _isReloading;
        private string _resolvedServerDomain;
        private readonly HashSet<string> _resolvedServerAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private sealed class BatchServerEntry
        {
            public string Host { get; set; }
            public int Port { get; set; }
            public string DisplayName { get; set; }
        }

        private sealed class BatchAttemptResult
        {
            public bool Success { get; set; }
            public string Summary { get; set; }
        }

        private sealed class BatchServerResult
        {
            public string Server { get; set; }
            public string Protocol5780 { get; set; }
            public List<BatchAttemptResult> Attempts5780 { get; } = new List<BatchAttemptResult>();
            public List<BatchAttemptResult> Attempts3489 { get; } = new List<BatchAttemptResult>();

            public bool Is5780Available
            {
                get { return Attempts5780.Exists(item => item.Success); }
            }

            public bool Is3489Available
            {
                get { return Attempts3489.Exists(item => item.Success); }
            }
        }

        //严格模式
        //bool strictMode = true;

        public NATTest()
        {
            InitializeComponent();
        }
        /// <summary>
        /// 通过 UDP connect 探测系统为指定目标选择的路由出口 IP。
        /// UDP connect 不会真正发包，系统仅完成路由决策。
        /// </summary>
        private IPAddress GetLocalRoutingIp(IPEndPoint targetServer)
        {
            try
            {
                using (Socket socket = new Socket(targetServer.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(targetServer);
                    IPAddress localAddress = ((IPEndPoint)socket.LocalEndPoint).Address;

                    if (localAddress.Equals(IPAddress.IPv6Any) || localAddress.Equals(IPAddress.Any))
                    {
                        return localAddress;
                    }

                    return localAddress;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                if (targetServer.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    return IPAddress.IPv6Any;
                }
                else
                {
                    return IPAddress.Any;
                }
            }
        }
        private async Task ApplyNATThemeAsync()
        {
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoControls = {
        lbl5780, lbl3489, lbl5780StartTime, lbl3489StartTime, lblExeName, lblSetting
    };
            foreach (var c in yumeyoControls) { if (c != null) c.ForeColor = yumeyoColor; }

            Control[] contrastControls = {
        lbl5780Binding, lbl5780Mapping, lbl5780Filtering, lbl5780LocalEnd,
        lbl5780PublicEnd, lbl3489Type, lbl3489LocalEnd, lbl3489PublicEnd,
        checkPortRandom, checkPortMode, checkPortRange, checkSelectIP, radioTCP, radioUDP, radioTLS, lblTimeout
    };
            foreach (var c in contrastControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    if (c is CheckBox) c.BackColor = Color.Transparent;
                }
            }

            Control[] editControls = {
    txt5780Debug, txt3489Debug, txt5780Binding, txt5780Mapping,
    txt5780Filtering, combo5780LocalEnd, txt5780PublicEnd,
    txt3489Type, combo3489LocalEnd, txt3489PublicEnd, comboServer, txtServerPort, txtTimeout
};

            foreach (var c in editControls)
            {
                if (c != null)
                {
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

                    if (c is ComboBox cb)
                    {
                        if (isLight)
                        {
                            cb.FlatStyle = FlatStyle.Standard;
                        }
                        else
                        {
                            cb.FlatStyle = FlatStyle.Flat;
                        }
                    }
                }
            }

            Control[] buttons = { btnCheck5780, btnCheck3489, btnRFCCompare, btnMiaoDong, btnReset, btnSettings };
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
                        btn.FlatAppearance.MouseOverBackColor = Global.Yumeyo;
                    }
                }
            }
        }
        private async void NATTest_Load(object sender, EventArgs e)
        {
            lblExeName.Text = Global.exeName + " " + Global.Version;
            _ = ApplyNATThemeAsync();

            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            pictureBox2.MouseDown += MyMouseDown;

            btnCheck5780.Click -= btnCheck5780_Click;
            btnCheck3489.Click -= btnCheck3489_Click;

            LoadCheckStates();
            LoadLocalIPs();

            CloudControl.LoadStunServers(comboServer);
            CloudControl.ApplyDevTitle(this);

            if (Global.IsBirthdayMonth)
            {
                if (Global.isThemelight)
                {
                    pictureBox2.Image = Global.GetIconw();
                }
                else
                {
                    pictureBox2.Image = Global.GetIcon();
                }
            }

            LoadSavedServerAndPort();

            if (comboServer.SelectedIndex < 0 && comboServer.Items.Count > 0)
            {
                comboServer.SelectedIndex = 0;
            }

            txtServerPort.KeyPress += (s, ev) =>
            {
                if (!char.IsDigit(ev.KeyChar) && !char.IsControl(ev.KeyChar))
                    ev.Handled = true;
            };

            txtServerPort.Leave += (s, ev) =>
            {
                if (!int.TryParse(txtServerPort.Text, out int port) || port < 1 || port > 65535)
                    txtServerPort.Text = "3478";
            };

            txtTimeout.KeyPress += (s, ev) =>
            {
                if (!char.IsDigit(ev.KeyChar) && !char.IsControl(ev.KeyChar))
                    ev.Handled = true;
            };

            txtTimeout.Leave += (s, ev) => GetTimeoutMs();

            SetupButtonEvents5780();
            SetupButtonEvents3489();
            CloudControl.UsedTimesCounter("NAT测试");
        }
        private void SetupButtonEvents5780()
        {
            btnCheck5780.Click -= btnCheck5780_Click;
            btnCheck5780.MouseDown -= Button_MouseDown5780;
            btnCheck5780.MouseWheel -= btnCheck5780_MouseWheel;

            btnCheck5780.Click += btnCheck5780_Click;
            btnCheck5780.MouseDown += Button_MouseDown5780;

            btnCheck5780.MouseEnter -= Button_MouseEnter5780;
            btnCheck5780.MouseLeave -= Button_MouseLeave5780;

            btnCheck5780.MouseEnter += Button_MouseEnter5780;
            btnCheck5780.MouseLeave += Button_MouseLeave5780;
        }

        private void Button_MouseDown5780(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && btnCheck5780.Enabled)
            {
                btnCheck5780_Click(sender, e);
            }
        }

        private void Button_MouseEnter5780(object sender, EventArgs e)
        {
            btnCheck5780.MouseWheel += btnCheck5780_MouseWheel;
        }

        private void Button_MouseLeave5780(object sender, EventArgs e)
        {
            btnCheck5780.MouseWheel -= btnCheck5780_MouseWheel;
        }

        private void btnCheck5780_MouseWheel(object sender, MouseEventArgs e)
        {
            if (e.Delta != 0 && btnCheck5780.Enabled)
            {
                btnCheck5780_Click(sender, e);
            }
        }

        private void SetupButtonEvents3489()
        {
            btnCheck3489.Click -= btnCheck3489_Click;
            btnCheck3489.MouseDown -= Button_MouseDown3489;
            btnCheck3489.MouseWheel -= btnCheck3489_MouseWheel;

            btnCheck3489.Click += btnCheck3489_Click;
            btnCheck3489.MouseDown += Button_MouseDown3489;

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
            btnCheck3489.MouseWheel += btnCheck3489_MouseWheel;
        }

        private void Button_MouseLeave3489(object sender, EventArgs e)
        {
            btnCheck3489.MouseWheel -= btnCheck3489_MouseWheel;
        }
        private void btnCheck3489_MouseWheel(object sender, MouseEventArgs e)
        {
            if (e.Delta != 0 && btnCheck3489.Enabled)
            {
                btnCheck3489_Click(sender, e);
            }
        }

        // 拆分两个端口记忆变量，防止 3489 和 5780 的端口设置互相干扰
        private int _lastPort3489 = 0;
        private int _lastPort5780 = 0;
        private bool _stopRequested = false;
        private readonly Random _portRandom = new Random();

        private string ExtractIPFromComboText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string ipPart = text;
            int descIdx = text.IndexOf(" (");
            if (descIdx > 0) ipPart = text.Substring(0, descIdx);

            if (ipPart.StartsWith("[") && ipPart.Contains("]:"))
            {
                int closeBracket = ipPart.IndexOf(']');
                return ipPart.Substring(1, closeBracket - 1);
            }

            if (ipPart.Contains(".") && ipPart.Contains(":"))
            {
                int lastColon = ipPart.LastIndexOf(':');
                return ipPart.Substring(0, lastColon);
            }

            return ipPart;
        }

        private void LoadSavedServerAndPort()
        {
            System.Text.StringBuilder temp = new System.Text.StringBuilder(255);

            GetPrivateProfileString("NATTest", "Server", "", temp, 255, iniPath);
            string savedServer = temp.ToString();

            GetPrivateProfileString("NATTest", "ServerPort", "", temp, 255, iniPath);
            string savedPort = temp.ToString();

            GetPrivateProfileString("NATTest", "Timeout", "2000", temp, 255, iniPath);
            string savedTimeout = temp.ToString();

            if (!string.IsNullOrEmpty(savedServer))
            {
                RestoreComboSelection(comboServer, savedServer);
            }

            if (!string.IsNullOrEmpty(savedPort) && int.TryParse(savedPort, out int port) && port >= 1 && port <= 65535)
            {
                txtServerPort.Text = port.ToString();
            }

            int timeout;
            txtTimeout.Text = int.TryParse(savedTimeout, out timeout) && timeout >= 1 && timeout <= 9999
                ? timeout.ToString()
                : "2000";
        }

        private int GetServerPort(string protocol = null)
        {
            if (protocol == "TLS") return 5349;
            if (int.TryParse(txtServerPort.Text?.Trim(), out int port) && port >= 1 && port <= 65535)
                return port;
            return 3478;
        }

        private int GetTimeoutMs()
        {
            int timeout;
            if (int.TryParse(txtTimeout.Text == null ? string.Empty : txtTimeout.Text.Trim(), out timeout) &&
                timeout >= 1 && timeout <= 9999)
            {
                return timeout;
            }

            txtTimeout.Text = "2000";
            return 2000;
        }

        private void RestoreComboSelection(ComboBox combo, string savedText)
        {
            if (combo.Items.Count == 0) return;

            if (string.IsNullOrEmpty(savedText))
            {
                combo.SelectedIndex = 0;
                return;
            }

            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i].ToString() == savedText)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            string savedIP = ExtractIPFromComboText(savedText);
            if (!string.IsNullOrEmpty(savedIP))
            {
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    string itemIP = ExtractIPFromComboText(combo.Items[i].ToString());
                    if (string.Equals(savedIP, itemIP, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.Text = savedText;
                        return;
                    }
                }
            }

            combo.SelectedIndex = 0;
        }

        private string GetCurrentTime()
        {
            return DateTime.Now.ToString("HH:mm:ss");
        }

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

            if (!string.IsNullOrEmpty(msg) && txt3489Debug.Visible)
            {
                txt3489Debug.AppendText(msg + "\r\n");
                txt3489Debug.SelectionStart = txt3489Debug.Text.Length;
                txt3489Debug.ScrollToCaret();
            }

            if (!string.IsNullOrEmpty(titleStatus))
            {
                lbl3489.Text = $"RFC3489: {titleStatus}";
            }

            // Update() 而非 DoEvents，避免重入
            txt3489Debug.Update();
        }

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
                txt5780Debug.AppendText(msg + "\r\n");
                txt5780Debug.SelectionStart = txt5780Debug.Text.Length;
                txt5780Debug.ScrollToCaret();
            }

            if (!string.IsNullOrEmpty(titleStatus))
            {
                lbl5780.Text = $"RFC5780: {titleStatus}";
            }

            // Update() 而非 DoEvents，避免重入
            txt5780Debug.Update();
        }

        private int GetPortToUse(bool is5780)
        {
            Action<string> logger = is5780
                ? (msg => Log5780(msg, null))
                : (Action<string>)(msg => Log(msg, null));

            int minPort = 1;
            int maxPort = 65535;

            if (checkPortRange.Checked) minPort = 49152;

            string input = is5780 ? combo5780LocalEnd.Text.Trim() : combo3489LocalEnd.Text.Trim();

            if (input.Contains(" ")) input = input.Split(' ')[0];

            int currentDisplayPort = 0;
            bool hasPortInDisplay = false;

            if (input.StartsWith("[") && input.Contains("]:"))
            {
                string portPart = input.Split(new string[] { "]:" }, StringSplitOptions.None)[1];
                if (int.TryParse(portPart, out currentDisplayPort)) hasPortInDisplay = true;
            }
            else if (input.Contains(":") && input.Split(':').Length == 2)
            {
                string portPart = input.Split(':')[1];
                if (int.TryParse(portPart, out currentDisplayPort)) hasPortInDisplay = true;
            }

            if (hasPortInDisplay && currentDisplayPort == 0)
            {
                logger($"[端口] 检测到手动输入端口为0，视为无效，将重新生成端口");
                hasPortInDisplay = false;
            }

            int lastPortRecord = is5780 ? _lastPort5780 : _lastPort3489;
            int otherPort = is5780 ? _lastPort3489 : _lastPort5780;

            // 若 UI 上显示的端口与上次记录的端口不同，视为用户手动指定
            if (hasPortInDisplay && currentDisplayPort != lastPortRecord)
            {
                if (currentDisplayPort == otherPort && otherPort != 0)
                {
                    logger($"[端口] 手动指定端口({currentDisplayPort})与另一协议冲突，按设置生成新端口");
                }
                else
                {
                    if (is5780) _lastPort5780 = currentDisplayPort;
                    else _lastPort3489 = currentDisplayPort;
                    logger($"[端口] 检测到手动指定端口，强制使用: {currentDisplayPort}");
                    return currentDisplayPort;
                }
            }

            if (!checkPortRandom.Checked && lastPortRecord != 0)
            {
                if (lastPortRecord == otherPort && otherPort != 0)
                {
                    logger($"[端口] 连续固定模式检测到端口冲突({lastPortRecord})，按设置生成新端口");
                    int resolvedPort;
                    if (checkPortMode.Checked)
                        resolvedPort = _portRandom.Next(minPort, maxPort + 1);
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
                newPort = _portRandom.Next(minPort, maxPort + 1);
                logger($"[端口] 生成随机端口: {newPort}");
            }
            else
            {
                if (lastPortRecord == 0)
                {
                    newPort = _portRandom.Next(minPort, maxPort + 1);
                }
                else
                {
                    newPort = lastPortRecord + 1;
                    if (newPort > maxPort) newPort = minPort;
                }
                logger($"[端口] 递增模式端口: {newPort}");
            }

            if (newPort == otherPort && otherPort != 0)
            {
                logger($"[端口] 检测到与另一协议端口冲突({newPort})，按设置生成新端口");
                if (checkPortMode.Checked)
                {
                    int attempts = 0;
                    do
                    {
                        newPort = _portRandom.Next(minPort, maxPort + 1);
                        attempts++;
                    } while (newPort == otherPort && attempts < 100);
                }
                else
                {
                    newPort = otherPort + 1;
                    if (newPort > maxPort) newPort = minPort;
                }
                logger($"[端口] 冲突解决，新端口: {newPort}");
            }

            if (is5780) _lastPort5780 = newPort;
            else _lastPort3489 = newPort;

            return newPort;
        }

        private void EnsureSelectedNICValid(bool is5780)
        {
            ComboBox combo = is5780 ? combo5780LocalEnd : combo3489LocalEnd;
            string selectedText = combo.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            string selectedIP = ExtractIPFromComboText(selectedText);
            if (selectedIP == "0.0.0.0" || selectedIP == "::" || string.IsNullOrEmpty(selectedIP)) return;
            LoadLocalIPs(true);
        }

        private async void LoadLocalIPs(bool preserveSelections = false)
        {
            string saved5780 = preserveSelections ? combo5780LocalEnd.Text : null;
            string saved3489 = preserveSelections ? combo3489LocalEnd.Text : null;

            combo5780LocalEnd.Items.Clear();
            combo3489LocalEnd.Items.Clear();

            string anyItemV4 = "0.0.0.0 (Any)";
            string anyItemV6 = ":: (IPv6 Any)";
            combo5780LocalEnd.Items.Add(anyItemV4);
            combo5780LocalEnd.Items.Add(anyItemV6);
            combo3489LocalEnd.Items.Add(anyItemV4);

            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                {
                    if (nicAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        combo5780LocalEnd.Items.Add(nicAddress.DisplayText);
                        combo3489LocalEnd.Items.Add(nicAddress.DisplayText);
                    }
                    else if (nicAddress.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        combo5780LocalEnd.Items.Add(nicAddress.DisplayText);
                    }
                }

                if (preserveSelections)
                {
                    RestoreComboSelection(combo5780LocalEnd, saved5780);
                    RestoreComboSelection(combo3489LocalEnd, saved3489);
                }
                else
                {
                    if (combo5780LocalEnd.Items.Count > 0) combo5780LocalEnd.SelectedIndex = 0;
                    if (combo3489LocalEnd.Items.Count > 0) combo3489LocalEnd.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                ShowErrorTooltip("获取IP失败: " + ex.Message);
            }
        }

        private async Task<bool> PrepareServerForTestAsync()
        {
            if (_isResolvingServer) return false;

            string input = comboServer.Text == null ? string.Empty : comboServer.Text.Trim();
            if (input.Length == 0)
            {
                ShowErrorTooltip("请输入 STUN 服务器地址", true);
                return false;
            }

            string host = ExtractIPFromComboText(input).Trim();
            IPAddress directAddress;
            if (IPAddress.TryParse(host, out directAddress))
            {
                if (string.IsNullOrEmpty(_resolvedServerDomain) || !_resolvedServerAddresses.Contains(host))
                {
                    _resolvedServerDomain = null;
                    _resolvedServerAddresses.Clear();
                }
                return true;
            }

            _resolvedServerDomain = null;
            _resolvedServerAddresses.Clear();

            _isResolvingServer = true;
            btnCheck5780.Enabled = false;
            btnCheck3489.Enabled = false;
            comboServer.Enabled = false;
            try
            {
                IPAddress[] resolved = await Task.Run(() => Dns.GetHostAddresses(host));
                List<string> ipv4Addresses = new List<string>();
                List<string> ipv6Addresses = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IPAddress address in resolved)
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork &&
                        address.AddressFamily != AddressFamily.InterNetworkV6)
                        continue;

                    string addressText = address.ToString();
                    if (!seen.Add(addressText)) continue;

                    if (address.AddressFamily == AddressFamily.InterNetwork)
                        ipv4Addresses.Add(addressText);
                    else
                        ipv6Addresses.Add(addressText);
                }

                List<string> uniqueAddresses = new List<string>(ipv4Addresses);
                uniqueAddresses.AddRange(ipv6Addresses);

                if (uniqueAddresses.Count == 0)
                    throw new Exception("未解析到 IPv4/IPv6 地址");

                comboServer.Items.Clear();
                comboServer.Items.Add(host);
                foreach (string address in uniqueAddresses) comboServer.Items.Add(address);

                _resolvedServerDomain = host;
                foreach (string address in uniqueAddresses) _resolvedServerAddresses.Add(address);

                if (uniqueAddresses.Count == 1)
                {
                    comboServer.SelectedIndex = 1;
                    return true;
                }

                comboServer.SelectedIndex = 1;
                const string resolvedMessage = "域名已解析, 请选择一个IP或直接默认IP开测";
                lbl5780StartTime.Text = resolvedMessage;
                lbl3489StartTime.Text = resolvedMessage;
                comboServer.DroppedDown = true;
                return false;
            }
            catch (Exception ex)
            {
                ShowErrorTooltip("DNS 解析失败：" + ex.Message, true);
                return false;
            }
            finally
            {
                _isResolvingServer = false;
                comboServer.Enabled = true;
                if (!_isBatchTesting && !_isReloading)
                {
                    btnCheck5780.Enabled = true;
                    btnCheck3489.Enabled = true;
                }
            }
        }

        private string GetServerHostForTest(bool useAutomaticResolvedAddress)
        {
            if (useAutomaticResolvedAddress && !string.IsNullOrEmpty(_resolvedServerDomain) && IsCurrentServerFromResolvedDomain())
                return _resolvedServerDomain;

            return comboServer.Text == null ? string.Empty : comboServer.Text.Trim();
        }

        private bool IsCurrentServerFromResolvedDomain()
        {
            if (string.IsNullOrEmpty(_resolvedServerDomain)) return false;

            string input = comboServer.Text == null ? string.Empty : ExtractIPFromComboText(comboServer.Text).Trim();
            if (string.Equals(input, _resolvedServerDomain, StringComparison.OrdinalIgnoreCase)) return true;

            IPAddress address;
            return IPAddress.TryParse(input, out address) && _resolvedServerAddresses.Contains(input);
        }

        private string GetOriginalDomainForDisplay()
        {
            if (!string.IsNullOrEmpty(_resolvedServerDomain) && IsCurrentServerFromResolvedDomain())
                return _resolvedServerDomain;

            string input = comboServer.Text == null ? string.Empty : ExtractIPFromComboText(comboServer.Text).Trim();
            IPAddress address;
            return IPAddress.TryParse(input, out address) ? null : input;
        }

        private bool TryGetBatchFileRequest(out string filePath)
        {
            filePath = null;
            if (!Global.isYumeyo) return false;

            string input = comboServer.Text == null ? string.Empty : comboServer.Text.Trim().Trim('"');
            if (!input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;

            filePath = Environment.ExpandEnvironmentVariables(input);
            try
            {
                filePath = Path.GetFullPath(filePath);
            }
            catch
            {
                // 保留原始内容，稍后由批量入口给出明确的路径错误。
            }
            return true;
        }

        private bool TryParseBatchServer(string line, out BatchServerEntry server, out string error)
        {
            server = null;
            error = null;
            string value = line == null ? string.Empty : line.Trim();
            if (value.Length == 0)
            {
                error = "空行";
                return false;
            }

            string host;
            string portText;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int closeBracket = value.IndexOf(']');
                if (closeBracket <= 1 || closeBracket + 1 >= value.Length || value[closeBracket + 1] != ':')
                {
                    error = "IPv6 地址应使用 [地址]:端口 格式";
                    return false;
                }
                host = value.Substring(1, closeBracket - 1).Trim();
                portText = value.Substring(closeBracket + 2).Trim();
            }
            else
            {
                int separator = value.LastIndexOf(':');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    error = "应使用 地址:端口 格式";
                    return false;
                }
                host = value.Substring(0, separator).Trim();
                portText = value.Substring(separator + 1).Trim();
                if (host.IndexOf(':') >= 0)
                {
                    error = "IPv6 地址应使用 [地址]:端口 格式";
                    return false;
                }
            }

            int port;
            if (host.Length == 0 || host.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
            {
                error = "服务器地址无效";
                return false;
            }
            if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
            {
                error = "端口必须是 1-65535 的整数";
                return false;
            }

            server = new BatchServerEntry
            {
                Host = host,
                Port = port,
                DisplayName = host.IndexOf(':') >= 0 ? "[" + host + "]:" + port : host + ":" + port
            };
            return true;
        }

        private BatchAttemptResult Create5780AttemptResult(string protocol, string runError)
        {
            string binding = NormalizeBatchValue(txt5780Binding.Text);
            string mapping = NormalizeBatchValue(txt5780Mapping.Text);
            string filtering = NormalizeBatchValue(txt5780Filtering.Text);
            string publicEndPoint = NormalizeBatchValue(txt5780PublicEnd.Text);

            bool mappingSuccess = mapping == "Direct" || mapping == "Endpoint-Independent" ||
                                  mapping == "Address-Dependent" || mapping == "Address-and-Port-Dependent";
            bool filteringSuccess = protocol != "UDP" || filtering == "Endpoint-Independent" ||
                                    filtering == "Address-Dependent" || filtering == "Address-and-Port-Dependent";
            bool success = string.Equals(binding, "Success", StringComparison.OrdinalIgnoreCase) &&
                           mappingSuccess && filteringSuccess && string.IsNullOrEmpty(runError);

            StringBuilder summary = new StringBuilder();
            summary.Append(success ? "成功" : "失败");
            summary.Append("；Binding=").Append(binding);
            summary.Append("，Mapping=").Append(mapping);
            summary.Append("，Filtering=").Append(filtering);
            if (publicEndPoint != "--") summary.Append("，外部地址=").Append(publicEndPoint);
            if (!string.IsNullOrEmpty(runError)) summary.Append("；错误=").Append(runError);
            return new BatchAttemptResult { Success = success, Summary = summary.ToString() };
        }

        private BatchAttemptResult Create3489AttemptResult(string runError)
        {
            string natType = NormalizeBatchValue(txt3489Type.Text);
            string publicEndPoint = NormalizeBatchValue(txt3489PublicEnd.Text);
            bool success = (natType == "OpenInternet" || natType == "FullCone" ||
                            natType == "SymmetricUdpFirewall" || natType == "Symmetric" ||
                            natType == "RestrictedCone" || natType == "PortRestrictedCone") &&
                           string.IsNullOrEmpty(runError);

            StringBuilder summary = new StringBuilder();
            summary.Append(success ? "成功" : "失败");
            summary.Append("；NAT类型=").Append(natType);
            if (publicEndPoint != "--") summary.Append("，外部地址=").Append(publicEndPoint);
            if (!string.IsNullOrEmpty(runError)) summary.Append("；错误=").Append(runError);
            return new BatchAttemptResult { Success = success, Summary = summary.ToString() };
        }

        private string NormalizeBatchValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "--";
            string result = value.Trim().Replace("\r", " ").Replace("\n", " ");
            return result.Length == 0 || result.Trim('.').Length == 0 ? "--" : result;
        }

        private async Task RunBatchTestsAsync(string filePath)
        {
            if (_isBatchTesting) return;

            _isBatchTesting = true;
            CancellationTokenSource batchCancellation = new CancellationTokenSource();
            _batchCts = batchCancellation;
            string originalServer = comboServer.Text;
            string originalPort = txtServerPort.Text;
            string original5780LocalEnd = combo5780LocalEnd.Text;
            string original3489LocalEnd = combo3489LocalEnd.Text;
            string originalResolvedDomain = _resolvedServerDomain;
            HashSet<string> originalResolvedAddresses = new HashSet<string>(_resolvedServerAddresses, StringComparer.OrdinalIgnoreCase);
            List<BatchServerResult> results = new List<BatchServerResult>();
            List<string> invalidLines = new List<string>();
            bool cancelled = false;
            string fatalError = null;
            string outputPath = null;

            try
            {
                if (!File.Exists(filePath))
                {
                    MessageBox.Show(this, "找不到批量测试文件：\r\n" + filePath,
                        "STUN 批量测试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string[] lines = await Task.Run(() => File.ReadAllLines(filePath));
                List<BatchServerEntry> servers = new List<BatchServerEntry>();
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i] == null ? string.Empty : lines[i].Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#") ||
                        trimmed.StartsWith(";") || trimmed.StartsWith("//"))
                        continue;

                    BatchServerEntry server;
                    string parseError;
                    if (TryParseBatchServer(trimmed, out server, out parseError))
                        servers.Add(server);
                    else
                        invalidLines.Add("第 " + (i + 1) + " 行：" + trimmed + "（" + parseError + "）");
                }

                if (servers.Count == 0)
                {
                    MessageBox.Show(this, "文件中没有有效的 地址:端口 记录。",
                        "STUN 批量测试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                SetBatchUiState(true);
                CancellationToken cancellationToken = batchCancellation.Token;
                for (int serverIndex = 0; serverIndex < servers.Count; serverIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BatchServerEntry server = servers[serverIndex];
                    BatchServerResult serverResult = new BatchServerResult
                    {
                        Server = server.DisplayName,
                        Protocol5780 = radioTCP.Checked ? "TCP" : radioTLS.Checked ? "TLS" : "UDP"
                    };
                    results.Add(serverResult);

                    _resolvedServerDomain = null;
                    _resolvedServerAddresses.Clear();
                    comboServer.Text = server.Host;
                    txtServerPort.Text = server.Port.ToString();
                    lbl5780StartTime.Text = string.Format("批量测试 {0}/{1}: {2}", serverIndex + 1, servers.Count, server.DisplayName);
                    lbl3489StartTime.Text = "每项测试 2 次";

                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        serverResult.Attempts5780.Add(await Run5780TestAsync(cancellationToken));
                    }

                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        serverResult.Attempts3489.Add(await Run3489TestAsync(cancellationToken));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                fatalError = ex.Message;
            }
            finally
            {
                try
                {
                    if (results.Count > 0 || invalidLines.Count > 0)
                    {
                        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        outputPath = GetUniqueBatchOutputPath(desktop);
                        string report = BuildBatchReport(filePath, results, invalidLines, cancelled, fatalError);
                        File.WriteAllText(outputPath, report, new UTF8Encoding(true));
                    }
                }
                catch (Exception ex)
                {
                    fatalError = string.IsNullOrEmpty(fatalError) ? ex.Message : fatalError + "；报告写入失败：" + ex.Message;
                    outputPath = null;
                }

                comboServer.Text = originalServer;
                txtServerPort.Text = originalPort;
                combo5780LocalEnd.Text = original5780LocalEnd;
                combo3489LocalEnd.Text = original3489LocalEnd;
                _resolvedServerDomain = originalResolvedDomain;
                _resolvedServerAddresses.Clear();
                foreach (string address in originalResolvedAddresses) _resolvedServerAddresses.Add(address);
                SetBatchUiState(false);
                if (ReferenceEquals(_batchCts, batchCancellation)) _batchCts = null;
                batchCancellation.Dispose();
                _isBatchTesting = false;
            }

            if (_isReloading || IsDisposed || Disposing) return;

            if (!string.IsNullOrEmpty(fatalError))
            {
                MessageBox.Show(this, "批量测试遇到错误：\r\n" + fatalError +
                    (outputPath == null ? string.Empty : "\r\n\r\n已保存当前结果：\r\n" + outputPath),
                    "STUN 批量测试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (outputPath != null)
            {
                string message = cancelled ? "批量测试已取消，当前结果已保存：" : "批量测试完成，结果已保存：";
                MessageBox.Show(this, message + "\r\n" + outputPath,
                    "STUN 批量测试", MessageBoxButtons.OK,
                    cancelled ? MessageBoxIcon.Information : MessageBoxIcon.Asterisk);
            }
        }

        private void SetBatchUiState(bool running)
        {
            comboServer.Enabled = !running;
            txtServerPort.Enabled = !running;
            combo5780LocalEnd.Enabled = !running;
            combo3489LocalEnd.Enabled = !running;
            radioUDP.Enabled = !running;
            radioTCP.Enabled = !running;
            radioTLS.Enabled = !running;
            checkPortMode.Enabled = !running;
            checkPortRandom.Enabled = !running;
            checkPortRange.Enabled = !running;
            checkSelectIP.Enabled = !running;
            txtTimeout.Enabled = !running;
            btnCheck5780.Enabled = !running;
            btnCheck3489.Enabled = !running;
            btnSettings.Enabled = !running;
            btnRFCCompare.Enabled = !running;
            btnMiaoDong.Enabled = !running;
            btnReset.Enabled = true;
        }

        private string GetUniqueBatchOutputPath(string desktop)
        {
            string baseName = "STUN批量测试_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(desktop, baseName + ".txt");
            int suffix = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(desktop, baseName + "_" + suffix + ".txt");
                suffix++;
            }
            return path;
        }

        private string BuildBatchReport(string sourceFile, List<BatchServerResult> results,
            List<string> invalidLines, bool cancelled, string fatalError)
        {
            int fullyAvailable = 0;
            int partiallyAvailable = 0;
            int unavailable = 0;
            foreach (BatchServerResult result in results)
            {
                if (result.Is5780Available && result.Is3489Available) fullyAvailable++;
                else if (result.Is5780Available || result.Is3489Available) partiallyAvailable++;
                else unavailable++;
            }

            StringBuilder report = new StringBuilder();
            report.AppendLine("NICX STUN服务器批量测试结果");
            report.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("源文件：" + sourceFile);
            report.AppendLine("状态：" + (cancelled ? "用户取消（结果可能不完整）" : string.IsNullOrEmpty(fatalError) ? "已完成" : "异常中止"));
            report.AppendLine("重试规则：RFC5780 与 RFC3489 各测试 2 次；同一项任一次成功即视为该项可用。");
            if (!string.IsNullOrEmpty(fatalError)) report.AppendLine("异常：" + fatalError);
            report.AppendLine();
            report.AppendLine(string.Format("统计：完全可用 {0}，部分可用 {1}，完全不可用 {2}，已记录服务器 {3}，格式错误 {4}",
                fullyAvailable, partiallyAvailable, unavailable, results.Count, invalidLines.Count));
            report.AppendLine();

            AppendBatchReportSection(report, "完全可用（RFC5780、RFC3489 均可用）", results, 0);
            AppendBatchReportSection(report, "部分可用（仅一项可用）", results, 1);
            AppendBatchReportSection(report, "完全不可用（两项均失败）", results, 2);

            if (invalidLines.Count > 0)
            {
                report.AppendLine("================ 输入格式错误（" + invalidLines.Count + "） ================");
                foreach (string invalidLine in invalidLines) report.AppendLine(invalidLine);
                report.AppendLine();
            }
            return report.ToString();
        }

        private void AppendBatchReportSection(StringBuilder report, string title,
            List<BatchServerResult> results, int category)
        {
            int count = 0;
            foreach (BatchServerResult result in results)
            {
                bool matches = category == 0
                    ? result.Is5780Available && result.Is3489Available
                    : category == 1
                        ? result.Is5780Available ^ result.Is3489Available
                        : !result.Is5780Available && !result.Is3489Available;
                if (matches) count++;
            }

            report.AppendLine("================ " + title + "（" + count + "） ================");
            foreach (BatchServerResult result in results)
            {
                bool matches = category == 0
                    ? result.Is5780Available && result.Is3489Available
                    : category == 1
                        ? result.Is5780Available ^ result.Is3489Available
                        : !result.Is5780Available && !result.Is3489Available;
                if (!matches) continue;

                report.AppendLine("[" + result.Server + "]");
                AppendBatchAttempts(report, "RFC5780/" + result.Protocol5780, result.Is5780Available, result.Attempts5780);
                AppendBatchAttempts(report, "RFC3489/UDP", result.Is3489Available, result.Attempts3489);
                report.AppendLine();
            }
        }

        private void AppendBatchAttempts(StringBuilder report, string name, bool available,
            List<BatchAttemptResult> attempts)
        {
            report.AppendLine("  " + name + "：" + (available ? "可用" : "不可用"));
            if (attempts.Count == 0)
            {
                report.AppendLine("    未执行");
                return;
            }
            for (int i = 0; i < attempts.Count; i++)
                report.AppendLine("    第 " + (i + 1) + " 次：" + attempts[i].Summary);
        }

        private async void btnCheck5780_Click(object sender, EventArgs e)
        {
            if (_isBatchTesting) return;

            string batchFilePath;
            if (TryGetBatchFileRequest(out batchFilePath))
            {
                await RunBatchTestsAsync(batchFilePath);
                return;
            }

            if (radioUDP.Checked && !checkSelectIP.Checked && !await PrepareServerForTestAsync()) return;

            await Run5780TestAsync(CancellationToken.None);
        }

        private async Task<BatchAttemptResult> Run5780TestAsync(CancellationToken externalCancellationToken)
        {
            btnCheck5780.Enabled = false;
            combo5780LocalEnd.Enabled = false;
            radioTCP.Enabled = false;
            radioUDP.Enabled = false;
            radioTLS.Enabled = false;

            ResetIPDetection5780();

            string originalDomain = GetOriginalDomainForDisplay();
            txt5780Binding.ForeColor = Color.Black;
            txt5780Mapping.ForeColor = Global.Yumeyo;
            txt5780Filtering.ForeColor = Global.Yumeyo;

            // 创建取消令牌
            CancellationTokenSource testCancellation = externalCancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken)
                : new CancellationTokenSource();
            _cts5780 = testCancellation;
            var cancellationToken = testCancellation.Token;
            string runError = null;

            _stopRequested = false;
            txt5780Debug.Clear();
            txt5780Mapping.Text = "......";
            txt5780Filtering.Text = "......";
            txt5780PublicEnd.Text = "";

            string protocol = "UDP";
            if (radioTCP.Checked) protocol = "TCP";
            else if (radioTLS.Checked) protocol = "TLS";
            bool useAutomaticResolvedAddress = protocol != "UDP" || checkSelectIP.Checked;
            int timeoutMs = GetTimeoutMs();

            txt5780Binding.Text = "";

            Socket socket = null;
            _activeSocket5780 = null;

            try
            {
                Log5780(string.Format("开始时间: " + Others.GetCurrentTime()));
                Log5780(string.Format("=== 开始 RFC5780 {0} 协议测试 ===", protocol), string.Format("{0} 测试初始化...", protocol));

                string serverHost = GetServerHostForTest(useAutomaticResolvedAddress);
                if (string.IsNullOrEmpty(serverHost)) throw new Exception("请选择服务器");

                IPAddress[] serverIps = await Task.Run(() => Dns.GetHostAddresses(serverHost), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                EnsureSelectedNICValid(true);
                string inputRaw = combo5780LocalEnd.Text.Trim();

                string ipPartToParse = inputRaw.Split(' ')[0];
                IPAddress selectedLocalIP = IPAddress.Any;
                bool parseSuccess = false;

                if (IPAddress.TryParse(ipPartToParse, out selectedLocalIP))
                {
                    parseSuccess = true;
                }
                else if (ipPartToParse.StartsWith("[") && ipPartToParse.Contains("]:"))
                {
                    int closeBracketIndex = ipPartToParse.IndexOf(']');
                    string ipOnly = ipPartToParse.Substring(1, closeBracketIndex - 1);
                    if (IPAddress.TryParse(ipOnly, out selectedLocalIP))
                    {
                        parseSuccess = true;
                    }
                }
                else if (ipPartToParse.Contains(":") && ipPartToParse.Split(':').Length == 2)
                {
                    string ipOnly = ipPartToParse.Split(':')[0];
                    if (IPAddress.TryParse(ipOnly, out selectedLocalIP))
                    {
                        parseSuccess = true;
                    }
                }

                if (!parseSuccess)
                {
                    if (ipPartToParse.Contains("IPv6") || ipPartToParse.Contains("::"))
                    {
                        selectedLocalIP = IPAddress.IPv6Any;
                    }
                    else
                    {
                        selectedLocalIP = IPAddress.Any;
                    }

                    if (!ipPartToParse.Contains("Any") && !ipPartToParse.Contains("0.0.0.0") && !ipPartToParse.Contains("::"))
                    {
                        Log5780(string.Format("[Warning] 无法解析地址 '{0}'，默认为 Any。", ipPartToParse));
                    }
                }

                AddressFamily testFamily = AddressFamily.InterNetwork;
                if (selectedLocalIP.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    testFamily = AddressFamily.InterNetworkV6;
                }

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

                int serverPort = GetServerPort(protocol);
                Log5780($"使用服务器端口 {serverPort}", "端口配置");

                IPEndPoint serverEp1 = new IPEndPoint(serverIp, serverPort);
                lbl5780StartTime.Text = string.Format("开测: {0} 服务器IP: [{2}]{1}",
                    GetCurrentTime(), serverIp, protocol);

                if (protocol == "UDP")
                {
                    await RunUdpTest5780(serverEp1, selectedLocalIP, testFamily, timeoutMs, cancellationToken, originalDomain);
                }
                else if (protocol == "TCP")
                {
                    await RunTcpTest5780(serverEp1, selectedLocalIP, testFamily, protocol, timeoutMs, cancellationToken, originalDomain);
                }
                else if (protocol == "TLS")
                {
                    string tlsServerName = string.IsNullOrEmpty(originalDomain) ? serverHost : originalDomain;
                    await RunTlsTest5780(serverEp1, selectedLocalIP, testFamily, protocol, timeoutMs, cancellationToken, tlsServerName, originalDomain);
                }

            }
            catch (OperationCanceledException)
            {
                runError = "测试已取消";
                Log5780("测试已被用户取消", "测试取消");
            }
            catch (Exception ex)
            {
                runError = ex.Message;
                Log5780(string.Format("[Error] {0}", ex.Message));
                if (!cancellationToken.IsCancellationRequested)
                {
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
                        Log5780(null, string.Format("{0}", ex.Message));
                    }
                    else
                    {
                        ShowErrorTooltip("" + ex.Message, true);
                        Log5780(null, string.Format("{0}", ex.Message));
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

                if (!cancellationToken.IsCancellationRequested && !_isBatchTesting)
                {
                    btnCheck5780.Enabled = true;
                    combo5780LocalEnd.Enabled = true;
                    radioTCP.Enabled = true;
                    radioUDP.Enabled = true;
                    radioTLS.Enabled = true;
                    if (!_stopRequested && !lbl5780.Text.Contains("结束"))
                    {
                        lbl5780.Text = "RFC5780 (完成" +
                            (string.IsNullOrEmpty(originalDomain) ? string.Empty : "@" + originalDomain) + ")";
                    }
                }

                if (ReferenceEquals(_cts5780, testCancellation))
                    _cts5780 = null;
                testCancellation.Dispose();
            }

            return Create5780AttemptResult(protocol, runError);
        }
        private async Task RunUdpTest5780(IPEndPoint serverEp1, IPAddress selectedLocalIP, AddressFamily testFamily, int timeoutMs, CancellationToken cancellationToken, string originalDomain)
        {
            Socket socket = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                Log5780($"[UDP] 目标服务器: {serverEp1}");
                if (!string.IsNullOrEmpty(originalDomain)) Log5780($"[UDP] 原域名: {originalDomain}");
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

                Log5780(">>> [UDP] Mapping Test I: Binding Request", "Mapping Test I");
                Log5780($"[UDP] 接收超时: {timeoutMs}ms");
                Log5780($"[UDP] CHANGE-REQUEST: changeIP=False, changePort=False");
                Log5780($"[UDP] 请求地址: {serverEp1}");
                var resultA = await Task.Run(() => StunClient.Query(socket, serverEp1, false, false, timeoutMs), cancellationToken);

                if (resultA?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultA.PublicEndPoint);
                    Log5780($"[UDP] Test I 成功");
                    Log5780($"[UDP] MAPPED-ADDRESS: {resultA.PublicEndPoint}");
                    if (resultA.ChangedEndPoint != null)
                        Log5780($"[UDP] CHANGED-ADDRESS: {resultA.ChangedEndPoint}");
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(resultA?.ErrorMessage)
                        ? resultA.ErrorMessage
                        : "请求超时或无响应";
                    if (!string.IsNullOrEmpty(resultA?.ErrorMessage))
                        ShowErrorTooltip(resultA.ErrorMessage, true);
                    Log5780($"[UDP] Test I 失败: {errDetail}");
                    txt5780Binding.Text = "Fail";
                    txt5780Binding.ForeColor = Color.Red;
                    txt5780Mapping.Text = "Unknown";
                    txt5780Filtering.Text = "Unknown";
                    txt5780Mapping.ForeColor = Color.DarkOrange;
                    txt5780Filtering.ForeColor = Color.DarkOrange;
                    txt5780PublicEnd.Text = "";
                    Log5780($"Mapping Test I 失败 ({errDetail})");
                    return;
                }

                txt5780PublicEnd.Text = FormatPrivateEndPoint(resultA.PublicEndPoint);
                txt5780Binding.Text = "Success";
                txt5780Binding.ForeColor = Color.LimeGreen;

                Log5780(">>>  验证服务器返回的 MAPPED-ADDRESS");
                if (IsAddressInvalid(resultA.PublicEndPoint.Address, out string mappedReason))
                {
                    Log5780($"错误：服务器返回的 MAPPED-ADDRESS 无效: {mappedReason}");
                    txt5780Binding.Text = "Fail";
                    txt5780Binding.ForeColor = Color.Red;
                    txt5780Mapping.Text = "Unsupported Server";
                    txt5780Mapping.ForeColor = Color.DarkOrange;
                    txt5780Filtering.Text = "Unsupported Server";
                    txt5780Filtering.ForeColor = Color.DarkOrange;
                    Log5780("Mapping Test I 失败 (服务器返回的地址无效)", "(完成)");
                    return;
                }

                bool isDirectMapping = resultA.PublicEndPoint.Equals(resultA.LocalEndPoint);
                Log5780($"[UDP] 是否公网: {isDirectMapping}");

                var changedEp = resultA.ChangedEndPoint;
                Log5780(">>>  验证服务器返回的 OTHER-ADDRESS");
                if (changedEp == null || !IsValidServerAddress(serverEp1, changedEp,
                    (msg, title) => Log5780(msg, title), "RFC5780"))
                {
                    txt5780Mapping.Text = "Fail";
                    txt5780Mapping.ForeColor = Color.Red;
                    txt5780Filtering.Text = "Unsupported Server";
                    txt5780Filtering.ForeColor = Color.DarkOrange;
                    Log5780("服务器不支持 RFC5780 OTHER-ADDRESS 无法继续测试", "(完成)");
                    return;
                }

                // RFC5780 Mapping 行为测试固定目标：
                // Test II -> (otherIP, primaryPort)
                // Test III -> (otherIP, otherPort)
                IPEndPoint mappingTest2Server = new IPEndPoint(changedEp.Address, serverEp1.Port);
                IPEndPoint mappingTest3Server = changedEp;

                cancellationToken.ThrowIfCancellationRequested();

                Log5780(">>> [UDP] Filtering Test II: Change IP & Port", "Filtering Test II");
                Log5780($"[UDP] 接收超时: {timeoutMs}ms");
                Log5780($"[UDP] CHANGE-REQUEST: changeIP=True, changePort=True");
                Log5780($"[UDP] 请求地址: {serverEp1}");

                var filteringII = await Task.Run(() => StunClient.Query(socket, serverEp1, true, true, timeoutMs), cancellationToken);
                if (filteringII?.ResponseEndPoint != null)
                {
                    Log5780($"[UDP] Test II 成功");
                    Log5780($"[UDP] 响应来源: {filteringII.ResponseEndPoint}");
                    if (filteringII.PublicEndPoint != null)
                        Log5780($"[UDP] MAPPED-ADDRESS: {filteringII.PublicEndPoint}");
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(filteringII?.ErrorMessage)
                        ? filteringII.ErrorMessage : "无响应";
                    if (!string.IsNullOrEmpty(filteringII?.ErrorMessage))
                        ShowErrorTooltip(filteringII.ErrorMessage, true);
                    Log5780($"[UDP] Test II 失败: {errDetail}");
                }

                cancellationToken.ThrowIfCancellationRequested();

                Log5780(">>> [UDP] Filtering Test III: Change Port", "Filtering Test III");
                Log5780($"[UDP] 接收超时: {timeoutMs}ms");
                Log5780($"[UDP] CHANGE-REQUEST: changeIP=False, changePort=True");
                Log5780($"[UDP] 请求地址: {serverEp1}");

                var filteringIII = await Task.Run(() => StunClient.Query(socket, serverEp1, false, true, timeoutMs), cancellationToken);
                if (filteringIII?.ResponseEndPoint != null)
                {
                    Log5780($"[UDP] Test III 成功");
                    Log5780($"[UDP] 响应来源: {filteringIII.ResponseEndPoint}");
                    if (filteringIII.PublicEndPoint != null)
                        Log5780($"[UDP] MAPPED-ADDRESS: {filteringIII.PublicEndPoint}");
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(filteringIII?.ErrorMessage)
                        ? filteringIII.ErrorMessage : "无响应";
                    if (!string.IsNullOrEmpty(filteringIII?.ErrorMessage))
                        ShowErrorTooltip(filteringIII.ErrorMessage, true);
                    Log5780($"[UDP] Test III 失败: {errDetail}");
                }

                cancellationToken.ThrowIfCancellationRequested();

                Log5780(">>> [UDP] 计算最终 Filtering 行为");
                string filteringType = CalculateFilteringType(filteringII, filteringIII, serverEp1, changedEp);
                txt5780Filtering.Text = filteringType;
                txt5780Filtering.ForeColor = GetFilteringColor(filteringType);
                Log5780($"[UDP] Filtering 判定结果: {filteringType}");

                if (filteringType == "Unsupported Server")
                {
                    Log5780("[UDP] Filtering 为 Unsupported Server，继续执行 Mapping 测试");
                }


                Log5780(">>> [UDP] Mapping Test II: otherIP + primaryPort", "Mapping Test II");
                Log5780($"[UDP] 接收超时: {timeoutMs}ms");
                Log5780($"[UDP] CHANGE-REQUEST: changeIP=False, changePort=False");
                Log5780($"[UDP] 请求地址: {mappingTest2Server}");

                StunResult resultB;
                resultB = await Task.Run(() => StunClient.Query(socket, mappingTest2Server, false, false, timeoutMs), cancellationToken);

                if (resultB != null && resultB.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultB.PublicEndPoint);
                    Log5780($"[UDP] Test II 成功");
                    Log5780($"[UDP] MAPPED-ADDRESS: {resultB.PublicEndPoint}");
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(resultB?.ErrorMessage)
                        ? resultB.ErrorMessage : "无响应";
                    if (!string.IsNullOrEmpty(resultB?.ErrorMessage))
                        ShowErrorTooltip(resultB.ErrorMessage, true);
                    Log5780($"[UDP] Test II 失败: {errDetail}");
                }

                cancellationToken.ThrowIfCancellationRequested();


                Log5780(">>> [UDP] Mapping Test III: otherIP + otherPort", "Mapping Test III");
                Log5780($"[UDP] 接收超时: {timeoutMs}ms");
                Log5780($"[UDP] CHANGE-REQUEST: changeIP=False, changePort=False");
                Log5780($"[UDP] 请求地址: {mappingTest3Server}");
                var resultC = await Task.Run(() => StunClient.Query(socket, mappingTest3Server, false, false, timeoutMs), cancellationToken);

                if (resultC != null && resultC.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultC.PublicEndPoint);
                    Log5780($"[UDP] Test III 成功");
                    Log5780($"[UDP] MAPPED-ADDRESS: {resultC.PublicEndPoint}");
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(resultC?.ErrorMessage)
                        ? resultC.ErrorMessage : "无响应";
                    if (!string.IsNullOrEmpty(resultC?.ErrorMessage))
                        ShowErrorTooltip(resultC.ErrorMessage, true);
                    Log5780($"[UDP] Test III 失败: {errDetail}");
                }

                cancellationToken.ThrowIfCancellationRequested();

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
        private async Task RunTcpTest5780(IPEndPoint serverEp1, IPAddress selectedLocalIP, AddressFamily testFamily, string protocol, int timeoutMs, CancellationToken cancellationToken, string originalDomain)
        {
            try
            {
                int serverPort = GetServerPort(protocol);
                if (serverEp1.Port != serverPort) serverEp1 = new IPEndPoint(serverEp1.Address, serverPort);

                Log5780($"[TCP] 目标服务器: {serverEp1}");
                if (!string.IsNullOrEmpty(originalDomain)) Log5780($"[TCP] 原域名: {originalDomain}");
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

                Log5780(">>> [TCP] Mapping Test I: Binding Request", "Mapping Test I");
                Log5780($"[TCP] CHANGE-REQUEST: changeIP=False, changePort=False");
                Log5780($"[TCP] 请求地址: {serverEp1}");
                var resultA = await StunClient.QueryTcpAsync(serverEp1, false, false, tcpLocalEndPoint, cancellationToken, timeoutMs);

                if (resultA?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultA.PublicEndPoint);
                    txt5780PublicEnd.Text = FormatPrivateEndPoint(resultA.PublicEndPoint);
                    txt5780Binding.Text = "Success";
                    txt5780Binding.ForeColor = Color.LimeGreen;

                    Log5780(">>>  验证服务器返回的 MAPPED-ADDRESS");
                    if (IsAddressInvalid(resultA.PublicEndPoint.Address, out string mappedReason))
                    {
                        Log5780($"错误：服务器返回的 MAPPED-ADDRESS 无效: {mappedReason}");
                        txt5780Binding.Text = "Fail";
                        txt5780Binding.ForeColor = Color.Red;
                        txt5780Mapping.Text = "Unsupported Server";
                        txt5780Mapping.ForeColor = Color.DarkOrange;
                        txt5780Filtering.Text = "Unsupported Server";
                        txt5780Filtering.ForeColor = Color.DarkOrange;
                        Log5780("Mapping Test I 失败 (服务器返回的地址无效)", "(完成)");
                        return;
                    }

                    Log5780($"[TCP] Test I 成功");
                    Log5780($"[TCP] MAPPED-ADDRESS: {resultA.PublicEndPoint}");
                    if (resultA.ChangedEndPoint != null)
                        Log5780($"[TCP] CHANGED-ADDRESS: {resultA.ChangedEndPoint}");
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(resultA?.ErrorMessage)
                        ? resultA.ErrorMessage : "无响应或超时";
                    if (!string.IsNullOrEmpty(resultA?.ErrorMessage))
                        ShowErrorTooltip(resultA.ErrorMessage, true);
                    Log5780($"[TCP] Test I 失败: {errDetail}");
                    throw new Exception($"Mapping Test I 失败: {errDetail}");
                }

                await Task.Delay(250, cancellationToken);
                Log5780($"[TCP] 等待 250ms 后继续...");

                IPEndPoint currentLocalEndPoint = tcpLocalEndPoint;
                if (resultA.LocalEndPoint != null)
                {
                    currentLocalEndPoint = resultA.LocalEndPoint;
                    Log5780($"[TCP] 实际本地端点: {currentLocalEndPoint}");
                }

                Log5780(">>> [TCP] Mapping Test II: otherIP + primaryPort", "Mapping Test II");
                var changedEp = resultA.ChangedEndPoint;
                IPEndPoint mappingTest2Server = null;
                IPEndPoint mappingTest3Server = null;
                StunResult resultB = null;
                Log5780(">>>  验证服务器返回的 OTHER-ADDRESS");
                if (changedEp != null && IsValidServerAddress(serverEp1, changedEp, (m, t) => Log5780(m, t), "RFC5780"))
                {
                    mappingTest2Server = new IPEndPoint(changedEp.Address, serverEp1.Port);
                    mappingTest3Server = changedEp;

                    Log5780($"[TCP] CHANGE-REQUEST: changeIP=False, changePort=False");
                    Log5780($"[TCP] 请求地址: {mappingTest2Server}");
                    resultB = await StunClient.QueryTcpAsync(mappingTest2Server, false, false, currentLocalEndPoint, cancellationToken, timeoutMs);
                    if (resultB?.PublicEndPoint != null)
                    {
                        RecordEndPoint5780(resultB.PublicEndPoint);
                        Log5780($"[TCP] Test II 成功");
                        Log5780($"[TCP] MAPPED-ADDRESS: {resultB.PublicEndPoint}");
                        if (resultB.LocalEndPoint != null)
                        {
                            currentLocalEndPoint = resultB.LocalEndPoint;
                            Log5780($"[TCP] 本地端点: {currentLocalEndPoint}");
                        }
                    }
                    else
                    {
                        string errDetail = !string.IsNullOrEmpty(resultB?.ErrorMessage)
                            ? resultB.ErrorMessage : "无响应";
                        if (!string.IsNullOrEmpty(resultB?.ErrorMessage))
                            ShowErrorTooltip(resultB.ErrorMessage, true);
                        Log5780($"[TCP] Test II 失败: {errDetail}");
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

                Log5780(">>> [TCP] Mapping Test III: otherIP + otherPort", "Mapping Test III");
                StunResult resultC = null;
                if (mappingTest3Server != null)
                {
                    Log5780($"[TCP] CHANGE-REQUEST: changeIP=False, changePort=False");
                    Log5780($"[TCP] 请求地址: {mappingTest3Server}");
                    resultC = await StunClient.QueryTcpAsync(mappingTest3Server, false, false, currentLocalEndPoint, cancellationToken, timeoutMs);
                }
                if (resultC?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultC.PublicEndPoint);
                    Log5780($"[TCP] Test III 成功");
                    Log5780($"[TCP] MAPPED-ADDRESS: {resultC.PublicEndPoint}");
                    if (resultC.LocalEndPoint != null)
                    {
                        currentLocalEndPoint = resultC.LocalEndPoint;
                        Log5780($"[TCP] 本地端点: {currentLocalEndPoint}");
                    }
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(resultC?.ErrorMessage)
                        ? resultC.ErrorMessage : "无响应";
                    if (!string.IsNullOrEmpty(resultC?.ErrorMessage))
                        ShowErrorTooltip(resultC.ErrorMessage, true);
                    Log5780($"[TCP] Test III 失败: {errDetail}");
                }

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
                // NatTypeTester: 验证响应来自完整的 OTHER-ADDRESS（IP + 端口都必须匹配）
                return filteringII.ResponseEndPoint.Equals(changedEp)
                    ? "Endpoint-Independent"
                    : "Unsupported Server";
            }

            if (filteringIII?.ResponseEndPoint == null)
            {
                return "Address-and-Port-Dependent";
            }

            // Test III(changePort) 收到响应 → NAT 允许不同端口入站 → Address-Dependent
            // 只校验响应是否来自主服务器 IP（端口可能被中间 NAT 改写）
            if (filteringIII.ResponseEndPoint.Address.Equals(serverEp1.Address))
            {
                return "Address-Dependent";
            }

            return "Unsupported Server";
        }
        private async Task RunTlsTest5780(
            IPEndPoint serverEp1,
            IPAddress selectedLocalIP,
            AddressFamily testFamily,
            string protocol,
            int timeoutMs,
            CancellationToken cancellationToken,
            string tlsServerName,
            string originalDomain)
        {
            try
            {
                int serverPort = 5349;
                if (serverEp1.Port != serverPort) serverEp1 = new IPEndPoint(serverEp1.Address, serverPort);

                Log5780($"[TLS] 目标服务器: {serverEp1}");
                if (!string.IsNullOrEmpty(originalDomain)) Log5780($"[TLS] 原域名: {originalDomain}");
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

                Log5780(">>> [TLS] Mapping Test I: Binding Request", "Mapping Test I");
                Log5780($"[TLS] CHANGE-REQUEST: changeIP=False, changePort=False");
                Log5780($"[TLS] 请求地址: {serverEp1}");
                var resultA = await StunClient.QueryTlsAsync(serverEp1, false, false, tlsLocalEndPoint, cancellationToken, tlsServerName, timeoutMs);

                if (resultA?.PublicEndPoint == null)
                {
                    string errDetail = !string.IsNullOrEmpty(resultA?.ErrorMessage)
                        ? resultA.ErrorMessage : "无响应或超时";
                    if (!string.IsNullOrEmpty(resultA?.ErrorMessage))
                        ShowErrorTooltip(resultA.ErrorMessage, true);
                    Log5780($"[TLS] Test I 失败: {errDetail}");
                    throw new Exception($"Mapping Test I (TLS) 失败: {errDetail}");
                }

                RecordEndPoint5780(resultA.PublicEndPoint);
                txt5780PublicEnd.Text = FormatPrivateEndPoint(resultA.PublicEndPoint);
                txt5780Binding.Text = "Success";
                txt5780Binding.ForeColor = Color.LimeGreen;

                Log5780(">>>  验证服务器返回的 MAPPED-ADDRESS");
                if (IsAddressInvalid(resultA.PublicEndPoint.Address, out string mappedReason))
                {
                    Log5780($"错误：服务器返回的 MAPPED-ADDRESS 无效: {mappedReason}");
                    txt5780Binding.Text = "Fail";
                    txt5780Binding.ForeColor = Color.Red;
                    txt5780Mapping.Text = "Unsupported Server";
                    txt5780Mapping.ForeColor = Color.DarkOrange;
                    txt5780Filtering.Text = "Unsupported Server";
                    txt5780Filtering.ForeColor = Color.DarkOrange;
                    Log5780("Mapping Test I 失败 (服务器返回的地址无效)", "(完成)");
                    return;
                }

                Log5780($"[TLS] Test I 成功");
                Log5780($"[TLS] MAPPED-ADDRESS: {resultA.PublicEndPoint}");
                if (resultA.ChangedEndPoint != null)
                    Log5780($"[TLS] CHANGED-ADDRESS: {resultA.ChangedEndPoint}");

                IPEndPoint currentLocalEndPoint = tlsLocalEndPoint;
                if (resultA.LocalEndPoint != null)
                {
                    currentLocalEndPoint = resultA.LocalEndPoint;
                    Log5780($"[TLS] 实际本地端点: {currentLocalEndPoint}");
                }

                await Task.Delay(250, cancellationToken);
                Log5780($"[TLS] 等待 250ms 后继续...");

                Log5780(">>> [TLS] Mapping Test II: otherIP + primaryPort", "Mapping Test II");
                var changedEp = resultA.ChangedEndPoint;
                IPEndPoint mappingTest2Server = null;
                IPEndPoint mappingTest3Server = null;
                StunResult resultB = null;
                Log5780(">>>  验证服务器返回的 OTHER-ADDRESS");
                if (changedEp != null && IsValidServerAddress(serverEp1, changedEp, (m, t) => Log5780(m, t), "RFC5780"))
                {
                    mappingTest2Server = new IPEndPoint(changedEp.Address, serverEp1.Port);
                    mappingTest3Server = changedEp;

                    Log5780($"[TLS] CHANGE-REQUEST: changeIP=False, changePort=False");
                    Log5780($"[TLS] 请求地址: {mappingTest2Server}");
                    resultB = await StunClient.QueryTlsAsync(mappingTest2Server, false, false, currentLocalEndPoint, cancellationToken, tlsServerName, timeoutMs);
                    if (resultB?.PublicEndPoint != null)
                    {
                        RecordEndPoint5780(resultB.PublicEndPoint);
                        Log5780($"[TLS] Test II 成功");
                        Log5780($"[TLS] MAPPED-ADDRESS: {resultB.PublicEndPoint}");
                        if (resultB.LocalEndPoint != null)
                        {
                            currentLocalEndPoint = resultB.LocalEndPoint;
                            Log5780($"[TLS] 本地端点: {currentLocalEndPoint}");
                        }
                    }
                    else
                    {
                        string errDetail = !string.IsNullOrEmpty(resultB?.ErrorMessage)
                            ? resultB.ErrorMessage : "无响应";
                        if (!string.IsNullOrEmpty(resultB?.ErrorMessage))
                            ShowErrorTooltip(resultB.ErrorMessage, true);
                        Log5780($"[TLS] Test II 失败: {errDetail}");
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

                Log5780(">>> [TLS] Mapping Test III: otherIP + otherPort", "Mapping Test III");
                StunResult resultC = null;
                if (mappingTest3Server != null)
                {
                    Log5780($"[TLS] CHANGE-REQUEST: changeIP=False, changePort=False");
                    Log5780($"[TLS] 请求地址: {mappingTest3Server}");
                    resultC = await StunClient.QueryTlsAsync(mappingTest3Server, false, false, currentLocalEndPoint, cancellationToken, tlsServerName, timeoutMs);
                }
                if (resultC?.PublicEndPoint != null)
                {
                    RecordEndPoint5780(resultC.PublicEndPoint);
                    Log5780($"[TLS] Test III 成功");
                    Log5780($"[TLS] MAPPED-ADDRESS: {resultC.PublicEndPoint}");
                    if (resultC.LocalEndPoint != null)
                    {
                        currentLocalEndPoint = resultC.LocalEndPoint;
                        Log5780($"[TLS] 本地端点: {currentLocalEndPoint}");
                    }
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(resultC?.ErrorMessage)
                        ? resultC.ErrorMessage : "无响应";
                    if (!string.IsNullOrEmpty(resultC?.ErrorMessage))
                        ShowErrorTooltip(resultC.ErrorMessage, true);
                    Log5780($"[TLS] Test III 失败: {errDetail}");
                }

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
            if (_isBatchTesting) return;

            string batchFilePath;
            if (TryGetBatchFileRequest(out batchFilePath))
            {
                await RunBatchTestsAsync(batchFilePath);
                return;
            }

            if (!checkSelectIP.Checked && !await PrepareServerForTestAsync()) return;

            await Run3489TestAsync(CancellationToken.None);
        }

        private async Task<BatchAttemptResult> Run3489TestAsync(CancellationToken externalCancellationToken)
        {
            btnCheck3489.Enabled = false;
            combo3489LocalEnd.Enabled = false;
            ResetIPDetection3489();
            string originalDomain = GetOriginalDomainForDisplay();
            bool useAutomaticResolvedAddress = checkSelectIP.Checked;
            txt3489Type.ForeColor = Global.Yumeyo;

            CancellationTokenSource testCancellation = externalCancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken)
                : new CancellationTokenSource();
            _cts3489 = testCancellation;
            var cancellationToken = testCancellation.Token;
            string runError = null;
            int timeoutMs = GetTimeoutMs();

            _stopRequested = false;
            txt3489Debug.Clear();
            txt3489Type.Text = "...";
            txt3489Type.ForeColor = Color.Gray;
            txt3489PublicEnd.Text = "";

            Socket socket = null;
            _activeSocket3489 = null;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string serverHost = GetServerHostForTest(useAutomaticResolvedAddress);
                if (string.IsNullOrEmpty(serverHost)) throw new Exception("请选择服务器");

                Log(string.Format("开始时间: " + Others.GetCurrentTime()));
                Log("=== 开始 RFC3489 测试 ===", "测试初始化...");
                Log("正在解析服务器地址...", "解析服务器 IP...");

                IPAddress[] serverIps = await Task.Run(() => Dns.GetHostAddresses(serverHost), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // RFC3489 仅实现 IPv4（IPv6 STUN 意义有限）
                IPAddress serverIp = null;
                foreach (var ip in serverIps)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) // 仅选择 IPv4
                    {
                        serverIp = ip;
                        break;
                    }
                }
                Log("RFC3489 仅实现 IPv4 经典 STUN 检测，已过滤 IPv6 地址");
                if (serverIp == null)
                {
                    throw new Exception($"服务器未提供IPv4地址，无法进行 RFC3489 测试");
                }

                IPEndPoint serverEp1 = new IPEndPoint(serverIp, GetServerPort());
                lbl3489StartTime.Text = "开测: " + GetCurrentTime() + " 服务器IP: " + serverIp;

                Log($"目标服务器: {serverEp1}");
                if (!string.IsNullOrEmpty(originalDomain)) Log($"原域名: {originalDomain}");

                cancellationToken.ThrowIfCancellationRequested();

                EnsureSelectedNICValid(false);
                string inputRaw = combo3489LocalEnd.Text.Trim();
                string ipPartString = inputRaw;
                if (inputRaw.Contains(":"))
                {
                    var parts = inputRaw.Split(':');
                    ipPartString = parts[0];
                }

                // 获取绑定端口
                int bindPort = GetPortToUse(false);
                Log($"获取绑定端口: {bindPort}");

                // 创建 UDP Socket
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _activeSocket3489 = socket; // 保存引用以便Reset时关闭
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                Log($"Socket 创建完成 (IPv4 UDP)");

                IPAddress finalBindIp;
                if (ipPartString.Contains("0.0.0.0") || ipPartString.Contains("Any"))
                {
                    Log($"检测系统路由出口...");
                    finalBindIp = await Task.Run(() => GetLocalRoutingIp(serverEp1), cancellationToken);
                    Log($"系统实际出口: {finalBindIp}");
                }
                else
                {
                    finalBindIp = IPAddress.Parse(ipPartString.Split(' ')[0]);
                    Log($"使用手动指定出口 IP: {finalBindIp}");
                }

                IPEndPoint localBindEp = new IPEndPoint(finalBindIp, bindPort);
                socket.Bind(localBindEp);

                combo3489LocalEnd.Text = localBindEp.ToString();
                Log($"本地绑定完成: {localBindEp}");

                cancellationToken.ThrowIfCancellationRequested();

                cancellationToken.ThrowIfCancellationRequested();
                Log(">>>  Test I: Binding Request", "Test I (Binding)");
                Log($"接收超时: {timeoutMs}ms");
                Log($"CHANGE-REQUEST: changeIP=False, changePort=False");
                Log($"请求地址: {serverEp1}");

                var result1 = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StunClient.Query3489(socket, serverEp1, false, false, timeoutMs);
                }, cancellationToken);

                if (result1?.PublicEndPoint != null)
                {
                    RecordEndPoint3489(result1.PublicEndPoint);
                    Log($"Test I 成功");
                    Log($"MAPPED-ADDRESS: {result1.PublicEndPoint}");
                    if (result1.LocalEndPoint != null)
                        Log($"本地端点: {result1.LocalEndPoint}");
                    if (result1.ChangedEndPoint != null)
                        Log($"CHANGED-ADDRESS: {result1.ChangedEndPoint}");
                    if (result1.ResponseEndPoint != null)
                        Log($"响应来源: {result1.ResponseEndPoint}");
                }

                if (result1 == null || result1.PublicEndPoint == null)
                {
                    string errDetail = !string.IsNullOrEmpty(result1?.ErrorMessage)
                        ? result1.ErrorMessage : "请求超时或无响应";
                    if (!string.IsNullOrEmpty(result1?.ErrorMessage))
                        ShowErrorTooltip(result1.ErrorMessage, false);
                    txt3489Type.Text = "UdpBlocked";
                    txt3489Type.ForeColor = Color.Red;
                    Log($"Test I 失败: {errDetail}");
                    Log("判定: UdpBlocked", "(完成)");
                    return Create3489AttemptResult(null); // 结束本次测试，不往下跑了
                }
                txt3489PublicEnd.Text = FormatPrivateEndPoint(result1.PublicEndPoint);

                Log(">>>  验证服务器返回的 MAPPED-ADDRESS");
                if (IsAddressInvalid(result1.PublicEndPoint.Address, out string mappedReason))
                {
                    Log($"错误：服务器返回的 MAPPED-ADDRESS 无效: {mappedReason}");
                    txt3489Type.Text = "Unsupported Server";
                    txt3489Type.ForeColor = Color.DarkOrange;
                    Log("Test I 失败 (服务器返回的地址无效)", "(完成)");
                    return Create3489AttemptResult(null);
                }

                cancellationToken.ThrowIfCancellationRequested();

                cancellationToken.ThrowIfCancellationRequested();
                Log(">>>  验证服务器返回的 CHANGED-ADDRESS");
                var changedEp = result1.ChangedEndPoint;
                var primaryRemoteEp = result1.ResponseEndPoint ?? serverEp1;

                if (changedEp == null || !IsValidServerAddress(primaryRemoteEp, changedEp,
                    (msg, title) => Log(msg, title), "RFC3489"))
                {
                    txt3489Type.Text = "Unsupported Server";
                    txt3489Type.ForeColor = Color.DarkOrange;
                    Log("服务器不支持 RFC3489 CHANGED-ADDRESS 无法继续测试", "(完成)");
                    return Create3489AttemptResult(null);
                }

                Log($"CHANGED-ADDRESS: {changedEp}");
                Log($"主服务器地址: {primaryRemoteEp}");

                bool isDirect = result1.PublicEndPoint.Equals(result1.LocalEndPoint);
                Log($"是否公网直连: {isDirect}");
                if (isDirect)
                    Log($"MAPPED-ADDRESS == 本地地址");

                if (changedEp.Port != primaryRemoteEp.Port)
                {
                    Log($"注意: 备用服务器端口与主服务器不同 ({primaryRemoteEp.Port} → {changedEp.Port})");
                }
                cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                Log(">>>  Test II: Binding Request (changeIP, changePORT)", "Test II (FullCone)");
                Log($"接收超时: {timeoutMs}ms");
                Log($"请求地址: {serverEp1}");
                Log($"CHANGE-REQUEST: changeIP=True, changePort=True");

                var result2 = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StunClient.Query3489(socket, serverEp1, true, true, timeoutMs);
                }, cancellationToken);

                if (result2 != null)
                {
                    Log($"Test II 成功");
                    if (result2.ResponseEndPoint != null)
                        Log($"响应来源: {result2.ResponseEndPoint}");
                    if (result2.PublicEndPoint != null)
                        Log($"MAPPED-ADDRESS: {result2.PublicEndPoint}");

                    // NatTypeTester: 验证服务器是否真正从备用地址响应（IP 和端口都必须与原始不同）
                    if (result2.ResponseEndPoint != null &&
                        (result2.ResponseEndPoint.Address.Equals(primaryRemoteEp.Address) ||
                         result2.ResponseEndPoint.Port == primaryRemoteEp.Port))
                    {
                        txt3489Type.Text = "Unsupported Server";
                        txt3489Type.ForeColor = Color.DarkOrange;
                        Log($"响应来源异常: 服务器未从备用地址响应，实际来自 {result2.ResponseEndPoint}，原始 {primaryRemoteEp}");
                        Log("判定: Unsupported Server", "(完成)");
                        return Create3489AttemptResult(null);
                    }

                    if (result2.PublicEndPoint != null)
                    {
                        RecordEndPoint3489(result2.PublicEndPoint);
                        txt3489PublicEnd.Text = FormatPrivateEndPoint(result2.PublicEndPoint);
                    }

                    if (isDirect)
                    {
                        txt3489Type.Text = "OpenInternet";
                        txt3489Type.ForeColor = Color.LimeGreen;
                        Log($"Test II 成功: {result2.ResponseEndPoint}");
                        Log("判定: OpenInternet", "(完成)");
                        return Create3489AttemptResult(null);
                    }

                    txt3489Type.Text = "FullCone";
                    txt3489Type.ForeColor = Color.LimeGreen;
                    Log($"Test II 成功: {result2.ResponseEndPoint}");
                    Log("判定: FullCone", "(完成)");
                    return Create3489AttemptResult(null);
                }

                string errDetail2 = !string.IsNullOrEmpty(result2?.ErrorMessage)
                    ? result2.ErrorMessage : "请求超时或无响应";
                if (!string.IsNullOrEmpty(result2?.ErrorMessage))
                    ShowErrorTooltip(result2.ErrorMessage, false);
                Log($"Test II 失败: {errDetail2}");
                if (isDirect)
                {
                    txt3489Type.Text = "SymmetricUdpFirewall";
                    txt3489Type.ForeColor = Color.OrangeRed;
                    Log("判定: Symmetric UDP Firewall", "(完成)");
                    return Create3489AttemptResult(null);
                }
                Log("Test II 失败: -> Test I#2");
                cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                Log(">>>  Test I#2: Binding Request (changedAddress)", "Test I#2 (Symmetric)");
                Log($"接收超时: {timeoutMs}ms");

                if (result1.ChangedEndPoint != null)
                {
                    IPEndPoint testI2Server = result1.ChangedEndPoint;
                    Log($"请求地址: {testI2Server}");
                    Log($"CHANGE-REQUEST: changeIP=False, changePort=False");
                    var result3 = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return StunClient.Query3489(socket, testI2Server, false, false, timeoutMs);
                    }, cancellationToken);

                    if (result3 == null || result3.PublicEndPoint == null)
                    {
                        string errDetail = !string.IsNullOrEmpty(result3?.ErrorMessage)
                            ? result3.ErrorMessage : "请求超时或无响应";
                        if (!string.IsNullOrEmpty(result3?.ErrorMessage))
                            ShowErrorTooltip(result3.ErrorMessage, false);
                        txt3489Type.Text = "Unknown";
                        txt3489Type.ForeColor = Color.DarkOrange;
                        Log($"Test I#2 失败: {errDetail}");
                        Log("判定: Unknown", "(完成)");
                        return Create3489AttemptResult(null);
                    }

                    Log($"Test I#2 成功");
                    Log($"MAPPED-ADDRESS: {result3.PublicEndPoint}");
                    RecordEndPoint3489(result3.PublicEndPoint);

                    bool mappingChanged = !result3.PublicEndPoint.Equals(result1.PublicEndPoint);
                    Log($"映射地址比较: \r\n[{result1.PublicEndPoint}] -> [{result3.PublicEndPoint}]");
                    Log($"映射是否改变: {mappingChanged}");

                    if (mappingChanged)
                    {
                        txt3489Type.Text = "Symmetric";
                        txt3489Type.ForeColor = Color.Red;
                        Log("判定: Symmetric", "(完成)");
                        return Create3489AttemptResult(null);
                    }

                    Log("Test I#2 成功: -> Test III");
                }
                else
                {
                    Log("警告: CHANGED-ADDRESS 为空，跳过 Test I#2");
                }

                cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                Log(">>>  Test III: Binding Request (changePORT)", "Test III (Restricted)");
                Log($"接收超时: {timeoutMs}ms");
                Log($"请求地址: {serverEp1}");
                Log($"CHANGE-REQUEST: changeIP=False, changePort=True");

                var result4 = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StunClient.Query3489(socket, serverEp1, false, true, timeoutMs);
                }, cancellationToken);

                if (result4 != null)
                {
                    if (result4.ResponseEndPoint != null)
                        Log($"响应来源: {result4.ResponseEndPoint}");
                    if (result4.PublicEndPoint != null)
                        Log($"MAPPED-ADDRESS: {result4.PublicEndPoint}");
                }
                else
                {
                    //Log($"Test III 无响应");
                }

                // RFC 3489 判定: 响应来自主服务器 IP 的不同端口 → Restricted Cone
                if (result4 != null &&
                    result4.PublicEndPoint != null &&
                    result4.ResponseEndPoint != null &&
                    result4.ResponseEndPoint.Address.Equals(primaryRemoteEp.Address) &&
                    result4.ResponseEndPoint.Port != primaryRemoteEp.Port)
                {
                    txt3489Type.Text = "RestrictedCone";
                    txt3489Type.ForeColor = Color.Orange;
                    Log($"Test III 成功");
                    Log($"MAPPED-ADDRESS: {result4.ResponseEndPoint}");
                    Log("判定: Restricted Cone", "(完成)");
                }
                else
                {
                    string errDetail = !string.IsNullOrEmpty(result4?.ErrorMessage)
                        ? result4.ErrorMessage : "请求超时或无响应";
                    if (!string.IsNullOrEmpty(result4?.ErrorMessage))
                        ShowErrorTooltip(result4.ErrorMessage, false);
                    txt3489Type.Text = "PortRestrictedCone";
                    txt3489Type.ForeColor = Color.DarkOrange;
                    Log($"Test III 失败: {errDetail}");
                    Log("判定: Port Restricted Cone", "(完成)");
                }

                Log("=== RFC3489 测试结束 ===");

            }
            catch (OperationCanceledException)
            {
                runError = "测试已取消";
                Log("测试已被用户取消", "测试取消");
            }
            catch (Exception ex)
            {
                runError = ex.Message;
                Log($"[Error] {ex.Message}");
                if (!cancellationToken.IsCancellationRequested)
                {
                    ShowErrorTooltip("" + ex.Message);
                    Log(null, $"{ex.Message}");
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
                if (!cancellationToken.IsCancellationRequested && !_isBatchTesting)
                {
                    btnCheck3489.Enabled = true;
                    combo3489LocalEnd.Enabled = true;
                    if (!_stopRequested && !lbl3489.Text.Contains("结束"))
                    {
                        lbl3489.Text = "RFC3489 (完成" +
                            (string.IsNullOrEmpty(originalDomain) ? string.Empty : "@" + originalDomain) + ")";
                    }
                }


                if (ReferenceEquals(_cts3489, testCancellation))
                    _cts3489 = null;
                testCancellation.Dispose();
            }

            return Create3489AttemptResult(runError);
        }


        private async void btnReset_Click(object sender, EventArgs e)
        {
            if (_isReloading) return;
            _isReloading = true;

            string serverText = comboServer.Text;
            string serverPort = txtServerPort.Text;
            string timeoutText = GetTimeoutMs().ToString();
            string localEnd5780 = combo5780LocalEnd.Text;
            string localEnd3489 = combo3489LocalEnd.Text;
            string resolvedDomain = _resolvedServerDomain;
            HashSet<string> resolvedAddresses = new HashSet<string>(_resolvedServerAddresses, StringComparer.OrdinalIgnoreCase);
            bool useUdp = radioUDP.Checked;
            bool useTcp = radioTCP.Checked;
            bool useTls = radioTLS.Checked;
            bool changeContinuously = checkPortRandom.Checked;
            bool randomPort = checkPortMode.Checked;
            bool recommendedRange = checkPortRange.Checked;
            bool automaticUdpServer = checkSelectIP.Checked;
            bool settingsExpanded = txt5780Debug.Visible;
            Point currentLocation = Location;
            Size currentSize = Size;

            try
            {
                SaveNATSettings();

                _stopRequested = true;
                _batchCts?.Cancel();
                _cts3489?.Cancel();
                _cts5780?.Cancel();
                try { _activeSocket3489?.Close(); } catch { }
                try { _activeSocket5780?.Close(); } catch { }

                for (int i = 0; i < 40 && _isBatchTesting; i++)
                    await Task.Delay(50);
                await Task.Delay(50);

                NATTest newForm = new NATTest();
                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentLocation;
                newForm.Show();
                newForm.pictureBox1.Image = this.pictureBox1.Image;

                newForm.comboServer.Text = serverText;
                newForm.txtServerPort.Text = serverPort;
                newForm.txtTimeout.Text = timeoutText;
                newForm.checkPortRandom.Checked = changeContinuously;
                newForm.checkPortMode.Checked = randomPort;
                newForm.checkPortRange.Checked = recommendedRange;
                newForm.checkSelectIP.Checked = automaticUdpServer;
                newForm.radioUDP.Checked = useUdp;
                newForm.radioTCP.Checked = useTcp;
                newForm.radioTLS.Checked = useTls;
                newForm.RestoreComboSelection(newForm.combo5780LocalEnd, localEnd5780);
                newForm.RestoreComboSelection(newForm.combo3489LocalEnd, localEnd3489);
                newForm._resolvedServerDomain = resolvedDomain;
                newForm._resolvedServerAddresses.Clear();
                foreach (string address in resolvedAddresses) newForm._resolvedServerAddresses.Add(address);

                if (settingsExpanded && !newForm.txt5780Debug.Visible)
                    newForm.btnSettings_Click(newForm.btnSettings, EventArgs.Empty);
                newForm.Location = currentLocation;
                newForm.Size = currentSize;

                Close();
                Dispose();
            }
            catch (Exception ex)
            {
                _isReloading = false;
                _stopRequested = false;
                btnReset.Enabled = true;
                ShowErrorTooltip("重载 NAT 测试窗口失败：" + ex.Message, true);
            }
        }
        private void checkPortRandom_CheckedChanged(object sender, EventArgs e)//连续模式设置
        {
            if (checkPortRandom.Checked == true)
            {
                checkPortRandom.Text = "端口更换: 自动(?)";
            }
            else
            {
                checkPortRandom.Text = "端口更换: 手动(?)";
            }
        }

        private void checkPortMode_CheckedChanged(object sender, EventArgs e)//随机端口设置
        { //
            if (checkPortMode.Checked == true)
            {
                checkPortMode.Text = "更换模式: 随机(?)";
            }
            else
            {
                checkPortMode.Text = "更换模式: 连号(?)";
            }
        }

        private void checkPortRange_CheckedChanged(object sender, EventArgs e)//范围设置
        {//
            if (checkPortRange.Checked == true)
            {
                checkPortRange.Text = "更换范围: 标准(?)";
            }
            else
            {
                checkPortRange.Text = "更换范围: 完全(?)";
            }
        }

        private void NATTest_SizeChanged(object sender, EventArgs e)
        {

        }

        private void btnSettings_Click(object sender, EventArgs e)
        {
            float dpiScale = GetDpiScale();
            int normalWidth = (int)(432 * dpiScale);
            int expandedWidth = (int)(780 * dpiScale);
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
            string fileName = "Nat.Stun.Compare.gif";
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(filePath))
            {
                SystemSounds.Beep.Play();
                Process.Start(filePath);
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }

        private void btnTrace_Click(object sender, EventArgs e)
        {
            Trace secondForm = new Trace();
            secondForm.Show();
        }

        private static long WritePrivateProfileString(string section, string key, string val, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, val, filePath);
        private static int GetPrivateProfileString(string section, string key, string def, System.Text.StringBuilder retVal, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, def, retVal, size, filePath);

        // 程序旁边的 ini 文件完整路径
        private string iniPath = System.IO.Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

        private void SaveNATSettings()
        {
            WritePrivateProfileString("NATTest", "checkPortRandom", checkPortRandom.Checked.ToString().ToLower(), iniPath);
            WritePrivateProfileString("NATTest", "checkPortMode", checkPortMode.Checked.ToString().ToLower(), iniPath);
            WritePrivateProfileString("NATTest", "checkPortRange", checkPortRange.Checked.ToString().ToLower(), iniPath);
            WritePrivateProfileString("NATTest", "checkSelectIP", checkSelectIP.Checked.ToString().ToLower(), iniPath);

            string serverText = comboServer.Text;
            if (!string.IsNullOrEmpty(serverText))
                WritePrivateProfileString("NATTest", "Server", serverText, iniPath);

            string portText = txtServerPort.Text;
            if (!string.IsNullOrEmpty(portText))
                WritePrivateProfileString("NATTest", "ServerPort", portText, iniPath);

            WritePrivateProfileString("NATTest", "Timeout", GetTimeoutMs().ToString(), iniPath);
        }

        private void LoadCheckStates()
        {
            // 准备一个"小篮子"来装读取到的字符串
            System.Text.StringBuilder temp = new System.Text.StringBuilder(255);

            // 定义一个简单的内部读取小方法，方便复用
            string ReadIni(string key, string defaultVal = "true")
            {
                GetPrivateProfileString("NATTest", key, defaultVal, temp, 255, iniPath);
                return temp.ToString();
            }

            // 设置勾选框状态
            checkPortRandom.Checked = ReadIni("checkPortRandom").ToLower() == "true";
            checkPortMode.Checked = ReadIni("checkPortMode").ToLower() == "true";
            checkPortRange.Checked = ReadIni("checkPortRange").ToLower() == "true";
            checkSelectIP.Checked = ReadIni("checkSelectIP").ToLower() == "true";

            // 触发一下 CheckedChanged 事件，确保按钮上的文字也被更新
            checkPortRandom_CheckedChanged(null, null);
            checkPortMode_CheckedChanged(null, null);
            checkPortRange_CheckedChanged(null, null);
            checkSelectIP_CheckedChanged(null, null);
        }
        private void NATTest_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // 取消和清理测试任务
                _cts3489?.Cancel();
                _cts5780?.Cancel();
                _batchCts?.Cancel();
                _activeSocket3489?.Close();
                _activeSocket5780?.Close();
                _cts3489?.Dispose();
                _cts5780?.Dispose();
                _cts3489 = null;
                _cts5780 = null;

                SaveNATSettings();
            }
            catch (Exception ex)
            {
                ShowErrorTooltip($"记录当前NAT测试设置失败，但问题不大，下次打开NAT测试时会自动使用默认设置喵。错误信息：{ex.Message}");
            }
        }

        // 增强地址验证：检查是否为无效地址（包括 0.0.0.0, ::, 内网, 组播, 保留等）
        private bool IsAddressInvalid(IPAddress ip, out string reason)
        {
            reason = null;
            if (ip == null) { reason = "地址为空"; return true; }

            // 0.0.0.0 (IPv4 未指定)
            if (ip.Equals(IPAddress.Any)) { reason = $"0.0.0.0 (未指定地址)"; return true; }

            // :: (IPv6 未指定)
            if (ip.Equals(IPAddress.IPv6Any)) { reason = $":: (未指定IPv6地址)"; return true; }

            // 回环地址
            if (IPAddress.IsLoopback(ip)) { reason = $"{ip} (回环地址)"; return true; }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = ip.GetAddressBytes();
                // 10.0.0.0/8 私有A类
                if (bytes[0] == 10) { reason = $"{ip} (10.0.0.0/8 私有A类)"; return true; }
                // 172.16.0.0/12 私有B类
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) { reason = $"{ip} (172.16.0.0/12 私有B类)"; return true; }
                // 192.168.0.0/16 私有C类
                if (bytes[0] == 192 && bytes[1] == 168) { reason = $"{ip} (192.168.0.0/16 私有C类)"; return true; }
                // 127.0.0.0/8 回环
                if (bytes[0] == 127) { reason = $"{ip} (127.0.0.0/8 回环地址)"; return true; }
                // 169.254.0.0/16 链路本地
                if (bytes[0] == 169 && bytes[1] == 254) { reason = $"{ip} (169.254.0.0/16 链路本地)"; return true; }
                // 224.0.0.0/4 组播
                if (bytes[0] >= 224 && bytes[0] <= 239) { reason = $"{ip} (组播地址)"; return true; }
                // 240.0.0.0/4 保留
                if (bytes[0] >= 240) { reason = $"{ip} (保留/实验地址)"; return true; }
                // 0.0.0.0/8 保留 (除 0.0.0.0 本身已在上方捕获)
                if (bytes[0] == 0) { reason = $"{ip} (0.0.0.0/8 保留地址)"; return true; }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // fe80::/10 链路本地
                if (ip.IsIPv6LinkLocal) { reason = $"{ip} (fe80::/10 链路本地)"; return true; }
                byte[] bytes = ip.GetAddressBytes();
                // fc00::/7 唯一本地地址 ULA
                if (bytes[0] == 0xFC || bytes[0] == 0xFD) { reason = $"{ip} (fc00::/7 唯一本地地址)"; return true; }
                // fec0::/10 站点本地 (已废弃)
                if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) { reason = $"{ip} (fec0::/10 站点本地)"; return true; }
                // ff00::/8 组播
                if (bytes[0] == 0xFF) { reason = $"{ip} (组播地址)"; return true; }
            }

            return false;
        }

        private bool IsValidServerAddress(IPEndPoint originalEp, IPEndPoint changedEp, Action<string, string> logger, string testType = "RFC3489")
        {
            if (changedEp == null)
            {
                logger?.Invoke($"错误：服务器未提供 {testType} 所需备用地址", "服务器不支持");
                return false;
            }

            if (IsAddressInvalid(originalEp.Address, out string origReason))
            {
                logger?.Invoke($"错误：服务器主地址无效: {origReason}\n{originalEp}", "服务器配置错误");
                return false;
            }

            if (IsAddressInvalid(changedEp.Address, out string changedReason))
            {
                logger?.Invoke($"错误：服务器备用地址无效: {changedReason}\n{changedEp}", "服务器配置错误");
                return false;
            }

            if (changedEp.Address.Equals(originalEp.Address))
            {
                logger?.Invoke($"错误：服务器提供的备用地址与原始地址相同: \r\n{originalEp} → {changedEp}", "服务器配置错误");
                return false;
            }

            if (changedEp.Equals(originalEp))
            {
                logger?.Invoke($"错误：服务器提供的备用地址与原始地址完全相同: \r\n{originalEp} → {changedEp}", "服务器配置错误");
                return false;
            }

            bool requireDifferentPort = testType.Equals("RFC5780", StringComparison.OrdinalIgnoreCase) ||
                                        testType.Equals("RFC5389", StringComparison.OrdinalIgnoreCase) ||
                                        testType.Equals("RFC3489", StringComparison.OrdinalIgnoreCase);

            if (requireDifferentPort && changedEp.Port == originalEp.Port)
            {
                logger?.Invoke($"错误：{testType} 备用地址端口必须不同: {originalEp.Port} == {changedEp.Port}", "服务器配置错误");
                return false;
            }

            if (changedEp.Port != originalEp.Port)
            {
                logger?.Invoke($"提示：备用服务器使用不同端口 ({originalEp.Port} → {changedEp.Port})", "");
            }

            logger?.Invoke($"服务器配置正确: {originalEp} → {changedEp}", "");
            return true;
        }

        private void CheckAndMarkIPChange5780()
        {
            List<IPAddress> uniqueIPs = new List<IPAddress>();
            foreach (var ep in _publicEndPoints5780)
            {
                if (!uniqueIPs.Contains(ep.Address))
                {
                    uniqueIPs.Add(ep.Address);
                }
            }

            if (uniqueIPs.Count > 1)
            {
                StringBuilder tipText = new StringBuilder();
                tipText.AppendLine("公网IP变动, 测试结果不可靠! [设置?]查看debug详情");
                for (int i = 0; i < _publicEndPoints5780.Count; i++)
                {
                    tipText.AppendLine($" → PublicEnd{i + 1}: {FormatPrivateEndPoint(_publicEndPoints5780[i])}");
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
        private void CheckAndMarkIPChange3489()
        {
            List<IPAddress> uniqueIPs = new List<IPAddress>();
            foreach (var ep in _publicEndPoints3489)
            {
                if (!uniqueIPs.Contains(ep.Address))
                {
                    uniqueIPs.Add(ep.Address);
                }
            }

            if (uniqueIPs.Count > 1)
            {
                StringBuilder tipText = new StringBuilder();
                tipText.AppendLine("公网IP变动, 测试结果不可靠! [设置?]查看debug详情");
                for (int i = 0; i < _publicEndPoints3489.Count; i++)
                {
                    tipText.AppendLine($" → PublicEnd{i + 1}: {FormatPrivateEndPoint(_publicEndPoints3489[i])}");
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

        private void ResetIPDetection5780()
        {
            _publicEndPoints5780.Clear();

            if (txt5780PublicEnd.Text.StartsWith("[!]"))
            {
                txt5780PublicEnd.Text = txt5780PublicEnd.Text.Substring(4);
            }

            bool isLight = Global.isThemelight;
            txt5780PublicEnd.ForeColor = isLight ? Color.Black : Color.White;

            toolTip1.SetToolTip(txt5780PublicEnd, null);
        }

        private void ResetIPDetection3489()
        {
            _publicEndPoints3489.Clear();

            if (txt3489PublicEnd.Text.StartsWith("[!]"))
            {
                txt3489PublicEnd.Text = txt3489PublicEnd.Text.Substring(4);
            }

            bool isLight = Global.isThemelight;
            txt3489PublicEnd.ForeColor = isLight ? Color.Black : Color.White;

            toolTip1.SetToolTip(txt3489PublicEnd, null);
        }

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

        private string FormatPrivateEndPoint(IPEndPoint endPoint)
        {
            if (endPoint == null) return string.Empty;
            if (!Global.isPrivate) return endPoint.ToString();

            string maskedIp = PrivacyHelper.MaskIP(endPoint.Address.ToString());
            return endPoint.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{maskedIp}]:{endPoint.Port}"
                : $"{maskedIp}:{endPoint.Port}";
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

                string batchFilePath;
                if (TryGetBatchFileRequest(out batchFilePath))
                {
                    await RunBatchTestsAsync(batchFilePath);
                    return;
                }

                // Enter 会同时发起 RFC5780 与 RFC3489；后者始终为 UDP，
                // 因此手动选服模式需要先让用户确认目标 IP。
                if (!checkSelectIP.Checked && !await PrepareServerForTestAsync()) return;

                btnCheck5780.PerformClick();
                await Task.Delay(10);
                btnCheck3489.PerformClick();
            }
        }

        private void btnMiaoDong_Click(object sender, EventArgs e)
        {
            string fileName = "Nat.Stun.XiaoBai.png";
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(filePath))
            {
                SystemSounds.Beep.Play();
                Process.Start(filePath);
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }

        // 弹出错误提示 Tooltip (2秒) 同时输出到 Debug
        private void ShowErrorTooltip(string message, bool is5780 = false)
        {
            Debug.WriteLine($"[错误] {message}");
            if (_isBatchTesting) return;
            Control anchor = is5780 ? lbl5780LocalEnd : lbl3489LocalEnd;
            toolTip1.Show(message, anchor, 0, anchor.Height + 4, 4000);
        }

        private async void txtServerPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                if (!checkSelectIP.Checked && !await PrepareServerForTestAsync()) return;

                btnCheck5780.PerformClick();
                await Task.Delay(10);
                btnCheck3489.PerformClick();
            }
        }

        private void checkSelectIP_CheckedChanged(object sender, EventArgs e)
        {
            if (checkSelectIP.Checked == true)
            {
                checkSelectIP.Text = "选服模式(UDP): 自动(?)";
            }
            else
            {
                checkSelectIP.Text = "选服模式(UDP): 手动(?)";
            }
        }
    }
}
