using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScottPlot;

namespace NetInfoCheckerX
{
    internal sealed partial class UDPGameTestChart : Form
    {
        private readonly object _sync = new object();
        private readonly List<double> _times = new List<double>(8192);
        private readonly List<double> _delays = new List<double>(8192);
        private readonly List<double> _upJitters = new List<double>(8192);
        private readonly List<double> _downJitters = new List<double>(8192);
        private readonly List<double> _upEvents = new List<double>();
        private readonly List<double> _downEvents = new List<double>();
        private bool _showDelay = true;
        private bool _showUpJitter = true;
        private bool _showDownJitter = true;
        private bool _showEvents = true;
        private bool _lastUpImpairment;
        private bool _lastDownImpairment;
        private bool _dirty;
        private bool _allowClose;
        private bool _autoScroll = true;
        private double _lastSetXMin;
        private double _lastSetXMax;
        private float _dpiScale = 1F;
        private string _target = "";
        private const double DisplayWindowSeconds = 10;
        private const double AutoScrollMarginSeconds = 2;
        private static readonly (double ms, Color color)[] Thresholds =
        {
            (10.0, Color.Lime),
            (20.0, Color.MediumSpringGreen),
            (30.0, Color.FromArgb(185, 210, 50)),
            (40.0, Color.Gold),
            (50.0, Color.Orange),
            (100.0, Color.Tomato)
        };

