using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
 
namespace NetInfoCheckerX
{
    public partial class HWInfoWMI : Form
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
        public HWInfoWMI()
        {
            InitializeComponent();

            // 定义需要检查的文件列表
            requiredFiles = new string[]
            {
            "TbToolsHWInfo1.exe",
            "硬件检测引擎.dll",
            };

            // 检查是否已有实例
            var existingForm = Application.OpenForms.OfType<HWInfoWMI>()
                                  .FirstOrDefault(f => f != this);

            if (existingForm != null)
            {
                existingForm.BringToFront();
                existingForm.Focus();
                this.Dispose(); // 释放自己
            }
            else
            {
                base.Show();
            }
        }
        private async Task ApplyHWInfoThemeAsync()
        {
            // 稍微延迟一下下，确保窗体句柄已经准备好，不卡主线程
            //await Task.Yield();

            bool isLight = Global.isThemelight;

            // 1. 窗口背景颜色
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 2. 梦酱专属紫色组 (Yumeyo vs Yumeyo2)
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Label[] yumeyoLabels = { lblPCName, lblExeName };
            foreach (var lbl in yumeyoLabels) { if (lbl != null) lbl.ForeColor = yumeyoColor; }

            // 3. 黑白对比组 (普通文字颜色)
            Color contrastColor = isLight ? Color.Black : Color.White;

            // 标签类
            Label[] contrastLabels = { lblCheckTime, lblSysInsTime, lblSysUpTime };
            foreach (var lbl in contrastLabels) { if (lbl != null) lbl.ForeColor = contrastColor; }

            // 4. 特殊处理文本框 txtPCINFO
            if (txtPCINFO != null)
            {
                txtPCINFO.ForeColor = contrastColor;
                // 重点：文本框的背景也要跟着变，不然深色模式下会很难看
                txtPCINFO.BackColor = isLight ? Global.themeLight : Global.themeBlack;

                // 如果夢酱希望文本框看起来更融入背景，可以尝试把边框去掉（可选）
                // txtPCINFO.BorderStyle = BorderStyle.None; 
            }
        }

