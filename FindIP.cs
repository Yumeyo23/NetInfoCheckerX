using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class FindIP : Form
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

        // INI 文件读写 API
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int WritePrivateProfileString(string section, string key, string value, string filePath);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath);

        // INI 文件路径
        private string IniFilePath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

        // 配置节和键的名称
        private const string SectionName = "FindIP";
        private const string KeyName = "GEO";

        private CancellationTokenSource _cts; // 用于取消查询
        private Task _currentQueryTask; // 跟踪当前查询任务
        private readonly object _queryLock = new object(); // 查询锁，防止并发问题

        // 写入 INI 配置的方法
        private void WriteGeoIndexToIni()
        {
            try
            {
                int index = comboGEO.SelectedIndex;
                if (index >= 0)
                {
                    WritePrivateProfileString(SectionName, KeyName, index.ToString(), IniFilePath);
                }
            }
            catch (Exception ex)
            {
                // 写入失败时静默处理，不影响程序运行
                Debug.WriteLine($"写入 INI 配置失败: {ex.Message}");
            }
        }

        // 读取 INI 配置的方法
        private int ReadGeoIndexFromIni()
        {
            try
            {
                if (File.Exists(IniFilePath))
                {
                    StringBuilder buffer = new StringBuilder(256);
                    GetPrivateProfileString(SectionName, KeyName, "", buffer, buffer.Capacity, IniFilePath);

                    string value = buffer.ToString();
                    Debug.WriteLine($"[梦酱调试] 读到的配置值是: '{value}'"); // 在输出窗口就能看到啦
                    if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int index))
                    {
                        return index;
                    }
                }
            }
            catch (Exception ex)
            {
                // 读取失败时返回默认值
                Debug.WriteLine($"读取 INI 配置失败: {ex.Message}");
            }

            return 0; // 默认返回 0（第一个）
        }

        public FindIP()
        {
            InitializeComponent();
        }

        private async Task ApplyFindIPThemeAsync()
        {
            // 异步等待，确保 UI 线程准备好
            //await Task.Yield();
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            // 文本框/下拉框背景：深色下纯黑，浅色下全局色
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60); // 梦酱专属 60 灰

            // 1. 窗口整体背景
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 2. 梦酱紫色标签组 (用于提示文字)
            Control[] yumeyoLabels = { lblTip, lblGEO };
            foreach (var l in yumeyoLabels)
            {
                if (l != null) l.ForeColor = yumeyoColor;
            }

            // 3. 文本框与下拉框处理 (txtIP, comboGEO)
            Control[] editControls = { txtIP, txtResult1, txtResult2, comboGEO };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = textBack;

                    // 下拉框智能样式：深色扁平化去白边
                    if (c is ComboBox cb)
                    {
                        cb.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                    }
                }
            }

            // 4. 按钮组处理
            Control[] allButtons = {
        btnOK, btnPaste, btnIP138, btnPing0, btnPing, btnTra
    };

            foreach (var b in allButtons)
            {
                if (b != null && b is Button btn)
                {
                    if (isLight)
                    {
                        // 浅色模式：原生 3D 风格
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
                        // 悬停时变梦酱紫
                        btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                    }
                }
            }

            // 5. 选择框 (置顶 chkTop)
            if (chkTop != null)
            {
                chkTop.ForeColor = contrastColor;
                chkTop.BackColor = Color.Transparent;
            }
        }

        private void FindIP_Load(object sender, EventArgs e)
        {
            //随意拖拽
            this.MouseDown += MyMouseDown;
            lblTip.MouseDown += MyMouseDown;
            // 启动梦酱换肤大法
            _ = ApplyFindIPThemeAsync();

            // 1. 同步 Form1 的 API 列表
            comboGEO.Items.Clear();
            foreach (var provider in Api2.GeoCN_Providers)
            {
                comboGEO.Items.Add(provider.Name);
            }
            CloudControl.ApplyDevTitle(this);

            // 2. 尝试从 INI 文件读取上次保存的索引
            int savedIndex = ReadGeoIndexFromIni();

            // 3. 验证索引的有效性
            if (savedIndex >= 0 && savedIndex < comboGEO.Items.Count)
            {
                comboGEO.SelectedIndex = savedIndex;
            }
            else
            {
                // 如果索引无效，选中第一个
                comboGEO.SelectedIndex = 0;
            }

            lblTip.Text = "准备就绪";
        }

        // --- 工具方法：格式化 IP 文本 ---
        private string GetFormattedIP()
        {
            string raw = txtIP.Text.Trim();

            //✨ 正则表达式大扫除：只保留 字母、数字、点(.)、冒号(:)
            raw = Regex.Replace(raw, @"[^a-zA-Z0-9\.\:\-]", "");

            // 检查清洗后是否还剩下内容
            if (string.IsNullOrEmpty(raw))
            {
                SystemSounds.Beep.Play(); // 播放系统提示音
                return String.Empty;
            }

            // 把清洗干净的地址放回输入框
            return raw;
        }

        private async void btnOK_Click(object sender, EventArgs e)
        {
            if (!CloudControl.CheckClickRate((Control)sender, this.toolTip1))
            {
                return;
            }
            // 防止重复点击导致的并发问题
            if (btnOK.Enabled == false)
                return;

            string ip = GetFormattedIP();
            txtIP.Text = ip;

            if (string.IsNullOrWhiteSpace(ip))
            {
                lblTip.Text = "当前没有有效内容，不可以查询";
                return;
            }

            int index = comboGEO.SelectedIndex;
            string comboIndex = comboGEO.Text;
            if (index < 0) return;

            // 取消之前的查询
            await CancelCurrentQueryAsync();

            // 如果 IPv6 还是看不全，梦酱可以考虑把 lblTip 的 AutoSize 设为 true 
            // 或者稍微把窗口拉宽一点点哦~
            lblTip.Text = $"正在查询 [{comboIndex}]: {ip}";

            txtResult1.Text = "...";
            txtResult2.Text = "...";

            // 禁用按钮防止重复点击（将在查询完成后重新启用）
            //btnOK.Enabled = false;

            try
            {
                // 创建新的CancellationTokenSource
                var cts = new CancellationTokenSource();

                // 获取对应的 API 并执行任务
                var provider = Api2.GeoCN_Providers[index];

                // 启动查询任务
                var queryTask = provider.GetGeoTask(ip, cts.Token);

                // 更新当前查询任务和CancellationTokenSource
                lock (_queryLock)
                {
                    _cts = cts;
                    _currentQueryTask = queryTask;
                }

                // 等待查询完成
                var result = await queryTask;

                // 检查是否被取消
                if (cts.Token.IsCancellationRequested)
                {
                    return;
                }

                // 将结果对应显示到文本框
                this.Invoke(new Action(() =>
                {
                    txtResult1.Text = result.Loc;
                    txtResult2.Text = result.AS;

                    // 查询完成后更新提示
                    lblTip.Text = $"查询结果 [{comboIndex}]: {ip}";
                }));
            }
            catch (OperationCanceledException)
            {
                // 查询被取消，不显示错误信息
                this.Invoke(new Action(() =>
                {
                    lblTip.Text = "查询已取消";
                }));
            }
            catch (Exception ex)
            {
                this.Invoke(new Action(() =>
                {
                    lblTip.Text = "查询出错啦~";
                    txtResult2.Text = ex.Message;
                }));
                Console.WriteLine(ex.Message);
            }
            finally
            {
                // 无论成功或失败，都重新启用按钮
                this.Invoke(new Action(() =>
                {
                    btnOK.Enabled = true;
                }));
            }
        }

        // 取消当前查询的方法
        private async Task CancelCurrentQueryAsync()
        {
            lock (_queryLock)
            {
                if (_cts != null)
                {
                    try
                    {
                        _cts.Cancel();
                        _cts.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 忽略已释放的对象
                    }
                    _cts = null;
                }
            }

            // 等待之前的任务完成（如果有）
            if (_currentQueryTask != null && !_currentQueryTask.IsCompleted)
            {
                try
                {
                    // 给一小段时间等待任务取消完成
                    await Task.WhenAny(_currentQueryTask, Task.Delay(500));
                }
                catch (OperationCanceledException)
                {
                    // 任务取消异常，这是正常的
                }
                catch (Exception)
                {
                    // 忽略其他异常
                }

                lock (_queryLock)
                {
                    _currentQueryTask = null;
                }
            }
        }

        private void btnIP138_Click(object sender, EventArgs e)
        {
            string ip = GetFormattedIP();
            if (string.IsNullOrEmpty(ip)) return;
            SystemSounds.Beep.Play();

            // 冒号替换为 %3A
            string safeIp = ip.Replace(":", "%3A");
            Process.Start($"https://www.ip138.com/iplookup.php?ip={safeIp}");
        }

        private void btnPing0_Click(object sender, EventArgs e)
        {
            string ip = GetFormattedIP();
            if (string.IsNullOrEmpty(ip)) return;
            SystemSounds.Beep.Play();

            Process.Start($"https://ping0.cc/ip/{ip}");
        }

        private void chkTop_CheckedChanged(object sender, EventArgs e)
        {
            this.TopMost = chkTop.Checked;
            if (chkTop.Checked)
            {
                chkTop.Text = "已顶";
            }
            else
            {
                chkTop.Text = "置顶";
            }
        }

        private void btnPaste_Click(object sender, EventArgs e)
        {
            string clipText = Clipboard.GetText();
            if (!string.IsNullOrEmpty(clipText))
            {
                txtIP.Text = clipText;
                string formatted = GetFormattedIP();
                txtIP.Text = formatted;

                // 梦酱看这里：这里一定要把 formatted 加上去，才能看到 IP 哦！
                lblTip.Text = $"已从剪贴板粘贴";
            }
        }

        private void lblGEO_MouseDown(object sender, MouseEventArgs e)
        {
            // 检查是否是右键点击
            if (e.Button == MouseButtons.Right)
            {
                // 写入当前选中的索引到 INI 文件
                WriteGeoIndexToIni();
                toolTip1.Show("已保存当前选中项", lblGEO, e.Location, 2000);
            }
        }

        private void txtIP_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }

        // 窗体关闭时清理资源
        private void FindIP_FormClosing(object sender, FormClosingEventArgs e)
        {
            CancelCurrentQueryAsync().Wait();
        }

        //PING按钮点击
        private void btnPing_Click(object sender, EventArgs e)
        {
            string ip = GetFormattedIP();
            SystemSounds.Beep.Play(); // 播放系统提示音
            if (string.IsNullOrEmpty(ip)) return;
            string cmd = $"ping -t {ip}";
            if (!string.IsNullOrEmpty(cmd)) RunCmd(cmd);
        }
        //TRACE按钮点击
        private void btnTra_Click(object sender, EventArgs e)
        {
            string ip = GetFormattedIP();
            SystemSounds.Beep.Play(); // 播放系统提示音
            if (string.IsNullOrEmpty(ip)) return;
            string cmd = $"tracert -w 1000 -d -h 64 {ip}";
            if (!string.IsNullOrEmpty(cmd)) RunCmd(cmd);
        }

        // 简易执行 CMD 的小助手
        private void RunCmd(string command)
        {
            Process.Start("cmd.exe", $"/c {command} & pause");
        }

        private void btnPaste_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; // 阻止系统默认处理

                // 调用按钮的点击事件
                btnOK_Click(sender, e);
            }
        }

        private void btnPing_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                PingPP secondForm = new PingPP();
                secondForm.Show();
            }
        }

        private void btnTra_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                Trace secondForm = new Trace();
                secondForm.Show();
            }
        }
    }
}
