using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class IPv6Time : Form
    {
        public IPv6Time()
        {
            InitializeComponent();
        }

        private void IPv6Time_Load(object sender, EventArgs e)
        {
            // --- ✨ 字体优化逻辑开始 ---
            using (Graphics g = this.CreateGraphics())
            {
                // 96 DPI 是 Windows 的标准 100% 缩放
                // 如果大于 96，说明缩放比例超过了 100%
                if (g.DpiX > 96)
                {
                    // 定义夢酱喜欢的现代感字体
                    // 微软雅黑适合中文，Segoe UI 适合英文数字，Consolas 或者 Cascadia Mono，C# 会自动回退匹配
                    Font modernFont = new Font("Cascadia Mono", 10.5F, FontStyle.Regular);

                    // 应用到文本框和下拉框
                    richTextBox1.Font = modernFont;
                }
                else
                {
                    // 100% 缩放时保持默认，或者显式指定为新宋体
                    //richTextBox1.Font = new Font("NSimSun", 10.5F, FontStyle.Regular);
                }
            }
            RefreshIPv6Info();
        }

        private void RefreshIPv6Info()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"    >>>> 刷新时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("=".PadLeft(64, '='));

                // 方法2: 通过netsh命令获取更详细的信息
                GetIPv6InfoFromNetsh(sb);

                // 更新富文本框
                if (richTextBox1.InvokeRequired)
                {
                    richTextBox1.Invoke(new Action(() =>
                    {
                        richTextBox1.Text = sb.ToString();
                    }));
                }
                else
                {
                    richTextBox1.Text = sb.ToString();
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"获取IPv6信息时出错:\n{ex.Message}";
                if (richTextBox1.InvokeRequired)
                {
                    richTextBox1.Invoke(new Action(() =>
                    {
                        richTextBox1.Text = errorMessage;
                    }));
                }
                else
                {
                    richTextBox1.Text = errorMessage;
                }
            }
        }
        private void GetIPv6InfoFromNetsh(StringBuilder sb)
        {
            // 1. 夢酱，我们先用新系统的“宠儿” UTF-8 试试
            string output = RunNetshCommand(Encoding.UTF8);

            // 2. 检查：如果没有“接口”且没有“Interface”，说明 UTF-8 解析失败（乱码了）
            if (!output.Contains("接口") || !output.Contains("Interface"))
            {
                // 尝试用 GB2312 (代码页 936) 重试。
                string legacyOutput = RunNetshCommand(Encoding.GetEncoding(936));

                // 如果 GB2312 拿到了正确的内容，就替换掉
                if (legacyOutput.Contains("接口") || legacyOutput.Contains("Interface"))
                {
                    output = legacyOutput;
                }
            }

            // 3. 交给夢酱的解析函数
            ParseNetshOutput(output, sb);
        }

        // 夢酱，这里把重复的执行逻辑提取出来，代码会更整洁哦！
        private string RunNetshCommand(Encoding encoding)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "netsh";
                    process.StartInfo.Arguments = "interface ipv6 show addresses";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.StandardOutputEncoding = encoding;

                    process.Start();
                    string result = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return result;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private void ParseNetshOutput(string output, StringBuilder sb)
        {
            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            string lastInterfaceName = ""; // 记录当前正在处理哪个网卡
            bool interfaceHeaderPrinted = false; // 标记当前网卡名是否已经打印过

            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;

                // 识别网卡行 (例如: "接口 2: 以太网")
                if (trimmedLine.StartsWith("接口") || trimmedLine.StartsWith("Interface"))
                {
                    lastInterfaceName = trimmedLine;
                    interfaceHeaderPrinted = false; // 切换到新网卡了，重置打印标记
                    continue;
                }

                // 过滤无关行
                if (trimmedLine.Contains("---") || trimmedLine.Contains("地址类型")) continue;

                // 识别地址行：必须包含冒号，且不是接口标题行
                if (trimmedLine.Contains(":") && !trimmedLine.Equals(lastInterfaceName))
                {
                    // 正则压缩空格：把多个空格变成一个，方便 Split
                    string normalized = System.Text.RegularExpressions.Regex.Replace(trimmedLine, @"\s+", " ");
                    string[] parts = normalized.Split(' ');

                    // netsh 输出通常格式：类型(0)  DAD状态(1)  有效寿命(2)  首选寿命(3)  地址(4)
                    if (parts.Length >= 4)
                    {
                        string ipAddress = parts[parts.Length - 1];

                        // 排除回环和本地链路地址
                        if (ipAddress == "::1" || ipAddress.StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // 如果没打印过网卡头，就打印一次
                        if (!interfaceHeaderPrinted && !string.IsNullOrEmpty(lastInterfaceName))
                        {
                            sb.AppendLine($"\r\n>>> {lastInterfaceName}");
                            interfaceHeaderPrinted = true;
                        }

                        string type = parts[0];     // 临时/公用
                        string status = parts[1];   // 首选项/已弃用(Deprecated)
                        string life = parts[2];     // 有效寿命

                        // 这种排版会比较清晰，能看到状态
                        // 梦酱的主题色是 #8e8cd8，我们在心里给它涂上色~
                        sb.AppendLine($"    [{type}] 状态: {status} | 寿命: {life}");
                        sb.AppendLine($"    └─ {ipAddress}");
                    }
                }
            }
        }

        private void timer1_Tick_1(object sender, EventArgs e)
        {
            RefreshIPv6Info();
        }

        private void IPv6Time_FormClosing(object sender, FormClosingEventArgs e)
        {
            timer1.Stop();
            this.Dispose();
        }
    }
}
