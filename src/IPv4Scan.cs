using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class IPv4Scan : Form
    {
        private const string DefaultAdapter = "0.0.0.0 (Any)";
        private readonly ConcurrentDictionary<string, IPv4ScanDevice> _pending = new ConcurrentDictionary<string, IPv4ScanDevice>();
        private readonly Dictionary<string, DataGridViewRow> _deviceRows = new Dictionary<string, DataGridViewRow>();
        private CancellationTokenSource _cancellation;
        private IPv4ScanEngine _engine;
        private IPv4ScanSnapshot _current;
        private IPv4ScanSnapshot _lastCompleted;
        private IPv4ScanSnapshot _baseline;
        private bool _running;
        private bool _closing;
        private Font _addedFont;
        private Font _removedFont;
        private string _databaseWarning = "";
        private Control _hoverControl;
        private string _hoverText;
        private Point _hoverPoint;
        private ToolStripItem _hoverStatusItem;

        public IPv4Scan()
        {
            InitializeComponent();
            // 减少整行重绘造成的闪烁；保留原生 DataGridView 及设计器编辑能力。
            typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).SetValue(gridDevices, true, null);
        }

        private void IPv4Scan_Load(object sender, EventArgs e)
        {
            MinimumSize = Size;
            ApplyTheme();
            UpdateGridMetrics();
            lblSoftware.Text = Global.exeName + " " + Global.Version;
            txtStart.Text = IPv4ScanSettings.Read("StartIP", "");
            txtEnd.Text = IPv4ScanSettings.Read("EndIP", "");
            txtCIDR.Text = IPv4ScanSettings.Read("CIDR", "");
            radioCIDR.Checked = IPv4ScanSettings.Read("Mode", "Range") == "CIDR";
            chkCompare.Checked = IPv4ScanSettings.Read("Compare", "false") == "true";
            RefreshAdapters(IPv4ScanSettings.Read("Adapter", DefaultAdapter));
            CloudControl.UsedTimesCounter("IPv4简单扫描");
            FitIdentityColumns();
            ApplyResultSort();
        }

        private void ApplyTheme()
        {
            bool light = Global.isThemelight;
            Color foreground = light ? Color.Black : Color.White;
            Color background = light ? Global.themeLight : Global.themeBlack;
            Color input = light ? SystemColors.Window : Color.FromArgb(32, 32, 32);
            Color accent = light ? Global.Yumeyo : Global.Yumeyo2;
            BackColor = background;
            ForeColor = foreground;
            foreach (Control control in Controls)
            {
                control.ForeColor = foreground;
                if (control is TextBox text)
                {
                    text.BackColor = input;
                    text.BorderStyle = light ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                }
                if (control is ComboBox combo)
                {
                    combo.BackColor = input;
                    combo.FlatStyle = light ? FlatStyle.Standard : FlatStyle.Flat;
                    combo.DrawMode = light ? DrawMode.Normal : DrawMode.OwnerDrawFixed;
                }
                if (control is Button button)
                {
                    button.BackColor = light ? SystemColors.Control : Color.FromArgb(60, 60, 60);
                    button.FlatStyle = light ? FlatStyle.Standard : FlatStyle.Flat;
                    button.UseVisualStyleBackColor = light;
                    button.FlatAppearance.BorderColor = Color.DimGray;
                    button.FlatAppearance.MouseOverBackColor = light ? Color.Empty : accent;
                }
            }
            lnkSettings.LinkColor = accent;
            lnkSettings.ActiveLinkColor = accent;
            statusStrip1.BackColor = background;
            statusStrip1.ForeColor = foreground;
            lblSoftware.ForeColor = accent;
            gridDevices.BackgroundColor = input;
            gridDevices.GridColor = light ? Color.Gainsboro : Color.FromArgb(60, 60, 60);
            gridDevices.EnableHeadersVisualStyles = false;
            gridDevices.DefaultCellStyle.BackColor = input;
            gridDevices.DefaultCellStyle.ForeColor = foreground;
            gridDevices.DefaultCellStyle.SelectionBackColor = accent;
            gridDevices.DefaultCellStyle.SelectionForeColor = accent.GetBrightness() > 0.55f ? Color.Black : Color.White;
            gridDevices.AlternatingRowsDefaultCellStyle.BackColor = light ? Color.FromArgb(248, 248, 248) : Color.FromArgb(25, 25, 25);
            gridDevices.ColumnHeadersDefaultCellStyle.BackColor = light ? SystemColors.Control : Color.FromArgb(45, 45, 48);
            gridDevices.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            gridDevices.ColumnHeadersDefaultCellStyle.SelectionBackColor = gridDevices.ColumnHeadersDefaultCellStyle.BackColor;
            gridDevices.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
        }

        private bool RefreshAdapters(string preferred)
        {
            try
            {
                NicHelper.RefreshAddressCombo(comboLocalEnd, new[] { DefaultAdapter, "系统默认 (ICMP兼容模式)" },
                    true, false, preferred, DefaultAdapter);
                return true;
            }
            catch (Exception ex) { SetStatus("获取网卡失败：" + ex.Message); return false; }
        }

        private void comboLocalEnd_DropDown(object sender, EventArgs e) => RefreshAdapters(comboLocalEnd.Text);

        private void comboLocalEnd_DrawItem(object sender, DrawItemEventArgs e)
        {
            var combo = (ComboBox)sender;
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            Color background = selected ? (Global.isThemelight ? Global.Yumeyo : Global.Yumeyo2) : combo.BackColor;
            Color foreground = selected ? (background.GetBrightness() > 0.55f ? Color.Black : Color.White) : combo.ForeColor;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0 && e.Index < combo.Items.Count)
            {
                Rectangle bounds = e.Bounds;
                TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, bounds, foreground,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
            e.DrawFocusRectangle();
        }

        private void lblAdapter_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (_running)
            {
                toolTip1.Show("请先停止扫描，再刷新网卡列表", lblAdapter, 0, lblAdapter.Height, 1200);
                return;
            }
            string preferred = comboLocalEnd.Text;
            if (!RefreshAdapters(preferred))
            {
                toolTip1.Show("网卡刷新失败，请查看状态栏", lblAdapter, 0, lblAdapter.Height, 1200);
                return;
            }
            bool retained = comboLocalEnd.Text == preferred;
            toolTip1.Show(retained ? "网卡列表已刷新" : "网卡列表已刷新，原网卡不存在，已回退到 0.0.0.0",
                lblAdapter, 0, lblAdapter.Height, retained ? 800 : 1500);
        }

        private void RangeMode_CheckedChanged(object sender, EventArgs e)
        {
            txtStart.Enabled = txtEnd.Enabled = radioRange.Checked;
            txtCIDR.Enabled = !radioRange.Checked;
        }

        private IPAddress ResolveScanSource(IPv4ScanRange range)
        {
            string selected = comboLocalEnd.Text;
            if (selected.StartsWith("系统默认", StringComparison.Ordinal)) return null;
            if (selected != DefaultAdapter)
                return IPv4ScanRange.Address(IPv4ScanRange.ParseAddress(selected.Split(' ')[0]));

            // 与 PingPP 一样用 UDP Connect 查询实际路由，不发送应用数据。
            // 选中后固定本轮源地址，界面显示与 ICMP/ARP/TCP 实际使用的出口一致。
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(IPv4ScanRange.Address(range.First), 1);
                    var local = socket.LocalEndPoint as IPEndPoint;
                    if (local == null || local.Address.Equals(IPAddress.Any)) return null;
                    string address = local.Address.ToString();
                    foreach (object item in comboLocalEnd.Items)
                    {
                        if (item.ToString().StartsWith(address + " (", StringComparison.Ordinal))
                        {
                            comboLocalEnd.SelectedItem = item;
                            return local.Address;
                        }
                    }
                    // 系统实际出口可能被全局网卡筛选隐藏；只补入本次实际使用的一项。
                    string name = "实际出口";
                    foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        IPInterfaceProperties properties;
                        if (NicHelper.TryGetIPProperties(adapter, out properties) &&
                            properties.UnicastAddresses.Any(a => a.Address.Equals(local.Address)))
                        {
                            name = adapter.Name;
                            break;
                        }
                    }
                    comboLocalEnd.SelectedIndex = comboLocalEnd.Items.Add(address + " (" + name + ")");
                    return local.Address;
                }
            }
            catch (SocketException) { return null; }
            catch (NetworkInformationException) { return null; }
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (_running)
            {
                _cancellation?.Cancel();
                btnStart.Enabled = false;
                btnStart.Text = "正在停止…";
                SetStatus("正在停止…");
                return;
            }
            IPv4ScanRange range;
            IPAddress source = null;
            IPv4ScanSettings settings;
            try
            {
                range = IPv4ScanRange.Parse(radioCIDR.Checked ? txtCIDR.Text : txtStart.Text, txtEnd.Text, radioCIDR.Checked);
                // 硬性限制避免误输 /0 后等待数年；合法大网段仍可用起止 IP 分批扫描。
                if (range.Count > 65536) throw new FormatException("简单扫描每轮最多支持 65536 个地址，请缩小范围后分批扫描。");
                source = ResolveScanSource(range);
                settings = IPv4ScanSettings.Load();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "IPv4简单扫描", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _baseline = _lastCompleted;
            _current = new IPv4ScanSnapshot { Range = range, Adapter = comboLocalEnd.Text };
            _databaseWarning = "";
            gridDevices.Rows.Clear();
            _deviceRows.Clear();
            _pending.Clear();
            _engine = new IPv4ScanEngine();
            _cancellation = new CancellationTokenSource();
            _running = true;
            SetRunningState();
            SaveInputs();
            progressTimer.Start();
            SetStatus("正在加载本地 MAC 数据库…");
            try
            {
                var database = await Task.Run(() => IPv4ScanVendorDatabase.Load(Application.StartupPath));
                if (_closing) return;
                _databaseWarning = database.Warning;
                string adapter = _current.Adapter;
                _current = await Task.Run(() => _engine.ScanAsync(range, source, settings, database, QueueResult, _cancellation.Token));
                if (_closing) return;
                _current.Adapter = adapter;
                _pending.Clear();
                if (_current.Completed) _lastCompleted = _current;
                RenderResults();
                string status = _current.Completed ? "扫描完成" : "已停止（部分结果，不判断增减）";
                SetResultStatus(status);
            }
            catch (Exception ex)
            {
                if (_closing) return;
                DrainResults();
                _current.Devices.Clear();
                _current.Devices.AddRange(gridDevices.Rows.Cast<DataGridViewRow>().Select(r => (IPv4ScanDevice)r.Tag));
                SetStatus("扫描失败（部分结果，不判断增减）：" + ex.Message);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                _running = false;
                if (!_closing)
                {
                    progressTimer.Stop();
                    SetRunningState();
                }
            }
        }

        private void SetRunningState()
        {
            foreach (Control c in new Control[] { radioRange, radioCIDR, comboLocalEnd, btnLoad })
                c.Enabled = !_running;
            btnStart.Enabled = true;
            btnStart.Text = _running ? "停止" : "开扫";
            toolTip1.SetToolTip(btnStart, _running ? "停止本轮扫描，保留已发现的设备；部分结果不判断设备增减。" :
                "扫描指定 IPv4 范围中的在线设备；再次点击可停止。");
            txtStart.Enabled = txtEnd.Enabled = !_running && radioRange.Checked;
            txtCIDR.Enabled = !_running && radioCIDR.Checked;
            btnSave.Enabled = !_running && _current != null;
        }

        private void progressTimer_Tick(object sender, EventArgs e)
        {
            DrainResults();
            if (_cancellation?.IsCancellationRequested == true) return;
            SetStatus($"扫描中 {_engine.Scanned}/{_current.Range.Count}，已发现 {gridDevices.Rows.Count} 台" +
                (string.IsNullOrEmpty(_databaseWarning) ? "" : "；" + _databaseWarning));
        }

        private void DrainResults()
        {
            var elapsed = Stopwatch.StartNew();
            int count = 0, ipWidth = colIP.Width, macWidth = colMAC.Width;
            gridDevices.SuspendLayout();
            try
            {
                foreach (var update in _pending)
                {
                    string ip = update.Key;
                    if (!_pending.TryRemove(ip, out IPv4ScanDevice device)) continue;
                    AddRow(device, "");
                    var row = _deviceRows[ip];
                    ipWidth = Math.Max(ipWidth, row.Cells[colIP.Index].PreferredSize.Width);
                    macWidth = Math.Max(macWidth, row.Cells[colMAC.Index].PreferredSize.Width);
                    // 大网段也不让一次刷新长时间占住 UI 线程，剩余更新留到下一次。
                    if (++count >= 256 || elapsed.ElapsedMilliseconds >= 30) break;
                }
                bool resized = colIP.Width != ipWidth || colMAC.Width != macWidth;
                if (colIP.Width != ipWidth) colIP.Width = ipWidth;
                if (colMAC.Width != macWidth) colMAC.Width = macWidth;
                if (resized) RestoreFillWeights();
                if (count > 0) ApplyResultSort();
            }
            finally { gridDevices.ResumeLayout(); }
        }

        private void QueueResult(IPv4ScanDevice device) => _pending[device.IP] = device;

        private void AddRow(IPv4ScanDevice device, string marker)
        {
            if (!_deviceRows.TryGetValue(device.IP, out DataGridViewRow row))
            {
                row = gridDevices.Rows[gridDevices.Rows.Add()];
                _deviceRows[device.IP] = row;
            }
            object[] values = { marker, device.IP, device.Latency, device.MAC, device.Vendor, device.HostName, device.Ports };
            for (int i = 0; i < values.Length; i++)
                if (!Equals(row.Cells[i].Value, values[i])) row.Cells[i].Value = values[i];
            row.Tag = device;
            row.DefaultCellStyle.Font = marker == "+" ? _addedFont : marker == "-" ? _removedFont : null;
            foreach (DataGridViewCell cell in row.Cells)
            {
                string text = cell.ColumnIndex == colVendor.Index && !string.IsNullOrEmpty(device.VendorToolTip)
                    ? device.VendorToolTip : Convert.ToString(cell.Value);
                cell.ToolTipText = text;
            }
        }

        private void RenderResults()
        {
            ClearHoverToolTip(gridDevices);
            colChange.Visible = chkCompare.Checked;
            gridDevices.Rows.Clear();
            _deviceRows.Clear();
            if (_current == null) return;
            foreach (var change in IPv4ScanChange.Compare(_current, _baseline, chkCompare.Checked)) AddRow(change.Device, change.Marker);
            FitIdentityColumns();
            ApplyResultSort();
        }

        private void ApplyResultSort()
        {
            // 保留用户当前选择；没有排序选择时使用 IP 数值升序。每批更新只排序一次。
            var column = gridDevices.SortedColumn ?? colIP;
            var direction = gridDevices.SortOrder == SortOrder.Descending ?
                System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
            gridDevices.Sort(column, direction);
        }

        private void FitIdentityColumns()
        {
            // 标识列只占完整显示所需空间，其余三列用 Fill 自动分配剩余窗口宽度。
            foreach (var column in new[] { colIP, colMAC })
                column.Width = column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true);
            colLatency.Width = Math.Max(colLatency.GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true),
                new[] { gridDevices.Font, _addedFont, _removedFont }.Where(font => font != null)
                    .Max(font => TextRenderer.MeasureText("1000ms (ICMP)", font).Width) + 10);
            colChange.Width = colChange.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true);
            foreach (var column in new[] { colVendor, colHostName, colPorts })
                column.MinimumWidth = column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true);
            RestoreFillWeights();
        }

        private void RestoreFillWeights()
        {
            // 调整其他列宽时，DataGridView 会重算 FillWeight；恢复比例，避免 TCP 列越扫越窄。
            colVendor.FillWeight = 80F;
            colHostName.FillWeight = 60F;
            colPorts.FillWeight = 300F;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (gridDevices != null && colIP != null && !_closing) UpdateGridMetrics();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            UpdateGridMetrics();
        }

        private void UpdateGridMetrics()
        {
            if (statusStrip1 != null) statusStrip1.Font = Font;
            gridDevices.ColumnHeadersDefaultCellStyle.Font = gridDevices.Font;
            Font oldAdded = _addedFont, oldRemoved = _removedFont;
            _addedFont = new Font(gridDevices.Font, FontStyle.Bold);
            _removedFont = new Font(gridDevices.Font, FontStyle.Italic);
            int height = Math.Max((int)(23 * DeviceDpi / 96f), gridDevices.Font.Height + 6);
            gridDevices.RowTemplate.Height = height;
            foreach (DataGridViewRow row in gridDevices.Rows)
            {
                row.DefaultCellStyle.Font = Convert.ToString(row.Cells[0].Value) == "+" ? _addedFont :
                    Convert.ToString(row.Cells[0].Value) == "-" ? _removedFont : null;
                row.Height = height;
            }
            oldAdded?.Dispose();
            oldRemoved?.Dispose();
            FitIdentityColumns();
        }

        private void chkCompare_CheckedChanged(object sender, EventArgs e)
        {
            colChange.Visible = chkCompare.Checked;
            if (!_running)
            {
                RenderResults();
                if (_current != null) SetResultStatus(_current.Completed ? "完整记录" : "部分记录，不判断增减");
            }
        }

        private void SetResultStatus(string prefix)
        {
            string text = prefix + $"，{_current.Devices.Count} 台设备";
            if (chkCompare.Checked && _current.Completed)
            {
                if (_baseline == null) text += "；无上次完整记录，本次将作为下次对比基准";
                else
                {
                    var changes = IPv4ScanChange.Compare(_current, _baseline, true).ToArray();
                    text += $"；增加 {changes.Count(c => c.Marker == "+")}，减少 {changes.Count(c => c.Marker == "-")}";
                    if (_current.Range.First != _baseline.Range.First || _current.Range.Last != _baseline.Range.Last)
                        text += "（只对比重叠范围）";
                    if (_current.Adapter != _baseline.Adapter) text += "；两次出口不同";
                }
            }
            if (!string.IsNullOrEmpty(_databaseWarning)) text += "；" + _databaseWarning;
            SetStatus(text);
        }

        private void SetStatus(string text)
        {
            lblStatus.Text = text;
            if (_hoverStatusItem == lblStatus) _hoverText = text;
        }

        private void QueueHoverToolTip(Control owner, string text, Point point)
        {
            if (_hoverControl != null)
            {
                toolTip1.Hide(_hoverControl);
                toolTip1.SetToolTip(_hoverControl, "");
            }
            hoverTimer.Stop();
            _hoverControl = owner;
            _hoverStatusItem = null;
            _hoverText = text;
            _hoverPoint = point;
            if (!string.IsNullOrEmpty(text)) hoverTimer.Start();
        }

        private void ClearHoverToolTip(Control owner)
        {
            if (_hoverControl != owner) return;
            hoverTimer.Stop();
            toolTip1.Hide(owner);
            toolTip1.SetToolTip(owner, "");
            _hoverControl = null;
            _hoverText = null;
            _hoverStatusItem = null;
        }

        private void hoverTimer_Tick(object sender, EventArgs e)
        {
            hoverTimer.Stop();
            if (_hoverControl != null && !_hoverControl.IsDisposed && !string.IsNullOrEmpty(_hoverText))
                toolTip1.Show(_hoverText, _hoverControl, _hoverPoint, toolTip1.AutoPopDelay);
        }

        private void gridDevices_CellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            string text = e.RowIndex < 0 ? GetColumnHint(gridDevices.Columns[e.ColumnIndex]) :
                gridDevices.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText;
            Rectangle cell = gridDevices.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, true);
            QueueHoverToolTip(gridDevices, text, new Point(cell.Left + 8, cell.Bottom + 2));
        }

        private static string GetColumnHint(DataGridViewColumn column)
        {
            switch (column.Name)
            {
                case "colChange": return "+：本次新增（粗体）；-：上次有、本次未发现（斜体）。\r\n只比较两次完整记录的重叠 IP 范围。";
                case "colIP": return "在线设备的 IPv4 地址；点击表头按地址排序。";
                case "colLatency": return "ICMP 和 ARP 分别显示探测延迟；ARP 仅适用于直连网段。";
                case "colMAC": return "直连设备的 MAC 地址；跨网段通常无法获取。";
                case "colVendor": return "来自 oui.txt（数据库 2）和 oui.csv（数据库 1）；悬停单元格查看分行来源。";
                case "colHostName": return "使用系统 DNS 反查的主机名，未找到时留空。";
                case "colPorts": return "本次已发现的开放 TCP 端口；未发现时留空。";
                default: return column.HeaderText;
            }
        }

        private void gridDevices_CellMouseLeave(object sender, DataGridViewCellEventArgs e) => ClearHoverToolTip(gridDevices);
        private void gridDevices_MouseLeave(object sender, EventArgs e) => ClearHoverToolTip(gridDevices);
        private void gridDevices_Scroll(object sender, ScrollEventArgs e) => ClearHoverToolTip(gridDevices);

        private void StatusItem_MouseEnter(object sender, EventArgs e)
        {
            var item = (ToolStripItem)sender;
            QueueHoverToolTip(statusStrip1, item.Text, new Point(item.Bounds.Left, -statusStrip1.Height));
            _hoverStatusItem = item;
        }

        private void StatusItem_MouseLeave(object sender, EventArgs e) => ClearHoverToolTip(statusStrip1);

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (_current == null || _running) return;
            using (var dialog = new SaveFileDialog
            {
                Title = "保存 IPv4 扫描记录",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                FileName = $"NICX_IPv4Scan_{_current.Range.Description}_{_current.Started:yyyyMMdd_HHmmss}.txt"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { _current.Save(dialog.FileName); SetStatus("记录已保存：" + dialog.FileName); }
                catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "IPv4简单扫描", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void btnLoad_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Title = "读取上次 IPv4 扫描记录", Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var loaded = IPv4ScanSnapshot.Load(dialog.FileName);
                    _baseline = null;
                    _current = loaded;
                    _lastCompleted = loaded.Completed ? loaded : null;
                    _databaseWarning = "";
                    radioRange.Checked = true;
                    txtStart.Text = IPv4ScanRange.Address(loaded.Range.First).ToString();
                    txtEnd.Text = IPv4ScanRange.Address(loaded.Range.Last).ToString();
                    chkCompare.Checked = true;
                    RenderResults();
                    btnSave.Enabled = true;
                    SetStatus($"已读取 {loaded.Devices.Count} 台设备；" + (loaded.Completed ? "下次扫描将与此记录对比" : "文件为部分记录，不能作为增减对比基准"));
                }
                catch (Exception ex) { MessageBox.Show(this, "读取失败：" + ex.Message, "IPv4简单扫描", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void lnkSettings_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var existing = Application.OpenForms.OfType<About>().FirstOrDefault();
            if (existing != null) { PrepareSettingsIcon(existing); existing.SelectSimpleScanSettings(); existing.Show(); existing.Activate(); }
            else
            {
                var settings = new About();
                PrepareSettingsIcon(settings);
                settings.SelectSimpleScanSettings();
                settings.Show(this);
            }
        }

        private void PrepareSettingsIcon(About settings)
        {
            var main = Application.OpenForms.OfType<Form1>().FirstOrDefault();
            if (main != null) main.PopulateAboutPictures(settings);
            if (settings.pictureBox1.Image == null)
                settings.pictureBox1.Image = pictureBox1.Image ?? (Global.isThemelight ? Global.GetIconw() : Global.GetIcon());
            if (settings.pictureBox2.Image == null)
                settings.pictureBox2.Image = pictureBox1.Image ?? (Global.isThemelight ? Global.GetIconw() : Global.GetIcon());
        }

        private void gridDevices_SortCompare(object sender, DataGridViewSortCompareEventArgs e)
        {
            if (e.Column != colIP) return;
            string left = Convert.ToString(e.CellValue1), right = Convert.ToString(e.CellValue2);
            e.SortResult = string.IsNullOrEmpty(left) ? (string.IsNullOrEmpty(right) ? 0 : 1) : string.IsNullOrEmpty(right) ? -1 :
                IPv4ScanRange.ParseAddress(left).CompareTo(IPv4ScanRange.ParseAddress(right));
            e.Handled = true;
        }

        private void SaveInputs()
        {
            bool saved = IPv4ScanSettings.Write("StartIP", txtStart.Text.Trim()) &
                IPv4ScanSettings.Write("EndIP", txtEnd.Text.Trim()) &
                IPv4ScanSettings.Write("CIDR", txtCIDR.Text.Trim()) &
                IPv4ScanSettings.Write("Mode", radioCIDR.Checked ? "CIDR" : "Range") &
                IPv4ScanSettings.Write("Compare", chkCompare.Checked ? "true" : "false") &
                IPv4ScanSettings.Write("Adapter", comboLocalEnd.Text);
            if (!saved) SetStatus("输入配置保存失败，请检查程序目录的写入权限。");
        }

        private void IPv4Scan_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveInputs();
            _closing = true;
            _cancellation?.Cancel();
            progressTimer.Stop();
            hoverTimer.Stop();
        }

        private void ReleaseScanResources()
        {
            _closing = true;
            _cancellation?.Cancel();
            _addedFont?.Dispose();
            _removedFont?.Dispose();
            _addedFont = _removedFont = null;
        }
    }
}