        private async void HWInfoWMI_Load(object sender, EventArgs e)  //配置检测-创建完毕
        {
            _ = ApplyHWInfoThemeAsync(); // 异步启动，丝滑变色

            //随意拖拽
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;

            try
            {
                // 获取程序运行目录
                string appPath = Application.StartupPath;

                // 检查所有必需文件
                List<string> missingFiles = new List<string>();

                foreach (string file in requiredFiles)
                {
                    string filePath = Path.Combine(appPath, file);
                    if (!File.Exists(filePath))
                    {
                        missingFiles.Add(file);
                    }
                }

                // 如果有缺失文件，显示提示并关闭窗口
                if (missingFiles.Count > 0)
                {
                    string message = $"缺少运行图吧硬件检测WMI版必要的文件：\n{string.Join("\n", missingFiles)}\n\n建议重新打开/解压查询器X/检查杀毒软件喵。";

                    MessageBox.Show(message, "文件缺失了",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.Close();
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查文件时出错：{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
                return;
            }

            lblExeName.Text = Global.exeName + " " + Global.Version;
            //启动后的默认显示文本
            txtPCINFO.Text = "                           🔰   正在读取配置(TbTools-WMI)   🔰\r\nTips:     1.本工具取自图吧工具箱API(WMI版)\r\n            2.已知WMI读取部分硬件可能信息有误, 仅供参考, 请自行核对~\r\n            3. 如遇程序报错, 请检查查询器X运行目录下组件是否完整, 是否被Defender, 360等软件拦截喵";

            //本机名
            lblPCName.Text = Environment.MachineName;

            //系统安装时间                               
            var os = new ManagementObjectSearcher(
                "SELECT InstallDate FROM Win32_OperatingSystem").Get()
                .Cast<ManagementObject>().First();
            var installDate = ManagementDateTimeConverter
                .ToDateTime(os["InstallDate"].ToString());
            lblSysInsTime.Text = "系统安装: " + installDate.ToString("yyyy-MM-dd HH:mm:ss");

            //已运行时长
            long ms = Environment.TickCount;
            TimeSpan up = TimeSpan.FromMilliseconds(ms);
            DateTime bootTime = DateTime.Now - up;
            string upStr = "";
            if (up.Days > 0) upStr += $"{up.Days}天";
            upStr += $"{up.Hours}时{up.Minutes}分{up.Seconds}秒";

            lblSysUpTime.Text = "系统开机: " + $"{bootTime:yyyy-MM-dd HH:mm:ss} (开机{upStr})";

            //配置检测时间
            lblCheckTime.Text = "检测时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            //读取图吧工具箱引擎和输出位置
            string exePath = Path.Combine(Application.StartupPath, "TbToolsHWInfo1.exe");
            string iniPath = Path.Combine(Application.StartupPath, "hwinfo.ini");

            var proc = new Process();        //启动 EXE
            proc.StartInfo.FileName = exePath;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.Start();

            await Task.Run(() => proc.WaitForExit());        //等待EXE完成执行

            if (File.Exists(iniPath))        //EXE执行结束，读取输出的ini
            {
                var iniText = File.ReadAllText("hwinfo.ini", Encoding.GetEncoding("GB18030"));
                var ini = ParseIni(iniText);   //使用解析器
                StringBuilder sb = new StringBuilder();

                {                //系统
                    var sys = ini["系统"];
                    var type = ini["类型"];
                    sb.AppendLine($"系统:\t{sys["版本"]} [{sys["Build"]}/{sys["系统位宽"]}] {type["类型"]}");
                }

                {          //CPU
                    var cpu = ini["处理器"];
                    string model = cpu["处理器型号"];
                    string core = cpu["处理器核心数"];
                    string thread = cpu["处理器线程数"];
                    sb.AppendLine($"CPU:\t{model} [{core}C/{thread}T]");
                }

                {          //主板
                    var board = ini["主板"];
                    var bios = ini["BIOS"];
                    string model = board["型号"];
                    string brand = board["品牌"];
                    string biosVer = bios["版本"];
                    sb.AppendLine($"主板:\t{model} [{brand}/{biosVer}]");
                }

                {         //内存
                    var mem = ini["内存"];
                    int count = int.Parse(mem["数量"]);
                    int totalGB = 0;
                    List<string> parts = new List<string>();

                    for (int i = 1; i <= count; i++)
                    {
                        string cap = mem[$"内存{i}容量"];
                        string speed = mem[$"内存{i}速度"];
                        string vendor = mem[$"内存{i}厂商"];
                        parts.Add($"{cap}-{speed}/{vendor}");

                        if (cap.EndsWith("GB"))       // 累计总容量
                        {
                            totalGB += int.Parse(cap.Replace("GB", ""));
                        }
                    }
                    sb.AppendLine($"内存:\t{totalGB}GB\t    [{string.Join(" | ", parts)}]");
                }

                {                  //  显卡
                    var gpu = ini["显卡"];
                    int count = int.Parse(gpu["数量"]);

                    string name1 = gpu["显卡1型号"];
                    string mem1 = gpu["显卡1显存"];
                    string drv1 = gpu["显卡1驱动版本"];

                    sb.AppendLine($"显卡:\t[1] {name1} [{mem1}/{drv1}]");         // 标题行 + 第一行

                    for (int i = 2; i <= count; i++)  // 后面的显卡另起行显示
                    {
                        string name = gpu[$"显卡{i}型号"];
                        string mem = gpu[$"显卡{i}显存"];
                        string drv = gpu[$"显卡{i}驱动版本"];

                        sb.AppendLine($"\t[{i}] {name} [{mem}/{drv}]");
                    }
                }

                {                  //  屏幕
                    var mon = ini["显示器"];
                    int count = int.Parse(mon["数量"]);

                    string name1 = mon["显示器1型号_测试版"];
                    string id1 = mon["显示器1ID"];
                    string size1 = mon["显示器1尺寸"];
                    string date1 = mon["显示器1生产日期"];

                    sb.AppendLine($"屏幕:\t[1] {name1} [{id1}/{size1}/{date1}]");

                    for (int i = 2; i <= count; i++)
                    {
                        string name = mon[$"显示器{i}型号_测试版"];
                        string id = mon[$"显示器{i}ID"];
                        string size = mon[$"显示器{i}尺寸"];
                        string date = mon[$"显示器{i}生产日期"];

                        sb.AppendLine($"\t[{i}] {name} [{id}/{size}/{date}]");
                    }
                }

                {                  //  硬盘
                    var dsk = ini["磁盘"];
                    int count = int.Parse(dsk["数量"]);

                    string model1 = dsk["磁盘1型号"];
                    string size1 = dsk["磁盘1实际容量"];

                    sb.AppendLine($"硬盘:\t[1] {model1} / {size1}");

                    for (int i = 2; i <= count; i++)
                    {
                        string model = dsk[$"磁盘{i}型号"];
                        string size = dsk[$"磁盘{i}实际容量"];
                        sb.AppendLine($"\t[{i}] {model} / {size}");
                    }
                }

                {                  //  网卡
                    var nic = ini["网卡"];
                    int count = int.Parse(nic["数量"]);

                    string model1 = nic["网卡1型号"];
                    string mac1 = nic["网卡1MAC"];
                    string spd1 = nic["网卡1速度"];

                    string spd1m = long.TryParse(spd1, out long v1) ? (v1 / 1000000 + "M") : spd1; // 速度转换 M

                    sb.AppendLine($"网卡:\t[1] {model1}");
                    sb.AppendLine($"\t        [{mac1} / {spd1m}]");

                    for (int i = 2; i <= count; i++)
                    {
                        string model = nic[$"网卡{i}型号"];
                        string mac = nic[$"网卡{i}MAC"];
                        string spd = nic[$"网卡{i}速度"];
                        string spdm = long.TryParse(spd, out long v) ? (v / 1000000 + "M") : spd;

                        sb.AppendLine($"\t[{i}] {model}");
                        sb.AppendLine($"\t        [{mac} / {spdm}]");
                    }
                }

                {                  //  声卡
                    var snd = ini["声卡"];
                    int count = int.Parse(snd["数量"]);

                    string model1 = snd["声卡1型号"];
                    sb.AppendLine($"声卡:\t[1] {model1}");

                    for (int i = 2; i <= count; i++)
                    {
                        string model = snd[$"声卡{i}型号"];
                        sb.AppendLine($"\t[{i}] {model}");
                    }
                }

                string finalText = sb.ToString();
                txtPCINFO.Text = finalText;
            }
            else
            {
                string info = "\U0001f6d1   获取失败   \U0001f6d1\r\n请检查:\r\n    1.查询器X目录下各个组件是否完整喵;\r\n    2.是否有Defender, 360等软件拦截喵";
                txtPCINFO.Text = info;
            }
            string path1 = Path.Combine(Application.StartupPath, "hwinfo.ini");
            string path2 = Path.Combine(Application.StartupPath, "PnPDevice.ini");

            try
            {
                if (File.Exists(path1))
                    File.Delete(path1);

                if (File.Exists(path2))
                    File.Delete(path2);
            }
            catch (Exception)
            {
                // 这里可以写日志或忽略，不让窗口崩
            }


        }

        Dictionary<string, Dictionary<string, string>> ParseIni(string text)  //ini解析，取出需要的部分
        {
            var result = new Dictionary<string, Dictionary<string, string>>();
            string currentSection = "";

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2);
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] = new Dictionary<string, string>();
                }
                else if (line.Contains("="))
                {
                    var idx = line.IndexOf('=');
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();
                    result[currentSection][key] = val;
                }
            }
            return result;
        }
        private void UpdateUptimeDisplay() //动态开机时间
        {
            long ms = Environment.TickCount;
            TimeSpan up = TimeSpan.FromMilliseconds(ms);
            DateTime bootTime = DateTime.Now - up;
            TimeSpan uptime = DateTime.Now - bootTime;

            var parts = new List<string>();
            if (uptime.Days > 0) parts.Add($"{uptime.Days}天");
            if (uptime.Hours > 0) parts.Add($"{uptime.Hours}时");
            if (uptime.Minutes > 0) parts.Add($"{uptime.Minutes}分");
            parts.Add($"{uptime.Seconds}秒");

            string uptimeStr = string.Join("", parts);
            lblSysUpTime.Text = $"系统开机: {bootTime:yyyy-MM-dd HH:mm:ss} (开机{uptimeStr})";
        }
        private void timer1_Tick(object sender, EventArgs e)
        {
            UpdateUptimeDisplay();
        }
    }
}
