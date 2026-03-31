using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

// 手动查询mac地址类

namespace NetInfoCheckerX
{
    public partial class FindMAC : Form
    {
        private readonly string[] requiredFiles;
        private bool _filesValidated = false;
        private bool _shouldShow = true; // 新增：控制窗体是否应该显示

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
        public FindMAC()
        {
            // 1. 先检查必需文件
            requiredFiles = new string[]
            {
            "oui.csv",
            "oui.txt",
            };

            // 2. 在InitializeComponent之前检查文件
            if (!CheckRequiredFiles())
            {
                _filesValidated = false;
                _shouldShow = false; // 设置不显示

                // 这里不调用InitializeComponent，直接返回
                // 注意：我们需要确保窗体完全不会显示
                return;
            }

            // 3. 单例检查
            var existingForm = Application.OpenForms.OfType<FindMAC>()
                                      .FirstOrDefault(f => f != this);
            if (existingForm != null)
            {
                existingForm.BringToFront();
                existingForm.Focus();
                _shouldShow = false; // 单例也不显示
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
                    string message = $"缺少运行MAC查询必要的文件：\n{string.Join("\n", missingFiles)}\n\n建议重新打开/解压查询器X/检查杀毒软件喵。";
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
        // 重写 Show 方法，控制窗体是否显示
        public new void Show()
        {
            if (_shouldShow)
            {
                base.Show();
            }
            else
            {
                // 如果不应该显示，直接关闭并释放
                this.Close();
                this.Dispose();
            }
        }

        // 同样重写 ShowDialog 方法
        public new DialogResult ShowDialog()
        {
            if (_shouldShow)
            {
                return base.ShowDialog();
            }
            else
            {
                // 如果不应该显示，直接关闭并释放
                this.Close();
                this.Dispose();
                return DialogResult.Cancel;
            }
        }

        // 重写 OnLoad 方法，确保窗体加载时检查
        protected override void OnLoad(EventArgs e)
        {
            if (!_shouldShow || !_filesValidated)
            {
                // 如果不需要显示或文件检查失败，立即关闭
                this.Close();
                this.Dispose();
                return;
            }

            base.OnLoad(e);
        }

        // --- 1. 格式化工具 ---
        // 夢酱，我们先把输入的内容统一转成纯大写的十六进制字符串
        private string GetCleanMAC()
        {
            string raw = txtMAC.Text.Trim().ToUpper();
            // 去掉所有非十六进制字符（只留下 0-9 和 A-F）
            return Regex.Replace(raw, @"[^0-9A-F]", "");
        }

        // 格式化为标准的 XX:XX:XX... 形式，用于 TXT 数据库匹配
        private string GetColonMAC(string cleanMac)
        {
            if (cleanMac.Length < 6) return cleanMac;
            // 每两个字符加一个冒号
            return Regex.Replace(cleanMac, ".{2}", "$0:").TrimEnd(':');
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
        private async Task ApplyFindMACThemeAsync()
        {
            // 异步等待，确保 UI 线程准备就绪
            //await Task.Yield();

            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            // 文本框背景：深色下纯黑，浅色下全局色
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? ColorTranslator.FromHtml("#8e8cd8") : ColorTranslator.FromHtml("#a8a5ff");
            Color btnDarkBack = Color.FromArgb(60, 60, 60); // 梦酱专属 60 灰

            // 1. 窗口整体背景颜色
            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            // 2. 提示标签 (lblTip) - 使用梦酱紫
            if (lblTip != null)
            {
                lblTip.ForeColor = yumeyoColor;
            }

            // 3. 文本框处理 (txtMAC, txtResult)
            Control[] textBoxes = { txtMAC, txtResult };
            foreach (var t in textBoxes)
            {
                if (t != null)
                {
                    t.ForeColor = contrastColor;
                    t.BackColor = textBack;
                }
            }

            // 4. 按钮组处理 (btnOK, btnPaste) - 智能样式切换
            Control[] buttons = { btnOK, btnPaste };
            foreach (var b in buttons)
            {
                if (b != null && b is Button btn)
                {
                    if (isLight)
                    {
                        // 浅色模式：恢复系统原生 3D 风格
                        btn.ForeColor = Color.Black;
                        btn.BackColor = SystemColors.Control;
                        btn.UseVisualStyleBackColor = true;
                        btn.FlatStyle = FlatStyle.Standard;
                    }
                    else
                    {
                        // 深色模式：60 灰扁平风格
                        btn.ForeColor = Color.White;
                        btn.BackColor = btnDarkBack;
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.FlatAppearance.BorderColor = Color.DimGray;
                        // 鼠标经过亮起梦酱紫
                        btn.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#8e8cd8");
                    }
                }
            }

            // 5. 置顶勾选框 (chkTop)
            if (chkTop != null)
            {
                chkTop.ForeColor = contrastColor;
                chkTop.BackColor = Color.Transparent; // 透明背景更整洁
            }
        }
        private void FindMAC_Load(object sender, EventArgs e)
        {
            //随意拖拽
            this.MouseDown += MyMouseDown;
            lblTip.MouseDown += MyMouseDown;

            if (!_filesValidated || !_shouldShow)    // 如果文件检查失败，直接关闭窗口
            {
                this.Close();
                return;
            }
            _ = ApplyFindMACThemeAsync();
        }

        private void btnPaste_Click(object sender, EventArgs e)
        {
            string clipText = Clipboard.GetText();
            if (!string.IsNullOrEmpty(clipText))
            {
                // 只保留 0-9, a-f, A-F 以及冒号和减号
                clipText = Regex.Replace(clipText, @"[^0-9a-fA-F\-\:]", "");

                txtMAC.Text = clipText;
                lblTip.Text = $"已从剪贴板粘贴";
            }
        }
        private void btnOK_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                DoMacSearch(true);
            }
            else if (e.Button == MouseButtons.Right)
            {
                DoMacSearch(false);
            }
        }

        private void DoMacSearch(bool useCsv)
        {
            string cleanMac = GetCleanMAC();
            if (string.IsNullOrWhiteSpace(cleanMac) || cleanMac.Length < 6)
            {
                lblTip.Text = "内容无效（至少需要6位）";
                return;
            }

            if (useCsv)
            {
                RunMACSearch("oui.csv", cleanMac, true);
            }
            else
            {
                RunMACSearch("oui.txt", cleanMac, false);
            }
        }

        private void RunMACSearch(string fileName, string cleanMac, bool isCSV)
        {
            string filePath = Path.Combine(Application.StartupPath, fileName);
            if (!File.Exists(filePath))
            {
                lblTip.Text = $"找不到数据库文件: {fileName}";
                return;
            }

            int dbId = isCSV ? 1 : 2;
            lblTip.Text = $"正在查询 [{dbId}]: {cleanMac}";
            txtResult.Clear();
            txtResult.Text = "请等待...";

            try
            {
                string resultLine = "";
                if (isCSV)
                {
                    // --- 左键逻辑 (CSV) ---
                    // 构造搜索特征：MA-L,286FB9,
                    string prefix = $"MA-L,{cleanMac.Substring(0, 6)},";
                    // 夢酱，我们用逐行读取，省内存又快速
                    resultLine = File.ReadLines(filePath)
                                     .FirstOrDefault(line => line.StartsWith(prefix));
                }
                else
                {
                    // --- 右键逻辑 (TXT) ---
                    string colonMac = GetColonMAC(cleanMac);
                    // 逻辑：寻找能匹配输入前缀的最长那一行
                    // 我们读取所有行，找那些开头被包含在输入 MAC 里的行
                    var matches = File.ReadLines(filePath)
                        .Where(line =>
                        {
                            // 提取行首的 MAC 前缀（空格前的内容，且去掉可能有的 /36）
                            string part = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                            string searchPart = part.Contains("/") ? part.Split('/')[0] : part;
                            return colonMac.StartsWith(searchPart);
                        })
                        .OrderByDescending(line => line.Length) // 越长的匹配越精确（比如 /36 肯定比 /24 长）
                        .FirstOrDefault();

                    if (matches != null)
                    {
                        // 去掉制表符 \t
                        resultLine = matches.Replace("\t", " ");
                    }
                }

                if (!string.IsNullOrEmpty(resultLine))
                {
                    txtResult.Text = resultLine.Trim();
                    lblTip.Text = $"查询结果 [{dbId}]: {cleanMac}";
                }
                else
                {
                    txtResult.Text = "数据库中未找到该 MAC 地址。";
                    lblTip.Text = $"查询完毕(未命中)";
                }
            }
            catch (Exception ex)
            {
                lblTip.Text = "查询出错啦~";
                txtResult.Text = ex.Message;
            }
        }

        private void txtMAC_KeyDown(object sender, KeyEventArgs e)
        {
            // 1. 判断按下的是否为回车键
            if (e.KeyCode == Keys.Enter)
            {
                // 2. 获取清理后的 MAC 地址（直接复用夢酱写好的工具函数）
                string cleanMac = GetCleanMAC();

                // 3. 仿照 btnOK_MouseDown 里的逻辑进行校验
                if (string.IsNullOrWhiteSpace(cleanMac) || cleanMac.Length < 6)
                {
                    lblTip.Text = "内容无效（至少需要6位）";
                    return;
                }

                // 4. 核心：直接调用查询方法！
                // 参数 "oui.csv" 和 true 就代表了夢酱想要的“数据库1”和“左键逻辑”
                RunMACSearch("oui.csv", cleanMac, true);
            }
        }

        private void btnPaste_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // 2. 获取清理后的 MAC 地址（直接复用夢酱写好的工具函数）
                string cleanMac = GetCleanMAC();

                // 3. 仿照 btnOK_MouseDown 里的逻辑进行校验
                if (string.IsNullOrWhiteSpace(cleanMac) || cleanMac.Length < 6)
                {
                    lblTip.Text = "内容无效（至少需要6位）";
                    return;
                }

                // 4. 核心：直接调用查询方法！
                // 参数 "oui.csv" 和 true 就代表了夢酱想要的“数据库1”和“左键逻辑”
                RunMACSearch("oui.csv", cleanMac, true);
            }
        }

    }
}
