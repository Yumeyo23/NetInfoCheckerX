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

        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);

        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);

        private string IniFilePath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

        private const string SectionName = "FindIP";
        private const string KeyName = "GEO";

        private CancellationTokenSource _cts;
        private Task _currentQueryTask;
        private readonly object _queryLock = new object();

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
                Debug.WriteLine($"写入 INI 配置失败: {ex.Message}");
            }
        }

        private int ReadGeoIndexFromIni()
        {
            try
            {
                if (File.Exists(IniFilePath))
                {
                    StringBuilder buffer = new StringBuilder(256);
                    GetPrivateProfileString(SectionName, KeyName, "", buffer, buffer.Capacity, IniFilePath);

                    string value = buffer.ToString();
                    Debug.WriteLine($"[梦酱调试] 读到的配置值是: '{value}'");
                    if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int index))
                    {
                        return index;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取 INI 配置失败: {ex.Message}");
            }

            return 0;
        }

        public FindIP()
        {
            InitializeComponent();
        }

        private async Task ApplyFindIPThemeAsync()
        {
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoLabels = { lblTip, lblGEO };
            foreach (var l in yumeyoLabels)
            {
                if (l != null) l.ForeColor = yumeyoColor;
            }

            Control[] editControls = { txtIP, txtResult1, txtResult2, comboGEO };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = textBack;

                    if (c is ComboBox cb)
                    {
                        cb.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                    }
                }
            }

            Control[] allButtons = {
        btnOK, btnPaste, btnIP138, btnPing0, btnPing, btnTra
    };

            foreach (var b in allButtons)
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

        private void FindIP_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            this.MouseDown += MyMouseDown;
            lblTip.MouseDown += MyMouseDown;
            _ = ApplyFindIPThemeAsync();

            comboGEO.Items.Clear();
            foreach (var provider in Api2.GeoCN_Providers)
            {
                comboGEO.Items.Add(provider.Name);
            }
            CloudControl.ApplyDevTitle(this);

            int savedIndex = ReadGeoIndexFromIni();

            if (savedIndex >= 0 && savedIndex < comboGEO.Items.Count)
            {
                comboGEO.SelectedIndex = savedIndex;
            }
            else
            {
                comboGEO.SelectedIndex = 0;
            }
            CloudControl.UsedTimesCounter("FindIP");
            lblTip.Text = "准备就绪";
        }

        private string GetFormattedIP()
        {
            string raw = txtIP.Text.Trim();

            raw = Regex.Replace(raw, @"[^a-zA-Z0-9\.\:\-]", "");

            if (string.IsNullOrEmpty(raw))
            {
                SystemSounds.Beep.Play();
                return String.Empty;
            }

            return raw;
        }

        private async void btnOK_Click(object sender, EventArgs e)
        {
            if (!CloudControl.CheckClickRate((Control)sender, this.toolTip1))
            {
                return;
            }
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

            await CancelCurrentQueryAsync();

            lblTip.Text = $"正在查询[{comboIndex}]: {ip}";

            txtResult1.Text = "...";
            txtResult2.Text = "...";

            try
            {
                var cts = new CancellationTokenSource();

                var provider = Api2.GeoCN_Providers[index];

                var queryTask = provider.GetGeoTaskIgnoringPrivacy(ip, cts.Token);

                lock (_queryLock)
                {
                    _cts = cts;
                    _currentQueryTask = queryTask;
                }

                var result = await queryTask;

                if (cts.Token.IsCancellationRequested)
                {
                    return;
                }

                this.Invoke(new Action(() =>
                {
                    txtResult1.Text = result.Loc;
                    txtResult2.Text = result.AS;

                    lblTip.Text = $"查询结果[{comboIndex}]: {ip}";
                }));
            }
            catch (OperationCanceledException)
            {
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
                this.Invoke(new Action(() =>
                {
                    btnOK.Enabled = true;
                }));
            }
        }

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
                    }
                    _cts = null;
                }
            }

            if (_currentQueryTask != null && !_currentQueryTask.IsCompleted)
            {
                try
                {
                    await Task.WhenAny(_currentQueryTask, Task.Delay(500));
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
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
            if (ClipboardHelper.TryGetText(out string clipText) && !string.IsNullOrEmpty(clipText))
            {
                txtIP.Text = clipText;
                string formatted = GetFormattedIP();
                txtIP.Text = formatted;

                lblTip.Text = $"已从剪贴板粘贴";
            }
        }

        private void lblGEO_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                WriteGeoIndexToIni();
                toolTip1.Show("已保存当前选中项", lblGEO, e.Location, 2000);
            }
        }

        private void txtIP_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }

        private void FindIP_FormClosing(object sender, FormClosingEventArgs e)
        {
            CancelCurrentQueryAsync().Wait();
        }

        private void btnPing_Click(object sender, EventArgs e)
        {
            string ip = GetFormattedIP();
            SystemSounds.Beep.Play();
            if (string.IsNullOrEmpty(ip)) return;
            string cmd = $"ping -t {ip}";
            if (!string.IsNullOrEmpty(cmd)) RunCmd(cmd);
        }
        private void btnTra_Click(object sender, EventArgs e)
        {
            string ip = GetFormattedIP();
            SystemSounds.Beep.Play();
            if (string.IsNullOrEmpty(ip)) return;
            string cmd = $"tracert -w 1000 -d -h 64 {ip}";
            if (!string.IsNullOrEmpty(cmd)) RunCmd(cmd);
        }

        private void RunCmd(string command)
        {
            Process.Start("cmd.exe", $"/c {command} & pause");
        }

        private void btnPaste_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

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
            else if (e.Button == MouseButtons.Right)
            {
                string command = NetworkTestSettingsDialog.ShowPing(this, GetFormattedIP());
                if (!string.IsNullOrEmpty(command)) RunCmd(command);
            }
        }

        private void btnTra_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                Trace secondForm = new Trace();
                secondForm.Show();
            }
            else if (e.Button == MouseButtons.Right)
            {
                string command = NetworkTestSettingsDialog.ShowTrace(this, GetFormattedIP());
                if (!string.IsNullOrEmpty(command)) RunCmd(command);
            }
        }

        private void txtResult1_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(txtResult1, txtResult1.Text);
        }

        private void txtResult2_TextChanged(object sender, EventArgs e)
        {
            toolTip1.SetToolTip(txtResult2, txtResult2.Text);
        }
    }
}
