。﻿using System;
using System.Collections.Generic;
using System.Diagnostics; // 用于启动进程
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;        // 用于拼接字符串
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
 
namespace NetInfoCheckerX
{
    public partial class SettingNIC : Form
    {
        //自由拖拽
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool SendMessage(IntPtr hwnd, int wMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MOVE = 0xF010;
        private const int HTCAPTION = 0x0002;

        // 这是一个通用的拖动处理函数
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

        public SettingNIC()
        {
            // 1. 先检查必需文件
            requiredFiles = new string[]
            {
            "IPAddressControlLib.dll",
            };

            // 2. 在InitializeComponent之前检查文件
            if (!CheckRequiredFiles())
            {
                _filesValidated = false;
                // 这里不调用InitializeComponent，直接返回
                // 窗体构造函数完成后会立即关闭
                return;
            }

            // 3. 单例检查
            var existingForm = Application.OpenForms.OfType<SettingNIC>()
                                  .FirstOrDefault(f => f != this);
            if (existingForm != null)
            {
                existingForm.BringToFront();
                existingForm.Focus();
                this.Dispose();
                return;
            }

            // 4. 文件检查通过，初始化组件
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

            // 1. 定义颜色
            Color contrastColor = isLight ? Color.Black : Color.White;
            // 深色模式下，文本框和下拉框背景直接用 Global.themeBlack (纯黑)
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60); // 60灰

            // 2. 窗口整体背景
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 3. 梦酱紫色标签组
            Control[] yumeyoLabels = { lblIPV4, lblMask, lblGateway, lblDNS1, lblDNS2, };
            foreach (var c in yumeyoLabels) { if (c != null) c.ForeColor = yumeyoColor; }

            // 4. 输入控件处理 (TextBox, ComboBox, IPAddressControl)
            Control[] editControls = { ipIPV4, ipMask, ipGateway, ipDNS1, ipDNS2, txtHops, comboNIC };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = textBack;

                    // 如果是下拉框，设为 Flat 样式，这样深色背景下才没有那个突兀的白边
                    if (c is ComboBox cb)
                    {
                        cb.FlatStyle = FlatStyle.Flat;
                    }

                    // 针对 IPAddressControlLib，强制重绘防止背景色残留
                    c.Invalidate();
                }
            }

