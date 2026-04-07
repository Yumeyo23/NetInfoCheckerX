using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class iPerfGUI : Form
    {
        // --- 🧪 夢酱的强力内存清理魔法 ---
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

        /// <summary>
        /// 强制释放程序占用的内存，把它还给系统
        /// </summary>
        public void FlushMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    // 将工作集大小设为 -1，触发 Windows 强制回收
                    SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
                }
            }
            catch { /* 静默处理，不打扰梦酱 */ }
        }
        // ------------------------------

        public iPerfGUI()
        {
            // 1. 先检查文件（在还没出生前就检查）
            string appPath = Application.StartupPath;
            var missing = new List<string>();
            foreach (var f in new[] { "cygwin1.dll", "iperf3.exe" })
            {
                if (!File.Exists(Path.Combine(appPath, f))) missing.Add(f);
            }

            // 2. 如果缺文件，直接在这里弹窗
            if (missing.Count > 0)
            {
                MessageBox.Show($"缺少运行iPerfGUI必要的文件：\n{string.Join("\n", missing)}\n建议重新打开/解压查询器X/检查杀毒软件喵。",
                                "文件缺失了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                throw new FileNotFoundException("缺少 iPerf 必要组件");
            }

            InitializeComponent();
        }

        private void btnClientStart_Click(object sender, EventArgs e)
        {
            // 确保使用绝对路径，防止 CMD 迷路
            string iperfPath = Path.Combine(Application.StartupPath, "iperf3.exe");

            StringBuilder arguments = new StringBuilder();
            arguments.Append("-c ").Append(txtClientIP.Text.Trim());

            if (!string.IsNullOrEmpty(txtClientPort.Text))
                arguments.Append(" -p ").Append(txtClientPort.Text.Trim());

            if (!chkTCP.Checked) arguments.Append(" -u");

            if (numTime.Value > 0) arguments.Append(" -t ").Append(numTime.Value);

            if (numThread.Value > 1) arguments.Append(" -P ").Append(numThread.Value);

            if (chkWay.Checked) arguments.Append(" -R");

            if (!string.IsNullOrEmpty(txtLimit.Text))
                arguments.Append(" -b ").Append(txtLimit.Text.Trim()).Append("M");

            string iperfArguments = arguments.ToString();

            // 使用双重引号确保路径中即便有空格也能正常运行
            string finalCmdArguments = $"/c \"\"{iperfPath}\" {iperfArguments} -f m & set /p=\">>> 测试完成，按回车键关闭\"\"";

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = finalCmdArguments,
                    UseShellExecute = true, // 必须开启以显示黑窗口
                    WorkingDirectory = Application.StartupPath // 锁定运行目录
                };

                // 使用 using 确保 C# 这边的进程句柄被立即释放
                using (Process.Start(startInfo)) { }

                // 运行完立刻呼唤“清道夫”
                FlushMemory();
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动 IPERF 失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnServerStart_Click(object sender, EventArgs e)
        {
            string iperfPath = Path.Combine(Application.StartupPath, "iperf3.exe");

            if (!File.Exists(iperfPath))
            {
                MessageBox.Show("找不到 iperf3.exe 文件！", "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            StringBuilder arguments = new StringBuilder();
            arguments.Append("-s");

            if (!string.IsNullOrEmpty(txtServerPort.Text))
                arguments.Append(" -p ").Append(txtServerPort.Text.Trim());

            arguments.Append(" -V");

            string iperfArguments = arguments.ToString();
            // 服务器模式通常保持开启，所以使用 /k
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
                FlushMemory();
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
            // 如果任务开始时窗口已经关闭了，或者正在关闭，就直接溜掉~
            if (this.IsDisposed || this.Disposing) return;

            bool isLight = Global.isThemelight;
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Color btnDarkBack = Color.FromArgb(60, 60, 60);
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color controlBack = isLight ? Global.colorWhite : Global.themeBlack;

            // 批量处理颜色，减少内存消耗
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
            setStyle(label1, yumeyoColor, Color.Empty);

            Control[] labels = { lblServerIP, lblClient, lblTime, lblThread, lblLimit };
            foreach (var c in labels) setStyle(c, contrastColor, Color.Empty);

            Control[] inputs = { txtServerIP, txtServerPort, txtClientIP, txtClientPort, txtLimit, numTime, numThread, chkWay, chkTCP, chkTop };
            foreach (var c in inputs)
            {
                setStyle(c, contrastColor, (c is CheckBox) ? Color.Transparent : controlBack);
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
                    // 悬停时变梦酱紫
                    btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                }
            }
        }

        private void iPerfGUI_Load(object sender, EventArgs e)
        {
            // 这里的 Load 逻辑就变简单了，因为能走到这里的肯定文件都齐了
            _ = ApplyIPerfThemeAsync();
        }

        private void txtServerIP_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) btnServerStart_Click(sender, e);
        }

        private void txtClientIP_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) btnClientStart_Click(sender, e);
        }
    }
}
