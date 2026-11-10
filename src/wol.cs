using System;
using System.Collections.Generic;
using System.Diagnostics; // 用于运行CMD和记事本
using System.Drawing;
using System.IO;          // 用于文件路径操作
using System.Linq;
using System.Media;       // 用于播放系统提示音
using System.Net;         // 网络相关
using System.Net.NetworkInformation; // 获取网卡信息
using System.Net.Sockets; // UDP发包
using System.Runtime.InteropServices; // 用于调用Windows API (读写ini)
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class wol : Form
    {
        private async Task ApplyWOLThemeAsync()
        {
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoLabels = { lbl, lblNote, lblRadio, lblMac };
            foreach (var c in yumeyoLabels) { if (c != null) c.ForeColor = yumeyoColor; }

            Control[] editControls = { txtReadMac, txtNote, comboRadio, comboMac };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
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

            Control[] buttons = { btnWake, btnWrite, btnRead, btnArp };
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
        }
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);

        string iniPath = Path.Combine(Application.StartupPath, "Wol.ini");
        private bool updatingMacInput;
        private readonly ToolTip macHint;

        public wol()
        {
            InitializeComponent();
            if (components == null) components = new System.ComponentModel.Container();
            macHint = new ToolTip(components);
            comboMac.TextChanged += ComboMac_TextChanged;
            txtReadMac.TextChanged += RecordMac_TextChanged;
        }

        private void wol_Load(object sender, EventArgs e)
        {
            _ = ApplyWOLThemeAsync();

            LoadNetworkInterfaces();

            LoadSavedMacs();
        }

        private void LoadNetworkInterfaces()
        {
            comboRadio.Items.Clear();
            foreach (var ni in NicHelper.GetCandidateAdapters(requireUp: true, preferGateway: true))
            {
                if (!NicHelper.TryGetIPProperties(ni, out IPInterfaceProperties ipProps)) continue;

                foreach (var ip in ipProps.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && NicHelper.IsUsableUnicastAddress(ip))
                    {
                        IPAddress broadcast = GetBroadcastAddress(ip.Address, ip.IPv4Mask);
                        if (broadcast != null)
                        {
                            if (!broadcast.ToString().StartsWith("127."))
                            {
                                comboRadio.Items.Add(broadcast.ToString());
                            }
                        }
                    }
                }
            }

            if (comboRadio.Items.Count > 0)
                comboRadio.SelectedIndex = 0;
            else
                comboRadio.Items.Add("无可用广播地址");
        }

        private IPAddress GetBroadcastAddress(IPAddress address, IPAddress subnetMask)
        {
            if (subnetMask == null) return IPAddress.Broadcast;

            byte[] ipAdressBytes = address.GetAddressBytes();
            byte[] subnetMaskBytes = subnetMask.GetAddressBytes();

            if (ipAdressBytes.Length != subnetMaskBytes.Length) return null;

            byte[] broadcastAddressBytes = new byte[ipAdressBytes.Length];
            for (int i = 0; i < broadcastAddressBytes.Length; i++)
            {
                broadcastAddressBytes[i] = (byte)(ipAdressBytes[i] | (subnetMaskBytes[i] ^ 255));
            }
            return new IPAddress(broadcastAddressBytes);
        }

        private void LoadSavedMacs()
        {
            string currentText = CleanMac(comboMac.Text);

            HashSet<string> macList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lastMac = string.Empty;

            if (File.Exists(iniPath))
            {
                StringBuilder sbLast = new StringBuilder(255);
                GetPrivateProfileString("History", "LastWake", "", sbLast, 255, iniPath);
                lastMac = CleanMac(sbLast.ToString());
                if (IsValidMac(lastMac))
                {
                    macList.Add(lastMac);
                }

                string[] lines = File.ReadAllLines(iniPath);
                bool inRecordSection = false;
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("["))
                    {
                        inRecordSection = trimmed.Equals("[Record]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    int separator = trimmed.IndexOf('=');
                    if (inRecordSection && separator > 0 && !trimmed.StartsWith(";"))
                    {
                        string mac = CleanMac(trimmed.Substring(0, separator));
                        if (IsValidMac(mac)) macList.Add(mac);
                    }
                }
            }

            updatingMacInput = true;
            try
            {
                comboMac.Items.Clear();
                foreach (var mac in macList) comboMac.Items.Add(mac);
                comboMac.Text = string.IsNullOrEmpty(currentText) && IsValidMac(lastMac) ? lastMac : currentText;
            }
            finally { updatingMacInput = false; }
            ComboMac_TextChanged(comboMac, EventArgs.Empty);
        }

        private void ComboMac_TextChanged(object sender, EventArgs e)
        {
            if (updatingMacInput) return;
            updatingMacInput = true;
            try
            {
                int caret = CleanMac(comboMac.Text.Substring(0, comboMac.SelectionStart)).Length;
                string normalized = CleanMac(comboMac.Text);
                if (comboMac.Text != normalized)
                {
                    comboMac.Text = normalized;
                    comboMac.SelectionStart = Math.Min(caret, normalized.Length);
                }
                txtReadMac.Text = normalized;
                UpdateMacDetails(normalized, true);
            }
            finally { updatingMacInput = false; }
        }

        private void RecordMac_TextChanged(object sender, EventArgs e)
        {
            if (updatingMacInput) return;
            updatingMacInput = true;
            try
            {
                int caret = CleanMac(txtReadMac.Text.Substring(0, txtReadMac.SelectionStart)).Length;
                string normalized = CleanMac(txtReadMac.Text);
                if (txtReadMac.Text != normalized)
                {
                    txtReadMac.Text = normalized;
                    txtReadMac.SelectionStart = Math.Min(caret, normalized.Length);
                }
                UpdateMacDetails(normalized, false);
            }
            finally { updatingMacInput = false; }
        }

        private void UpdateMacDetails(string selectedMac, bool updateTargetHint)
        {
            if (!IsValidMac(selectedMac))
            {
                txtNote.Clear();
                const string invalidHint = "请输入 12 位十六进制 MAC 地址，支持冒号、横杠、点和空格分隔。";
                if (updateTargetHint) macHint.SetToolTip(comboMac, invalidHint);
                macHint.SetToolTip(txtReadMac, invalidHint);
                return;
            }
            StringBuilder sb = new StringBuilder(255);
            GetPrivateProfileString("Record", selectedMac, "", sb, 255, iniPath);
            string note = sb.ToString();
            txtNote.Text = note;

            StringBuilder sbLast = new StringBuilder(255);
            GetPrivateProfileString("History", "LastWake", "", sbLast, 255, iniPath);

            string hint = CleanMac(sbLast.ToString()) == selectedMac
                ? "上次唤醒的就是这个" : "MAC 地址有效";
            if (updateTargetHint) macHint.SetToolTip(comboMac, hint);
            macHint.SetToolTip(txtReadMac, hint);
        }

        private void btnWrite_Click(object sender, EventArgs e)
        {
            string inputMac = txtReadMac.Text.Trim();
            string cleanMac = CleanMac(inputMac);

            if (!IsValidMac(cleanMac))
            {
                MessageBox.Show("MAC地址格式好像不对哦. 检查一下？", "提示");
                return;
            }

            StringBuilder sb = new StringBuilder(255);
            GetPrivateProfileString("Record", cleanMac, "IsNew", sb, 255, iniPath);

            if (sb.ToString() != "IsNew")
            {
                DialogResult dr = MessageBox.Show($"MAC地址 {cleanMac} 已经被记录过了. \n是否覆盖之前的备注？", "重复记录", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.No)
                {
                    return;
                }
            }

            WritePrivateProfileString("Record", cleanMac, txtNote.Text, iniPath);

            MessageBox.Show("记录成功喵", "成功");

            LoadSavedMacs();
            comboMac.Text = cleanMac;
        }

        private void btnWake_Click(object sender, EventArgs e)
        {
            string targetMacStr = comboMac.Text.Trim();
            string cleanMac = CleanMac(targetMacStr);
            string broadcastIp = comboRadio.Text.Trim();

            if (!IsValidMac(cleanMac))
            {
                MessageBox.Show("MAC地址格式不对喵", "错误");
                return;
            }

            if (string.IsNullOrEmpty(broadcastIp) || broadcastIp == "无可用广播地址")
            {
                MessageBox.Show("没有广播地址，请检查网卡", "错误");
                return;
            }

            try
            {
                byte[] macBytes = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    macBytes[i] = Convert.ToByte(cleanMac.Substring(i * 2, 2), 16);
                }

                byte[] packet = new byte[6 + 16 * 6];

                for (int i = 0; i < 6; i++) packet[i] = 0xFF;

                for (int i = 0; i < 16; i++)
                {
                    Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
                }

                using (UdpClient client = new UdpClient())
                {
                    client.EnableBroadcast = true;

                    IPAddress destAddr = IPAddress.Parse(broadcastIp);

                    IPEndPoint target7 = new IPEndPoint(destAddr, 7);
                    IPEndPoint target9 = new IPEndPoint(destAddr, 9);

                    client.Send(packet, packet.Length, target7);
                    client.Send(packet, packet.Length, target9);
                }

                SystemSounds.Beep.Play();

                WritePrivateProfileString("History", "LastWake", cleanMac, iniPath);

            }
            catch (Exception ex)
            {
                MessageBox.Show("发送失败了. 错误信息：\n" + ex.Message, "出错了");
            }
            LoadSavedMacs();
        }

        private void btnRead_Click(object sender, EventArgs e)
        {
            if (!File.Exists(iniPath))
            {
                File.Create(iniPath).Close();
            }
            Process.Start("notepad.exe", iniPath);
        }

        private void btnArp_Click(object sender, EventArgs e)
        {
            Process.Start("cmd.exe", "/k arp -a");
        }

        private static bool IsValidMac(string mac)
        {
            return mac != null && mac.Length == 12 && mac.All(Uri.IsHexDigit);
        }

        private static string CleanMac(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return new string(input.Where(c => c != ':' && c != '-' && c != '.' && !char.IsWhiteSpace(c))
                .Select(char.ToUpperInvariant).ToArray());
        }
    }
}