        public UDPGameTestChart()
        {
            InitializeComponent();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _plot.MouseDoubleClick += (s, e) => { _autoScroll = true; };
            ConfigurePlotStyle();
            DrawThresholdLines();
            _plot.Refresh();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("复制图像", null, (s, e) =>
            {
                using (Bitmap bitmap = _plot.Plot.Render())
                {
                    if (!ClipboardHelper.TrySetImage(bitmap))
                        MessageBox.Show("剪贴板正被其他程序占用，请稍后重试。", "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            menu.Items.Add("图像另存为...", null, (s, e) =>
            {
                using (SaveFileDialog dialog = new SaveFileDialog
                {
                    Filter = "PNG图像|*.png|JPEG图像|*.jpg|BMP图像|*.bmp",
                    FileName = "UDPGameTest_" + _target.Replace(':', '_') + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                })
                {
                    if (dialog.ShowDialog() == DialogResult.OK) _plot.Plot.SaveFig(dialog.FileName);
                }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("缩放至适应数据", null, (s, e) => _plot.Plot.AxisAuto());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("操作帮助", null, (s, e) =>
                MessageBox.Show("左键拖拽：平移\n右键拖拽：框选缩放\n滚轮：缩放\n中键：适应数据\n双击：恢复自动滚动\n最小化：暂停绘制",
                    "UDP 游戏模拟统计图", MessageBoxButtons.OK, MessageBoxIcon.Information));
            _plot.ContextMenuStrip = menu;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        public void SetInfo(string target, int load, int bufferTicks)
        {
            lock (_sync)
            {
                _target = target ?? "";
                ClearDataLocked();
                _dirty = false;
                _autoScroll = true;
            }
            Text = string.Format("延迟测试-UDP游戏模拟 统计图 ✧ NICX - {0} [负载{1}/缓冲{2}tick]",
                target, load, bufferTicks);
            _plot.Plot.Clear();
            ConfigurePlotStyle(_dpiScale);
            DrawThresholdLines();
            _plot.Refresh();
        }

        public void SetSeriesVisibility(bool delay, bool upJitter, bool downJitter, bool events)
        {
            lock (_sync)
            {
                _showDelay = delay;
                _showUpJitter = upJitter;
                _showDownJitter = downJitter;
                _showEvents = events;
                _dirty = true;
            }
        }

        public void AddDataPoint(double elapsed, double delay, double upJitter, double downJitter,
            bool upImpairment, bool downImpairment)
        {
            if (WindowState == FormWindowState.Minimized) return;
            lock (_sync)
            {
                _times.Add(elapsed);
                _delays.Add(delay);
                _upJitters.Add(upJitter);
                _downJitters.Add(downJitter);
                if (upImpairment && !_lastUpImpairment) _upEvents.Add(elapsed);
                if (downImpairment && !_lastDownImpairment) _downEvents.Add(elapsed);
                _lastUpImpairment = upImpairment;
                _lastDownImpairment = downImpairment;
                _dirty = true;
            }
        }

        private void UDPGameTestChart_Load(object sender, EventArgs e)
        {
            _dpiScale = GetDpiForWindow(Handle) / 96F;
            if (_dpiScale > 1.05F) ConfigurePlotStyle(_dpiScale);
            MinimumSize = Size;
        }

        private void UDPGameTestChart_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized) PauseAndClear();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshChart();
        }

        private void ConfigurePlotStyle(float scale = 1F)
        {
            Plot plot = _plot.Plot;
            plot.Style(figureBackground: Color.FromArgb(17, 17, 17), dataBackground: Color.FromArgb(24, 24, 24));
            plot.Grid(color: Color.FromArgb(42, 42, 42), lineStyle: LineStyle.Solid);
            plot.XAxis.Color(Color.FromArgb(160, 160, 160));
            plot.YAxis.Color(Color.FromArgb(160, 160, 160));
            plot.XAxis.Label(string.Format("测试时长(s) ✧ {0} {1}", Global.exeName, Global.Version),
                color: Color.FromArgb(200, 200, 200), size: 11 * scale);
            plot.YAxis.Label("延迟/抖动(ms)/过/丢 ✧ NICX", color: Color.FromArgb(200, 200, 200), size: 11 * scale);
            plot.XAxis.TickLabelStyle(color: Color.FromArgb(180, 180, 180), fontSize: 8 * scale);
            plot.YAxis.TickLabelStyle(color: Color.FromArgb(180, 180, 180), fontSize: 8 * scale);
            plot.Layout(left: 12 * scale, right: 3 * scale, bottom: 12 * scale, top: 3 * scale);
            plot.XAxis2.Hide();
            plot.YAxis2.Hide();
            plot.SetAxisLimits(0, 10, 0, 100);
            _plot.Refresh();
        }

        private void DrawThresholdLines()
        {
            Plot plot = _plot.Plot;
            foreach ((double ms, Color color) threshold in Thresholds)
            {
                var line = plot.AddHorizontalLine(threshold.ms, threshold.color, width: 1, style: LineStyle.Dot);
                line.IgnoreAxisAuto = true;
            }
        }

        private void RefreshChart()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                PauseAndClear();
                return;
            }

            double[] times, delays, up, down, upEvents, downEvents;
            bool showDelay, showUp, showDown, showEvents, autoScroll;
            lock (_sync)
            {
                if (!_dirty || _times.Count == 0) return;
                _dirty = false;
                times = _times.ToArray();
                delays = _delays.ToArray();
                up = _upJitters.ToArray();
                down = _downJitters.ToArray();
                upEvents = _upEvents.ToArray();
                downEvents = _downEvents.ToArray();
                showDelay = _showDelay;
                showUp = _showUpJitter;
                showDown = _showDownJitter;
                showEvents = _showEvents;
                autoScroll = _autoScroll;
            }

            Plot plot = _plot.Plot;
            AxisLimits currentLimits = plot.GetAxisLimits();
            if (_lastSetXMax > 0 && autoScroll &&
                (Math.Abs(currentLimits.XMin - _lastSetXMin) > .5 || Math.Abs(currentLimits.XMax - _lastSetXMax) > .5))
            {
                autoScroll = false;
                _autoScroll = false;
            }

            //主要线段颜色设置
            plot.Clear();
            DrawThresholdLines();
            if (showDelay) plot.AddScatter(times, delays, Color.Cyan, lineWidth: 1, markerSize: times.Length <= 200 ? 3 : 0, label: "当前延迟");
            if (showUp) plot.AddScatter(times, up, Color.LightGreen, lineWidth: 1, markerSize: 0, label: "上行抖动");
            if (showDown) plot.AddScatter(times, down, Color.Orange, lineWidth: 1, markerSize: 0, label: "下行抖动");
            if (showEvents)
            {
                foreach (double x in upEvents) plot.AddVerticalLine(x, Color.OrangeRed, width: 2, style: LineStyle.Dot);
                foreach (double x in downEvents) plot.AddVerticalLine(x, Color.HotPink, width: 2, style: LineStyle.Dot);
            }
            if (showDelay || showUp || showDown) plot.Legend(location: Alignment.UpperRight);

            double elapsed = times[times.Length - 1];
            double xMin;
            double xMax;
            if (autoScroll)
            {
                xMax = elapsed + AutoScrollMarginSeconds;
                xMin = Math.Max(0, xMax - DisplayWindowSeconds);
            }
            else
            {
                xMin = currentLimits.XMin;
                xMax = currentLimits.XMax;
                if (elapsed > 0 && xMax >= elapsed + AutoScrollMarginSeconds) _autoScroll = true;
            }

            double visibleMax = 10;
            for (int i = 0; i < times.Length; i++)
            {
                if (times[i] < xMin - .5 || times[i] > xMax + .5) continue;
                if (showDelay) visibleMax = Math.Max(visibleMax, delays[i]);
                if (showUp) visibleMax = Math.Max(visibleMax, up[i]);
                if (showDown) visibleMax = Math.Max(visibleMax, down[i]);
            }
            plot.SetAxisLimits(xMin, xMax, 0, Math.Max(20, visibleMax * 1.15));
            _lastSetXMin = xMin;
            _lastSetXMax = xMax;
            ConfigureLabelsAfterClear();
            _plot.Refresh();
        }

        private void ConfigureLabelsAfterClear()
        {
            Plot plot = _plot.Plot;
            plot.Style(figureBackground: Color.FromArgb(17, 17, 17), dataBackground: Color.FromArgb(24, 24, 24));
            plot.Grid(color: Color.FromArgb(42, 42, 42), lineStyle: LineStyle.Solid);
            plot.XAxis.Color(Color.FromArgb(160, 160, 160));
            plot.YAxis.Color(Color.FromArgb(160, 160, 160));
            plot.XAxis.Label(string.Format("测试时长(s) ✧ {0} {1}", Global.exeName, Global.Version),
                color: Color.FromArgb(200, 200, 200), size: 11 * _dpiScale);
            plot.YAxis.Label("延迟/抖动(ms)/过/丢 ✧ NICX", color: Color.FromArgb(200, 200, 200), size: 11 * _dpiScale);
            plot.XAxis.TickLabelStyle(color: Color.FromArgb(180, 180, 180), fontSize: 8 * _dpiScale);
            plot.YAxis.TickLabelStyle(color: Color.FromArgb(180, 180, 180), fontSize: 8 * _dpiScale);
            plot.Layout(left: 12 * _dpiScale, right: 3 * _dpiScale, bottom: 12 * _dpiScale, top: 3 * _dpiScale);
            plot.XAxis2.Hide();
            plot.YAxis2.Hide();
        }

        private void ClearDataLocked()
        {
            _times.Clear();
            _delays.Clear();
            _upJitters.Clear();
            _downJitters.Clear();
            _upEvents.Clear();
            _downEvents.Clear();
            _lastUpImpairment = false;
            _lastDownImpairment = false;
            _lastSetXMin = _lastSetXMax = 0;
        }

        private void PauseAndClear()
        {
            bool hadData;
            lock (_sync)
            {
                hadData = _times.Count > 0 || _upEvents.Count > 0 || _downEvents.Count > 0;
                if (!hadData) return;
                ClearDataLocked();
                _dirty = false;
                _autoScroll = true;
            }
            _plot.Plot.Clear();
            ConfigurePlotStyle(_dpiScale);
            DrawThresholdLines();
            _plot.Refresh();
        }

        private void UDPGameTestChart_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_allowClose)
            {
                _refreshTimer.Stop();
                return;
            }
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
        }

        public void Shutdown()
        {
            _allowClose = true;
            try { Close(); } catch { }
        }

        private const int CS_NOCLOSE = 0x200;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CS_NOCLOSE;
                return parameters;
            }
        }

        private void _plot_Load(object sender, EventArgs e)
        {

        }
    }
}
