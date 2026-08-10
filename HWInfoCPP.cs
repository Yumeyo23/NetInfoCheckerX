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
    public partial class HWInfoCPP : Form
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
        private readonly string[] requiredFiles;
        public HWInfoCPP()
        {
            InitializeComponent();
            // 定义需要检查的文件列表
            requiredFiles = new string[]
            {
            "TbToolsHWInfo2.exe",
            };

            // 检查是否已有实例
            var existingForm = Application.OpenForms.OfType<HWInfoCPP>()
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

            bool isLight = Global.isThemelight;

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Label[] yumeyoLabels = { lblPCName, lblExeName };
            foreach (var lbl in yumeyoLabels) { if (lbl != null) lbl.ForeColor = yumeyoColor; }

            Color contrastColor = isLight ? Color.Black : Color.White;

            Label[] contrastLabels = { lblCheckTime, lblSysInsTime, lblSysUpTime };
            foreach (var lbl in contrastLabels) { if (lbl != null) lbl.ForeColor = contrastColor; }

            if (txtPCINFO != null)
            {
                txtPCINFO.ForeColor = contrastColor;
                txtPCINFO.BackColor = isLight ? Global.themeLight : Global.themeBlack;
            }
        }

        private async void HWInfoCPP_Load(object sender, EventArgs e)
        {
            _ = ApplyHWInfoThemeAsync();
            this.MinimumSize = this.Size;
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;

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
                    string message = $"缺少运行图吧硬件检测C++版必要的文件：\n{string.Join("\n", missingFiles)}\n\n建议重新打开/解压查询器X/检查杀毒软件喵。";

                    MessageBox.Show(message, "文件缺失了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

            txtPCINFO.Text = "                           🔰   正在读取配置(TbTools-C++)   🔰\r\nTips:     1.本工具取自图吧工具箱API(C++预览版)\r\n            2.配置检测仅供参考, 请自行核对~\r\n            3. 如遇程序报错, 请检查查询器X运行目录下组件是否完整, 是否被Defender, 360等软件拦截喵";

            lblPCName.Text = Environment.MachineName;
            lblExeName.Text = Global.exeName + " " + Global.Version;

            try
            {
                var os = new ManagementObjectSearcher(
                    "SELECT InstallDate FROM Win32_OperatingSystem").Get()
                    .Cast<ManagementObject>().First();
                var installDate = ManagementDateTimeConverter
                    .ToDateTime(os["InstallDate"].ToString());
                lblSysInsTime.Text = "系统安装: " + installDate.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                lblSysInsTime.Text = "系统安装: 无法获取(WMI服务未开启)";
            }

            long ms = Environment.TickCount;
            TimeSpan up = TimeSpan.FromMilliseconds(ms);
            DateTime bootTime = DateTime.Now - up;
            string upStr = "";
            if (up.Days > 0) upStr += $"{up.Days}天";
            upStr += $"{up.Hours}时{up.Minutes}分{up.Seconds}秒";

            lblSysUpTime.Text = "系统开机: " + $"{bootTime:yyyy-MM-dd HH:mm:ss} (开机{upStr})";

            lblCheckTime.Text = "检测时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            string result = await RunHWInfo2Async();

            txtPCINFO.Text = result;
        }
        private void UpdateUptimeDisplay()
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
        private async Task<string> RunHWInfo2Async()
        {
            string txt = Path.Combine(Application.StartupPath, "hwinfo2.txt");

            int delay = 333;
            for (int i = 0; i < 10; i++)
            {
                bool ran = await RunHWInfo2AndCreateFileAsync(delay);
                if (!ran)
                {
                    delay += 333;
                    continue;
                }

                await Task.Delay(100);

                string text = "";
                try
                {
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try
                        {
                            text = File.ReadAllText(txt, Encoding.GetEncoding("GB18030"));
                            break;
                        }
                        catch
                        {
                            await Task.Delay(100);
                        }
                    }
                }
                catch
                {
                    delay += 333;
                    continue;
                }

                // 检查是否完全输出
                if (text.Contains("管理员身份运行"))
                {
                    // 执行解析
                    string result = ExtractHWInfo2(text);

                    // 删除文件
                    try
                    {
                        await Task.Delay(100);
                        File.Delete(txt);
                    }
                    catch { }

                    return result;
                }

                delay += 333;
            }

            return "获取失败：EXE 多次运行未生成完整信息。";
        }

        private async Task<bool> RunHWInfo2AndCreateFileAsync(int delayMs)
        {
            string exe = Path.Combine(Application.StartupPath, "TbToolsHWInfo2.exe");
            string txt = Path.Combine(Application.StartupPath, "hwinfo2.txt");

            if (File.Exists(txt))
            {
                try { File.Delete(txt); } catch { }
            }

            var psi = new ProcessStartInfo()
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{exe}\" > \"{txt}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Process p = null;
            try
            {
                p = Process.Start(psi);
                await Task.Delay(delayMs);

                // 先尝试正常结束
                if (!p.HasExited)
                {
                    p.CloseMainWindow();
                    await Task.Delay(100);
                }

                // 如果还在运行，强制结束进程树
                if (!p.HasExited)
                {
                    p.Kill();

                    // 确保所有相关进程都结束
                    foreach (var process in Process.GetProcessesByName("TbToolsHWInfo2"))
                    {
                        try { process.Kill(); } catch { }
                    }
                }

                p.WaitForExit(1000);
                p.Close();
            }
            catch
            {
                try { p?.Kill(); } catch { }
            }
            finally
            {
                p?.Dispose();
            }

            await Task.Delay(200);

            return File.Exists(txt);
        }

        private string ExtractHWInfo2(string text)
        {
            var lines = text.Split('\n').Where(l => !l.Contains("作者可能")).ToList();
            string clean = string.Join("\n", lines);

            int start = clean.IndexOf("型号：\t\t");
            if (start < 0)
            {
                // 如果没找到，尝试其他可能的格式
                start = clean.IndexOf("型号：");
                if (start < 0) start = 0;
            }

            int end = clean.IndexOf("提示", start);
            if (end < 0) end = clean.Length;

            string result = clean.Substring(start, end - start).Trim();

            // 清理多余的空行
            result = string.Join("\n", result.Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line)));
            // 使用正则表达式将所有连续的制表符替换为单个制表符
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\t+", "\t");

            result = TranslateManufacturers(result);

            return result;
        }

        private string TranslateManufacturers(string text)
        {
            string result = text;

            foreach (var mapping in ManufacturerMappings)
            {
                result = result.Replace(mapping.Key, mapping.Value);
            }

            return result;
        }
        private static readonly Dictionary<string, string> ManufacturerMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
             { "Gigabyte Technology Co., Ltd.", "技嘉" },
             { "ASUSTeK COMPUTER INC.", "华硕" },
             { "Micro-Star International Co., Ltd.", "微星" },
             { "Maxsun", "铭瑄" },
             { "Colorful Technology And Development Co.,LTD", "七彩虹" },
             { "Microsoft Corporation", "微软" }
        };

        private void timer1_Tick(object sender, EventArgs e)
        {
            UpdateUptimeDisplay();
        }
    }
}
