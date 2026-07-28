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

namespace NetInfoCheckerX
{
    public partial class FindMAC : Form
    {
        private readonly string[] requiredFiles;
        private bool _filesValidated = false;
        private bool _shouldShow = true;

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
        public FindMAC()
        {
            requiredFiles = new string[]
            {
            "oui.csv",
            "oui.txt",
            };

            if (!CheckRequiredFiles())
            {
                _filesValidated = false;
                _shouldShow = false;

                return;
            }

            var existingForm = Application.OpenForms.OfType<FindMAC>()
                                      .FirstOrDefault(f => f != this);
            if (existingForm != null)
            {
                existingForm.BringToFront();
                existingForm.Focus();
                _shouldShow = false;
                this.Dispose();
                return;
            }

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
        public new void Show()
        {
            if (_shouldShow)
            {
                base.Show();
            }
            else
            {
                this.Close();
                this.Dispose();
            }
        }

        public new DialogResult ShowDialog()
        {
            if (_shouldShow)
            {
                return base.ShowDialog();
            }
            else
            {
                this.Close();
                this.Dispose();
                return DialogResult.Cancel;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            if (!_shouldShow || !_filesValidated)
            {
                this.Close();
                this.Dispose();
                return;
            }

            base.OnLoad(e);
        }

        private string GetCleanMAC()
        {
            string raw = txtMAC.Text.Trim().ToUpper();
            return Regex.Replace(raw, @"[^0-9A-F]", "");
        }

        private string GetColonMAC(string cleanMac)
        {
            if (cleanMac.Length < 6) return cleanMac;
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

            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            if (lblTip != null)
            {
                lblTip.ForeColor = yumeyoColor;
            }

            Control[] textBoxes = { txtMAC, txtResult };
            foreach (var t in textBoxes)
            {
                if (t != null)
                {
                    t.ForeColor = contrastColor;
                    t.BackColor = textBack;
                }
            }

            Control[] buttons = { btnOK, btnPaste };
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
                        btn.FlatAppearance.MouseOverBackColor = Global.Yumeyo;
                    }
                }
            }

            if (chkTop != null)
            {
                chkTop.ForeColor = contrastColor;
                chkTop.BackColor = Color.Transparent;
            }
        }
        private void FindMAC_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            this.MouseDown += MyMouseDown;
            lblTip.MouseDown += MyMouseDown;

            if (!_filesValidated || !_shouldShow)
            {
                this.Close();
                return;
            }
            _ = ApplyFindMACThemeAsync();
        }

        private void btnPaste_Click(object sender, EventArgs e)
        {
            if (ClipboardHelper.TryGetText(out string clipText) && !string.IsNullOrEmpty(clipText))
            {
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
                    string prefix = $"MA-L,{cleanMac.Substring(0, 6)},";
                    resultLine = File.ReadLines(filePath)
                                     .FirstOrDefault(line => line.StartsWith(prefix));
                }
                else
                {
                    string colonMac = GetColonMAC(cleanMac);
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
            if (e.KeyCode == Keys.Enter)
            {
                string cleanMac = GetCleanMAC();

                if (string.IsNullOrWhiteSpace(cleanMac) || cleanMac.Length < 6)
                {
                    lblTip.Text = "内容无效（至少需要6位）";
                    return;
                }

                RunMACSearch("oui.csv", cleanMac, true);
            }
        }

        private void btnPaste_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                string cleanMac = GetCleanMAC();

                if (string.IsNullOrWhiteSpace(cleanMac) || cleanMac.Length < 6)
                {
                    lblTip.Text = "内容无效（至少需要6位）";
                    return;
                }

                RunMACSearch("oui.csv", cleanMac, true);
            }
        }

    }
}
