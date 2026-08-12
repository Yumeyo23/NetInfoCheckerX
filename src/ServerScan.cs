using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class ServerScan : Form
    {
        private const string IniSection = "ServerScan";
        private const string NpcapDownloadUrl = "https://npcap.com/#download";
        private const string WindowTitle = "DHCP/PPPoE服务器扫描 ✧ NetInfoCheckerX";
        private CancellationTokenSource scanCancellation;
        private bool formClosing;
        private ContextMenuStrip gridCopyMenu;
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

        public ServerScan()
        {
            InitializeComponent();
            ConfigureResultsGrid();
            SetupGridCopyMenu();
            txtTimeout.KeyPress += txtTimeout_KeyPress;
            linkLabel1.LinkClicked += linkLabel1_LinkClicked;
            lblNIC.MouseUp += lblNIC_MouseUp;
            colOffering.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            colSummary.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        }

        private void ServerScan_Load(object sender, EventArgs e)
        {
            _ = ApplyServerScanTheme();
            this.MinimumSize = this.Size;
            Text = WindowTitle;
            LoadSettings();
            LoadCaptureAdapters();
            CloudControl.UsedTimesCounter("ServerScan");
        }

        private void ConfigureResultsGrid()
        {
            gridResults.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            colIndex.AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader;
            colProtocol.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            colIdentity.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            colMac.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            colOffering.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            colResponse.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            colSummary.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colSummary.MinimumWidth = 160;
        }

        private void SetupGridCopyMenu()
        {
            gridCopyMenu = new ContextMenuStrip();
            ToolStripMenuItem copyItem = new ToolStripMenuItem("复制");
            copyItem.Click += (sender, e) => CopyCurrentCell();
            gridCopyMenu.Items.Add(copyItem);

            gridResults.MouseDown += (sender, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                DataGridView.HitTestInfo hit = gridResults.HitTest(e.X, e.Y);
                if (hit.RowIndex < 0 || hit.ColumnIndex < 0) return;
                gridResults.CurrentCell = gridResults.Rows[hit.RowIndex].Cells[hit.ColumnIndex];
                gridCopyMenu.Show(gridResults, e.Location);
            };
        }

        private void CopyCurrentCell()
        {
            DataGridViewCell cell = gridResults.CurrentCell;
            if (cell == null || cell.Value == null) return;
            string text = cell.Value.ToString();
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { }
        }

        private async Task ApplyServerScanTheme()
        {
            bool isLight = Global.isThemelight;
            Color foreground = isLight ? Global.colorBlack : Global.colorWhite;
            Color formBackground = isLight ? Global.themeLight : Global.themeBlack;
            Color controlBackground = isLight ? SystemColors.Window : Color.FromArgb(32, 32, 32);
            Color headerBackground = isLight ? SystemColors.Control : Color.FromArgb(45, 45, 48);
            Color alternateBackground = isLight ? Color.FromArgb(248, 248, 248) : Color.FromArgb(25, 25, 25);
            Color accent = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color accentText = GetReadableTextColor(accent);

            BackColor = formBackground;
            ForeColor = foreground;

            foreach (Control control in new Control[]
            {
                lblNIC, lblTry, lblTimeout, radioDHCP, radioPPPoE
            })
            {
                control.BackColor = Color.Transparent;
                control.ForeColor = foreground;
            }

            lblStatus.BackColor = Color.Transparent;
            lblStatus.ForeColor = accent;
            linkLabel1.BackColor = Color.Transparent;
            linkLabel1.ForeColor = accent;
            linkLabel1.LinkColor = accent;
            linkLabel1.ActiveLinkColor = accent;
            linkLabel1.VisitedLinkColor = accent;
            linkLabel1.DisabledLinkColor = accent;
            linkLabel1.LinkVisited = false;

            comboNIC.BackColor = controlBackground;
            comboNIC.ForeColor = foreground;
            comboNIC.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
            txtTimeout.BackColor = controlBackground;
            txtTimeout.ForeColor = foreground;
            txtTimeout.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            numTry.BackColor = controlBackground;
            numTry.ForeColor = foreground;

            ApplyButtonTheme(btnScan, isLight, foreground, accent);

            gridResults.EnableHeadersVisualStyles = false;
            gridResults.BackgroundColor = controlBackground;
            gridResults.GridColor = isLight ? Color.FromArgb(210, 210, 210) : Color.FromArgb(65, 65, 65);
            gridResults.ColumnHeadersDefaultCellStyle.BackColor = headerBackground;
            gridResults.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            gridResults.ColumnHeadersDefaultCellStyle.SelectionBackColor = headerBackground;
            gridResults.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
            gridResults.RowHeadersDefaultCellStyle.BackColor = headerBackground;
            gridResults.RowHeadersDefaultCellStyle.ForeColor = foreground;
            gridResults.RowHeadersDefaultCellStyle.SelectionBackColor = accent;
            gridResults.RowHeadersDefaultCellStyle.SelectionForeColor = accentText;
            gridResults.DefaultCellStyle.BackColor = controlBackground;
            gridResults.DefaultCellStyle.ForeColor = foreground;
            gridResults.DefaultCellStyle.SelectionBackColor = accent;
            gridResults.DefaultCellStyle.SelectionForeColor = accentText;
            gridResults.RowsDefaultCellStyle.BackColor = controlBackground;
            gridResults.RowsDefaultCellStyle.ForeColor = foreground;
            gridResults.RowsDefaultCellStyle.SelectionBackColor = accent;
            gridResults.RowsDefaultCellStyle.SelectionForeColor = accentText;
            gridResults.AlternatingRowsDefaultCellStyle.BackColor = alternateBackground;
            gridResults.AlternatingRowsDefaultCellStyle.ForeColor = foreground;
            gridResults.AlternatingRowsDefaultCellStyle.SelectionBackColor = accent;
            gridResults.AlternatingRowsDefaultCellStyle.SelectionForeColor = accentText;
        }

        private static void ApplyButtonTheme(Button button, bool isLight, Color foreground, Color accent)
        {
            if (isLight)
            {
                button.ForeColor = SystemColors.ControlText;
                button.BackColor = SystemColors.Control;
                button.FlatStyle = FlatStyle.Standard;
                button.UseVisualStyleBackColor = true;
            }
            else
            {
                button.ForeColor = foreground;
                button.UseVisualStyleBackColor = false;
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Color.FromArgb(60, 60, 60);
                button.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
                button.FlatAppearance.MouseOverBackColor = accent;
            }
        }

        private static Color GetReadableTextColor(Color background)
        {
            int brightness = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
            return brightness >= 150 ? Color.Black : Color.White;
        }

        private void LoadSettings()
        {
            try
            {
                string protocol = ReadSetting("Protocol");
                if (protocol.Equals("PPPoE", StringComparison.OrdinalIgnoreCase))
                {
                    radioPPPoE.Checked = true;
                }
                else if (protocol.Equals("DHCP", StringComparison.OrdinalIgnoreCase))
                {
                    radioDHCP.Checked = true;
                }

                int attemptCount;
                if (int.TryParse(ReadSetting("TryCount"), out attemptCount))
                {
                    attemptCount = Math.Max((int)numTry.Minimum,
                        Math.Min((int)numTry.Maximum, attemptCount));
                    numTry.Value = attemptCount;
                }

                int timeoutMs;
                if (int.TryParse(ReadSetting("TimeoutMs"), out timeoutMs))
                {
                    timeoutMs = Math.Max(100, Math.Min(9999, timeoutMs));
                    txtTimeout.Text = timeoutMs.ToString();
                }
            }
            catch
            {
                // 设置损坏时继续使用设计器中的默认值
            }
        }

        private void SaveSettings()
        {
            try
            {
                int timeoutMs;
                if (!int.TryParse(txtTimeout.Text, out timeoutMs)) timeoutMs = 2000;
                timeoutMs = Math.Max(100, Math.Min(9999, timeoutMs));

                IniFileHelper.WritePrivateProfileString(IniSection, "Protocol",
                    radioPPPoE.Checked ? "PPPoE" : "DHCP", IniPath);
                IniFileHelper.WritePrivateProfileString(IniSection, "TryCount",
                    Decimal.ToInt32(numTry.Value).ToString(), IniPath);
                IniFileHelper.WritePrivateProfileString(IniSection, "TimeoutMs",
                    timeoutMs.ToString(), IniPath);
            }
            catch
            {
            }
        }

        private string ReadSetting(string key)
        {
            var buffer = new StringBuilder(64);
            IniFileHelper.GetPrivateProfileString(
                IniSection, key, string.Empty, buffer, buffer.Capacity, IniPath);
            return buffer.ToString();
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = NpcapDownloadUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开 Npcap 官网：" + ex.Message, "打开链接失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            finally
            {
                linkLabel1.LinkVisited = false;
            }
        }

        private void lblNIC_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (scanCancellation != null)
            {
                lblStatus.Text = "扫描期间不能刷新网卡列表";
                return;
            }

            LoadCaptureAdapters();
        }

        private void LoadCaptureAdapters()
        {
            comboNIC.Items.Clear();

            try
            {
                IReadOnlyList<CaptureAdapterInfo> adapters = ServerDiscoveryScanner.GetCaptureAdapters();
                foreach (CaptureAdapterInfo adapter in adapters)
                {
                    comboNIC.Items.Add(adapter);
                }

                if (comboNIC.Items.Count > 0)
                {
                    comboNIC.SelectedIndex = 0;
                    lblStatus.Text = $"找到 {comboNIC.Items.Count} 张可扫描的网卡";
                }
                else
                {
                    lblStatus.Text = "没有找到Npcap可用的无线网卡";
                }
            }
            catch (NpcapNotInstalledException)
            {
                lblStatus.Text = "Npcap未安装";
                DialogResult choice = MessageBox.Show(this,
                    "当前系统内未安装Npcap驱动，相关功能无法使用。跳转到下载页面吗？",
                    "未安装Npcap",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (choice == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = NpcapDownloadUrl,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "无法打开 Npcap 官网：" + ex.Message, "打开链接失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                }
                else
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Npcap初始化失败";
                MessageBox.Show(this, ex.Message, "无法使用Npcap",
                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        private async void btnScan_Click(object sender, EventArgs e)
        {
            if (scanCancellation != null)
            {
                lblStatus.Text = "正在停止扫描……";
                scanCancellation.Cancel();
                return;
            }

            CaptureAdapterInfo adapter = comboNIC.SelectedItem as CaptureAdapterInfo;
            if (adapter == null)
            {
                MessageBox.Show(this, "请先选择一张可用网卡", "网卡没有了",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ServerScanProtocol protocol = radioDHCP.Checked
                ? ServerScanProtocol.Dhcp
                : ServerScanProtocol.Pppoe;

            int timeoutMs;
            if (!int.TryParse(txtTimeout.Text, out timeoutMs) || timeoutMs < 100 || timeoutMs > 9999)
            {
                MessageBox.Show(this, "超时时间请输入 100 到 9999 毫秒", "扫描参数不正确",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtTimeout.Focus();
                txtTimeout.SelectAll();
                return;
            }
            int attemptCount = Decimal.ToInt32(numTry.Value);

            Text = WindowTitle;
            gridResults.Rows.Clear();
            scanCancellation = new CancellationTokenSource();
            SetScanningState(true);
            lblStatus.Text = protocol == ServerScanProtocol.Dhcp
                ? "正在广播 DHCPDISCOVER 并等待 DHCPOFFER ……"
                : "正在广播 PADI 并等待 PADO ……";

            int resultCount = 0;
            try
            {
                CancellationToken token = scanCancellation.Token;
                await Task.Run(() => ServerDiscoveryScanner.Scan(adapter, protocol,
                    attemptCount, timeoutMs, token, result =>
                {
                    Interlocked.Increment(ref resultCount);
                    PostResult(result);
                }), token);

                if (!formClosing)
                {
                    lblStatus.Text = resultCount == 0
                        ? "扫描完成，没有收到服务器响应"
                        : $"扫描完成，共有 {resultCount} 个响应";
                    Text = WindowTitle + " | 扫描完成于：" + Others.GetCurrentTime();
                }
            }
            catch (OperationCanceledException)
            {
                if (!formClosing) lblStatus.Text = $"扫描已停止，共有 {resultCount} 个响应";
            }
            catch (Exception ex)
            {
                if (!formClosing)
                {
                    lblStatus.Text = "扫描失败";
                    MessageBox.Show(this, ex.Message, "扫描失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
            finally
            {
                scanCancellation.Dispose();
                scanCancellation = null;
                if (!formClosing) SetScanningState(false);
            }
        }

        private void PostResult(ServerDiscoveryResult result)
        {
            if (formClosing || IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!formClosing && !IsDisposed) AddResult(result);
                }));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
                // 窗口可能刚好在 BeginInvoke 前关闭
            }
        }

        private void AddResult(ServerDiscoveryResult result)
        {
            int resultNumber = gridResults.Rows.Count;
            int rowIndex = gridResults.Rows.Add(
                resultNumber,
                result.ProtocolName,
                result.MacAddress,
                result.ServerIdentity,
                result.Offering,
                result.ResponseTimeMs + " ms",
                result.Summary);

            gridResults.Rows[rowIndex].Tag = result;
        }

        private void SetScanningState(bool scanning)
        {
            comboNIC.Enabled = !scanning;
            radioDHCP.Enabled = !scanning;
            radioPPPoE.Enabled = !scanning;
            numTry.Enabled = !scanning;
            txtTimeout.Enabled = !scanning;
            btnScan.Text = scanning ? "停止扫描" : "开始扫描";
        }

        private void txtTimeout_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar);
        }

        private void ServerScan_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            formClosing = true;
            if (scanCancellation != null)
            {
                scanCancellation.Cancel();
            }
        }
    }
}
