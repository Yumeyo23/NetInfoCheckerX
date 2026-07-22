using System;
using System.Collections.Generic;
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
        private readonly string[] requiredFiles;
        private bool _filesValidated = false;
        private bool _suppressMaskSync = false;
        private System.Windows.Forms.Timer _btnSaveTimer;
        private System.Windows.Forms.Timer _btnReadTimer;
        private bool _btnSavePending = false;
        private bool _btnReadPending = false;

        public SettingNIC()
        {
            requiredFiles = new string[]
            {
            "IPAddressControlLib.dll",
            };

            if (!CheckRequiredFiles())
            {
                _filesValidated = false;
                return;
            }

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
            _filesValidated = true;
        }
        private bool CheckRequiredFiles()
        {
            try
            {
                string appPath = Application.StartupPath;
                List<string> missingFiles = new List<string>();

                foreach (string file in requiredFiles)
                {
                    string filePath = Path.Combine(appPath, file);
                    if (!File.Exists(filePath))
                    {
                        missingFiles.Add(file);
                    }
                }

                if (missingFiles.Count > 0)
                {
                    string message = $"缺少运行修改本机网卡必要的文件：\n{string.Join("\n", missingFiles)}\n\n建议重新打开/解压查询器X/检查杀毒软件喵。";
                    MessageBox.Show(message, "文件缺失了",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查文件时出错：{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
        private async Task ApplyNICThemeAsync()
        {
            //await Task.Yield();
            bool isLight = Global.isThemelight;

            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoLabels = { lblIPV4, lblMask, lblGateway, lblDNS1, lblDNS2, };
            foreach (var c in yumeyoLabels) { if (c != null) c.ForeColor = yumeyoColor; }

            Control[] editControls = { ipIPV4, ipMask, ipGateway, ipDNS1, ipDNS2, txtHops, comboNIC, txtMAC, txtMask };
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

                    // 针对 IPAddressControlLib，强制重绘防止背景色残留
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
                        btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                    }
                }
            }

            Control[] checkBoxes = { checkDHCP, checkDNS, checkHops, checkChangeIPV6State, checkIPV6State, checkMAC };
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

        /// <summary>
        /// 自动调整 IP 地址输入框的宽度，适配高 DPI 屏幕
        /// </summary>
        private void AutoScaleIPControlsWidth()
        {
            int paddingRight = 12;

            Control[] ipControls = { ipIPV4, ipMask, ipGateway, ipDNS1, ipDNS2 };

            foreach (var ctrl in ipControls)
            {
                if (ctrl != null)
                {
                    ctrl.Width = this.ClientSize.Width - ctrl.Left - paddingRight;
                    ctrl.MinimumSize = new Size(0, 0);
                }
            }
        }

        private void SettingNIC_Load(object sender, EventArgs e)
        {

            this.MouseDown += MyMouseDown;

            if (!_filesValidated)
            {
                this.Close();
                return;
            }

            _ = ApplyNICThemeAsync();
            AutoScaleIPControlsWidth();
            RefreshNICList();

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

        private void RefreshNICList()
        {
            string lastSelectedId = (comboNIC.SelectedItem is NetworkInterface lastNic) ? lastNic.Id : null;
            int lastSelectedIndex = comboNIC.SelectedIndex;

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

                if (!string.IsNullOrEmpty(lastSelectedId))
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
            if (!NicHelper.TryGetIPProperties(nic, out IPInterfaceProperties props))
            {
                checkDHCP.Checked = false;
                ipIPV4.Text = ipMask.Text = ipGateway.Text = "0.0.0.0";
                SyncTxtMaskFromIpMask();
                checkDNS.Checked = true;
                ipDNS1.Text = ipDNS2.Text = "0.0.0.0";
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
        }

        private void checkChangeIPV6State_CheckedChanged(object sender, EventArgs e)
        {
            if (checkChangeIPV6State.Checked == true)
            {
                checkIPV6State.Enabled = true;
            }
            else
            {
                checkIPV6State.Enabled = false;
            }
        }

        private async Task<bool> ExecuteAdminCommandsAsync(string commands)
        {
            if (string.IsNullOrEmpty(commands)) return false;

            return await Task.Run(() =>
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c {commands}";
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;

                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        return p.ExitCode == 0;
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            });
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

            btnOK.Enabled = false;
            btnOK.Text = "修改中...";

            string nicName = nic.Name;
            StringBuilder cmdBuilder = new StringBuilder();

            if (checkDHCP.Checked)
            {
                cmdBuilder.Append($"netsh interface ip set address name=\"{nicName}\" source=dhcp & ");
            }
            else
            {
                cmdBuilder.Append($"netsh interface ip set address name=\"{nicName}\" source=static address={ipIPV4.Text} mask={ipMask.Text} gateway={ipGateway.Text} & ");
            }

            if (checkDNS.Checked)
            {
                cmdBuilder.Append($"netsh interface ip set dns name=\"{nicName}\" source=dhcp & ");
            }
            else
            {
                cmdBuilder.Append($"netsh interface ip set dns name=\"{nicName}\" source=static address={ipDNS1.Text} register=primary & ");
                if (!string.IsNullOrEmpty(ipDNS2.Text) && ipDNS2.Text != "0.0.0.0")
                {
                    cmdBuilder.Append($"netsh interface ip add dns name=\"{nicName}\" address={ipDNS2.Text} index=2 & ");
                }
            }

            if (checkHops.Checked)
            {
                cmdBuilder.Append($"netsh interface ipv4 set interface \"{nicName}\" metric={txtHops.Text} & ");
            }

            if (checkChangeIPV6State.Checked)
            {
                string stateCmd = checkIPV6State.Checked ? "Enable" : "Disable";
                cmdBuilder.Append($"powershell -Command \"{stateCmd}-NetAdapterBinding -Name '{nicName}' -ComponentID ms_tcpip6\" & ");
            }

            if (checkMAC.Checked)
            {
                string regPath = GetRegistryPath(nic.Id);
                if (!string.IsNullOrEmpty(regPath))
                {
                    string newMac = txtMAC.Text.Trim().Replace("-", "").Replace(":", "");

                    if (string.IsNullOrEmpty(newMac))
                    {
                        cmdBuilder.Append($"reg delete \"HKEY_LOCAL_MACHINE\\{regPath}\" /v \"NetworkAddress\" /f & ");
                    }
                    else
                    {
                        cmdBuilder.Append($"reg add \"HKEY_LOCAL_MACHINE\\{regPath}\" /v \"NetworkAddress\" /t REG_SZ /d {newMac} /f & ");
                    }

                    cmdBuilder.Append($"powershell -Command \"Restart-NetAdapter -Name '{nicName}'\" & ");
                }
            }

            string finalCmd = cmdBuilder.ToString().TrimEnd(' ', '&');

            bool success = await ExecuteAdminCommandsAsync(finalCmd);

            {
                MessageBox.Show("修改成功!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await Task.Delay(100);
                var mainForm = Application.OpenForms["Form1"];
                if (mainForm != null)
                {
                    var btnRefresh = mainForm.Controls.Find("btnRefreshNIC", true).FirstOrDefault() as Button;
                    btnRefresh?.PerformClick();
                }
                btnRefreshList.PerformClick();

                var updatedNic = NetworkInterface.GetAllNetworkInterfaces()
                                    .FirstOrDefault(n => n.Id == nic.Id);

                if (updatedNic != null)
                {
                    LoadNicInfo(updatedNic);
                }
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
