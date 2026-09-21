using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EmbedIO;
using EmbedIO.Actions;

namespace NetInfoCheckerX
{
    public partial class LibreSpeed : Form
    {
        private sealed class BindItem
        {
            public string Text { get; set; }
            public string Value { get; set; }
            public override string ToString() { return Text; }
        }

        private WebServer _server;
        private bool _isServerRunning = false;
        private const int GarbageChunksPerResponse = 500;
        private const int DefaultDownloadChunkMegabytes = 2;
        private static readonly byte[] SharedBuffer = CreateRandomBuffer(4 * 1024 * 1024);
        private static readonly long ActiveClientTimeoutTicks = TimeSpan.FromSeconds(5).Ticks;
        private string _dlcFolder = null;
        private readonly ConcurrentDictionary<string, long> _activeClients = new ConcurrentDictionary<string, long>();
        private System.Windows.Forms.Timer _statusTimer;
        private long _bytesReceived;
        private long _bytesSent;
        private long _lastBytesReceived;
        private long _lastBytesSent;
        private string _usageHint = "";

        private static byte[] CreateRandomBuffer(int size)
        {
            byte[] buffer = new byte[size];
            new Random().NextBytes(buffer);
            return buffer;
        }

        private string DlcSuffix => _dlcFolder != null ? " (OpenSpeedTest DLC已加载)" : "";

        private bool CheckDlc()
        {
            _dlcFolder = null;
            if (Global.isTemp) return false;
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string dlcFolder = Path.Combine(exeDir, "NICX_DLC_OpenSpeedTest");
            string indexPath = Path.Combine(dlcFolder, "index.html");

            if (!Directory.Exists(dlcFolder)) return false;
            if (!File.Exists(indexPath)) return false;

            string expectedMd5 = "144325934DBBE3209E4A4287CBBCAB8E";
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(indexPath))
            {
                byte[] hash = md5.ComputeHash(stream);
                string actualMd5 = BitConverter.ToString(hash).Replace("-", "");
                if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            _dlcFolder = dlcFolder;
            return true;
        }

        public LibreSpeed()
        {
            InitializeComponent();
            this.FormClosing += LibreSpeed_FormClosing;
        }
        private async Task ApplyLibreThemeAsync()
        {
            bool isLight = Global.isThemelight;
            Color windowBack = isLight ? Global.themeLight : Global.themeBlack;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;

            Color baseContrastColor = isLight ? Color.Black : Color.White;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            this.BackColor = windowBack;

            foreach (Control ctrl in this.Controls)
            {
                if (ctrl is Label && ctrl.Name.ToLower().StartsWith("label"))
                {
                    ctrl.ForeColor = yumeyoColor;
                    ctrl.BackColor = Color.Transparent;
                }

                else if (ctrl is TextBox || ctrl is ComboBox)
                {
                    ctrl.ForeColor = baseContrastColor;
                    ctrl.BackColor = textBack;

                    if (ctrl is TextBox tb)
                        tb.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;

                    if (ctrl is ComboBox cb)
                        cb.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
                }

                else if (ctrl is Button btn)
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
                    }
                }

                else if (ctrl.Name == "lblStatus")
                {
                    ctrl.ForeColor = baseContrastColor;
                    ctrl.BackColor = Color.Transparent;
                }
            }