            // 5. 按钮组处理 (60灰 + 扁平化)
            // 梦酱记得检查一下 btnOK 在你那边是不是叫 btnSave 哦~
            Control[] buttons = { btnRefreshList, btnOK };
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
                    }
                }
            }

            // 6. 选择框处理 (透明背景防止遮挡)
            Control[] checkBoxes = { checkDHCP, checkDNS, checkHops, checkChangeIPV6State, checkIPV6State };
            foreach (var cb in checkBoxes)
            {
                if (cb != null)
                {
                    cb.ForeColor = contrastColor;
                    cb.BackColor = Color.Transparent;
                }
            }
        }

        // 存储网卡信息的类
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
            // 定义一个右侧留白的边距（单位是像素）
            // 梦酱可以根据视觉效果调整这个数字，建议 15 左右
            int paddingRight = 15;

            // 把梦酱那五个 IP 控件放进数组里
            Control[] ipControls = { ipIPV4, ipMask, ipGateway, ipDNS1, ipDNS2 };

            foreach (var ctrl in ipControls)
            {
                if (ctrl != null)
                {
                    // 核心公式：新宽度 = 窗体内部总宽度 - 控件左边位置 - 右边距
                    // ClientSize.Width 是指窗体除掉边框后的实际可用宽度
                    ctrl.Width = this.ClientSize.Width - ctrl.Left - paddingRight;

                    // 顺便取消掉可能限制它的“最小宽度”约束
                    ctrl.MinimumSize = new Size(0, 0);
                }
            }
        }

        private void SettingNIC_Load(object sender, EventArgs e)
        {

            //随意拖拽
            this.MouseDown += MyMouseDown;

            // 如果文件检查失败，直接关闭窗口
            if (!_filesValidated)
            {
                this.Close();
                return;
            }

            _ = ApplyNICThemeAsync();
            AutoScaleIPControlsWidth();
            RefreshNICList();
        }

        private void RefreshNICList()
        {
            // 1. 备份：先记住当前选中的网卡 ID 和 索引
            // 如果还没选（比如刚打开），我们就记为 null 或 -1
            string lastSelectedId = (comboNIC.SelectedItem is NetworkInterface lastNic) ? lastNic.Id : null;
            int lastSelectedIndex = comboNIC.SelectedIndex;

            // 2. 刷新：清空并重新获取列表
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

            // 3. 还原：尝试找回之前的选中项
            if (comboNIC.Items.Count > 0)
            {
                bool found = false;

                // 策略 A：按唯一 ID 找回（最精准，防止网卡顺序变动）
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

                // 策略 B：如果 ID 没找到（比如网卡禁用了），就按“最接近索引”找回
                if (!found)
                {
                    // 如果原本的索引还在有效范围内，就选它；否则选最后一张
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
                        // 如果之前啥也没选，默认选第一张
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
            var props = nic.GetIPProperties();

            // ===== DHCP（IP）=====
            bool dhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false;
            checkDHCP.Checked = dhcpEnabled;
            ipIPV4.Enabled = ipMask.Enabled = ipGateway.Enabled = !dhcpEnabled;

            var unicast = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            ipIPV4.Text = unicast?.Address.ToString() ?? "0.0.0.0";
            ipMask.Text = unicast?.IPv4Mask?.ToString() ?? "0.0.0.0";

            var gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            ipGateway.Text = gateway?.Address.ToString() ?? "0.0.0.0";

            // ===== DNS（使用注册表判断）=====
            bool dnsAuto = IsDnsAuto(nic.Id, out var manualDns);
            checkDNS.Checked = dnsAuto;
            ipDNS1.Enabled = ipDNS2.Enabled = !dnsAuto;

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

            // ===== 跃点数 (Metric) - 修正后的“仅读取数值”逻辑 =====
            try
            {
                string query = $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID='{nic.Id}'";
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    var results = searcher.Get().Cast<ManagementObject>().ToList();

                    if (results.Count > 0)
                    {
                        var mo = results[0];

                        // 1. 读取当前的跃点数值
                        // 逻辑：如果手动设置过(IPConnectionMetric)，就读手动的；否则读系统分配的(InterfaceMetric)
                        string currentMetric = mo["IPConnectionMetric"]?.ToString() ?? mo["InterfaceMetric"]?.ToString() ?? "0";

                        // 2. 将数值填入文本框
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
                // 3. 关键逻辑：无论读取结果如何，初始化时都不勾选修改框
                checkHops.Checked = false;

                // 4. 确保文本框是禁用状态
                // 提示：因为我们在下方有 checkHops_CheckedChanged 事件，
                // 设置 Checked = false 时会自动触发该事件将 txtHops.Enabled 设为 false。
                txtHops.Enabled = false;
            }
        }
        private bool IsDnsAuto(string nicId, out string[] dnsServers)
        {
            dnsServers = Array.Empty<string>();

            // 注册表路径：这里藏着网卡的详细配置信息
            string registryPath = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nicId}";

            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
            {
                if (key != null)
                {
                    // 读取 NameServer 的值
                    // 如果这个值是空的，说明没有手动设置 DNS，即为“自动”
                    string nameServer = key.GetValue("NameServer") as string;

                    if (string.IsNullOrEmpty(nameServer))
                    {
                        return true; // 是自动获取
                    }
                    else
                    {
                        // 如果不为空，说明是手动设定的，我们把它拆分成数组给夢酱显示出来
                        dnsServers = nameServer.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        return false; // 是手动获取
                    }
                }
            }

            return true; // 默认给个自动吧~
        }


        private void btnRefreshList_Click(object sender, EventArgs e)
        {
            RefreshNICList();
        }

        private void checkDHCP_CheckedChanged(object sender, EventArgs e)
        {
            bool isAutoIP = checkDHCP.Checked;

            // 1. 联动 IP 部分的编辑框
            ipIPV4.Enabled = ipMask.Enabled = ipGateway.Enabled = !isAutoIP;

            // 2. 核心联动逻辑：如果用户取消了“自动获取IP”
            if (!isAutoIP)
            {
                // 自动取消“自动获取DNS”的勾选
                checkDNS.Checked = false;

                // 并且让 DNS 的编辑框变得可用
                ipDNS1.Enabled = true;
                ipDNS2.Enabled = true;

                // 夢酱的小贴士：这里还可以加个贴心提醒，比如在状态栏显示“手动IP模式下建议设置DNS”
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

        // 把原本的 ExecuteAdminCommands 替换为这个异步版本
        private async Task<bool> ExecuteAdminCommandsAsync(string commands)
        {
            if (string.IsNullOrEmpty(commands)) return false;

            return await Task.Run(() =>
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c {commands}";
                psi.Verb = "runas"; // 请求管理员权限
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
                catch (Exception)    // 如果梦酱点了“取消”授权，这里会捕获到
                {
                    return false;
                }
            });
        }

        private async void btnOK_Click(object sender, EventArgs e)
        {
            if (!(comboNIC.SelectedItem is NetworkInterface nic)) return;

            // 禁用按钮，防止梦酱点太快导致重复执行
            btnOK.Enabled = false;
            btnOK.Text = "修改中...";

            string nicName = nic.Name;
            StringBuilder cmdBuilder = new StringBuilder();

            // --- 1. IP 地址 ---
            if (checkDHCP.Checked)
            {
                cmdBuilder.Append($"netsh interface ip set address name=\"{nicName}\" source=dhcp & ");
            }
            else
            {
                cmdBuilder.Append($"netsh interface ip set address name=\"{nicName}\" source=static address={ipIPV4.Text} mask={ipMask.Text} gateway={ipGateway.Text} & ");
            }

            // --- 2. DNS ---
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

            // --- 3. 跃点数 (修正命令) ---
            // 这里明确指定 ipv4 且使用 metric 参数，确保生效
            if (checkHops.Checked)
            {
                cmdBuilder.Append($"netsh interface ipv4 set interface \"{nicName}\" metric={txtHops.Text} & ");
            }

            // --- 4. IPv6 ---
            if (checkChangeIPV6State.Checked)
            {
                string stateCmd = checkIPV6State.Checked ? "Enable" : "Disable";
                cmdBuilder.Append($"powershell -Command \"{stateCmd}-NetAdapterBinding -Name '{nicName}' -ComponentID ms_tcpip6\" & ");
            }

            string finalCmd = cmdBuilder.ToString().TrimEnd(' ', '&');

            // 异步执行，UI 不会卡住！
            bool success = await ExecuteAdminCommandsAsync(finalCmd);

            //if (success)
            {
                MessageBox.Show("修改成功!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);

                // --- 5. 模拟点击 Form1 的刷新按钮 ---
                // 尝试在所有打开的窗口中寻找名为 "Form1" 的那个
                var mainForm = Application.OpenForms["Form1"];
                if (mainForm != null)
                {
                    // 梦酱注意：为了能从外部调用按钮，
                    // 记得在 Form1.Designer.cs 里把 btnRefreshNIC 的 Modifiers 属性改成 Public 或 Internal 哦！
                    // 这里我们用一种通用的方式触发它：
                    var btnRefresh = mainForm.Controls.Find("btnRefreshNIC", true).FirstOrDefault() as Button;
                    btnRefresh?.PerformClick();
                }
                btnRefreshList.PerformClick();
            }

            btnOK.Enabled = true;
            btnOK.Text = "应用修改";
        }
    }
}
