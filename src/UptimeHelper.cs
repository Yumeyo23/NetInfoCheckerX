using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Win32;

namespace NetInfoCheckerX
{
    public partial class UptimeHelper : Form
    {
        private const string FirmwarePowerRegistryPath =
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        private const string BootPerformanceLogName =
            "Microsoft-Windows-Diagnostics-Performance/Operational";
        private const int SmartCloseDelaySeconds = 30;

        private readonly long _displayMilliseconds;
        private readonly Image _displayImage;
        private readonly Timer _smartCloseTimer;
        private readonly Point _initialCursorPosition;
        private Color _gradientStart;
        private Color _gradientEnd;
        private Color _borderColor;
        private bool _mouseMovementObserved;
        private Stopwatch _closeCountdown;

        public UptimeHelper() : this(0L, 0m, null)
        {
        }

        public UptimeHelper(
            long bootMilliseconds,
            decimal offsetSeconds,
            Image displayImage)
        {
            InitializeComponent();

            offsetSeconds = Math.Max(-60m, Math.Min(60m, offsetSeconds));
            long offsetMilliseconds = (long)Math.Round(
                offsetSeconds * 1000m, 0, MidpointRounding.AwayFromZero);
            _displayMilliseconds = Math.Max(0L, bootMilliseconds + offsetMilliseconds);
            _displayImage = displayImage;
            _initialCursorPosition = Cursor.Position;
            _smartCloseTimer = new Timer { Interval = 100 };
            _smartCloseTimer.Tick += SmartCloseTimer_Tick;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        private void UptimeHelper_Load(object sender, EventArgs e)
        {
            ConfigureContentAndTheme();
            PositionAtTopRight();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PositionAtTopRight();
            _smartCloseTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _smartCloseTimer.Stop();
            _smartCloseTimer.Dispose();

            Image image = pictureBox2.Image;
            pictureBox2.Image = null;
            if (image != null) image.Dispose();

            base.OnFormClosed(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_gradientStart.IsEmpty || _gradientEnd.IsEmpty)
            {
                base.OnPaintBackground(e);
                return;
            }

            using (var brush = new LinearGradientBrush(
                ClientRectangle, _gradientStart, _gradientEnd, 18f))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (var pen = new Pen(_borderColor, 1f))
            {
                e.Graphics.DrawRectangle(
                    pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        private void ConfigureContentAndTheme()
        {
            bool isLight = Global.isThemelight;
            Color normalText = isLight ? Color.FromArgb(28, 28, 28) : Color.White;

            label1.Text = string.Format("{0}, 欢迎使用!", Environment.UserName);
            label2.Text = FormatDuration(_displayMilliseconds);
            label3.Text = "电脑本次开机用时";
            label4.Text = Environment.MachineName;
            pictureBox2.Image = _displayImage ?? new Bitmap(Global.GetIcon());

            label1.ForeColor = normalText;
            label3.ForeColor = normalText;
            label4.ForeColor = normalText;
            btnClose.BackColor = Color.Transparent;
            btnClose.UseVisualStyleBackColor = false;

            if (_displayMilliseconds <= 30000L)
            {
                _gradientStart = isLight ? Color.FromArgb(241, 252, 244) : Color.FromArgb(17, 43, 27);
                _gradientEnd = isLight ? Color.FromArgb(194, 235, 204) : Color.FromArgb(25, 77, 43);
                label2.ForeColor = isLight ? Color.FromArgb(24, 120, 58) : Color.FromArgb(133, 235, 158);
                _borderColor = label2.ForeColor;
            }
            else if (_displayMilliseconds <= 60000L)
            {
                _gradientStart = isLight ? Color.FromArgb(255, 249, 235) : Color.FromArgb(53, 37, 18);
                _gradientEnd = isLight ? Color.FromArgb(255, 218, 159) : Color.FromArgb(99, 62, 23);
                label2.ForeColor = isLight ? Color.FromArgb(186, 87, 0) : Color.FromArgb(255, 192, 103);
                _borderColor = label2.ForeColor;
            }
            else
            {
                _gradientStart = isLight ? Color.FromArgb(255, 243, 243) : Color.FromArgb(52, 23, 23);
                _gradientEnd = isLight ? Color.FromArgb(246, 193, 193) : Color.FromArgb(96, 35, 35);
                label2.ForeColor = isLight ? Color.FromArgb(190, 38, 45) : Color.FromArgb(255, 132, 136);
                _borderColor = label2.ForeColor;
            }

            btnClose.FlatAppearance.MouseOverBackColor = _borderColor;
            BackColor = _gradientStart;
            Invalidate();
        }

        private void PositionAtTopRight()
        {
            Screen targetScreen = Screen.PrimaryScreen;
            Rectangle bounds = targetScreen != null ? targetScreen.Bounds : SystemInformation.VirtualScreen;
            Location = new Point(bounds.Right - Width, bounds.Top + 50);
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void SmartCloseTimer_Tick(object sender, EventArgs e)
        {
            // 第一阶段：等待检测鼠标是否移动
            if (!_mouseMovementObserved)
            {
                // 鼠标如果一直在初始位置，就什么都不做，继续等待
                if (Cursor.Position == _initialCursorPosition) return;

                // 鼠标一旦动了，标记为已触发，并开启秒表计时
                _mouseMovementObserved = true;
                _closeCountdown = Stopwatch.StartNew();
                return;
            }

            // 第二阶段：检测到鼠标移动后，开始刷新倒计时并判断关闭
            if (_closeCountdown != null)
            {
                // 计算剩余时间（总秒数 30 - 已经过去的秒数）
                double elapsedSeconds = _closeCountdown.Elapsed.TotalSeconds;
                int remainingSeconds = SmartCloseDelaySeconds - (int)Math.Floor(elapsedSeconds);

                // 防止计算出现负数，最小显示 0 秒
                remainingSeconds = Math.Max(0, remainingSeconds);

                // 动态更新 label3 的文字
                label3.Text = $"本次开机用时 ({remainingSeconds}秒后自动关闭)";

                // 达到或超过 30 秒时关闭窗口
                if (elapsedSeconds >= SmartCloseDelaySeconds)
                {
                    _smartCloseTimer.Stop();
                    Close();
                }
            }
        }

        internal static string FormatDuration(long milliseconds)
        {
            milliseconds = Math.Max(0L, milliseconds);
            if (milliseconds > 60000L)
            {
                long roundedSeconds = (long)Math.Round(
                    milliseconds / 1000d, MidpointRounding.AwayFromZero);
                return string.Format("{0} 分 {1} 秒", roundedSeconds / 60, roundedSeconds % 60);
            }

            decimal seconds = milliseconds / 1000m;
            return string.Format(CultureInfo.InvariantCulture, "{0:00.0} 秒", seconds);
        }

        internal static bool TryGetLastBootDurationMilliseconds(out long milliseconds)
        {
            if (!TryReadFirmwarePostMilliseconds(out milliseconds) &&
                !TryReadBootEventMilliseconds(out milliseconds))
            {
                milliseconds = 0;
                return false;
            }

            return true;
        }

        private static bool TryReadFirmwarePostMilliseconds(out long milliseconds)
        {
            milliseconds = 0;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(FirmwarePowerRegistryPath, false))
                {
                    object value = key != null ? key.GetValue("FwPOSTTime", null) : null;
                    if (value == null) return false;

                    milliseconds = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    return IsPlausibleDuration(milliseconds);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadBootEventMilliseconds(out long milliseconds)
        {
            milliseconds = 0;
            try
            {
                var query = new EventLogQuery(
                    BootPerformanceLogName,
                    PathType.LogName,
                    "*[System[(EventID=100)]]")
                {
                    ReverseDirection = true,
                    TolerateQueryErrors = true
                };

                using (var reader = new EventLogReader(query))
                using (EventRecord record = reader.ReadEvent())
                {
                    if (record == null) return false;

                    XDocument document = XDocument.Parse(record.ToXml());
                    XElement value = document
                        .Descendants()
                        .FirstOrDefault(element =>
                            element.Name.LocalName == "Data" &&
                            string.Equals(
                                (string)element.Attribute("Name"),
                                "MainPathBootTime",
                                StringComparison.Ordinal));

                    return value != null &&
                           long.TryParse(value.Value, NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out milliseconds) &&
                           IsPlausibleDuration(milliseconds);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPlausibleDuration(long milliseconds)
        {
            return milliseconds > 0 && milliseconds <= TimeSpan.FromHours(24).TotalMilliseconds;
        }

        private void label3_Click(object sender, EventArgs e)
        {

        }
    }
}