            if (pictureBox1 != null) pictureBox1.BackColor = Color.Transparent;
        }
        private void EnsureSelectedNICValid()
        {
            string selectedText = comboServer.Text;
            PopulateBindAddresses(selectedText);
        }

        private void PopulateBindAddresses(string preferredText = null)
        {
            string preferredValue = ExtractBindValue(preferredText);
            comboServer.DataSource = null;
            comboServer.DropDownStyle = ComboBoxStyle.DropDown;
            comboServer.Items.Clear();
            comboServer.Items.Add(new BindItem { Text = "Any (全部网卡)", Value = "+" });
            comboServer.Items.Add(new BindItem { Text = "0.0.0.0 (IPv4 Any)", Value = "0.0.0.0" });
            comboServer.Items.Add(new BindItem { Text = ":: (IPv6 Any)", Value = "::" });

            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                    comboServer.Items.Add(new BindItem { Text = nicAddress.DisplayText, Value = nicAddress.AddressText });
            }
            catch
            {
            }

            foreach (BindItem item in comboServer.Items)
            {
                if ((!string.IsNullOrEmpty(preferredText) && item.Text == preferredText) ||
                    (!string.IsNullOrEmpty(preferredValue) && item.Value == preferredValue))
                {
                    comboServer.SelectedItem = item;
                    return;
                }
            }
            if (comboServer.Items.Count > 0) comboServer.SelectedIndex = 0;
        }

        private static string ExtractBindValue(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.StartsWith("Any", StringComparison.OrdinalIgnoreCase) || text == "+") return "+";
            int descriptionIndex = text.IndexOf(" (", StringComparison.Ordinal);
            if (descriptionIndex > 0) text = text.Substring(0, descriptionIndex);
            IPAddress address;
            return IPAddress.TryParse(text, out address) ? address.ToString() : null;
        }

        private string ResolveSelectedBindValue()
        {
            BindItem item = comboServer.SelectedItem as BindItem;
            string value = item == null ? ExtractBindValue(comboServer.Text) : item.Value;
            if (value == "+") return value;

            IPAddress address;
            if (!IPAddress.TryParse(value, out address))
                throw new InvalidOperationException("请选择有效的监听网卡 IP。");

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                IPAddress detected = NicHelper.GetDefaultLocalAddress(address.AddressFamily);
                if (detected == null)
                    throw new InvalidOperationException(address.AddressFamily == AddressFamily.InterNetworkV6
                        ? "未找到可用的系统默认 IPv6 出口网卡。"
                        : "未找到可用的系统默认 IPv4 出口网卡。");
                value = detected.ToString();
                SelectBindAddress(detected);
            }
            return value;
        }

        private void SelectBindAddress(IPAddress address)
        {
            foreach (BindItem item in comboServer.Items)
            {
                if (item.Value == address.ToString())
                {
                    comboServer.SelectedItem = item;
                    return;
                }
            }
            comboServer.Text = address.ToString();
        }

        private void label1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || _isServerRunning) return;
            EnsureSelectedNICValid();
        }

        private void LibreSpeed_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            _ = ApplyLibreThemeAsync();
            comboServer.DropDownStyle = ComboBoxStyle.DropDown;

            try
            {
                PopulateBindAddresses();
                CheckDlc();
                lblStatus.Text = "初始化完成, 等待开服" + DlcSuffix;
            }
            catch (Exception ex)
            {
                lblStatus.Text = ex.Message;

            }

            CloudControl.UsedTimesCounter("LibreSpeed");

            _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            CheckDlc();
            EnsureSelectedNICValid();

            if (_isServerRunning) { StopServer(); return; }

            try
            {
                if (!int.TryParse(txtPort.Text, out int portInt)) { portInt = 9123; }

                string selectedValue = ResolveSelectedBindValue();

                _activeClients.Clear();
                Interlocked.Exchange(ref _bytesReceived, 0);
                Interlocked.Exchange(ref _bytesSent, 0);
                _lastBytesReceived = 0;
                _lastBytesSent = 0;

                StartServer(selectedValue, portInt);

                _isServerRunning = true;
                btnStart.Text = "停服";
                comboServer.Enabled = false;
                txtPort.Enabled = false;

                if (lblStatus != null)
                {
                    if (selectedValue == "+")
                    {
                        _usageHint = $"请用浏览器访问 [本机任意IP]:{portInt} 进行测速{DlcSuffix}。";
                    }
                    else
                    {
                        string displayIp = selectedValue.Contains(":") ? $"[{selectedValue}]" : selectedValue;
                        _usageHint = $"请用浏览器访问 {displayIp}:{portInt} 进行测速{DlcSuffix}。";
                    }
                    UpdateStatus(0, 0);
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"{ex.Message}\n{ex.StackTrace}";
            }
        }

        private void LibreSpeed_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_statusTimer != null) _statusTimer.Stop();
            if (_isServerRunning)
            {
                StopServer();
            }
        }

        private void StopServer()
        {
            try
            {
                _server?.Dispose();
                _server = null;
                _isServerRunning = false;
                _activeClients.Clear();
                btnStart.Text = "开启";
                lblStatus.Text = "服务已停止" + DlcSuffix;
                comboServer.Enabled = true;
                txtPort.Enabled = true;
            }
            catch { }
        }

        private void RecordClientActivity(IHttpContext ctx)
        {
            if (ctx == null || ctx.RemoteEndPoint == null) return;
            IPAddress address = ctx.RemoteEndPoint.Address;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            _activeClients[address.ToString()] = DateTime.UtcNow.Ticks;
        }

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            if (!_isServerRunning) return;

            long now = DateTime.UtcNow.Ticks;
            foreach (var client in _activeClients)
            {
                if (now - client.Value <= ActiveClientTimeoutTicks) continue;
                long lastSeen;
                _activeClients.TryRemove(client.Key, out lastSeen);
            }

            long received = Interlocked.Read(ref _bytesReceived);
            long sent = Interlocked.Read(ref _bytesSent);
            long receiveRate = Math.Max(0, received - _lastBytesReceived);
            long sendRate = Math.Max(0, sent - _lastBytesSent);
            _lastBytesReceived = received;
            _lastBytesSent = sent;
            UpdateStatus(sendRate, receiveRate);
        }

        private void UpdateStatus(long sendRate, long receiveRate)
        {
            lblStatus.Text = string.Format(
                "在线客户端：{0}\r\n↑ {1}/s    ↓ {2}/s\r\n{3}",
                _activeClients.Count,
                FormatRate(sendRate),
                FormatRate(receiveRate),
                _usageHint);
        }

        private static string FormatRate(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024d / 1024d).ToString("0.00") + " MB";
            return (bytes / 1024d).ToString("0.0") + " KB";
        }

        private async Task CopyUploadAsync(IHttpContext ctx)
        {
            byte[] buffer = new byte[81920];
            int count;
            while ((count = await ctx.Request.InputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                Interlocked.Add(ref _bytesReceived, count);
                RecordClientActivity(ctx);
            }
        }

        private static int GetDownloadChunkSize(IHttpContext ctx)
        {
            int megabytes;
            string value = ctx.Request.QueryString["ckSize"];
            if (!int.TryParse(value, out megabytes) || (megabytes != 1 && megabytes != 2 && megabytes != 4))
                megabytes = DefaultDownloadChunkMegabytes;
            return megabytes * 1024 * 1024;
        }

        private static async Task ServeEmbeddedResourceAsync(IHttpContext ctx, string resourceName, string mimeType)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    ctx.Response.ContentType = mimeType + "; charset=UTF-8";
                    ctx.Response.StatusCode = 200;
                    await stream.CopyToAsync(ctx.Response.OutputStream);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
            }
        }

        private static void SetNoCacheHeaders(IHttpContext ctx)
        {
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
        }

        private void StartServer(string ip, int port)
        {
            string url;
            if (ip == "Any" || ip == "+")
                url = $"http://+:{port}/";
            else if (ip.Contains(":"))
                url = $"http://[{ip}]:{port}/";
            else
                url = $"http://{ip}:{port}/";

            var server = new WebServer(o => o
                    .WithUrlPrefix(url)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithCors("*")
                .WithModule(new ActionModule("/getIP", HttpVerbs.Get, async ctx =>
                {
                    RecordClientActivity(ctx);
                    SetNoCacheHeaders(ctx);
                    var remoteIp = ctx.RemoteEndPoint.Address;
                    string clientIp = remoteIp.IsIPv4MappedToIPv6
                        ? remoteIp.MapToIPv4().ToString()
                        : remoteIp.ToString();
                    await ctx.SendStringAsync(clientIp, "text/plain", Encoding.UTF8);
                }))
                .WithModule(new ActionModule("/empty", HttpVerbs.Get | HttpVerbs.Post, async ctx =>
                {
                    RecordClientActivity(ctx);
                    SetNoCacheHeaders(ctx);
                    if (ctx.Request.HttpMethod == "POST")
                    {
                        await CopyUploadAsync(ctx);
                    }
                    await ctx.SendStringAsync("", "text/plain", Encoding.UTF8);
                }))
                .WithModule(new ActionModule("/garbage", HttpVerbs.Get, async ctx =>
                {
                    RecordClientActivity(ctx);
                    ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    SetNoCacheHeaders(ctx);
                    ctx.Response.ContentType = "application/octet-stream";
                    ctx.Response.SendChunked = true;
                    int chunkSize = GetDownloadChunkSize(ctx);

                    try
                    {
                        for (int i = 0; i < GarbageChunksPerResponse; i++)
                        {
                            if (ctx.CancellationToken.IsCancellationRequested) break;
                            await ctx.Response.OutputStream.WriteAsync(SharedBuffer, 0, chunkSize);
                            Interlocked.Add(ref _bytesSent, chunkSize);
                            RecordClientActivity(ctx);
                        }
                    }
                    catch (IOException) { }
                    catch (HttpListenerException) { }
                    catch (ObjectDisposedException) { }
                }));

            if (_dlcFolder != null)
            {
                _server = server.WithStaticFolder("/", _dlcFolder, true);
            }
            else
            {
                _server = server
                    .WithModule(new ActionModule("/speedtest_worker.js", HttpVerbs.Get, async ctx =>
                    {
                        await ServeEmbeddedResourceAsync(ctx, "NetInfoCheckerX.Resources.web.speedtest_worker.js", "application/javascript");
                    }))
                    .WithModule(new ActionModule("/speedtest.js", HttpVerbs.Get, async ctx =>
                    {
                        await ServeEmbeddedResourceAsync(ctx, "NetInfoCheckerX.Resources.web.speedtest.js", "application/javascript");
                    }))
                    .WithModule(new ActionModule("/index.html", HttpVerbs.Get, async ctx =>
                    {
                        await ServeEmbeddedResourceAsync(ctx, "NetInfoCheckerX.Resources.web.index.html", "text/html");
                    }))
                    .WithModule(new ActionModule("/", HttpVerbs.Get, async ctx =>
                    {
                        if (ctx.Request.Url.AbsolutePath == "/" || ctx.Request.Url.AbsolutePath == "")
                            await ServeEmbeddedResourceAsync(ctx, "NetInfoCheckerX.Resources.web.index.html", "text/html");
                    }));
            }

            _server.RunAsync();
        }
    }
}
