using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class FindIPs : Form
    {
        private const string IniSection = "FindIPs";
        private const string IniKeyGeo = "GEO";
        private const string IniKeyCompactRows = "CompactRows";
        private static readonly char[] IpSeparators = { ',', '，' };

        private readonly SemaphoreSlim _geoSemaphore = new SemaphoreSlim(3, 3);
        private readonly Dictionary<string, DataGridViewRow> _gridRows =
            new Dictionary<string, DataGridViewRow>();

        private readonly object _runLock = new object();
        private CancellationTokenSource _runCts;
        private int _runId;
        private int _geoTotal;
        private int _geoDone;
        private bool _compactRows;
        private string _baseTitle;

        private string IniFilePath =>
            Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

        public FindIPs()
        {
            InitializeComponent();
            dataGridView1.AllowUserToAddRows = false;
            ConfigureResultGrid();
        }

        private void ConfigureResultGrid()
        {
            // IP 列改成固定宽度：默认按实际地址长度自适应，同时允许用户拖动调整。
            // 参考位置列保持 Fill，吸收剩余宽度。
            coIP.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            coIP.Resizable = DataGridViewTriState.True;

            float dpiScale = dataGridView1.DeviceDpi / 96F;
            int ipv4Width = TextRenderer.MeasureText("255.255.255.255 ",
                dataGridView1.Font).Width;
            coIP.MinimumWidth = Math.Max((int)Math.Ceiling(120F * dpiScale),
                ipv4Width + (int)Math.Ceiling(24F * dpiScale));
            if (coIP.Width < coIP.MinimumWidth) coIP.Width = coIP.MinimumWidth;
        }

        private void FitIpColumnWidth()
        {
            int widest = TextRenderer.MeasureText(coIP.HeaderText,
                dataGridView1.Font).Width;
            foreach (DataGridViewRow row in dataGridView1.Rows)
            {
                if (row.IsNewRow) continue;

                int width = TextRenderer.MeasureText(
                    Convert.ToString(row.Cells[coIP.Name].Value),
                    dataGridView1.Font).Width;
                if (width > widest) widest = width;
            }

            float dpiScale = dataGridView1.DeviceDpi / 96F;
            coIP.Width = Math.Max(coIP.MinimumWidth,
                widest + (int)Math.Ceiling(24F * dpiScale));
        }

        private void FindIPs_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            lblVersion.Text = Global.exeName + " " + Global.Version;
            comboGEO.Items.Clear();
            foreach (var provider in Api2.GeoCN_Providers)
                comboGEO.Items.Add(provider.Name);

            string geoToolTip = Api2.GetExternalToolTip("GEOCN");
            if (!string.IsNullOrWhiteSpace(geoToolTip))
                toolTip1.SetToolTip(comboGEO, geoToolTip);

            int savedIndex = ReadGeoIndexFromIni();
            if (savedIndex >= 0 && savedIndex < comboGEO.Items.Count)
                comboGEO.SelectedIndex = savedIndex;
            else if (comboGEO.Items.Count > 0)
                comboGEO.SelectedIndex = 0;

            bool compactRows;
            _compactRows = bool.TryParse(ReadIniValue(IniKeyCompactRows, "False"),
                out compactRows) && compactRows;
            ApplyRowHeightMode();

            ApplyFindIPsTheme();
            CloudControl.ApplyDevTitle(this);
            _baseTitle = Text;
            SetStatus(null);
            CloudControl.UsedTimesCounter("手动查询IP");
        }

        private void SetStatus(string status)
        {
            Text = string.IsNullOrEmpty(status) ? _baseTitle : _baseTitle + " | " + status;
        }

        private void ShowInputTip(string message)
        {
            SystemSounds.Beep.Play();
            toolTip1.Show(message, txtIPs, 0, txtIPs.Height, 3000);
        }

        private void FindIPs_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveRowHeightSetting();

            // 只发取消信号，不等待在途请求，避免关闭窗口时卡住界面。
            lock (_runLock)
            {
                _runCts?.Cancel();
                _runCts = null;
            }
        }

        #region 行高切换

        private void lnkHeight_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            _compactRows = !_compactRows;
            ApplyRowHeightMode();
        }

        private void ApplyRowHeightMode()
        {
            int rowHeight = _compactRows
                ? Math.Max(23, dataGridView1.Font.Height + 6)
                : Math.Max(40, dataGridView1.Font.Height * 2 + 8);
            DataGridViewTriState wrapMode = _compactRows
                ? DataGridViewTriState.False : DataGridViewTriState.True;

            dataGridView1.SuspendLayout();
            try
            {
                dataGridView1.RowTemplate.Height = rowHeight;
                dataGridView1.DefaultCellStyle.WrapMode = wrapMode;
                dataGridView1.RowsDefaultCellStyle.WrapMode = wrapMode;
                dataGridView1.AlternatingRowsDefaultCellStyle.WrapMode = wrapMode;
                foreach (DataGridViewRow row in dataGridView1.Rows)
                    row.Height = rowHeight;
            }
            finally
            {
                dataGridView1.ResumeLayout();
            }

            toolTip1.SetToolTip(lnkHeight,
                _compactRows ? "当前为单行高度，点击切换为双行" : "当前为双行高度，点击切换为单行");
        }

        private void SaveRowHeightSetting()
        {
            try
            {
                IniFileHelper.WritePrivateProfileString(IniSection, IniKeyCompactRows,
                    _compactRows.ToString(), IniFilePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"写入 INI 配置失败: {ex.Message}");
            }
        }

        #endregion

        #region 位置 API 记忆

        private string ReadIniValue(string key, string defaultValue)
        {
            var buffer = new StringBuilder(32);
            IniFileHelper.GetPrivateProfileString(IniSection, key, defaultValue,
                buffer, buffer.Capacity, IniFilePath);
            return buffer.ToString();
        }

        private int ReadGeoIndexFromIni()
        {
            try
            {
                if (int.TryParse(ReadIniValue(IniKeyGeo, "0"), out int index))
                    return index;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取 INI 配置失败: {ex.Message}");
            }
            return 0;
        }

        private void WriteGeoIndexToIni()
        {
            try
            {
                int index = comboGEO.SelectedIndex;
                if (index < 0) return;
                IniFileHelper.WritePrivateProfileString(IniSection, IniKeyGeo,
                    index.ToString(), IniFilePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"写入 INI 配置失败: {ex.Message}");
            }
        }

        private void lblGEO_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            WriteGeoIndexToIni();
            toolTip1.Show("已保存当前选中项", lblGEO, e.Location, 2000);
        }

        #endregion

        #region 输入解析

        private bool TryParseAddresses(out List<string> addresses, out int invalidCount,
            out string error)
        {
            addresses = new List<string>();
            invalidCount = 0;
            error = null;

            string raw = txtIPs.Text.Trim().Trim('"');
            if (raw.Length == 0)
            {
                error = "当前没有有效内容，不可以查询";
                return false;
            }

            var candidates = new List<string>();
            bool fromFile = false;
            if (File.Exists(raw))
            {
                try
                {
                    candidates.AddRange(File.ReadAllLines(raw));
                    fromFile = true;
                }
                catch (Exception ex)
                {
                    error = "读取文件失败：" + ex.Message;
                    return false;
                }
            }
            else if (raw.IndexOfAny(IpSeparators) >= 0)
            {
                candidates.AddRange(raw.Split(IpSeparators, StringSplitOptions.None));
            }
            else
            {
                candidates.Add(raw);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in candidates)
            {
                string text = (candidate ?? string.Empty).Trim();
                if (text.Length == 0) continue;

                if (TextHelper.TryNormalizeIP(text, out string normalized))
                {
                    if (seen.Add(normalized)) addresses.Add(normalized);
                }
                else
                {
                    invalidCount++;
                }
            }

            if (addresses.Count == 0)
            {
                if (fromFile)
                    error = "文件里没有有效的IP地址";
                else if (raw.IndexOfAny(IpSeparators) >= 0)
                    error = "没有解析到有效的IP地址";
                else
                    error = "找不到该文件，也不是有效的IP地址";
                return false;
            }

            return true;
        }

        private void btnPaste_Click(object sender, EventArgs e)
        {
            if (!ClipboardHelper.TryGetText(out string clipText) ||
                string.IsNullOrEmpty(clipText))
                return;

            txtIPs.Text = clipText.Trim();
            SetStatus("已粘贴");
        }

        private void btnSelect_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "请选择包含IP地址的文本文件";
                ofd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                if (ofd.ShowDialog() != DialogResult.OK) return;

                txtIPs.Text = ofd.FileName;
                SetStatus("已选择 " + Path.GetFileName(ofd.FileName));
            }
        }

        #endregion

        #region 查询

        private async void btnOK_Click(object sender, EventArgs e)
        {
            if (!CloudControl.CheckClickRate((Control)sender, toolTip1)) return;

            if (!TryParseAddresses(out List<string> addresses, out int invalidCount,
                    out string error))
            {
                ShowInputTip(error);
                return;
            }

            int providerIndex = comboGEO.SelectedIndex;
            if (providerIndex < 0)
            {
                ShowInputTip("请先在“位置”中选择一个查询接口");
                return;
            }

            int runId = ++_runId;
            CancelCurrentQuery();

            _gridRows.Clear();
            dataGridView1.Rows.Clear();

            foreach (string ip in addresses)
            {
                string localGeo = Trace.ResolveSharedLocalGeo(ip);
                if (string.IsNullOrWhiteSpace(localGeo)) localGeo = "未知";

                int index = dataGridView1.Rows.Add(dataGridView1.Rows.Count + 1, ip, localGeo);
                _gridRows[ip] = dataGridView1.Rows[index];
            }
            FitIpColumnWidth();

            _geoTotal = addresses.Count;
            _geoDone = 0;
            SetStatus(invalidCount > 0
                ? $"共 {addresses.Count} 个IP，忽略 {invalidCount} 个无效项"
                : $"共 {addresses.Count} 个IP");

            bool onlineEnabled = providerIndex > 0 &&
                providerIndex < Api2.GeoCN_Providers.Count;
            if (!onlineEnabled)
            {
                _geoDone = _geoTotal;
                SetStatus($"查询完毕(本地库，共 {_geoTotal} 个IP)");
                return;
            }

            var cts = new CancellationTokenSource();
            lock (_runLock) { _runCts = cts; }
            CancellationToken token = cts.Token;

            IDisposable usageScope = GeoProvider.BeginUsageCountScope();
            try
            {
                // 保留地址（内网、回环等）本地库已有结论，不再走在线接口。
                var onlineIps = new List<string>();
                foreach (string ip in addresses)
                {
                    if (string.IsNullOrEmpty(IanaReservedIP.Check(ip)))
                        onlineIps.Add(ip);
                }

                _geoDone = _geoTotal - onlineIps.Count;
                UpdateRunningTip(runId);

                var geoTasks = new List<Task>();
                foreach (string ip in onlineIps)
                    geoTasks.Add(UpdateGeoAsync(ip, _gridRows[ip], providerIndex,
                        token, runId));

                try
                {
                    await Task.WhenAll(geoTasks);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[FindIPs-GEO] " + ex.Message);
                    if (runId == _runId && !IsDisposed) SetStatus("查询出错");
                }
            }
            finally
            {
                usageScope.Dispose();
                lock (_runLock)
                {
                    if (ReferenceEquals(_runCts, cts)) _runCts = null;
                }
                cts.Dispose();
            }

            if (runId == _runId && !IsDisposed)
                SetStatus($"查询完毕(共 {_geoTotal} 个IP)");
        }

        private void CancelCurrentQuery()
        {
            // 只发取消信号并摘掉引用；取消令牌由创建它的那次查询负责释放。
            lock (_runLock)
            {
                _runCts?.Cancel();
                _runCts = null;
            }
        }

        private async Task UpdateGeoAsync(string ip, DataGridViewRow row, int providerIndex,
            CancellationToken token, int runId)
        {
            bool entered = false;
            try
            {
                await _geoSemaphore.WaitAsync(token);
                entered = true;

                string location = await Trace.ResolveSharedGeoAsync(
                    providerIndex, ip, token);
                if (token.IsCancellationRequested) return;

                if (!IsDisposed && runId == _runId &&
                    _gridRows.TryGetValue(ip, out DataGridViewRow current) &&
                    ReferenceEquals(current, row))
                {
                    row.Cells[colGEO.Name].Value =
                        string.IsNullOrWhiteSpace(location) ? "未知" : location;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FindIPs-GEO] " + ex.Message);
            }
            finally
            {
                if (entered) _geoSemaphore.Release();

                if (runId == _runId && !IsDisposed)
                {
                    _geoDone++;
                    UpdateRunningTip(runId);
                }
            }
        }

        private void UpdateRunningTip(int runId)
        {
            if (runId != _runId || IsDisposed) return;

            SetStatus(_geoDone >= _geoTotal
                ? $"查询完毕(共 {_geoTotal} 个IP)"
                : $"查询中({_geoDone}/{_geoTotal})...");
        }

        private void dataGridView1_CellMouseUp(object sender,
            DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 ||
                e.ColumnIndex != colGEO.Index)
                return;

            dataGridView1.ClearSelection();
            dataGridView1.Rows[e.RowIndex].Selected = true;
            ShowGeoDetails(e.RowIndex);
        }

        private void ShowGeoDetails(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= dataGridView1.Rows.Count) return;

            DataGridViewRow row = dataGridView1.Rows[rowIndex];
            string geo = Convert.ToString(row.Cells[colGEO.Name].Value);
            if (string.IsNullOrEmpty(geo)) return;

            string ip = Convert.ToString(row.Cells[coIP.Name].Value);
            Color foreground = Global.isThemelight ? Global.colorBlack : Global.colorWhite;
            Color background = Global.isThemelight
                ? SystemColors.Window : Color.FromArgb(32, 32, 32);

            using (var dialog = new Form())
            using (var details = new TextBox())
            {
                dialog.Text = "参考位置 (" + ip + ")";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.AutoScaleDimensions = new SizeF(96F, 96F);
                dialog.AutoScaleMode = AutoScaleMode.Dpi;
                dialog.ClientSize = new Size(640, 240);
                dialog.FormBorderStyle = FormBorderStyle.FixedSingle;
                dialog.ShowIcon = false;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.BackColor = background;
                dialog.ForeColor = foreground;

                details.Dock = DockStyle.Fill;
                details.Multiline = true;
                details.ReadOnly = true;
                details.WordWrap = true;
                details.ScrollBars = ScrollBars.Vertical;
                details.Font = Font;
                details.BackColor = background;
                details.ForeColor = foreground;
                details.Text = geo;
                details.SelectionStart = 0;
                details.SelectionLength = 0;

                dialog.Controls.Add(details);
                dialog.ShowDialog(this);
            }
        }

        #endregion

        #region 保存

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (dataGridView1.Rows.Count == 0)
            {
                MessageBox.Show("当前没有查询结果可以保存喵", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "请选择保存查询结果的位置";
                sfd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                string saveTime = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                sfd.FileName = $"NICX_FindIPs_{SanitizeFileName(comboGEO.Text)}_{saveTime}.txt";

                if (sfd.ShowDialog() != DialogResult.OK) return;

                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("=== 欢迎使用 手动批量查询IP ❤ 网络综合查询器X by Yumeyo ===");
                    sb.AppendLine("🔥 本次 手动批量查询IP 输出详情: ");
                    sb.AppendLine($"[位置接口]  {comboGEO.Text}");
                    sb.AppendLine($"[IP数量]  {dataGridView1.Rows.Count}");
                    sb.AppendLine($"[导出时间]  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine();

                    var headers = new List<string>();
                    foreach (DataGridViewColumn column in dataGridView1.Columns)
                        headers.Add(column.HeaderText);
                    sb.AppendLine(string.Join("\t", headers));

                    foreach (DataGridViewRow row in dataGridView1.Rows)
                    {
                        if (row.IsNewRow) continue;

                        var cells = new List<string>();
                        foreach (DataGridViewCell cell in row.Cells)
                            cells.Add(Convert.ToString(cell.Value));
                        sb.AppendLine(string.Join("\t", cells));
                    }

                    sb.AppendLine();
                    sb.AppendLine("=== 感谢使用 手动批量查询IP ❤ 网络综合查询器X by Yumeyo ===");
                    sb.AppendLine("======== 导出于 NetInfoCheckerX by Yumeyo ========");

                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);

                    MessageBox.Show($"保存[{sfd.FileName}]成功!", "保存成功了",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";

            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        #endregion

        #region 主题

        private void ApplyFindIPsTheme()
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

            lblVersion.BackColor = Color.Transparent;
            lblVersion.ForeColor = accent;
            lblGEO.BackColor = Color.Transparent;
            lblGEO.ForeColor = accent;

            txtIPs.BackColor = controlBackground;
            txtIPs.ForeColor = foreground;
            txtIPs.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;

            comboGEO.BackColor = controlBackground;
            comboGEO.ForeColor = foreground;
            comboGEO.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;

            lnkHeight.BackColor = Color.Transparent;
            lnkHeight.LinkColor = accent;
            lnkHeight.ActiveLinkColor = foreground;
            lnkHeight.VisitedLinkColor = accent;

            ApplyButtonTheme(btnOK);
            ApplyButtonTheme(btnSelect);
            ApplyButtonTheme(btnPaste);
            ApplyButtonTheme(btnSave);

            dataGridView1.EnableHeadersVisualStyles = false;
            dataGridView1.BackgroundColor = controlBackground;
            dataGridView1.GridColor = isLight
                ? Color.FromArgb(210, 210, 210) : Color.FromArgb(65, 65, 65);
            dataGridView1.ColumnHeadersDefaultCellStyle.BackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionBackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
            dataGridView1.DefaultCellStyle.BackColor = controlBackground;
            dataGridView1.DefaultCellStyle.ForeColor = foreground;
            dataGridView1.DefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.DefaultCellStyle.SelectionForeColor = accentText;
            dataGridView1.RowsDefaultCellStyle.BackColor = controlBackground;
            dataGridView1.RowsDefaultCellStyle.ForeColor = foreground;
            dataGridView1.RowsDefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.RowsDefaultCellStyle.SelectionForeColor = accentText;
            dataGridView1.AlternatingRowsDefaultCellStyle.BackColor = alternateBackground;
            dataGridView1.AlternatingRowsDefaultCellStyle.ForeColor = foreground;
            dataGridView1.AlternatingRowsDefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.AlternatingRowsDefaultCellStyle.SelectionForeColor = accentText;
        }

        private static Color GetReadableTextColor(Color background)
        {
            int brightness = (background.R * 299 + background.G * 587 +
                background.B * 114) / 1000;
            return brightness >= 145 ? Color.Black : Color.White;
        }

        private static void ApplyButtonTheme(Button button)
        {
            if (Global.isThemelight)
            {
                button.ForeColor = SystemColors.ControlText;
                button.BackColor = SystemColors.Control;
                button.FlatStyle = FlatStyle.Standard;
                button.UseVisualStyleBackColor = true;
                return;
            }

            button.ForeColor = Global.colorWhite;
            button.BackColor = Color.FromArgb(60, 60, 60);
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
            button.FlatAppearance.MouseOverBackColor = Global.Yumeyo2;
        }

        #endregion

        private void txtIPs_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;

                btnOK_Click(sender, e);
            }
        }
    }
}
