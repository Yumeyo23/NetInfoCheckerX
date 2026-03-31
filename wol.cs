using System;
using System.Collections.Generic;
using System.Diagnostics; // 用于运行CMD和记事本
using System.Drawing;
using System.IO;          // 用于文件路径操作
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
            // 异步等待，确保 UI 线程准备就绪
            //await Task.Yield();

            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            // 文本框/下拉框背景：深色下纯黑，浅色下全局色
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60); // 梦酱专属 60 灰

            // 1. 窗口整体背景颜色
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 2. 梦酱专属紫色标签组 (lbl, lblNote, lblVer)
            // 提示：如果设计器里还有 等，也请填入数组
            Control[] yumeyoLabels = { lbl, lblNote, lblRadio, lblMac };
            foreach (var c in yumeyoLabels) { if (c != null) c.ForeColor = yumeyoColor; }

            // 3. 文本框与下拉框处理
            Control[] editControls = { txtReadMac, txtNote, comboRadio, comboMac };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = textBack;

                    // --- 核心优化：下拉框智能样式 ---
                    if (c is ComboBox cb)
                    {
                        if (isLight)
                        {
                            cb.FlatStyle = FlatStyle.Standard; // 浅色恢复原生感
                        }
                        else
                        {
                            cb.FlatStyle = FlatStyle.Flat;     // 深色扁平化去白边
                        }
                    }
                }
            }

            // --- 4. 按钮组深度美化 (智能切换样式) ---
            Control[] buttons = { btnWake, btnWrite, btnRead, btnArp };
            foreach (var b in buttons)
            {
                if (b != null && b is Button btn)
                {
                    if (isLight)
                    {
                        // 浅色模式：原生 3D 效果
                        btn.ForeColor = Color.Black;
                        btn.BackColor = SystemColors.Control;
                        btn.UseVisualStyleBackColor = true;
                        btn.FlatStyle = FlatStyle.Standard;
                    }
                    else
                    {
                        // 深色模式：60 灰扁平风
                        btn.ForeColor = Color.White;
                        btn.BackColor = btnDarkBack;
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.FlatAppearance.BorderColor = Color.DimGray;
                        // 鼠标悬停时亮起梦酱紫，增强互动感喵！
                        btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                    }
                }
            }
        }
        // === 这里是读取INI文件需要的声明，就像易语言引用DLL命令一样 ===
        [DllImport("kernel32")]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);
        [DllImport("kernel32")]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);

        // 定义ini文件的路径：在程序运行目录下的 Wol.ini
        string iniPath = Path.Combine(Application.StartupPath, "Wol.ini");

        public wol()
        {
            InitializeComponent();
        }

        // === 窗口载入事件 (Form_Load) ===
        private void wol_Load(object sender, EventArgs e)
        {
            _ = ApplyWOLThemeAsync();

            // 1. 加载网卡广播地址
            LoadNetworkInterfaces();

            // 2. 加载保存的MAC地址记录
            LoadSavedMacs();

            // 3. 关联事件：当用户选择MAC下拉框时，自动填入文本框
            // 注意：请确保设计器里 comboMac 的 SelectedIndexChanged 事件绑定到了这个函数
            comboMac.SelectedIndexChanged += ComboMac_SelectedIndexChanged;
        }

        // === 功能1：获取本机所有网卡的广播地址 ===
        private void LoadNetworkInterfaces()
        {
            comboRadio.Items.Clear();
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in interfaces)
            {
                // --- 过滤逻辑开始 ---
                // 1. 必须是正在运行的网卡
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                // 2. 排除环回网卡 (就是夢酱发现的 127 开头的那个源头)
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                // 3. 排除隧道和未知类型的接口
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Unknown) continue;
                // --- 过滤逻辑结束 ---

                var ipProps = ni.GetIPProperties();
                foreach (var ip in ipProps.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        IPAddress broadcast = GetBroadcastAddress(ip.Address, ip.IPv4Mask);
                        if (broadcast != null)
                        {
                            // 检查是否已经是 127 开头，双重保险
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

        // 计算广播地址的小函数
        private IPAddress GetBroadcastAddress(IPAddress address, IPAddress subnetMask)
        {
            if (subnetMask == null) return IPAddress.Broadcast; // 默认 255.255.255.255

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

        // === 功能2：加载INI记录到下拉框 ===
        private void LoadSavedMacs()
        {
            // 先保存当前用户可能正在输入的文本，防止刷新后消失
            string currentText = comboMac.Text;

            // 使用 HashSet 可以自动去重，非常方便哦！
            HashSet<string> macList = new HashSet<string>();

            if (File.Exists(iniPath))
            {
                // 1. 读取“上次唤醒”的地址
                StringBuilder sbLast = new StringBuilder(255);
                GetPrivateProfileString("History", "LastWake", "", sbLast, 255, iniPath);
                string lastMac = sbLast.ToString().Trim();
                if (!string.IsNullOrEmpty(lastMac))
                {
                    macList.Add(lastMac);
                }

                // 2. 读取所有“记录”中的地址
                // 这里沿用之前的逻辑，遍历文件内容
                string[] lines = File.ReadAllLines(iniPath);
                foreach (string line in lines)
                {
                    if (line.Contains("=") && !line.StartsWith("[") && !line.StartsWith(";"))
                    {
                        string mac = line.Split('=')[0].Trim();
                        if (mac.Length >= 12) macList.Add(mac.ToUpper());
                    }
                }
            }

            // 3. 填充到下拉框
            comboMac.Items.Clear();
            foreach (var mac in macList)
            {
                comboMac.Items.Add(mac);
            }

            // 还原用户之前在输入的文本
            comboMac.Text = currentText;
        }

        // === 事件：选中MAC下拉框 ===
        private void ComboMac_SelectedIndexChanged(object sender, EventArgs e)
        {
            string selectedMac = comboMac.Text;

            // 1. 填入 txtReadMac
            txtReadMac.Text = selectedMac;

            // 2. 读取备注填入 txtNote
            StringBuilder sb = new StringBuilder(255);
            GetPrivateProfileString("Record", selectedMac, "", sb, 255, iniPath);
            string note = sb.ToString();

            // 3. 检查是否是上次唤醒的地址
            StringBuilder sbLast = new StringBuilder(255);
            GetPrivateProfileString("History", "LastWake", "", sbLast, 255, iniPath);

            if (sbLast.ToString() == selectedMac)
            {
                txtNote.Text = "上次唤醒的就是这个" + note;
            }
            else
            {
                txtNote.Text = note;
            }
        }

        // === 按钮：写入记录 (btnWrite) ===
        private void btnWrite_Click(object sender, EventArgs e)
        {
            // 获取输入框里的MAC，并清洗格式
            string inputMac = txtReadMac.Text.Trim();
            string cleanMac = CleanMac(inputMac);

            if (cleanMac.Length != 12)
            {
                MessageBox.Show("MAC地址格式好像不对哦. 检查一下？", "提示");
                return;
            }

            // 检查是否已存在
            StringBuilder sb = new StringBuilder(255);
            GetPrivateProfileString("Record", cleanMac, "IsNew", sb, 255, iniPath);

            if (sb.ToString() != "IsNew")
            {
                // 已经存在记录
                DialogResult dr = MessageBox.Show($"MAC地址 {cleanMac} 已经被记录过了. \n是否覆盖之前的备注？", "重复记录", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.No)
                {
                    return;
                }
            }

            // 写入INI
            // 格式： [Record]  112233445566 = 备注名
            WritePrivateProfileString("Record", cleanMac, txtNote.Text, iniPath);

            MessageBox.Show("记录成功喵", "成功");

            // 刷新一下列表
            LoadSavedMacs();
        }

        // === 按钮：唤醒 (btnWake) ===
        private void btnWake_Click(object sender, EventArgs e)
        {
            string targetMacStr = comboMac.Text.Trim();
            string cleanMac = CleanMac(targetMacStr);
            string broadcastIp = comboRadio.Text.Trim();

            if (cleanMac.Length != 12)
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
                // 1. 构造魔术包 (Magic Packet)
                byte[] macBytes = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    // 每2个字符转成一个byte
                    macBytes[i] = Convert.ToByte(cleanMac.Substring(i * 2, 2), 16);
                }

                // 魔术包格式：6个FF，然后重复16次目标MAC地址
                byte[] packet = new byte[6 + 16 * 6];

                // 前6个字节是 FF
                for (int i = 0; i < 6; i++) packet[i] = 0xFF;

                // 后面重复16次MAC
                for (int i = 0; i < 16; i++)
                {
                    Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
                }

                // 2. 发送UDP包（双端口并发模式）
                using (UdpClient client = new UdpClient())
                {
                    // 设置允许发送广播包（有的系统环境需要显式开启）
                    client.EnableBroadcast = true;

                    // 解析广播地址
                    IPAddress destAddr = IPAddress.Parse(broadcastIp);

                    // 准备两个目标：端口 7 和 端口 9
                    IPEndPoint target7 = new IPEndPoint(destAddr, 7);
                    IPEndPoint target9 = new IPEndPoint(destAddr, 9);

                    // 依次发送，对于网卡来说，这几乎是瞬间同时收到的
                    client.Send(packet, packet.Length, target7);
                    client.Send(packet, packet.Length, target9);
                }

                // 3. 成功后鸣叫一声
                SystemSounds.Beep.Play();

                // 4. 记录“上次唤醒”
                WritePrivateProfileString("History", "LastWake", cleanMac, iniPath);

            }
            catch (Exception ex)
            {
                MessageBox.Show("发送失败了. 错误信息：\n" + ex.Message, "出错了");
            }
            LoadSavedMacs();
        }

        // === 按钮：打开记录文件 (btnRead) ===
        private void btnRead_Click(object sender, EventArgs e)
        {
            if (!File.Exists(iniPath))
            {
                // 如果文件不存在，先创建一个空的，免得记事本报错
                File.Create(iniPath).Close();
            }
            Process.Start("notepad.exe", iniPath);
        }

        // === 按钮：ARP查询 (btnArp) ===
        private void btnArp_Click(object sender, EventArgs e)
        {
            // /k 表示执行完命令后不关闭窗口，这样能看到结果
            Process.Start("cmd.exe", "/k arp -a");
        }

        // === 辅助工具：清洗MAC地址 ===
        // 把 AA:BB-CC 这种变成 AABBCC
        private string CleanMac(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            // 替换掉冒号、横杠、空格
            string result = input.Replace(":", "").Replace("-", "").Replace(" ", "");
            return result.ToUpper(); // 统一转大写
        }
    }
}
