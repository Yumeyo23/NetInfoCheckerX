using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class iPerfGUI : Form
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);
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
        private string _tempBatPath = null;

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

            StringBuilder arguments = new StringBuilder();
            arguments.Append("-c ").Append(txtClientIP.Text.Trim());

            string bindNIC = GetSelectedIP(comboClientNIC);
            if (!string.IsNullOrEmpty(bindNIC))
                arguments.Append(" -B ").Append(bindNIC);

            if (!string.IsNullOrEmpty(txtClientPort.Text))
                arguments.Append(" -p ").Append(txtClientPort.Text.Trim());

            if (!chkTCP.Checked) arguments.Append(" -u");

            if (numTime.Value > 0) arguments.Append(" -t ").Append(numTime.Value);

            if (numThread.Value > 1) arguments.Append(" -P ").Append(numThread.Value);

            if (chkWay.Checked) arguments.Append(" -R");

            if (!string.IsNullOrEmpty(txtLimit.Text))
                arguments.Append(" -b ").Append(txtLimit.Text.Trim()).Append("M");

            string iperfArguments = arguments.ToString();

            string direction = chkWay.Checked ? "下载" : "上传";
            string protocol = chkTCP.Checked ? "TCP" : "UDP";
            string threads = numThread.Value.ToString();
            string limit = string.IsNullOrEmpty(txtLimit.Text.Trim()) ? "无" : txtLimit.Text.Trim();
            string time = numTime.Value.ToString();
            string serverIp = txtClientIP.Text.Trim();
            string port = string.IsNullOrEmpty(txtClientPort.Text.Trim()) ? "5201" : txtClientPort.Text.Trim();
            string nicDisplay = string.IsNullOrEmpty(bindNIC) ? "系统默认" : bindNIC;

            string tempBat = Path.Combine(Path.GetTempPath(), $"nicx_iperf_{Environment.TickCount}.cmd");
            string esc = "\x1b";
            string colorOn = $"{esc}[38;2;255;255;0m";
            string colorOff = $"{esc}[0m";

            StringBuilder bat = new StringBuilder();
            bat.AppendLine("@echo off");
            bat.AppendLine("chcp 65001 >nul 2>&1");
            bat.AppendLine($"echo {colorOn}^>^>^>本次iperf运行参数：{colorOff}");
            bat.AppendLine($"echo {colorOn}● 服务器[{serverIp}]  端口[{port}]  使用网卡[{nicDisplay}]{colorOff}");
            bat.AppendLine($"echo {colorOn}方向[{direction}]  协议[{protocol}]  线程[{threads}]  限速[{limit}]Mbps  时长[{time}]秒{colorOff}");
            bat.AppendLine($"echo {colorOn}……………………………………………………………………………………{colorOff}");
            bat.AppendLine($"\"{iperfPath}\" {iperfArguments} -f m");
            bat.AppendLine($"echo {colorOn}……………………………………………………………………………………{colorOff}");
            bat.AppendLine($"echo {colorOn}^>^>^>测试完毕，按回车键关闭{colorOff}");
            bat.AppendLine("set /p dummy=");

            try
            {
                File.WriteAllText(tempBat, bat.ToString(), new UTF8Encoding(false));
                _tempBatPath = tempBat;

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{tempBat}\"",
                    UseShellExecute = true,
                    WorkingDirectory = Application.StartupPath
                };

                using (Process.Start(startInfo)) { }
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

            if (!string.IsNullOrEmpty(txtServerPort.Text))
                arguments.Append(" -p ").Append(txtServerPort.Text.Trim());

            arguments.Append(" -V");

            string iperfArguments = arguments.ToString();
            string finalCmdArguments = $"/k \"\"{iperfPath}\" {iperfArguments}\"";

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = finalCmdArguments,
                    UseShellExecute = true,
                    WorkingDirectory = Application.StartupPath
                };

                using (Process.Start(startInfo)) { }
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
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
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
                    btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
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
            try
            {
                if (!string.IsNullOrEmpty(_tempBatPath) && File.Exists(_tempBatPath))
                {
                    File.Delete(_tempBatPath);
                    _tempBatPath = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[iPerfGUI] 删除临时文件失败: {ex.Message}");
            }
        }
    }
}
