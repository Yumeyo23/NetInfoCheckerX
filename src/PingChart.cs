using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScottPlot;

namespace NetInfoCheckerX
{
    public partial class PingChart : Form
    {
        private System.Windows.Forms.Timer _refreshTimer;

        private readonly object _dataLock = new object();
        private readonly List<double> _times = new List<double>(8192);
        private readonly List<double> _rtts = new List<double>(8192);

        private string _target = "";
        private string _protocol = "";
        private double _elapsedSeconds;
        private bool _autoScroll = true;
        private bool _chartDirty;
        private bool _allowClose;
        private double _lastSetXMin, _lastSetXMax;
        private const double DisplayWindowSeconds = 10;//超过自动滚动显示
        private const double AutoScrollMarginSec = 2;
        private const int RefreshMs = 100;

        private static readonly (double ms, Color color)[] _thresholds = new[]
        {
            (15.0,  Color.Lime),
            (30.0,  Color.MediumSpringGreen),
            (50.0,  Color.FromArgb(185, 210, 50)),
            (100.0, Color.Gold),
            (200.0, Color.Orange),
            (500.0, Color.Tomato),
        };

        public PingChart()
        {
            InitializeComponent();

            formsPlot1.MouseDoubleClick += (s, e) => { _autoScroll = true; };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("复制图像", null, (s2, e2) =>
            {
                using (var bmp = formsPlot1.Plot.Render())
                {
                    if (!ClipboardHelper.TrySetImage(bmp))
                        MessageBox.Show("剪贴板正被其他程序占用，请稍后重试。", "复制失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            contextMenu.Items.Add("图像另存为...", null, (s2, e2) =>
            {
                using (var sfd = new SaveFileDialog { Filter = "PNG图像|*.png|JPEG图像|*.jpg|BMP图像|*.bmp", FileName = $"PingChart_{_target}_{DateTime.Now:yyyyMMdd_HHmmss}" })
                {
                    if (sfd.ShowDialog() == DialogResult.OK)
                        formsPlot1.Plot.SaveFig(sfd.FileName);
                }
            });
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("缩放至适应数据", null, (s2, e2) => formsPlot1.Plot.AxisAuto());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("操作帮助", null, (s2, e2) =>
                MessageBox.Show("左键拖拽：平移\n右键拖拽：框选缩放\n滚轮：缩放\n中键：适应数据\n双击：恢复自动滚动\n最小化：暂停绘制",
                    "Ping+ 统计图", MessageBoxButtons.OK, MessageBoxIcon.Information));
            formsPlot1.ContextMenuStrip = contextMenu;

            ConfigurePlotStyle();
            DrawThresholdLines();
            formsPlot1.Refresh();

            _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshMs };
            _refreshTimer.Tick += (s, e) => RefreshChart();
            _refreshTimer.Start();

            this.FormClosing += (s, e) =>
            {
                if (_allowClose)
                {
                    _refreshTimer.Stop();
                    return;
                }
                e.Cancel = true;
                this.WindowState = FormWindowState.Minimized;
            };
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private void ConfigurePlotStyle(float scale = 1f)
        {
            var plt = formsPlot1.Plot;
            plt.Style(
                figureBackground: Color.FromArgb(17, 17, 17),
                dataBackground: Color.FromArgb(24, 24, 24)
            );
            plt.Grid(color: Color.FromArgb(42, 42, 42), lineStyle: LineStyle.Solid);
            plt.XAxis.Color(Color.FromArgb(160, 160, 160));
            plt.YAxis.Color(Color.FromArgb(160, 160, 160));
            plt.XAxis.Label($"测试时长(s) ✧ {Global.exeName} {Global.Version}", color: Color.FromArgb(200, 200, 200), size: 11 * scale);
            plt.YAxis.Label("延迟(ms) ✧ NICX", color: Color.FromArgb(200, 200, 200), size: 11 * scale);
            plt.XAxis.TickLabelStyle(color: Color.FromArgb(180, 180, 180), fontSize: 8 * scale);
            plt.YAxis.TickLabelStyle(color: Color.FromArgb(180, 180, 180), fontSize: 8 * scale);
            plt.Layout(left: 12 * scale, right: 3 * scale, bottom: 12 * scale, top: 3 * scale);
            plt.XAxis2.Hide();
            plt.YAxis2.Hide();
            plt.SetAxisLimits(0, 10, 0, 100);
            formsPlot1.Refresh();
        }

        private void DrawThresholdLines()
        {
            var plt = formsPlot1.Plot;
            foreach (var (ms, color) in _thresholds)
            {
                var hline = plt.AddHorizontalLine(ms, color, width: 1, style: LineStyle.Dot);
                hline.IgnoreAxisAuto = true;
            }
        }

        public void SetInfo(string target, string protocol, int tickRate)
        {
            lock (_dataLock)
            {
                _target = target;
                _protocol = protocol;
                _times.Clear();
                _rtts.Clear();
                _times.Capacity = 8192;
                _rtts.Capacity = 8192;
                _elapsedSeconds = 0;
                _autoScroll = true;
            }

            UpdateTitle(target, protocol, tickRate);
        }

        private void UpdateTitle(string target, string protocol, int tickRate)
        {
            if (!this.IsHandleCreated) return;
            this.BeginInvoke(new Action(() =>
            {
                this.Text = $"Ping+ 统计图 ✧ NetInfoCheckerX - {target} [{protocol}] @{tickRate}tick";
            }));
        }

        public void AddDataPoint(double elapsedSeconds, double rttMs)
        {
            lock (_dataLock)
            {
                _times.Add(elapsedSeconds);
                _rtts.Add(rttMs);
                _elapsedSeconds = elapsedSeconds;
                _chartDirty = true;
            }
        }

        private void PingChart_Load(object sender, EventArgs e)
        {
            float scale = GetDpiForWindow(this.Handle) / 96f;
            if (scale > 1.05f)
                ConfigurePlotStyle(scale);
            this.MinimumSize = this.Size;
        }

        private void RefreshChart()
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                // Discard all accumulated data so restore is instant
                lock (_dataLock)
                {
                    if (_times.Count > 0)
                    {
                        _times.Clear();
                        _rtts.Clear();
                        _lastSetXMin = _lastSetXMax = 0;
                        _autoScroll = true;
                        _chartDirty = false;
                    }
                }
                return;
            }

            double[] xs, ys;
            double elapsed;
            bool autoScroll;

            lock (_dataLock)
            {
                if (_times.Count == 0) return;
                if (!_chartDirty) return;
                xs = _times.ToArray();
                ys = _rtts.ToArray();
                elapsed = _elapsedSeconds;
                autoScroll = _autoScroll;
                _chartDirty = false;
            }

            var plt = formsPlot1.Plot;

            // Detect user pan/zoom BEFORE setting limits
            var curLimits = plt.GetAxisLimits();
            if (_lastSetXMax > 0 && autoScroll)
            {
                if (Math.Abs(curLimits.XMin - _lastSetXMin) > 0.5 || Math.Abs(curLimits.XMax - _lastSetXMax) > 0.5)
                {
                    autoScroll = false;
                    _autoScroll = false;
                }
            }

            plt.Clear();

            DrawThresholdLines();

            var scatter = plt.AddScatter(xs, ys, Color.Cyan, lineWidth: 1, markerSize: 0);
            scatter.MarkerShape = ScottPlot.MarkerShape.filledCircle;

            double xMin, xMax;
            if (autoScroll)
            {
                xMax = elapsed + AutoScrollMarginSec;
                xMin = Math.Max(0, xMax - DisplayWindowSeconds);
            }
            else
            {
                xMin = curLimits.XMin;
                xMax = curLimits.XMax;
                // Only resume auto-scroll if user scrolls past the latest data
                if (elapsed > 0 && xMax >= elapsed + AutoScrollMarginSec)
                {
                    _autoScroll = true;
                }
            }

            double yMin = double.MaxValue, yMax = double.MinValue;
            bool hasVisible = false;
            int visibleCount = 0;
            for (int i = 0; i < xs.Length; i++)
            {
                if (xs[i] >= xMin - 0.5 && xs[i] <= xMax + 0.5)
                {
                    if (ys[i] < yMin) yMin = ys[i];
                    if (ys[i] > yMax) yMax = ys[i];
                    hasVisible = true;
                    visibleCount++;
                }
            }
            scatter.MarkerSize = visibleCount <= 200 ? 3 : 0;
            if (!hasVisible) { yMin = 0; yMax = 100; }
            else
            {
                double margin = Math.Max((yMax - yMin) * 0.12, 2);
                yMin = Math.Max(0, yMin - margin);
                yMax += margin;
            }

            plt.SetAxisLimits(xMin, xMax, yMin, yMax);
            _lastSetXMin = xMin;
            _lastSetXMax = xMax;
            formsPlot1.Refresh();
        }

        public void Shutdown()
        {
            _allowClose = true;
            try { this.Close(); } catch { }
        }

        private const int CS_NOCLOSE = 0x200;

        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= CS_NOCLOSE;
                return cp;
            }
        }
    }
}
