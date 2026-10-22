using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetInfoCheckerX
{
    public partial class SettingNIC : Form
    {
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hwnd, int wMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        private const int HTCAPTION = 0x0002;

        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);
        private static int GetPrivateProfileString(string section, string key, string defaultValue, StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "NicSettings";

        private void MyMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_SYSCOMMAND, SC_MOVE + HTCAPTION, 0);
            }
        }
        private bool _suppressMaskSync = false;
        private System.Windows.Forms.Timer _btnSaveTimer;
        private System.Windows.Forms.Timer _btnReadTimer;
        private bool _btnSavePending = false;
        private bool _btnReadPending = false;
        private bool _loadedIPv6DnsAuto = true;
        private string _loadedIPv6Dns1 = "::";
        private string _loadedIPv6Dns2 = "::";
        private bool _loadedDHCPv6;
        private bool _loadedDHCPv6Dns;
        private bool _loadedSlaac;
        private bool _loadedSlaacDns;
        private bool _loadedIPv6StackEnabled;
        private string _loadedNicId;
        private bool _loadingNicInfo;
        private bool _ipv4AddressDirty;
        private bool _ipv4DnsDirty;

        public SettingNIC()
        {
            var existingForm = Application.OpenForms.OfType<SettingNIC>()
                                  .FirstOrDefault(f => f != this);
            if (existingForm != null)
            {
                existingForm.BringToFront();
                existingForm.Focus();
                this.Dispose();
                return;
            }

            InitializeComponent();
        }
        private async Task ApplyNICThemeAsync()
        {
            //await Task.Yield();
            bool isLight = Global.isThemelight;

            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoLabels = { lblIPV4, lblMask, lblGateway, lblDNS1, lblDNS2, lblIPv6, lblV6DNS1, lblV6DNS2 };
            foreach (var c in yumeyoLabels) { if (c != null) c.ForeColor = yumeyoColor; }

            Control[] editControls = { ipIPV4, ipMask, ipGateway, ipDNS1, ipDNS2, txtV6DNS1, txtV6DNS2, txtHops, comboNIC, txtMAC, txtMask };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = textBack;

                    if (c is ComboBox cb)
                    {
                        cb.FlatStyle = FlatStyle.Flat;
                    }

                    c.Invalidate();
                }
            }

            Control[] buttons = { btnRefreshList, btnOK, btnRead, btnSave };
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
                        btn.FlatAppearance.MouseOverBackColor = Global.Yumeyo;
                    }
                }
            }

            Control[] checkBoxes = { checkDHCP, checkDNS, checkHops, checkChangeIPV6State, checkIPV6State,
                checkMAC, checkDHCPv6, checkDHCPv6DNS, checkSLAAC, checkSLAACDNS, checkIPv6DNS };
            foreach (var cb in checkBoxes)
            {
                if (cb != null)
                {
                    cb.ForeColor = contrastColor;
                    cb.BackColor = Color.Transparent;
                }
            }
        }

        private class NicInfo
        {
            public string Description { get; set; }
            public string Name { get; set; }
            public string Id { get; set; }
            public string MacAddress { get; set; }
        }

        private Dictionary<string, NicInfo> _nicDictionary = new Dictionary<string, NicInfo>();

        private void SettingNIC_Load(object sender, EventArgs e)
        {
            this.MouseDown += MyMouseDown;
            pictureBox2.MouseDown += MyMouseDown;
            lblVersion.Text = Global.exeName + " " + Global.Version;
            CloudControl.UsedTimesCounter("手动设置网卡");

            _ = ApplyNICThemeAsync();
            foreach (TextBox textBox in new[]
            {
                ipIPV4, ipMask, ipGateway, ipDNS1, ipDNS2,
                txtV6DNS1, txtV6DNS2, txtMAC, txtMask, txtHops
            })
            {
                textBox.CharacterCasing = CharacterCasing.Lower;
                textBox.TextChanged += ForceLowercaseTextBox;
            }

            foreach (TextBox textBox in new[] { ipIPV4, ipMask, ipGateway, ipDNS1, ipDNS2, txtV6DNS1, txtV6DNS2 })
            {
                IpAddressTextBoxHelper.Attach(textBox);
            }
            RefreshNICList();

            ipIPV4.TextChanged += (s, args) => MarkDirty(ref _ipv4AddressDirty);
            ipMask.TextChanged += (s, args) => MarkDirty(ref _ipv4AddressDirty);
            ipGateway.TextChanged += (s, args) => MarkDirty(ref _ipv4AddressDirty);
            ipDNS1.TextChanged += (s, args) => MarkDirty(ref _ipv4DnsDirty);
            ipDNS2.TextChanged += (s, args) => MarkDirty(ref _ipv4DnsDirty);
            checkDHCP.Click += (s, args) => MarkDirty(ref _ipv4AddressDirty);
            checkDNS.Click += (s, args) => MarkDirty(ref _ipv4DnsDirty);

            ipMask.TextChanged += ipMask_TextChanged;
            ipMask.Leave += ipMask_Leave;
            txtMask.TextChanged += txtMask_TextChanged;
            txtMask.KeyPress += txtMask_KeyPress;
            btnSave.Click += btnSave_Click;
            btnRead.Click += btnRead_Click;

            var mainForm = Application.OpenForms["Form1"];
            if (mainForm != null)
            {
                mainForm.Activated += (s2, e2) =>
                {
                    _ = ApplyNICThemeAsync();
                };
            }
        }

        private static void ForceLowercaseTextBox(object sender, EventArgs e)
        {
            TextBox textBox = sender as TextBox;
            if (textBox == null || string.IsNullOrEmpty(textBox.Text)) return;

            string lowercase = textBox.Text.ToLowerInvariant();
            if (lowercase == textBox.Text) return;

            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            textBox.Text = lowercase;
            textBox.SelectionStart = Math.Min(selectionStart, textBox.TextLength);
            textBox.SelectionLength = Math.Min(selectionLength, textBox.TextLength - textBox.SelectionStart);
        }

        private void RefreshNICList(bool preserveSelection = true)
        {
            string lastSelectedId = preserveSelection && comboNIC.SelectedItem is NetworkInterface lastNic
                ? lastNic.Id
                : null;
            int lastSelectedIndex = preserveSelection ? comboNIC.SelectedIndex : -1;

            comboNIC.Items.Clear();

            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n =>
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            foreach (var nic in nics)
            {
                comboNIC.Items.Add(nic);
            }

            comboNIC.DisplayMember = "Description";

            if (comboNIC.Items.Count > 0)
            {
                bool found = false;

                if (!preserveSelection)
                {
                    NetworkInterface defaultNic = NicHelper.GetCandidateAdapters(
                            requireUp: true,
                            preferGateway: true,
                            filterVirtualAdapters: false,
                            filterNoGatewayAdapters: false,
                            filterUncommonAdapterTypes: false,
                            filterNoUsableAddressAdapters: false)
                        .FirstOrDefault();
                    if (defaultNic != null)
                    {
                        for (int i = 0; i < comboNIC.Items.Count; i++)
                        {
                            if (((NetworkInterface)comboNIC.Items[i]).Id == defaultNic.Id)
                            {
                                comboNIC.SelectedIndex = i;
                                found = true;
                                break;
                            }
                        }
                    }
                }

                if (!found && !string.IsNullOrEmpty(lastSelectedId))
                {
                    for (int i = 0; i < comboNIC.Items.Count; i++)
                    {
                        if (((NetworkInterface)comboNIC.Items[i]).Id == lastSelectedId)
                        {
                            comboNIC.SelectedIndex = i;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    if (lastSelectedIndex >= 0)
                    {
                        if (lastSelectedIndex < comboNIC.Items.Count)
                        {
                            comboNIC.SelectedIndex = lastSelectedIndex;
                        }
                        else
                        {
                            comboNIC.SelectedIndex = comboNIC.Items.Count - 1;
                        }
                    }
                    else
                    {
                        comboNIC.SelectedIndex = 0;
                    }
                }
            }
        }
        private void comboNIC_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboNIC.SelectedItem is NetworkInterface nic)
            {
                LoadNicInfo(nic);
            }
        }

        private void LoadNicInfo(NetworkInterface nic)
        {
            bool preserveIPv6Options = string.Equals(_loadedNicId, nic.Id, StringComparison.OrdinalIgnoreCase);
            _loadingNicInfo = true;
            try
            {
                LoadNicInfoCore(nic, preserveIPv6Options);
            }
            finally
            {
                _loadedNicId = nic.Id;
                _loadingNicInfo = false;
                UpdateIPv6OptionsEnabledState();
                ResetDirtyState();
            }
        }

        private void LoadNicInfoCore(NetworkInterface nic, bool preserveIPv6Options)
        {
            if (!NicHelper.TryGetIPProperties(nic, out IPInterfaceProperties props))
            {
                checkDHCP.Checked = false;
                ipIPV4.Text = ipMask.Text = ipGateway.Text = "0.0.0.0";
                SyncTxtMaskFromIpMask();
                checkDNS.Checked = true;
                ipDNS1.Text = ipDNS2.Text = "0.0.0.0";
                if (!preserveIPv6Options)
                {
                    ResetIPv6Controls();
                }
                else
                {
                    _loadedIPv6StackEnabled = false;
                    checkIPV6State.Checked = false;
                }
                txtMAC.Text = nic.GetPhysicalAddress().ToString();
                return;
            }

            bool dhcpEnabled = NicHelper.TryGetIPv4Properties(props, out IPv4InterfaceProperties ipv4Props) && ipv4Props.IsDhcpEnabled;
            checkDHCP.Checked = dhcpEnabled;
            ipIPV4.Enabled = ipMask.Enabled = ipGateway.Enabled = txtMask.Enabled = !dhcpEnabled;

            var unicast = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            ipIPV4.Text = unicast?.Address.ToString() ?? "0.0.0.0";
            ipMask.Text = unicast?.IPv4Mask?.ToString() ?? "0.0.0.0";
            SyncTxtMaskFromIpMask();

            var gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            ipGateway.Text = gateway?.Address.ToString() ?? "0.0.0.0";

            bool dnsAuto = IsDnsAuto(nic.Id, out var manualDns);
            checkDNS.Checked = dnsAuto;
            ipDNS1.Enabled = ipDNS2.Enabled = !dnsAuto;

            checkMAC.Checked = false;
            txtMAC.Enabled = false;
            string currentMac = nic.GetPhysicalAddress().ToString();
            if (currentMac.Length == 12)
            {
                txtMAC.Text = string.Join(":", Enumerable.Range(0, 6)
                    .Select(i => currentMac.Substring(i * 2, 2)));
            }
            else
            {
                txtMAC.Text = currentMac;
            }


            if (!dnsAuto)
            {
                ipDNS1.Text = manualDns.Length > 0 ? manualDns[0] : "0.0.0.0";
                ipDNS2.Text = manualDns.Length > 1 ? manualDns[1] : "0.0.0.0";
            }
            else
            {
                var liveDns = props.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString())
                    .ToList();
                ipDNS1.Text = liveDns.Count > 0 ? liveDns[0] : "0.0.0.0";
                ipDNS2.Text = liveDns.Count > 1 ? liveDns[1] : "0.0.0.0";
            }

            LoadIPv6Info(nic, props, preserveIPv6Options);

            try
            {
                string query = $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID='{nic.Id}'";
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    var results = searcher.Get().Cast<ManagementObject>().ToList();

                    if (results.Count > 0)
                    {
                        var mo = results[0];

                        string currentMetric = mo["IPConnectionMetric"]?.ToString() ?? mo["InterfaceMetric"]?.ToString() ?? "0";

                        txtHops.Text = currentMetric;
                    }
                    else
                    {
                        txtHops.Text = "0";
                    }
                }
            }

            catch (Exception)
            {
                txtHops.Text = "0";
            }
            finally
            {
                checkHops.Checked = false;

                txtHops.Enabled = false;
            }
        }

        private void MarkDirty(ref bool dirtyFlag)
        {
            if (!_loadingNicInfo) dirtyFlag = true;
        }

        private void ResetDirtyState()
        {
            _ipv4AddressDirty = false;
            _ipv4DnsDirty = false;
        }
        private bool IsDnsAuto(string nicId, out string[] dnsServers)
        {
            dnsServers = Array.Empty<string>();

            string registryPath = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nicId}";

            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
            {
                if (key != null)
                {
                    string nameServer = key.GetValue("NameServer") as string;

                    if (string.IsNullOrEmpty(nameServer))
                    {
                        return true;
                    }
                    else
                    {
                        dnsServers = nameServer.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        return false;
                    }
                }
            }

            return true;
        }

        private bool IsIPv6DnsAuto(string nicId, int interfaceIndex, out string[] dnsServers)
        {
            dnsServers = Array.Empty<string>();
            string registryPath = $@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\{nicId}";

            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
            {
                string nameServer = key?.GetValue("NameServer") as string;
                if (!string.IsNullOrWhiteSpace(nameServer))
                {
                    dnsServers = nameServer.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    return false;
                }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "interface ipv6 show dnsservers name=" + interfaceIndex,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                        return output.IndexOf("DHCP", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }

            return true;
        }

        private void ResetIPv6Controls()
        {
            _loadedIPv6StackEnabled = false;
            checkIPV6State.Checked = false;
            checkDHCPv6.Checked = false;
            checkDHCPv6DNS.Checked = false;
            checkSLAAC.Checked = false;
            checkSLAACDNS.Checked = false;
            checkIPv6DNS.Checked = true;
            txtV6DNS1.Text = txtV6DNS2.Text = "::";
        }

        private void LoadIPv6Info(NetworkInterface nic, IPInterfaceProperties props, bool preserveIPv6Options)
        {
            bool stackEnabled = nic.Supports(NetworkInterfaceComponent.IPv6);
            if (!stackEnabled)
            {
                if (!preserveIPv6Options)
                {
                    ResetIPv6Controls();
                }
                _loadedIPv6StackEnabled = false;
                checkIPV6State.Checked = false;
                return;
            }

            ResetIPv6Controls();
            _loadedIPv6StackEnabled = true;
            checkIPV6State.Checked = true;

            int interfaceIndex;
            try
            {
                interfaceIndex = props.GetIPv6Properties().Index;
            }
            catch
            {
                return;
            }

            try
            {
                string query = "SELECT Dhcp, RouterDiscovery, ManagedAddressConfiguration, " +
                    "OtherStatefulConfiguration FROM MSFT_NetIPInterface WHERE InterfaceIndex=" +
                    interfaceIndex + " AND AddressFamily=23";
                using (var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", query))
                using (var results = searcher.Get())
                {
                    ManagementObject item = results.Cast<ManagementObject>().FirstOrDefault();
                    if (item != null)
                    {
                        // Dhcp 表示本机是否允许 IPv6 DHCP；ManagedAddressConfiguration 在启用
                        // Router Discovery 时会被 RA 的 M 标志覆盖，不能代表用户是否允许 DHCPv6
                        checkDHCPv6.Checked = Convert.ToUInt32(item["Dhcp"]) == 1;
                        checkSLAAC.Checked = Convert.ToUInt32(item["RouterDiscovery"]) != 0;
                        checkDHCPv6DNS.Checked = Convert.ToUInt32(item["OtherStatefulConfiguration"]) == 1;
                    }
                }
            }
            catch
            {
                checkDHCPv6.Checked = props.UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetworkV6 && a.PrefixOrigin == PrefixOrigin.Dhcp);
                checkSLAAC.Checked = props.UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetworkV6 && a.PrefixOrigin == PrefixOrigin.RouterAdvertisement);
            }

            if (TryReadRaDnsState(interfaceIndex, out bool raDnsEnabled))
            {
                checkSLAACDNS.Checked = raDnsEnabled;
            }

            bool dnsAuto = IsIPv6DnsAuto(nic.Id, interfaceIndex, out string[] manualDns);
            checkIPv6DNS.Checked = dnsAuto;
            IEnumerable<string> dns = dnsAuto
                ? props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetworkV6).Select(d => d.ToString())
                : manualDns.Where(d => System.Net.IPAddress.TryParse(d, out System.Net.IPAddress parsed) &&
                    parsed.AddressFamily == AddressFamily.InterNetworkV6);
            string[] addresses = dns.Take(2).ToArray();
            txtV6DNS1.Text = addresses.Length > 0 ? addresses[0] : "::";
            txtV6DNS2.Text = addresses.Length > 1 ? addresses[1] : "::";
            _loadedIPv6DnsAuto = checkIPv6DNS.Checked;
            _loadedIPv6Dns1 = txtV6DNS1.Text;
            _loadedIPv6Dns2 = txtV6DNS2.Text;
            _loadedDHCPv6 = checkDHCPv6.Checked;
            _loadedDHCPv6Dns = checkDHCPv6DNS.Checked;
            _loadedSlaac = checkSLAAC.Checked;
            _loadedSlaacDns = checkSLAACDNS.Checked;
        }

        private bool TryReadRaDnsState(int interfaceIndex, out bool enabled)
        {
            enabled = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "interface ipv6 show interface " + interfaceIndex,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    string line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(value => value.IndexOf("RFC 6106", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (line == null) return false;
                    string valueText = line.Substring(line.LastIndexOf(':') + 1).Trim();
                    enabled = valueText.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) < 0 &&
                              valueText.IndexOf("禁用", StringComparison.OrdinalIgnoreCase) < 0 &&
                              valueText.IndexOf("关闭", StringComparison.OrdinalIgnoreCase) < 0;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private string GetRegistryPath(string nicId)
        {
            string baseKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
            using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(baseKey))
            {
                if (rk == null) return null;

                foreach (string subKeyName in rk.GetSubKeyNames())
                {
                    using (RegistryKey sk = rk.OpenSubKey(subKeyName))
                    {
                        if (sk != null)
                        {
                            object val = sk.GetValue("NetCfgInstanceId");
                            if (val != null && val.ToString().Equals(nicId, StringComparison.OrdinalIgnoreCase))
                            {
                                return baseKey + "\\" + subKeyName;
                            }
                        }
                    }
                }
            }
            return null;
        }

        private int MaskToCidr(string mask)
        {
            if (!System.Net.IPAddress.TryParse(mask, out System.Net.IPAddress addr)) return -1;
            byte[] bytes = addr.GetAddressBytes();
            if (bytes.Length != 4) return -1;
            uint val = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
            bool foundZero = false;
            int cidr = 0;
            for (int i = 31; i >= 0; i--)
            {
                if ((val & (1u << i)) != 0)
                {
                    if (foundZero) return -1;
                    cidr++;
                }
                else
                {
                    foundZero = true;
                }
            }
            return cidr;
        }

        private string CidrToMask(int cidr)
        {
            if (cidr < 0 || cidr > 32) return "";
            uint mask = cidr == 0 ? 0 : ~0u << (32 - cidr);
            return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
        }

        private void SyncTxtMaskFromIpMask()
        {
            if (_suppressMaskSync) return;
            _suppressMaskSync = true;
            int cidr = MaskToCidr(ipMask.Text);
            txtMask.Text = cidr >= 0 ? cidr.ToString() : "";
            _suppressMaskSync = false;
        }

        private void SyncIpMaskFromTxtMask()
        {
            if (_suppressMaskSync) return;
            _suppressMaskSync = true;
            if (int.TryParse(txtMask.Text, out int cidr))
            {
                if (cidr < 0) cidr = 0;
                if (cidr > 32) cidr = 32;
                ipMask.Text = CidrToMask(cidr);
            }
            _suppressMaskSync = false;
        }

        private void AutoFillSubnetAndGateway()
        {
            if (checkDHCP.Checked) return;

            string ipText = ipIPV4.Text?.Trim();
            if (string.IsNullOrEmpty(ipText) || ipText == "0.0.0.0" || ipText == "...") return;

            if (!System.Net.IPAddress.TryParse(ipText, out System.Net.IPAddress ipAddress)) return;

            byte[] bytes = ipAddress.GetAddressBytes();
            if (bytes.Length != 4) return;

            string mask, gateway;

            if (bytes[0] == 10)
            {
                mask = "255.0.0.0";
                gateway = $"10.{bytes[1]}.{bytes[2]}.1";
            }
            else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                mask = "255.255.0.0";
                gateway = $"172.{bytes[1]}.0.1";
            }
            else if (bytes[0] == 192 && bytes[1] == 168)
            {
                mask = "255.255.255.0";
                gateway = $"192.168.{bytes[2]}.1";
            }
            else if (bytes[0] == 169 && bytes[1] == 254)
            {
                mask = "255.255.0.0";
                gateway = "0.0.0.0";
            }
            else
            {
                mask = "255.255.255.255";
                gateway = "0.0.0.0";
            }

            ipMask.Text = mask;
            ipGateway.Text = gateway;
            SyncTxtMaskFromIpMask();
        }

        private void ipIPV4_Leave(object sender, EventArgs e)
        {
            AutoFillSubnetAndGateway();
        }

        private async void btnRefreshList_Click(object sender, EventArgs e)
        {
            btnRefreshList.Enabled = false;
            btnRefreshList.Text = "刷新中";

            await Task.Delay(200);

            RefreshNICList();

            if (comboNIC.SelectedItem is NetworkInterface nic)
            {
                LoadNicInfo(nic);
            }

            btnRefreshList.Enabled = true;
            btnRefreshList.Text = "刷新";
        }

        private void checkDHCP_CheckedChanged(object sender, EventArgs e)
        {
            bool isAutoIP = checkDHCP.Checked;

            ipIPV4.Enabled = ipMask.Enabled = ipGateway.Enabled = txtMask.Enabled = !isAutoIP;

            if (!isAutoIP)
            {
                checkDNS.Checked = false;
                ipDNS1.Enabled = true;
                ipDNS2.Enabled = true;
            }
        }

        private void checkDNS_CheckedChanged(object sender, EventArgs e)
        {
            bool isAutoDNS = checkDNS.Checked;
            ipDNS1.Enabled = ipDNS2.Enabled = !isAutoDNS;
        }

        private void checkIPv6DNS_CheckedChanged(object sender, EventArgs e)
        {
            UpdateIPv6OptionsEnabledState();
        }

        private void checkHops_CheckedChanged(object sender, EventArgs e)
        {
            txtHops.Enabled = checkHops.Checked;
        }

        private void checkIPV6State_CheckedChanged(object sender, EventArgs e)
        {
            if (checkIPV6State.Checked == true)
            {
                checkIPV6State.Text = "开";
            }
            else
            {
                checkIPV6State.Text = "关";
            }

            UpdateIPv6OptionsEnabledState();
        }

        private void checkChangeIPV6State_CheckedChanged(object sender, EventArgs e)
        {
            checkIPV6State.Enabled = checkChangeIPV6State.Checked;
            if (!checkChangeIPV6State.Checked && !_loadingNicInfo)
            {
                checkIPV6State.Checked = _loadedIPv6StackEnabled;
            }

            UpdateIPv6OptionsEnabledState();
        }

        private void UpdateIPv6OptionsEnabledState()
        {
            bool ipv6Enabled = checkChangeIPV6State.Checked
                ? checkIPV6State.Checked
                : _loadedIPv6StackEnabled;

            checkDHCPv6.Enabled = ipv6Enabled;
            checkDHCPv6DNS.Enabled = ipv6Enabled;
            checkSLAAC.Enabled = ipv6Enabled;
            checkSLAACDNS.Enabled = ipv6Enabled;
            checkIPv6DNS.Enabled = ipv6Enabled;
            lblV6DNS1.Enabled = ipv6Enabled;
            lblV6DNS2.Enabled = ipv6Enabled;
            txtV6DNS1.Enabled = ipv6Enabled && !checkIPv6DNS.Checked;
            txtV6DNS2.Enabled = ipv6Enabled && !checkIPv6DNS.Checked;
        }

        private static async Task WaitForIPv6BindingStateAsync(string nicId, bool expectedEnabled)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                try
                {
                    NetworkInterface current = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(item => string.Equals(item.Id, nicId, StringComparison.OrdinalIgnoreCase));
                    if (current != null && current.Supports(NetworkInterfaceComponent.IPv6) == expectedEnabled)
                    {
                        // Give router discovery and DHCPv6 a moment to restore their active state.
                        await Task.Delay(expectedEnabled ? 750 : 150);
                        return;
                    }
                }
                catch
                {
                }

                await Task.Delay(250);
            }
        }

        private sealed class AdminCommand
        {
            public string Title { get; set; }
            public string Command { get; set; }
        }

        private sealed class AdminCommandResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string FailedStep { get; set; }
            public string Output { get; set; }
        }

        private async Task<AdminCommandResult> ExecuteAdminCommandsAsync(IList<AdminCommand> commands)
        {
            if (commands == null || commands.Count == 0)
            {
                return new AdminCommandResult { Success = true, Output = "没有需要应用的更改" };
            }

            return await Task.Run(() =>
            {
                string token = Guid.NewGuid().ToString("N");
                string batchPath = Path.Combine(Path.GetTempPath(), "nicx_" + token + ".cmd");
                string logPath = Path.Combine(Path.GetTempPath(), "nicx_" + token + ".log");
                Encoding commandEncoding = new UTF8Encoding(false);

                try
                {
                    var batch = new StringBuilder();
                    batch.AppendLine("@echo off");
                    batch.AppendLine("chcp 65001 > nul");
                    batch.AppendLine("call :nicx_main > \"" + logPath.Replace("%", "%%") + "\" 2>&1");
                    batch.AppendLine("exit /b %errorlevel%");
                    batch.AppendLine(":nicx_main");
                    for (int i = 0; i < commands.Count; i++)
                    {
                        batch.AppendLine("echo __NICX_STEP_" + i + "__");
                        batch.AppendLine(commands[i].Command.Replace("%", "%%"));
                        batch.AppendLine("if errorlevel 1 exit /b %errorlevel%");
                    }
                    batch.AppendLine("exit /b 0");
                    File.WriteAllText(batchPath, batch.ToString(), commandEncoding);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/d /c \"\"" + batchPath + "\"\"",
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = true
                    };

                    using (Process process = Process.Start(psi))
                    {
                        process.WaitForExit();
                        string output = File.Exists(logPath) ? File.ReadAllText(logPath, commandEncoding).Trim() : string.Empty;
                        int failedIndex = FindLastCommandMarker(output, commands.Count);
                        string cleanOutput = RemoveCommandMarkers(output);
                        return new AdminCommandResult
                        {
                            Success = process.ExitCode == 0,
                            ExitCode = process.ExitCode,
                            FailedStep = process.ExitCode == 0 || failedIndex < 0 ? null : commands[failedIndex].Title,
                            Output = cleanOutput
                        };
                    }
                }
                catch (Win32Exception ex)
                {
                    string message = ex.NativeErrorCode == 1223
                        ? "管理员权限请求被取消"
                        : ex.Message;
                    return new AdminCommandResult { Success = false, ExitCode = ex.NativeErrorCode, Output = message };
                }
                catch (Exception ex)
                {
                    return new AdminCommandResult { Success = false, ExitCode = -1, Output = ex.Message };
                }
                finally
                {
                    try { if (File.Exists(batchPath)) File.Delete(batchPath); } catch { }
                    try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
                }
            });
        }

        private static int FindLastCommandMarker(string output, int commandCount)
        {
            int result = -1;
            int lastPosition = -1;
            for (int i = 0; i < commandCount; i++)
            {
                int position = (output ?? string.Empty).LastIndexOf("__NICX_STEP_" + i + "__", StringComparison.Ordinal);
                if (position > lastPosition)
                {
                    lastPosition = position;
                    result = i;
                }
            }
            return result;
        }

        private static string RemoveCommandMarkers(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return string.Empty;
            return string.Join(Environment.NewLine, output
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line => !line.StartsWith("__NICX_STEP_", StringComparison.Ordinal))
                .Where(line => !string.IsNullOrWhiteSpace(line)))
                .Trim();
        }

        private static void AddCommand(ICollection<AdminCommand> commands, string title, string command)
        {
            commands.Add(new AdminCommand { Title = title, Command = command });
        }

        private static bool IsAddress(string text, AddressFamily addressFamily, bool allowUnspecified)
        {
            if (!System.Net.IPAddress.TryParse((text ?? string.Empty).Trim(), out System.Net.IPAddress address) ||
                address.AddressFamily != addressFamily)
            {
                return false;
            }

            return allowUnspecified ||
                   (!address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any));
        }

        private bool ValidateNetworkSettings()
        {
            if (!checkDHCP.Checked)
            {
                if (!IsAddress(ipIPV4.Text, AddressFamily.InterNetwork, false) ||
                    MaskToCidr(ipMask.Text) < 0 ||
                    !IsAddress(ipGateway.Text, AddressFamily.InterNetwork, true))
                {
                    MessageBox.Show("请填写有效的 IPv4 地址、连续子网掩码和默认网关", "IPv4 设置无效",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            if (!checkDNS.Checked &&
                (!IsAddress(ipDNS1.Text, AddressFamily.InterNetwork, false) ||
                 (!string.IsNullOrWhiteSpace(ipDNS2.Text) && ipDNS2.Text != "0.0.0.0" &&
                  !IsAddress(ipDNS2.Text, AddressFamily.InterNetwork, false))))
            {
                MessageBox.Show("请填写有效的 IPv4 DNS 地址", "IPv4 DNS 无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            bool noPrimaryIPv6Dns = string.IsNullOrWhiteSpace(txtV6DNS1.Text) || txtV6DNS1.Text == "::";
            bool hasSecondaryIPv6Dns = !string.IsNullOrWhiteSpace(txtV6DNS2.Text) && txtV6DNS2.Text != "::";
            bool ipv6OptionsEnabled = checkChangeIPV6State.Checked
                ? checkIPV6State.Checked
                : _loadedIPv6StackEnabled;
            if (ipv6OptionsEnabled && !checkIPv6DNS.Checked &&
                ((!noPrimaryIPv6Dns && !IsAddress(txtV6DNS1.Text, AddressFamily.InterNetworkV6, false)) ||
                 (hasSecondaryIPv6Dns && (noPrimaryIPv6Dns ||
                  !IsAddress(txtV6DNS2.Text, AddressFamily.InterNetworkV6, false)))))
            {
                MessageBox.Show("请填写有效的 IPv6 DNS 地址", "IPv6 DNS 无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (checkHops.Checked && (!int.TryParse(txtHops.Text, out int metric) || metric < 1 || metric > 999))
            {
                MessageBox.Show("跃点数必须是 1 到 999 之间的整数", "跃点数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private async void btnOK_Click(object sender, EventArgs e)
        {
            if (!(comboNIC.SelectedItem is NetworkInterface nic))
            {
                RefreshNICList();
                if (comboNIC.Items.Count > 0) comboNIC.SelectedIndex = 0;
                if (!(comboNIC.SelectedItem is NetworkInterface refreshedNic)) return;
                nic = refreshedNic;
            }
            else
            {
                string selectedId = nic.Id;
                bool stillExists = false;
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.Id == selectedId && ni.OperationalStatus == OperationalStatus.Up)
                        {
                            stillExists = true;
                            break;
                        }
                    }
                }
                catch { }
                if (!stillExists)
                {
                    RefreshNICList();
                    if (!(comboNIC.SelectedItem is NetworkInterface refreshedNic)) return;
                    nic = refreshedNic;
                }
            }

            if (!ValidateNetworkSettings()) return;

            string nicName = nic.Name;
            string nicDisplayName = string.IsNullOrWhiteSpace(nic.Description) ? nicName : nic.Description;
            string escapedNicName = EscapePowerShellSingleQuoted(nicName);
            var commands = new List<AdminCommand>();
            var restartChanges = new List<string>();
            bool restartAdapter = false;

            bool systemDhcpV4 = false;
            try
            {
                IPInterfaceProperties currentProperties = nic.GetIPProperties();
                systemDhcpV4 = NicHelper.TryGetIPv4Properties(currentProperties, out IPv4InterfaceProperties currentIpv4) &&
                    currentIpv4.IsDhcpEnabled;
            }
            catch { }

            if (_ipv4AddressDirty && checkDHCP.Checked && !systemDhcpV4)
            {
                // Re-check in the elevated process as well.  Windows' netsh returns exit code 1
                // when DHCP is already enabled, although the requested state has been reached.
                // The PowerShell guard keeps this operation idempotent without hiding real errors.
                AddCommand(commands, "IPv4 自动获取地址",
                    $"powershell -NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; $i=Get-NetIPInterface -InterfaceAlias '{escapedNicName}' -AddressFamily IPv4 -ErrorAction Stop; if ($i.Dhcp -ne 'Enabled') {{ Set-NetIPInterface -InputObject $i -Dhcp Enabled -Confirm:$false -ErrorAction Stop }}\"");
            }
            else if (_ipv4AddressDirty && !checkDHCP.Checked)
            {
                AddCommand(commands, "IPv4 静态地址", $"netsh interface ip set address name=\"{nicName}\" source=static address={ipIPV4.Text} mask={ipMask.Text} gateway={ipGateway.Text}");
            }

            bool systemDnsAutoV4 = IsDnsAuto(nic.Id, out string[] ignoredDnsServers);
            if (_ipv4DnsDirty && checkDNS.Checked && !systemDnsAutoV4)
            {
                AddCommand(commands, "IPv4 自动 DNS", $"netsh interface ip set dns name=\"{nicName}\" source=dhcp");
            }
            else if (_ipv4DnsDirty && !checkDNS.Checked)
            {
                AddCommand(commands, "IPv4 首选 DNS", $"netsh interface ip set dns name=\"{nicName}\" source=static address={ipDNS1.Text} register=primary validate=no");
                if (!string.IsNullOrEmpty(ipDNS2.Text) && ipDNS2.Text != "0.0.0.0")
                {
                    AddCommand(commands, "IPv4 备选 DNS", $"netsh interface ip add dns name=\"{nicName}\" address={ipDNS2.Text} index=2 validate=no");
                }
            }

            if (checkHops.Checked)
            {
                AddCommand(commands, "IPv4 跃点数", $"netsh interface ipv4 set interface \"{nicName}\" metric={txtHops.Text}");
            }

            bool applyIPv6Options = checkChangeIPV6State.Checked
                ? checkIPV6State.Checked
                : _loadedIPv6StackEnabled;
            bool ipv6BindingChanged = checkChangeIPV6State.Checked &&
                checkIPV6State.Checked != _loadedIPv6StackEnabled;
            bool dhcpv6Changed = checkDHCPv6.Checked != _loadedDHCPv6;
            bool dhcpv6DnsChanged = checkDHCPv6DNS.Checked != _loadedDHCPv6Dns;
            bool slaacChanged = checkSLAAC.Checked != _loadedSlaac;
            bool slaacDnsChanged = checkSLAACDNS.Checked != _loadedSlaacDns;
            bool ipv6DnsChanged = checkIPv6DNS.Checked != _loadedIPv6DnsAuto ||
                !string.Equals(txtV6DNS1.Text, _loadedIPv6Dns1, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(txtV6DNS2.Text, _loadedIPv6Dns2, StringComparison.OrdinalIgnoreCase);

            // When IPv6 is being enabled, bind the protocol before applying its options.
            if (ipv6BindingChanged && checkIPV6State.Checked)
            {
                AddCommand(commands, "IPv6 协议栈", $"powershell -NoProfile -NonInteractive -Command \"Enable-NetAdapterBinding -Name '{escapedNicName}' -ComponentID ms_tcpip6 -Confirm:$false\"");
            }

            if (applyIPv6Options)
            {
                string enabledDhcp = checkDHCPv6.Checked ? "Enabled" : "Disabled";
                string enabledDhcpDns = checkDHCPv6DNS.Checked ? "enabled" : "disabled";
                string enabledSlaac = checkSLAAC.Checked ? "enabled" : "disabled";
                string enabledSlaacDns = checkSLAACDNS.Checked ? "enabled" : "disabled";

                if (dhcpv6Changed)
                {
                    AddCommand(commands, "DHCPv6 地址", $"powershell -NoProfile -NonInteractive -Command \"Set-NetIPInterface -InterfaceAlias '{escapedNicName}' -AddressFamily IPv6 -Dhcp {enabledDhcp} -Confirm:$false\"");
                    restartChanges.Add((checkDHCPv6.Checked ? "启用" : "禁用") + "DHCPv6 地址");
                    restartAdapter = true;
                }
                if (dhcpv6DnsChanged)
                {
                    AddCommand(commands, "DHCPv6 DNS（当前会话）", $"netsh interface ipv6 set interface interface=\"{nicName}\" otherstateful={enabledDhcpDns} store=active");
                    AddCommand(commands, "DHCPv6 DNS（永久配置）", $"netsh interface ipv6 set interface interface=\"{nicName}\" otherstateful={enabledDhcpDns} store=persistent");
                    restartChanges.Add((checkDHCPv6DNS.Checked ? "启用" : "禁用") + "DHCPv6 DNS");
                    restartAdapter = true;
                }
                if (slaacChanged)
                {
                    AddCommand(commands, "SLAAC/路由发现（当前会话）", $"netsh interface ipv6 set interface interface=\"{nicName}\" routerdiscovery={enabledSlaac} store=active");
                    AddCommand(commands, "SLAAC/路由发现（永久配置）", $"netsh interface ipv6 set interface interface=\"{nicName}\" routerdiscovery={enabledSlaac} store=persistent");
                    restartChanges.Add((checkSLAAC.Checked ? "启用" : "禁用") + "SLAAC 地址");
                    restartAdapter = true;
                }
                if (slaacDnsChanged)
                {
                    AddCommand(commands, "SLAAC RA DNS（当前会话）", $"netsh interface ipv6 set interface interface=\"{nicName}\" rabaseddnsconfig={enabledSlaacDns} store=active");
                    AddCommand(commands, "SLAAC RA DNS（永久配置）", $"netsh interface ipv6 set interface interface=\"{nicName}\" rabaseddnsconfig={enabledSlaacDns} store=persistent");
                    restartChanges.Add((checkSLAACDNS.Checked ? "启用" : "禁用") + "SLAAC DNS");
                    restartAdapter = true;
                }

                if (ipv6DnsChanged && checkIPv6DNS.Checked)
                {
                    AddCommand(commands, "IPv6 自动 DNS", $"netsh interface ipv6 set dnsservers name=\"{nicName}\" source=dhcp");
                }
                else if (ipv6DnsChanged)
                {
                    bool clearIPv6Dns = string.IsNullOrWhiteSpace(txtV6DNS1.Text) || txtV6DNS1.Text == "::";
                    if (clearIPv6Dns)
                    {
                        AddCommand(commands, "清空 IPv6 DNS", $"netsh interface ipv6 set dnsservers name=\"{nicName}\" source=static address=none");
                    }
                    else
                    {
                        AddCommand(commands, "IPv6 首选 DNS", $"netsh interface ipv6 set dnsservers name=\"{nicName}\" source=static address={txtV6DNS1.Text} register=primary validate=no");
                    }
                    if (!clearIPv6Dns && !string.IsNullOrWhiteSpace(txtV6DNS2.Text) && txtV6DNS2.Text != "::")
                    {
                        AddCommand(commands, "IPv6 备选 DNS", $"netsh interface ipv6 add dnsservers name=\"{nicName}\" address={txtV6DNS2.Text} index=2 validate=no");
                    }
                }
            }

            if (checkMAC.Checked)
            {
                string regPath = GetRegistryPath(nic.Id);
                if (!string.IsNullOrEmpty(regPath))
                {
                    string newMac = txtMAC.Text.Trim().Replace("-", "").Replace(":", "");

                    if (string.IsNullOrEmpty(newMac))
                    {
                        AddCommand(commands, "恢复默认 MAC", $"reg delete \"HKEY_LOCAL_MACHINE\\{regPath}\" /v \"NetworkAddress\" /f");
                        restartChanges.Add("恢复默认 MAC");
                    }
                    else
                    {
                        AddCommand(commands, "修改 MAC", $"reg add \"HKEY_LOCAL_MACHINE\\{regPath}\" /v \"NetworkAddress\" /t REG_SZ /d {newMac} /f");
                        restartChanges.Add("修改 MAC 地址");
                    }
                    restartAdapter = true;
                }
            }

            // When IPv6 is being disabled, apply the binding change last and skip all
            // subordinate IPv6 settings because they cannot have an effect.
            if (ipv6BindingChanged && !checkIPV6State.Checked)
            {
                AddCommand(commands, "IPv6 协议栈", $"powershell -NoProfile -NonInteractive -Command \"Disable-NetAdapterBinding -Name '{escapedNicName}' -ComponentID ms_tcpip6 -Confirm:$false\"");
            }

            if (restartAdapter)
            {
                string changedItems = string.Join(string.Empty, restartChanges.Select(item => "【" + item + "】"));
                DialogResult confirmation = MessageBox.Show(
                    $"当前已修改{changedItems}，\r\n需重启网卡【{nicDisplayName}】才能生效，网络将临时断开确认继续吗？",
                    "确认重启网卡",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirmation != DialogResult.Yes)
                {
                    return;
                }

                AddCommand(commands, "重启网卡", $"powershell -NoProfile -NonInteractive -Command \"Restart-NetAdapter -Name '{escapedNicName}' -Confirm:$false\"");
                AddCommand(commands, "刷新 DNS 缓存", "ipconfig /flushdns");
            }

            btnOK.Enabled = false;
            btnOK.Text = "修改中...";

            AdminCommandResult result = await ExecuteAdminCommandsAsync(commands);

            if (result.Success)
            {
                if (restartAdapter)
                {
                    await Task.Delay(2500);
                }
                else if (ipv6BindingChanged)
                {
                    await WaitForIPv6BindingStateAsync(nic.Id, checkIPV6State.Checked);
                }
                else
                {
                    await Task.Delay(100);
                }

                MessageBox.Show("修改成功!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                var mainForm = Application.OpenForms["Form1"];
                if (mainForm != null)
                {
                    var btnRefresh = mainForm.Controls.Find("btnRefreshNIC", true).FirstOrDefault() as Button;
                    btnRefresh?.PerformClick();
                }
                // Rebuild from fresh NetworkInterface instances.  A restarted adapter may
                // temporarily disappear, so do not restore the pre-restart selection.
                RefreshNICList(preserveSelection: !restartAdapter);
            }
            else
            {
                string failedStep = string.IsNullOrEmpty(result.FailedStep) ? "未知步骤" : result.FailedStep;
                string details = string.IsNullOrWhiteSpace(result.Output) ? "系统未返回错误文本" : result.Output;
                if (details.Length > 3000) details = details.Substring(details.Length - 3000);
                MessageBox.Show($"修改未完成\r\n\r\n失败步骤：{failedStep}\r\n退出码：{result.ExitCode}\r\n\r\n系统返回：\r\n{details}",
                    "修改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            btnOK.Enabled = true;
            btnOK.Text = "应用修改";
        }

        private void checkMAC_CheckedChanged(object sender, EventArgs e)
        {
            txtMAC.Enabled = checkMAC.Checked;
        }

        private void txtMAC_KeyPress(object sender, KeyPressEventArgs e)
        {
            char c = e.KeyChar;
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F') ||
                         c == (char)Keys.Back;

            if (!isHex)
            {
                e.Handled = true;
            }
        }

        private void txtMAC_TextChanged(object sender, EventArgs e)
        {
            txtMAC.TextChanged -= txtMAC_TextChanged;

            int cursorPosition = txtMAC.SelectionStart;
            string rawText = txtMAC.Text.ToUpper().Replace(":", "");

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < rawText.Length; i++)
            {
                sb.Append(rawText[i]);
                if ((i + 1) % 2 == 0 && (i + 1) < rawText.Length && sb.Length < 17)
                {
                    sb.Append(":");
                }
            }

            txtMAC.Text = sb.ToString();

            txtMAC.SelectionStart = txtMAC.Text.Length;

            txtMAC.TextChanged += txtMAC_TextChanged;
        }

        private void ipMask_TextChanged(object sender, EventArgs e)
        {
            SyncTxtMaskFromIpMask();
        }

        private void txtMask_TextChanged(object sender, EventArgs e)
        {
            if (_suppressMaskSync) return;
            string text = txtMask.Text;
            if (string.IsNullOrEmpty(text)) return;
            if (int.TryParse(text, out int cidr))
            {
                if (cidr < 0 || cidr > 32)
                {
                    _suppressMaskSync = true;
                    txtMask.Text = cidr < 0 ? "0" : "32";
                    txtMask.SelectionStart = txtMask.Text.Length;
                    _suppressMaskSync = false;
                }
            }
            else
            {
                _suppressMaskSync = true;
                txtMask.Text = "";
                _suppressMaskSync = false;
                return;
            }
            SyncIpMaskFromTxtMask();
        }

        private void txtMask_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsDigit(e.KeyChar) && e.KeyChar != (char)Keys.Back)
                e.Handled = true;
        }

        private void ipMask_Leave(object sender, EventArgs e)
        {
            SyncTxtMaskFromIpMask();
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (!_btnSavePending)
            {
                _btnSavePending = true;
                btnSave.Text = "确认写";
                btnSave.Font = new Font(btnSave.Font, FontStyle.Bold);
                btnSave.ForeColor = Color.Gold;
                if (_btnSaveTimer == null)
                {
                    _btnSaveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                    _btnSaveTimer.Tick += BtnSaveTimer_Tick;
                }
                _btnSaveTimer.Stop();
                _btnSaveTimer.Start();
            }
            else
            {
                _btnSaveTimer.Stop();
                _btnSavePending = false;
                SaveNicSettings();
                btnSave.Text = "写成功";
                _btnSaveTimer.Interval = 1000;
                _btnSaveTimer.Tick -= BtnSaveTimer_Tick;
                _btnSaveTimer.Tick += BtnSaveSuccessTimer_Tick;
                _btnSaveTimer.Start();
            }
        }

        private void BtnSaveTimer_Tick(object sender, EventArgs e)
        {
            _btnSaveTimer.Stop();
            _btnSavePending = false;
            btnSave.Text = "写记录";
            btnSave.Font = new Font(btnSave.Font, FontStyle.Regular);
            bool isLight = Global.isThemelight;
            btnSave.ForeColor = isLight ? Color.Black : Color.White;
        }

        private void BtnSaveSuccessTimer_Tick(object sender, EventArgs e)
        {
            _btnSaveTimer.Stop();
            btnSave.Text = "写记录";
            btnSave.Font = new Font(btnSave.Font, FontStyle.Regular);
            bool isLight = Global.isThemelight;
            btnSave.ForeColor = isLight ? Color.Black : Color.White;
            _btnSaveTimer.Tick -= BtnSaveSuccessTimer_Tick;
            _btnSaveTimer.Tick += BtnSaveTimer_Tick;
            _btnSaveTimer.Interval = 2000;
        }

        private void btnRead_Click(object sender, EventArgs e)
        {
            if (!_btnReadPending)
            {
                _btnReadPending = true;
                btnRead.Text = "确认读";
                btnRead.Font = new Font(btnRead.Font, FontStyle.Bold);
                btnRead.ForeColor = Color.Gold;
                if (_btnReadTimer == null)
                {
                    _btnReadTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                    _btnReadTimer.Tick += BtnReadTimer_Tick;
                }
                _btnReadTimer.Stop();
                _btnReadTimer.Start();
            }
            else
            {
                _btnReadTimer.Stop();
                _btnReadPending = false;
                LoadNicSettings();
                BtnReadReset();
            }
        }

        private void BtnReadTimer_Tick(object sender, EventArgs e)
        {
            _btnReadTimer.Stop();
            _btnReadPending = false;
            BtnReadReset();
        }

        private void BtnReadReset()
        {
            btnRead.Text = "读记录";
            btnRead.Font = new Font(btnRead.Font, FontStyle.Regular);
            bool isLight = Global.isThemelight;
            btnRead.ForeColor = isLight ? Color.Black : Color.White;
        }

        private void SaveNicSettings()
        {
            try
            {
                WritePrivateProfileString(IniSection, "DHCP", checkDHCP.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "IP", ipIPV4.Text, IniPath);
                WritePrivateProfileString(IniSection, "Mask", ipMask.Text, IniPath);
                WritePrivateProfileString(IniSection, "Gateway", ipGateway.Text, IniPath);
                WritePrivateProfileString(IniSection, "DNSAuto", checkDNS.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "DNS1", ipDNS1.Text, IniPath);
                WritePrivateProfileString(IniSection, "DNS2", ipDNS2.Text, IniPath);
                WritePrivateProfileString(IniSection, "HopsChecked", checkHops.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "Hops", txtHops.Text, IniPath);
                WritePrivateProfileString(IniSection, "ChangeIPV6", checkChangeIPV6State.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "IPV6State", checkIPV6State.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "DHCPv6", checkDHCPv6.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "DHCPv6DNS", checkDHCPv6DNS.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "SLAAC", checkSLAAC.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "SLAACDNS", checkSLAACDNS.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "IPv6DNSAuto", checkIPv6DNS.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "IPv6DNS1", txtV6DNS1.Text, IniPath);
                WritePrivateProfileString(IniSection, "IPv6DNS2", txtV6DNS2.Text, IniPath);
                WritePrivateProfileString(IniSection, "MACChecked", checkMAC.Checked ? "1" : "0", IniPath);
                WritePrivateProfileString(IniSection, "MAC", txtMAC.Text, IniPath);
                WritePrivateProfileString(IniSection, "CIDRMask", txtMask.Text, IniPath);
            }
            catch { }
        }

        private void LoadNicSettings()
        {
            try
            {
                _suppressMaskSync = true;
                var sb = new StringBuilder(256);
                string val;

                GetPrivateProfileString(IniSection, "DHCP", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkDHCP.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "IP", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) ipIPV4.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "Mask", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) ipMask.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "Gateway", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) ipGateway.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "DNSAuto", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkDNS.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "DNS1", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) ipDNS1.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "DNS2", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) ipDNS2.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "HopsChecked", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkHops.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "Hops", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtHops.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "ChangeIPV6", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkChangeIPV6State.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "IPV6State", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkIPV6State.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "DHCPv6", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkDHCPv6.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "DHCPv6DNS", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkDHCPv6DNS.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "SLAAC", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkSLAAC.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "SLAACDNS", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkSLAACDNS.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "IPv6DNSAuto", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkIPv6DNS.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "IPv6DNS1", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtV6DNS1.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "IPv6DNS2", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtV6DNS2.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "MACChecked", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkMAC.Checked = val == "1";
                sb.Clear();

                GetPrivateProfileString(IniSection, "MAC", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtMAC.Text = val;
                sb.Clear();

                GetPrivateProfileString(IniSection, "CIDRMask", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtMask.Text = val;
                sb.Clear();
            }
            catch { }
            finally
            {
                _suppressMaskSync = false;
            }
        }
    }
}
