using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class iPerfGUI : Form
    {
        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);
        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "iPerfGUI";

        private void SaveSettings()
        {
            try
            {
                WritePrivateProfileString(IniSection, "ClientIP", txtClientIP.Text, IniPath);
                WritePrivateProfileString(IniSection, "ClientPort", txtClientPort.Text, IniPath);
                WritePrivateProfileString(IniSection, "ServerPort", txtServerPort.Text, IniPath);
                WritePrivateProfileString(IniSection, "Limit", txtLimit.Text, IniPath);
                WritePrivateProfileString(IniSection, "TCP", chkTCP.Checked.ToString().ToLower(), IniPath);
                WritePrivateProfileString(IniSection, "Way", chkWay.Checked.ToString().ToLower(), IniPath);
                WritePrivateProfileString(IniSection, "Top", chkTop.Checked.ToString().ToLower(), IniPath);
                WritePrivateProfileString(IniSection, "Time", numTime.Value.ToString(), IniPath);
                WritePrivateProfileString(IniSection, "Thread", numThread.Value.ToString(), IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var sb = new StringBuilder(256);
                string val;
                GetPrivateProfileString(IniSection, "ClientIP", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtClientIP.Text = val;
                GetPrivateProfileString(IniSection, "ClientPort", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtClientPort.Text = val;
                GetPrivateProfileString(IniSection, "ServerPort", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtServerPort.Text = val;
                GetPrivateProfileString(IniSection, "Limit", "", sb, sb.Capacity, IniPath);
                txtLimit.Text = sb.ToString(); // Limit can legitimately be empty
                GetPrivateProfileString(IniSection, "TCP", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) chkTCP.Checked = val.ToLower() == "true";
                GetPrivateProfileString(IniSection, "Way", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) chkWay.Checked = val.ToLower() == "true";
                GetPrivateProfileString(IniSection, "Top", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) chkTop.Checked = val.ToLower() == "true";
                GetPrivateProfileString(IniSection, "Time", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString()) && decimal.TryParse(val, out decimal t)) numTime.Value = t;
                GetPrivateProfileString(IniSection, "Thread", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString()) && decimal.TryParse(val, out decimal n)) numThread.Value = n;
            }
            catch { }
        }
        public iPerfGUI()
        {
            string appPath = Application.StartupPath;
            var missing = new List<string>();
            foreach (var f in new[] { "cygwin1.dll", "iperf3.exe" })
            {
                if (!File.Exists(Path.Combine(appPath, f))) missing.Add(f);
            }

            if (missing.Count > 0)
            {
                MessageBox.Show($"缺少运行iPerfGUI必要的文件：\n{string.Join("\n", missing)}\n建议重新打开/解压查询器X/检查杀毒软件喵。",
                                "文件缺失了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                throw new FileNotFoundException("缺少 iPerf 必要组件");
            }

            InitializeComponent();
            this.FormClosing += iPerfGUI_FormClosing;
        }

        private void btnClientStart_Click(object sender, EventArgs e)
        {
            EnsureClientNICValid();

            string iperfPath = Path.Combine(Application.StartupPath, "iperf3.exe");
            if (!File.Exists(iperfPath))
            {
                MessageBox.Show("找不到 iperf3.exe ", "启动错误了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string serverIp = txtClientIP.Text.Trim();
            if (!IsValidHost(serverIp))
            {
                MessageBox.Show("服务器地址格式无效。", "启动错误了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtClientIP.Focus();
                return;
            }

            if (!TryGetPort(txtClientPort.Text, 5201, out int clientPort))
            {
                MessageBox.Show("客户端端口必须是 1 到 65535 之间的整数。", "启动错误了",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtClientPort.Focus();
                return;
            }

            string normalizedLimit = null;
            if (!string.IsNullOrWhiteSpace(txtLimit.Text))
            {
                if (!decimal.TryParse(txtLimit.Text.Trim(), NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out decimal limitValue) || limitValue <= 0)
                {
                    MessageBox.Show("限速必须是大于 0 的数字，单位为 Mbps。", "启动错误了",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtLimit.Focus();
                    return;
                }

                normalizedLimit = limitValue.ToString(CultureInfo.InvariantCulture);
            }

            StringBuilder arguments = new StringBuilder();
            arguments.Append("-c ").Append(serverIp);

            string bindNIC = GetSelectedIP(comboClientNIC);
            if (!string.IsNullOrEmpty(bindNIC))
                arguments.Append(" -B ").Append(bindNIC);

            arguments.Append(" -p ").Append(clientPort);

            if (!chkTCP.Checked) arguments.Append(" -u");

            if (numTime.Value > 0)
                arguments.Append(" -t ").Append(numTime.Value.ToString(CultureInfo.InvariantCulture));

            if (numThread.Value > 1)
                arguments.Append(" -P ").Append(numThread.Value.ToString(CultureInfo.InvariantCulture));

            if (chkWay.Checked) arguments.Append(" -R");

            if (!string.IsNullOrEmpty(normalizedLimit))
                arguments.Append(" -b ").Append(normalizedLimit).Append("M");

            string iperfArguments = arguments.ToString();

            string direction = chkWay.Checked ? "下载" : "上传";
            string protocol = chkTCP.Checked ? "TCP" : "UDP";
            string threads = numThread.Value.ToString(CultureInfo.InvariantCulture);
            string limit = string.IsNullOrEmpty(normalizedLimit) ? "无" : normalizedLimit;
            string time = numTime.Value.ToString(CultureInfo.InvariantCulture);
            string port = clientPort.ToString(CultureInfo.InvariantCulture);
            string nicDisplay = string.IsNullOrEmpty(bindNIC) ? "系统默认" : bindNIC;

            string command = string.Join(" & ", new[]
            {
                "chcp 65001 >nul 2>&1",
                "echo ^>^>^>本次iperf运行参数：",
                $"echo ● 服务器[{serverIp}]  端口[{port}]  使用网卡[{nicDisplay}]",
                $"echo 方向[{direction}]  协议[{protocol}]  线程[{threads}]  限速[{limit}]Mbps  时长[{time}]秒",
                "echo ……………………………………………………………………………………",
                $"\"{iperfPath}\" {iperfArguments} -f m",
                "echo ……………………………………………………………………………………",
                "echo ^>^>^>测试完毕，按回车键关闭",
                "pause >nul"
            });

            try
            {
                StartCommandWindow(command, keepOpen: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动 IPERF 失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnServerStart_Click(object sender, EventArgs e)
        {
            EnsureServerNICValid();

            string iperfPath = Path.Combine(Application.StartupPath, "iperf3.exe");

            if (!File.Exists(iperfPath))
            {
                MessageBox.Show("找不到 iperf3.exe ", "启动错误了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            StringBuilder arguments = new StringBuilder();
            arguments.Append("-s");

            string bindIP = GetSelectedIP(comboServerIP);
            if (!string.IsNullOrEmpty(bindIP))
                arguments.Append(" -B ").Append(bindIP);

            if (!TryGetPort(txtServerPort.Text, 5201, out int serverPort))
            {
                MessageBox.Show("服务器端口必须是 1 到 65535 之间的整数。", "启动错误了",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtServerPort.Focus();
                return;
            }

            arguments.Append(" -p ").Append(serverPort);

            arguments.Append(" -V");

            string iperfArguments = arguments.ToString();
            try
            {
                StartCommandWindow($"\"{iperfPath}\" {iperfArguments}", keepOpen: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动服务器失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void chkTCP_CheckedChanged(object sender, EventArgs e)
        {
            chkTCP.Text = chkTCP.Checked ? "TCP" : "UDP";
        }

        private static bool TryGetPort(string text, int defaultPort, out int port)
        {
            string value = text?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                port = defaultPort;
                return true;
            }

            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                   port >= 1 && port <= 65535;
        }

        private static bool IsValidHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
            if (IPAddress.TryParse(host, out _)) return true;

            foreach (char c in host)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    return false;
            }

            return true;
        }

        private static void StartCommandWindow(string command, bool keepOpen)
        {
            string mode = keepOpen ? "/k" : "/c";
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s {mode} \"{command}\"",
                UseShellExecute = true,
                WorkingDirectory = Application.StartupPath
            };

            using (Process.Start(startInfo)) { }
        }

        private void chkWay_CheckedChanged(object sender, EventArgs e)
        {
            chkWay.Text = chkWay.Checked ? "下载" : "上传";
        }

        private void chkTop_CheckedChanged(object sender, EventArgs e)
        {
            this.TopMost = chkTop.Checked;
            chkTop.Text = chkTop.Checked ? "已顶" : "置顶";
        }

        private async Task ApplyIPerfThemeAsync()
        {
            if (this.IsDisposed || this.Disposing) return;

            bool isLight = Global.isThemelight;
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Color btnDarkBack = Color.FromArgb(60, 60, 60);
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color controlBack = isLight ? Global.colorWhite : Global.themeBlack;

            Action<Control, Color, Color> setStyle = (ctrl, fore, back) =>
            {
                if (ctrl != null)
                {
                    ctrl.ForeColor = fore;
                    if (back != Color.Empty) ctrl.BackColor = back;
                }
            };

            setStyle(lblServerTitle, yumeyoColor, Color.Empty);
            setStyle(lblClientTitle, yumeyoColor, Color.Empty);

            Control[] labels = { lblServerIP, lblClient, lblClientNIC, lblTime, lblThread, lblLimit };
            foreach (var c in labels) setStyle(c, contrastColor, Color.Empty);

            Control[] inputs = { comboServerIP, txtServerPort, txtClientIP, txtClientPort, txtLimit, numTime, numThread, chkWay, chkTCP, chkTop, comboClientNIC };
            foreach (var c in inputs)
            {
                setStyle(c, contrastColor, (c is CheckBox) ? Color.Transparent : controlBack);

                if (c is ComboBox cb)
                {
                    if (isLight)
                        cb.FlatStyle = FlatStyle.Standard;
                    else
                        cb.FlatStyle = FlatStyle.Flat;
                }
            }

            Button[] buttons = { btnServerStart, btnClientStart };
            foreach (var btn in buttons)
            {
                if (btn == null) continue;
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

        private void iPerfGUI_Load(object sender, EventArgs e)
        {
            InitNICList();
            LoadSettings();
            _ = ApplyIPerfThemeAsync();
            this.MinimumSize = this.Size;
        }

        private void txtClientIP_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) btnClientStart_Click(sender, e);
        }

        // ===================== 网卡列表与纠错机制 =====================

        /// <summary>
        /// 从 ComboBox 选中项中提取纯 IP 地址。默认选项返回 null。
        /// </summary>
        private string GetSelectedIP(ComboBox combo)
        {
            string text = combo.Text.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (text == "(Any)" || text == "(系统默认)") return null;

            // "192.168.1.5 (以太网)" → "192.168.1.5"
            if (text.Contains(" ")) text = text.Split(' ')[0];

            if (IPAddress.TryParse(text, out _)) return text;
            return null;
        }

        /// <summary>
        /// 加载本机物理网卡 IP 列表到两个 ComboBox
        /// </summary>
        private void InitNICList()
        {
            string serverSelected = comboServerIP.Text;
            string clientSelected = comboClientNIC.Text;

            comboServerIP.Items.Clear();
            comboServerIP.Items.Add("(Any)");

            comboClientNIC.Items.Clear();
            comboClientNIC.Items.Add("(系统默认)");

            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                {
                    comboServerIP.Items.Add(nicAddress.DisplayText);
                    comboClientNIC.Items.Add(nicAddress.DisplayText);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("初始化网卡失败: " + ex.Message, "获取失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }

            // 恢复之前选中的项，找不到则保持默认
            RestoreComboSelection(comboServerIP, serverSelected);
            RestoreComboSelection(comboClientNIC, clientSelected);
        }

        private void RestoreComboSelection(ComboBox combo, string previousText)
        {
            if (string.IsNullOrEmpty(previousText))
            {
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                return;
            }

            foreach (var item in combo.Items)
            {
                if (item.ToString() == previousText)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void EnsureServerNICValid()
        {
            string selectedText = comboServerIP.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any")) return;

            InitNICList();
        }

        private void EnsureClientNICValid()
        {
            string selectedText = comboClientNIC.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("系统默认")) return;

            InitNICList();
        }

        private void iPerfGUI_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
        }
    }
}
