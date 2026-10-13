using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class MaskCalc : Form
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

        private bool _settingsLoaded;
        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "MaskCalc";

        private void LoadSettings()
        {
            var buffer = new StringBuilder(32768);
            IniFileHelper.GetPrivateProfileString(IniSection, "IPAddress", ipAddress.Text,
                buffer, buffer.Capacity, IniPath);
            try { ipAddress.Text = buffer.ToString(); }
            catch (ArgumentException) { /* Ignore malformed manually edited addresses. */ }

            IniFileHelper.GetPrivateProfileString(IniSection, "CIDR", txtCIDR.Text,
                buffer, buffer.Capacity, IniPath);
            txtCIDR.Text = buffer.ToString();
            if (int.TryParse(txtCIDR.Text, out int cidr) &&
                cidr >= trackMask.Minimum && cidr <= trackMask.Maximum)
                trackMask.Value = cidr;
            _settingsLoaded = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || !_settingsLoaded) return;
            IniFileHelper.WritePrivateProfileString(IniSection, "IPAddress", ipAddress.Text, IniPath);
            IniFileHelper.WritePrivateProfileString(IniSection, "CIDR", txtCIDR.Text, IniPath);
        }

        public MaskCalc()
        {
            var existingForm = Application.OpenForms.OfType<MaskCalc>()
                                  .FirstOrDefault(f => f != this);
            if (existingForm != null)
            {
                existingForm.BringToFront();
                existingForm.Focus();
                this.Dispose();
                return;
            }

            InitializeComponent();
        }

        private void MaskCalc_Load(object sender, EventArgs e)
        {
            _ = ApplyMaskThemeAsync();
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            lblExeName.Text = Global.exeName + " " + Global.Version;

            LoadSettings();
            IpAddressTextBoxHelper.Attach(ipAddress);

            this.trackMask.Scroll += new System.EventHandler(this.trackMask_Scroll);
            this.txtCIDR.TextChanged += new System.EventHandler(this.ipMask_TextChanged);
            this.ipAddress.TextChanged += new System.EventHandler(this.ipAddress_TextChanged);

            DoCalculate();
        }

        private uint IpToUint(string ip)
        {
            System.Net.IPAddress address = System.Net.IPAddress.Parse(ip);
            byte[] bytes = address.GetAddressBytes();
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }

        private string UintToIp(uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return new System.Net.IPAddress(bytes).ToString();
        }

        private void DoCalculate()
        {
            try
            {
                string ipStr = ipAddress.Text;
                if (string.IsNullOrEmpty(ipStr) || ipStr == "0.0.0.0") return;

                uint ip = IpToUint(ipStr);
                int cidr = trackMask.Value;

                uint mask = cidr == 0 ? 0 : uint.MaxValue << (32 - cidr);

                uint network = ip & mask;
                uint broadcast = network | ~mask;

                uint firstIP, lastIP, hostCount;

                if (cidr == 32)
                {
                    firstIP = lastIP = network;
                    hostCount = 1;
                }
                else if (cidr == 31)
                {
                    firstIP = network;
                    lastIP = broadcast;
                    hostCount = 2;
                }
                else
                {
                    firstIP = network + 1;
                    lastIP = broadcast - 1;
                    hostCount = (uint)Math.Pow(2, 32 - cidr) - 2;
                }

                lblMask2.Text = UintToIp(mask);
                lblNet2.Text = UintToIp(network);
                lblBoardcast2.Text = UintToIp(broadcast);
                lblStartIP2.Text = UintToIp(firstIP);
                lblEndIP2.Text = UintToIp(lastIP);
                lblCounts2.Text = hostCount.ToString("N0");
            }
            catch { /* 输入不完整时先不管它喵~ */ }
        }

        private void trackMask_Scroll(object sender, EventArgs e)
        {
            txtCIDR.Text = trackMask.Value.ToString();
            DoCalculate();
        }

        private void ipMask_TextChanged(object sender, EventArgs e)
        {
            if (int.TryParse(txtCIDR.Text, out int val))
            {
                if (val >= 1 && val <= 32)
                {
                    trackMask.Value = val;
                    DoCalculate();
                }
            }
        }

        private void ipAddress_TextChanged(object sender, EventArgs e)
        {
            DoCalculate();
        }

        private async Task ApplyMaskThemeAsync()
        {
            bool isLight = Global.isThemelight;
            Color contrastColor = isLight ? Color.Black : Color.White;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;

            this.BackColor = isLight ? Global.themeLight : Global.themeBlack;

            Control[] yumeyoControls = {
        lblMask, lblCounts, lblStartIP, lblEndIP, lblNet, lblBoardcast, lblExeName
    };
            foreach (var c in yumeyoControls)
            {
                if (c != null)
                {
                    c.ForeColor = yumeyoColor;
                    c.BackColor = Color.Transparent;
                }
            }

            Control[] contrastControls = {
        lblMask2, lblCounts2, lblStartIP2, lblEndIP2, lblNet2, lblBoardcast2, label1
    };
            foreach (var c in contrastControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = Color.Transparent;
                }
            }

            Control[] editControls = { ipAddress, txtCIDR };
            foreach (var c in editControls)
            {
                if (c != null)
                {
                    c.ForeColor = contrastColor;
                    c.BackColor = textBack;

                    if (c is TextBox txt)
                    {
                        txt.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                    }
                }
            }

            if (trackMask != null)
            {
                trackMask.BackColor = isLight ? Global.themeLight : Global.themeBlack;
            }
        }
    }
}
