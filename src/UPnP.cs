using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Open.Nat;

namespace NetInfoCheckerX
{
    public partial class UPnP : Form
    {
        private int _refreshCountdown = 120;
        private NatDevice _device;
        private CancellationTokenSource _discoveryCancellation;
        private int _discoveryGeneration;
        private bool _updatingIpLists;
        private bool _updatingServerSelection;
        private bool _isDiscovering;
        private bool _controlsBusy;

        private sealed class UpnpServerEntry
        {
            internal IPAddress Address;
            internal NatDevice Device;
            public override string ToString() => Address.ToString();
        }
        private readonly string[] requiredFiles;
        private bool _suppressNameUpdate = true;
        private SemaphoreSlim _scanSemaphore = new SemaphoreSlim(100);
        private bool _isConfirmingDelete = false;
        private DateTime _lastDelClickTime;
        private bool _isConfirmingDeleteAll = false;
        private DateTime _lastDelAllClickTime;
        private ContextMenuStrip gridCopyMenu;

        // 反射缓存：绕过 SSDP，直接从 TCP 端口拉取 UPnP 设备描述 XML
        private static System.Reflection.MethodInfo _cachedBuildMethod;
        private static System.Reflection.ConstructorInfo _cachedDeviceCtor;
        private static object _cachedSearcher;
        private static readonly object _reflectionLock = new object();

        private static long WritePrivateProfileString(string section, string key, string val, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, val, filePath);
        private static int GetPrivateProfileString(string section, string key, string def, System.Text.StringBuilder retVal, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, def, retVal, size, filePath);

        private string _iniPath = Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");

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
        private string ReadConfig(string section, string key)
        {
            var temp = new System.Text.StringBuilder(255);
            GetPrivateProfileString(section, key, "", temp, 255, _iniPath);
            return temp.ToString();
        }

        public UPnP()
        {
            InitializeComponent();
            SetupGridCopyMenu();
            requiredFiles = new string[]
            {
            "Open.Nat.dll",
            };
        }

        private void ApplyUPnPTheme()
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
                lblPublicPort, lblClientIP, lblName, lblTime, lblClientPort,
                label1, lblUPnPServer, chkTCP, chkUDP
            })
            {
                control.BackColor = Color.Transparent;
                control.ForeColor = foreground;
            }

            foreach (NumericUpDown numeric in new[] { numClientPort, numPublicPort })
            {
                numeric.BackColor = controlBackground;
                numeric.ForeColor = foreground;
                numeric.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

            foreach (TextBox textBox in new[] { txtName })
            {
                textBox.BackColor = controlBackground;
                textBox.ForeColor = foreground;
                textBox.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

            foreach (ComboBox comboBox in new[] { comboCilentIP, comboTime, comboNIC, comboUPnPServer })
            {
                comboBox.BackColor = controlBackground;
                comboBox.ForeColor = foreground;
                comboBox.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
            }

            foreach (Button button in new[] { btnCreate, btnDel, btnRefresh, btnRefreshNIC, btnDelAll })
            {
                ApplyUPnPButtonTheme(button);
            }

            dataGridView1.EnableHeadersVisualStyles = false;
            dataGridView1.BackgroundColor = controlBackground;
            dataGridView1.GridColor = isLight ? Color.FromArgb(210, 210, 210) : Color.FromArgb(65, 65, 65);
            dataGridView1.ColumnHeadersDefaultCellStyle.BackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionBackColor = headerBackground;
            dataGridView1.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
            dataGridView1.RowHeadersDefaultCellStyle.BackColor = headerBackground;
            dataGridView1.RowHeadersDefaultCellStyle.ForeColor = foreground;
            dataGridView1.RowHeadersDefaultCellStyle.SelectionBackColor = accent;
            dataGridView1.RowHeadersDefaultCellStyle.SelectionForeColor = accentText;
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

        private void ApplyUPnPButtonTheme(Button button)
        {
            bool isLight = Global.isThemelight;
            Color foreground = isLight ? Global.colorBlack : Global.colorWhite;
            Color accent = isLight ? Global.Yumeyo : Global.Yumeyo2;

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

        private void SetupGridCopyMenu()
        {
            gridCopyMenu = new ContextMenuStrip(components);
            ToolStripMenuItem copyItem = new ToolStripMenuItem("复制");
            copyItem.Click += (sender, e) => CopyCurrentCell();
            gridCopyMenu.Items.Add(copyItem);

            dataGridView1.MouseDown += (sender, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                DataGridView.HitTestInfo hit = dataGridView1.HitTest(e.X, e.Y);
                if (hit.RowIndex < 0 || hit.ColumnIndex < 0) return;
                dataGridView1.CurrentCell = dataGridView1.Rows[hit.RowIndex].Cells[hit.ColumnIndex];
                gridCopyMenu.Show(dataGridView1, e.Location);
            };
        }

        private void CopyCurrentCell()
        {
            DataGridViewCell cell = dataGridView1.CurrentCell;
            if (cell == null || cell.Value == null) return;
            string text = cell.Value.ToString();
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { }
        }

        #region 核心逻辑：获取与筛选设备

        private Task<NatDevice> GetNatDevice()
        {
            // 所有读写操作只使用用户已选定的设备，绝不在创建/删除时隐式选择。
            return Task.FromResult(_device);
        }

        private async Task DiscoverServersAsync(string preferredServer = null)
        {
            _discoveryCancellation?.Cancel();
            int generation = ++_discoveryGeneration;
            var cancellation = new CancellationTokenSource();
            _discoveryCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            _device = null;
            timer1.Stop();
            _isDiscovering = true;
            _updatingServerSelection = true;
            comboUPnPServer.Items.Clear();
            comboUPnPServer.Items.Add("正在扫描…");
            comboUPnPServer.SelectedIndex = 0;
            _updatingServerSelection = false;
            ShowServerPrompt("正在扫描所选网卡的 UPnP 服务器…");
            UpdateServerControls();
            try
            {
                IPAddress selectedIp;
                if (!IPAddress.TryParse(comboNIC.Text, out selectedIp))
                {
                    ApplyDiscoveredServers(new List<UpnpServerEntry>(), null);
                    return;
                }
                IPAddress gateway = GetGatewayIp(selectedIp, selectedIp.ToString());
                // 与旧版一样并行执行 SSDP 和网关常用端口探测；两路均完成后合并，
                // 不因先找到一台服务器而取消另一路，保证能发现多个服务器。
                Task<List<UpnpServerEntry>> ssdpTask = DiscoverDevicesOnInterface(selectedIp, 1500, token);
                Task<NatDevice> commonPortsTask = DiscoverDeviceOnCommonPorts(gateway, selectedIp, token);
                await Task.WhenAll(ssdpTask, commonPortsTask);
                var servers = await ssdpTask;
                var gatewayDevice = await commonPortsTask;
                token.ThrowIfCancellationRequested();
                if (gatewayDevice != null)
                    servers.Add(new UpnpServerEntry { Address = gateway, Device = gatewayDevice });
                if (servers.Count == 0 && gateway != null && CheckFullScanConfig())
                {
                    var device = await RunFullScanLogic(gateway, selectedIp, token);
                    token.ThrowIfCancellationRequested();
                    if (device != null) servers.Add(new UpnpServerEntry { Address = gateway, Device = device });
                }
                if (generation != _discoveryGeneration || IsDisposed || Disposing) return;
                ApplyDiscoveredServers(servers, preferredServer);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (generation == _discoveryGeneration && !IsDisposed && !Disposing)
                {
                    ApplyDiscoveredServers(new List<UpnpServerEntry>(), null);
                    ShowServerPrompt("发现 UPnP 服务器失败：" + ex.Message);
                }
            }
            finally
            {
                if (ReferenceEquals(_discoveryCancellation, cancellation)) _discoveryCancellation = null;
                cancellation.Dispose();
                if (generation == _discoveryGeneration && !IsDisposed && !Disposing)
                {
                    _isDiscovering = false;
                    UpdateServerControls();
                }
            }
            if (generation != _discoveryGeneration || IsDisposed || Disposing) return;
            if (comboUPnPServer.SelectedItem is UpnpServerEntry)
                await UseSelectedServerAsync();
            else if (comboUPnPServer.Items.Count > 1 && comboUPnPServer.Enabled)
                comboUPnPServer.DroppedDown = true;
        }

        private void ApplyDiscoveredServers(List<UpnpServerEntry> servers, string preferredServer)
        {
            var unique = servers.GroupBy(x => x.Address.ToString()).Select(x => x.First())
                .OrderBy(x => x.Address.ToString(), StringComparer.Ordinal).ToList();
            _updatingServerSelection = true;
            comboUPnPServer.Items.Clear();
            _device = null;
            if (unique.Count == 0) comboUPnPServer.Items.Add("未发现服务器");
            else
            {
                if (unique.Count > 1) comboUPnPServer.Items.Add("请选择 UPnP 服务器");
                foreach (var server in unique) comboUPnPServer.Items.Add(server);
            }
            comboUPnPServer.SelectedIndex = 0;
            var preferred = unique.FirstOrDefault(x => x.Address.ToString() == preferredServer);
            if (preferred != null) comboUPnPServer.SelectedItem = preferred;
            _updatingServerSelection = false;
            ShowServerPrompt(unique.Count == 0 ? "未发现 UPnP 服务器，请检查路由器服务或刷新网卡重试。" :
                "发现多个 UPnP 服务器，请先选择服务器，再操作映射。");
        }

        private async Task<List<UpnpServerEntry>> DiscoverDevicesOnInterface(
            IPAddress localAddress, int timeoutMs, CancellationToken token)
        {
            var multicastEndPoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            string[] searchTargets =
            {
                "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
                "upnp:rootdevice"
            };
            var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var locations = new List<Tuple<Uri, IPAddress>>();

            try
            {
                using (var client = new UdpClient(AddressFamily.InterNetwork))
                using (token.Register(() => client.Close()))
                {
                    // Windows 会将某些 ICMP 不可达通知转换成下一次 UDP 接收的
                    // WSAECONNRESET (10054)。SSDP 无连接，不能据此中止整个发现流程。
                    try
                    {
                        const int sioUdpConnReset = unchecked((int)0x9800000C);
                        client.Client.IOControl(sioUdpConnReset, new byte[4], null);
                    }
                    catch (SocketException) { /* 不支持此选项时由接收循环处理 */ }
                    catch (NotSupportedException) { }

                    client.Client.Bind(new IPEndPoint(localAddress, 0));
                    client.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        localAddress.GetAddressBytes());

                    foreach (string searchTarget in searchTargets)
                    {
                        token.ThrowIfCancellationRequested();
                        string request =
                            "M-SEARCH * HTTP/1.1\r\n" +
                            "HOST: 239.255.255.250:1900\r\n" +
                            "MAN: \"ssdp:discover\"\r\n" +
                            "MX: 1\r\n" +
                            "ST: " + searchTarget + "\r\n\r\n";
                        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                        try
                        {
                            await client.SendAsync(requestBytes, requestBytes.Length, multicastEndPoint);
                        }
                        catch (SocketException ex) when (IsTransientSsdpError(ex))
                        {
                            // 一次探测失败仍继续发送其他搜索目标并接收回复。
                        }
                    }

                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        int remainingMs = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                        UdpReceiveResult response;
                        try
                        {
                            Task<UdpReceiveResult> receiveTask = client.ReceiveAsync();
                            Task delayTask = Task.Delay(remainingMs, token);
                            Task completedTask = await Task.WhenAny(receiveTask, delayTask);

                            if (completedTask != receiveTask)
                            {
                                // 关闭后观察未完成的接收任务，避免留下未观察的套接字异常。
                                client.Close();
                                try { await receiveTask; }
                                catch (SocketException) { }
                                catch (ObjectDisposedException) { }
                                token.ThrowIfCancellationRequested();
                                break;
                            }

                            response = await receiveTask;
                        }
                        catch (SocketException ex) when (IsTransientSsdpError(ex))
                        {
                            token.ThrowIfCancellationRequested();
                            await Task.Delay(Math.Min(10, remainingMs), token);
                            continue;
                        }

                        string responseText = Encoding.UTF8.GetString(response.Buffer);
                        string location = GetSsdpHeaderValue(responseText, "LOCATION");
                        if (string.IsNullOrWhiteSpace(location) || !seenLocations.Add(location)) continue;

                        Uri locationUri;
                        if (!Uri.TryCreate(location, UriKind.Absolute, out locationUri)) continue;
                        if (locationUri.Scheme != Uri.UriSchemeHttp && locationUri.Scheme != Uri.UriSchemeHttps) continue;

                        locations.Add(Tuple.Create(locationUri, response.RemoteEndPoint.Address));
                    }
                }
            }
            catch (SocketException ex)
            {
                // SSDP 不可用时仍保留已收到的地址，并允许常用端口及全量扫描兜底。
                token.ThrowIfCancellationRequested();
                System.Diagnostics.Debug.WriteLine("UPnP SSDP 搜索失败，继续直连探测：" + ex.Message);
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            // 解析设备描述不占用 SSDP 接收窗口，避免慢设备使其他回复丢失。
            var results = await Task.WhenAll(locations.Select(location => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    IPAddress address;
                    if (!IPAddress.TryParse(location.Item1.Host, out address)) address = location.Item2;
                    if (address.AddressFamily != AddressFamily.InterNetwork) return null;
                    object info = CallBuildUpnpNatDeviceInfo(localAddress, location.Item1.ToString());
                    var device = info == null ? null : CreateUpnpDevice(info);
                    return device == null ? null : new UpnpServerEntry { Address = address, Device = device };
                }
                catch { return null; }
            }, token)));
            token.ThrowIfCancellationRequested();
            return results.Where(x => x != null).ToList();
        }

        private static bool IsTransientSsdpError(SocketException exception)
        {
            return exception.SocketErrorCode == SocketError.ConnectionReset ||
                exception.SocketErrorCode == SocketError.ConnectionRefused ||
                exception.SocketErrorCode == SocketError.NetworkReset;
        }

        private async Task<NatDevice> DiscoverDeviceOnCommonPorts(
            IPAddress gateway, IPAddress localAddress, CancellationToken token)
        {
            if (gateway == null) return null;
            foreach (int port in new[] { 1900, 2189, 2869, 5000, 5800, 5431, 80, 8080 })
            {
                token.ThrowIfCancellationRequested();
                if (!await QuickCheckPort(gateway, port, localAddress)) continue;
                var device = await TryGetDeviceOnPort(gateway, port, localAddress);
                token.ThrowIfCancellationRequested();
                if (device != null) return device;
            }
            return null;
        }

        private static string GetSsdpHeaderValue(string response, string headerName)
        {
            string[] lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                int separator = line.IndexOf(':');
                if (separator <= 0) continue;
                if (!line.Substring(0, separator).Trim().Equals(headerName, StringComparison.OrdinalIgnoreCase)) continue;
                return line.Substring(separator + 1).Trim();
            }
            return null;
        }

        private async Task<NatDevice> RunFullScanLogic(IPAddress gatewayIp, IPAddress localAddress, CancellationToken cancellationToken)
        {
            var masterCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return await Task.Run(async () =>
            {
                var foundDevice = new TaskCompletionSource<NatDevice>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    const int firstPort = 1024;
                    const int lastPort = 65535;
                    const int workerCount = 500;
                    int nextPort = firstPort - 1;

                    Func<Task<NatDevice>> scanWorker = async () =>
                    {
                        while (!masterCts.IsCancellationRequested)
                        {
                            int targetPort = Interlocked.Increment(ref nextPort);
                            if (targetPort > lastPort) return null;

                            try
                            {
                                if (masterCts.IsCancellationRequested) return null;
                                if (await QuickCheckPort(gatewayIp, targetPort, localAddress))
                                {
                                    var device = await TryGetDeviceOnPort(gatewayIp, targetPort, localAddress);
                                    if (device != null)
                                    {
                                        foundDevice.TrySetResult(device);
                                        masterCts.Cancel();
                                        return device;
                                    }
                                }
                            }
                            catch { }
                            finally
                            {
                                if (targetPort % 100 == 0 && !IsDisposed && IsHandleCreated)
                                {
                                    try
                                    {
                                        BeginInvoke(new Action(() =>
                                        {
                                            if (!IsDisposed && !Disposing)
                                                btnRefresh.Text = targetPort.ToString();
                                        }));
                                    }
                                    catch (InvalidOperationException) { }
                                }
                            }
                        }
                        return null;
                    };

                    var scanTasks = new Task<NatDevice>[workerCount];
                    for (int i = 0; i < scanTasks.Length; i++)
                        scanTasks[i] = scanWorker();

                    Task<NatDevice[]> allWorkers = Task.WhenAll(scanTasks);
                    Task completed = await Task.WhenAny(foundDevice.Task, allWorkers);
                    if (completed == foundDevice.Task)
                    {
                        if (allWorkers.IsCompleted)
                            masterCts.Dispose();
                        else
                            _ = allWorkers.ContinueWith(_ => masterCts.Dispose(),
                                TaskScheduler.Default);
                        return await foundDevice.Task;
                    }

                    var results = await allWorkers;
                    masterCts.Dispose();
                    return results.FirstOrDefault(device => device != null);
                }
                catch
                {
                    masterCts.Dispose();
                    throw;
                }
            });
        }

        private async Task<bool> QuickCheckPort(IPAddress ip, int port, IPAddress localAddress)
        {
            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    client.Client.Bind(new IPEndPoint(localAddress, 0));
                    var task = client.ConnectAsync(ip, port);
                    if (await Task.WhenAny(task, Task.Delay(100)) == task && client.Connected) return true;
                }
            }
            catch { }
            return false;
        }

        private async Task<NatDevice> TryGetDeviceOnPort(IPAddress gatewayIp, int port, IPAddress localAddress)
        {
            string[] commonPaths = {
                "/rootDesc.xml",
                "/igddesc.xml",
                "/upnp/rootDesc.xml",
                "/gateway.xml",
                "/device.xml",
                "/description.xml",
                "/upnp/desc.xml"
            };

            foreach (string path in commonPaths)
            {
                string url = $"http://{gatewayIp}:{port}{path}";
                try
                {
                    NatDevice device;
                    using (var cts = new CancellationTokenSource(800))
                    {
                        device = await Task.Run(() =>
                        {
                            try
                            {
                                var info = CallBuildUpnpNatDeviceInfo(localAddress, url);
                                if (info == null) return null;
                                return CreateUpnpDevice(info);
                            }
                            catch { return null; }
                        }, cts.Token);
                    }

                    if (device != null) return device;
                }
                catch { }
            }
            return null;
        }

        private static object CallBuildUpnpNatDeviceInfo(IPAddress localAddress, string url)
        {
            EnsureReflectionCache();
            var info = _cachedBuildMethod.Invoke(_cachedSearcher, new object[] { localAddress, new Uri(url) });
            return info;
        }

        private static NatDevice CreateUpnpDevice(object deviceInfo)
        {
            EnsureReflectionCache();
            return (NatDevice)_cachedDeviceCtor.Invoke(new[] { deviceInfo });
        }

        private static void EnsureReflectionCache()
        {
            if (_cachedSearcher != null) return;
            lock (_reflectionLock)
            {
                if (_cachedSearcher != null) return;
                var asm = typeof(NatDiscoverer).Assembly;
                var ipProviderType = asm.GetType("Open.Nat.IPAddressesProvider");
                var ipProvider = Activator.CreateInstance(ipProviderType);
                var searcherType = asm.GetType("Open.Nat.UpnpSearcher");
                var ipProviderInterfaceType = asm.GetType("Open.Nat.IIPAddressesProvider");
                var searcherConstructor = searcherType.GetConstructor(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    null, new[] { ipProviderInterfaceType }, null);
                if (searcherConstructor == null)
                    throw new MissingMethodException("Open.Nat.UpnpSearcher 构造函数不可用");
                _cachedSearcher = searcherConstructor.Invoke(new[] { ipProvider });
                _cachedBuildMethod = searcherType.GetMethod("BuildUpnpNatDeviceInfo",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var deviceType = asm.GetType("Open.Nat.UpnpNatDevice");
                _cachedDeviceCtor = deviceType.GetConstructor(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { asm.GetType("Open.Nat.UpnpNatDeviceInfo") }, null);
            }
        }

        private IPAddress GetGatewayIp(IPAddress selectedIp, string selectedIpStr)
        {
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!NicHelper.TryGetIPProperties(adapter, out IPInterfaceProperties properties)) continue;
                    if (properties.UnicastAddresses.Any(ua => ua.Address.Equals(selectedIp)))
                    {
                        var gateway = properties.GatewayAddresses
                            .Select(g => g.Address)
                            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                        if (gateway != null) return gateway;
                    }
                }
            }
            catch { }

            // 兜底：假设网关是 .1
            string[] parts = selectedIpStr.Split('.');
            if (parts.Length == 4)
            {
                return IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.1");
            }

            return null;
        }

        private bool CheckFullScanConfig()
        {
            string savedChoice = ReadConfig("UPnP", "RememberFullScan");

            if (savedChoice == "Yes") return true;
            if (savedChoice == "No") return false;

            var result = MessageBox.Show(
                "正常扫描未找到UPnP设备，是否尝试全量扫描？\n将依次扫描当前选择网关的所有端口，尝试发现非标准设备",
                "扫描失败了",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            bool userWantsScan = (result == DialogResult.Yes);

            string actionText = userWantsScan ? "【直接进入】" : "【不进入】";
            string rememberMessage = $"下次是否默认{actionText}全量扫描，不再询问？\n需恢复请删除运行目录\\NetInfoCheckerX.ini\\[UPnP]节";

            var remember = MessageBox.Show(
                rememberMessage,
                "是否要记住",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (remember == DialogResult.Yes)
            {
                WritePrivateProfileString("UPnP", "RememberFullScan", userWantsScan ? "Yes" : "No", _iniPath);
            }

            return userWantsScan;
        }

        #endregion

        #region UI 交互与状态控制

        private void ToggleControls(bool isEnabled, Button activeBtn = null, bool exceptButtons = false)
        {
            _controlsBusy = !isEnabled || exceptButtons;
            foreach (Control c in this.Controls)
            {
                if (c is DataGridView) continue;

                // 中间恢复阶段：逐类恢复控件时，先只启用非交互组件
                if (exceptButtons && (c is Button || c is ComboBox || c is RadioButton || c is TextBox))
                {
                    c.Enabled = false;
                    continue;
                }
                c.Enabled = isEnabled;
            }
            UpdateServerControls();
        }

        private void UpdateServerControls()
        {
            comboNIC.Enabled = btnRefreshNIC.Enabled = !_controlsBusy;
            comboUPnPServer.Enabled = !_controlsBusy && !_isDiscovering &&
                comboUPnPServer.Items.OfType<UpnpServerEntry>().Any();
            btnRefresh.Enabled = !_controlsBusy && !_isDiscovering && comboNIC.SelectedIndex >= 0;
            btnCreate.Enabled = btnDel.Enabled = btnDelAll.Enabled = !_controlsBusy && !_isDiscovering && _device != null;
        }

        private void ShowServerPrompt(string message)
        {
            dataGridView1.Rows.Clear();
            dataGridView1.Rows.Add(null, message, null, null, null, null, null);
        }

        private async void comboUPnPServer_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_updatingServerSelection) return;
            await UseSelectedServerAsync();
        }

        private async Task UseSelectedServerAsync()
        {
            _device = (comboUPnPServer.SelectedItem as UpnpServerEntry)?.Device;
            timer1.Stop();
            _isConfirmingDelete = _isConfirmingDeleteAll = false;
            btnDel.Text = "删除映射";
            ApplyUPnPButtonTheme(btnDel);
            RestoreDelAllButton();
            if (_device == null)
            {
                ShowServerPrompt("请先选择 UPnP 服务器，再操作映射。");
                UpdateServerControls();
                return;
            }
            ToggleControls(false);
            try
            {
                await RefreshListInternal();
                if (IsDisposed || Disposing) return;
                ResetTimer();
                timer1.Start();
            }
            finally
            {
                if (!IsDisposed && !Disposing) ToggleControls(true);
            }
        }

        private async Task RefreshListInternal()
        {
            dataGridView1.Rows.Clear();
            try
            {
                var device = await GetNatDevice();
                if (device == null)
                {
                    ShowServerPrompt("请先点击“获取映射”扫描服务器，再选择 UPnP 服务器。");
                    timer1.Stop();
                    return;
                }

                var mappings = await device.GetAllMappingsAsync();
                if (!ReferenceEquals(device, _device) || IsDisposed || Disposing) return;
                var sortedList = mappings.OrderBy(x => x.PrivateIP.ToString())
                                         .ThenBy(x => x.PrivatePort)
                                         .ToList();

                if (sortedList.Count == 0)
                {
                    dataGridView1.Rows.Add(null, "当前没有任何映射规则", null, null, "喵", null, "");
                    return;
                }

                for (int i = 0; i < sortedList.Count; i++)
                {
                    var m = sortedList[i];
                    string expireDisplay = m.Expiration.Year < 2000 ? "永久" : m.Expiration.ToLocalTime().ToString();
                    dataGridView1.Rows.Add(
                        i + 1,
                        m.PrivateIP,
                        m.PrivatePort,
                        m.PublicPort,
                        m.Protocol.ToString().ToUpper(),
                        expireDisplay,
                        m.Description
                    );
                }
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                dataGridView1.Rows.Add(null, "读取异常：", ex.Message, null, "喵", null, "");
            }
        }

        #endregion

        #region 按钮事件

        private async void btnRefresh_Click(object sender, EventArgs e)
        {
            if (_controlsBusy || _isDiscovering) return;
            EnsureSelectedNICValid();

            try
            {
                timer1.Stop();
                btnRefresh.Text = "获取中...";
                string preferred = (comboUPnPServer.SelectedItem as UpnpServerEntry)?.Address.ToString();
                await DiscoverServersAsync(preferred);
            }
            catch (Exception ex)
            {
                MessageBox.Show("刷新时出错：" + ex.Message, "刷新失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            finally
            {
                if (!IsDisposed && !Disposing)
                {
                    btnRefresh.Text = "获取映射";
                    UpdateServerControls();
                }
            }
        }

        private async void btnCreate_Click(object sender, EventArgs e)
        {
            var protocols = new List<Protocol>();
            if (chkTCP.Checked) protocols.Add(Protocol.Tcp);
            if (chkUDP.Checked) protocols.Add(Protocol.Udp);
            if (protocols.Count == 0)
            {
                MessageBox.Show("请至少选择 TCP 或 UDP 协议。", "请选择协议", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // EnsureSelectedNICValid 会重置 comboCilentIP，先保存再恢复
            string savedClientIP = comboCilentIP.Text;

            EnsureSelectedNICValid();

            _suppressNameUpdate = true;
            comboCilentIP.Text = savedClientIP;
            _suppressNameUpdate = false;

            string pattern = @"^[a-zA-Z0-9\x20-\x7e]+$";
            if (string.IsNullOrWhiteSpace(txtName.Text) || !System.Text.RegularExpressions.Regex.IsMatch(txtName.Text, pattern))
            {
                MessageBox.Show("描述只能用英文、数字和英文符号", "描述太坏了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string originalText = btnCreate.Text;
            try
            {
                btnCreate.Text = "创建中";
                ToggleControls(false, btnCreate);

                var device = await GetNatDevice();
                if (device != null)
                {
                    int createdCount = 0;
                    var errors = new List<string>();
                    foreach (Protocol protocol in protocols)
                    {
                        try
                        {
                            Mapping newMap = new Mapping(
                                protocol,
                                IPAddress.Parse(savedClientIP),
                                (int)numClientPort.Value,
                                (int)numPublicPort.Value,
                                GetLifetimeInSeconds(comboTime.Text),
                                txtName.Text
                            );
                            await device.CreatePortMapAsync(newMap);
                            createdCount++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{protocol.ToString().ToUpper()}: {ex.Message}");
                        }
                    }

                    if (createdCount > 0)
                    {
                        btnCreate.Text = errors.Count == 0 ? "创建完毕" : "部分完成";
                        timer1.Start();
                        await RefreshListInternal();

                        _suppressNameUpdate = true;
                        comboCilentIP.Text = savedClientIP;
                        _suppressNameUpdate = false;
                    }

                    if (errors.Count > 0)
                    {
                        string title = createdCount > 0 ? "部分完成" : "创建失败了";
                        string message = createdCount > 0
                            ? $"已成功创建 {createdCount} 个协议映射，以下协议失败：\n{string.Join("\n", errors)}"
                            : $"所选协议映射均创建失败：\n{string.Join("\n", errors)}";
                        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                }
                else
                    MessageBox.Show("请先扫描并选择 UPnP 服务器。", "尚未选择服务器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                MessageBox.Show(msg, "创建失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            finally
            {
                ToggleControls(true, exceptButtons: true);
                ToggleControls(true);
                btnCreate.Text = originalText;
            }
        }

        private static bool IsSamePortMapping(Mapping left, Mapping right)
        {
            return left != null && right != null &&
                left.Protocol == right.Protocol &&
                left.PublicPort == right.PublicPort &&
                left.PrivatePort == right.PrivatePort &&
                object.Equals(left.PrivateIP, right.PrivateIP);
        }

        private async void btnDel_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先在列表中点击选中想要删除的行喵~", "提示");
                return;
            }

            if (btnDel.Text == "删除中") return;

            // 双击确认：首次点击进入确认状态，2 秒内再次点击才执行删除
            if (!_isConfirmingDelete || (DateTime.Now - _lastDelClickTime).TotalSeconds > 2)
            {
                _isConfirmingDelete = true;
                _lastDelClickTime = DateTime.Now;
                btnDel.Text = "确认删除";
                btnDel.ForeColor = Color.Yellow;

                await Task.Run(async () =>
                {
                    await Task.Delay(2000);

                    if (_isConfirmingDelete)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            // 再次检查时间，防止极端情况下刚好被重置
                            if ((DateTime.Now - _lastDelClickTime).TotalSeconds >= 2)
                            {
                                _isConfirmingDelete = false;
                                btnDel.Text = "删除映射";
                                ApplyUPnPButtonTheme(btnDel);
                            }
                        }));
                    }
                });
                return;
            }

            _isConfirmingDelete = false;

            try
            {
                btnDel.Text = "删除中";
                ApplyUPnPButtonTheme(btnDel);
                ToggleControls(false, btnDel);

                var device = await GetNatDevice();
                if (device == null) return;

                var mappings = await device.GetAllMappingsAsync();
                var sortedList = mappings.OrderBy(x => x.PrivateIP.ToString()).ThenBy(x => x.PrivatePort).ToList();

                var selectedMappings = new List<Mapping>();
                foreach (DataGridViewRow row in dataGridView1.SelectedRows)
                {
                    if (row.Cells[0].Value != null && int.TryParse(row.Cells[0].Value.ToString(), out int seq))
                    {
                        if (seq > 0 && seq <= sortedList.Count)
                            selectedMappings.Add(sortedList[seq - 1]);
                    }
                }

                int attempted = 0;
                foreach (Mapping mapping in selectedMappings)
                {
                    try { await device.DeletePortMapAsync(mapping); }
                    catch { /* 继续删除其余映射，最终按路由器实际列表核对结果 */ }
                    btnDel.Text = $"处理中{++attempted}/{selectedMappings.Count}";
                }

                List<Mapping> remainingMappings;
                try
                {
                    remainingMappings = (await device.GetAllMappingsAsync()).ToList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("删除请求已发送，但无法核对实际结果：" + ex.Message,
                        "无法确认删除结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    await RefreshListInternal();
                    return;
                }

                int successCount = selectedMappings.Count(mapping =>
                    !remainingMappings.Any(remaining => IsSamePortMapping(mapping, remaining)));
                int failCount = selectedMappings.Count - successCount;
                btnDel.Text = $"已删{successCount}条";
                if (failCount > 0)
                {
                    MessageBox.Show($"本次删除映射 {selectedMappings.Count} 条，成功 {successCount} 条，失败 {failCount} 条",
                        "删除完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                await Task.Delay(1000);
                await RefreshListInternal();
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除出错：" + ex.Message);
            }
            finally
            {
                ToggleControls(true);
                btnDel.Text = "删除映射";
                ApplyUPnPButtonTheme(btnDel);
            }
        }

        private async void btnDelAll_Click(object sender, EventArgs e)
        {
            if (btnDelAll.Text == "删除中") return;

            // 双击确认：首次点击进入确认状态，2 秒内再次点击才执行
            if (!_isConfirmingDeleteAll || (DateTime.Now - _lastDelAllClickTime).TotalSeconds > 2)
            {
                _isConfirmingDeleteAll = true;
                _lastDelAllClickTime = DateTime.Now;
                btnDelAll.Text = "确认全删";
                btnDelAll.BackColor = Color.Red;
                btnDelAll.ForeColor = Color.Yellow;
                btnDelAll.Font = new Font(btnDelAll.Font, FontStyle.Bold);

                await Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    if (_isConfirmingDeleteAll)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if ((DateTime.Now - _lastDelAllClickTime).TotalSeconds >= 2)
                            {
                                _isConfirmingDeleteAll = false;
                                RestoreDelAllButton();
                            }
                        }));
                    }
                });
                return;
            }

            _isConfirmingDeleteAll = false;

            try
            {
                btnDelAll.Text = "删除中";
                ApplyUPnPButtonTheme(btnDelAll);
                ToggleControls(false, btnDelAll);

                var device = await GetNatDevice();
                if (device == null) return;

                var mappings = (await device.GetAllMappingsAsync()).ToList();
                int total = mappings.Count();

                foreach (var m in mappings)
                {
                    try { await device.DeletePortMapAsync(m); }
                    catch { /* 最后按路由器实际列表核对，不以请求是否抛异常作为删除结果 */ }
                }

                List<Mapping> remainingMappings;
                try
                {
                    remainingMappings = (await device.GetAllMappingsAsync()).ToList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("删除请求已发送，但无法核对实际结果：" + ex.Message,
                        "无法确认删除结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    await RefreshListInternal();
                    return;
                }

                int success = mappings.Count(mapping =>
                    !remainingMappings.Any(remaining => IsSamePortMapping(mapping, remaining)));
                int fail = total - success;
                MessageBox.Show($"本次删除映射 {total} 条，成功 {success} 条，失败 {fail} 条", "清理完毕", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshListInternal();
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除出错：" + ex.Message);
            }
            finally
            {
                ToggleControls(true);
                RestoreDelAllButton();
            }
        }

        private void RestoreDelAllButton()
        {
            btnDelAll.Text = "删除全部";
            btnDelAll.Font = new Font(btnDelAll.Font, FontStyle.Bold);
            ApplyUPnPButtonTheme(btnDelAll);
        }

        #endregion

        #region 辅助工具与初始化

        private void comboNIC_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_updatingIpLists) return;
            ResetServerDiscovery();
        }

        private void ResetServerDiscovery()
        {
            _discoveryCancellation?.Cancel();
            _discoveryGeneration++;
            _isDiscovering = false;
            _device = null;
            timer1.Stop();
            _isConfirmingDelete = _isConfirmingDeleteAll = false;
            _updatingServerSelection = true;
            comboUPnPServer.Items.Clear();
            comboUPnPServer.Items.Add("尚未扫描");
            comboUPnPServer.SelectedIndex = 0;
            _updatingServerSelection = false;
            btnRefresh.Text = "获取映射";
            ShowServerPrompt("请选择网卡，再点击“获取映射”扫描 UPnP 服务器。");
            UpdateServerControls();
        }

        private void UPnP_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            this.MouseDown += MyMouseDown;
            pictureBox1.MouseDown += MyMouseDown;
            ApplyUPnPTheme();
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
                    string message = $"缺少运行UPNP中控台必要的文件：\n{string.Join("\n", missingFiles)}\n\n建议重新打开/解压查询器X/检查杀毒软件喵。";
                    MessageBox.Show(message, "文件缺失了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.Close();
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查文件时出错：{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
                return;
            }
            InitIpLists();
            ResetTimer();
            comboTime.SelectedIndex = 2;

            comboCilentIP.SelectedIndexChanged += (s, ea) => UpdateMappingName();
            comboTime.SelectedIndexChanged += (s, ea) => UpdateMappingName();

            _suppressNameUpdate = false;
            UpdateMappingName();

            CloudControl.UsedTimesCounter("UPnP");
        }

        private void UpdateMappingName()
        {
            if (_suppressNameUpdate) return;

            string ip = comboCilentIP.Text;
            string intPort = numClientPort.Value.ToString();
            string extPort = numPublicPort.Value.ToString();
            string protocol = chkTCP.Checked
                ? (chkUDP.Checked ? "TCP_UDP" : "TCP")
                : (chkUDP.Checked ? "UDP" : "NONE");
            string time = comboTime.Text;

            txtName.Text = $"NICX_{ip}_{intPort}_{extPort}_{protocol}_{time}";
        }

        private void EnsureSelectedNICValid()
        {
            string selectedText = comboNIC.Text;
            // 日常读写不要重建下拉框，否则会丢失用户指定的服务器。
            if (!NicHelper.GetUsableIPAddresses(includeIPv6: false).Any(x => x.AddressText == selectedText))
                InitIpLists();
        }

        private void InitIpLists()
        {
            string previousNic = comboNIC.Text;
            string previousClient = comboCilentIP.Text;
            _updatingIpLists = true;
            _suppressNameUpdate = true;

            comboNIC.Items.Clear();
            comboCilentIP.Items.Clear();
            foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses(includeIPv6: false))
            {
                comboNIC.Items.Add(nicAddress.AddressText);
                comboCilentIP.Items.Add(nicAddress.AddressText);
            }
            if (comboNIC.Items.Count > 0) { comboNIC.SelectedIndex = 0; comboCilentIP.SelectedIndex = 0; }
            if (comboNIC.Items.Contains(previousNic)) comboNIC.SelectedItem = previousNic;
            IPAddress previousClientAddress;
            if (IPAddress.TryParse(previousClient, out previousClientAddress) &&
                previousClientAddress.AddressFamily == AddressFamily.InterNetwork &&
                !previousClientAddress.Equals(IPAddress.Any) && !previousClientAddress.Equals(IPAddress.Broadcast))
                comboCilentIP.Text = previousClient;

            _suppressNameUpdate = false;
            _updatingIpLists = false;
            ResetServerDiscovery();
        }

        private async void timer1_Tick(object sender, EventArgs e)
        {
            if (_isDiscovering || _controlsBusy || _device == null) return;
            _refreshCountdown--;
            this.Text = $"UPnP控制台 ✧ NetInfoCheckerX | 自动刷新倒计时: {_refreshCountdown}秒";

            if (_refreshCountdown <= 0)
            {
                timer1.Stop();
                ToggleControls(false);
                try
                {
                    await RefreshListInternal();
                    if (IsDisposed || Disposing) return;
                    ResetTimer();
                    timer1.Start();
                }
                finally { if (!IsDisposed && !Disposing) ToggleControls(true); }
            }
        }

        private void ResetTimer() => _refreshCountdown = 120;

        private int GetLifetimeInSeconds(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "0") return 0;

            try
            {
                int v = int.Parse(s.Substring(0, s.Length - 1));

                if (s.EndsWith("m")) return v * 60;
                if (s.EndsWith("h")) return v * 3600;
                if (s.EndsWith("d")) return v * 86400;

                return v;
            }
            catch
            {
                return 0;
            }
        }

        private void btnRefreshNIC_Click(object sender, EventArgs e) => InitIpLists();

        #endregion

        private void txtName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnCreate.PerformClick();
            }
        }

        private void numPublicPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnCreate.PerformClick();
            }
        }

        private void numClientPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnCreate.PerformClick();
            }
        }

        private void UPnP_FormClosing(object sender, FormClosingEventArgs e)
        {
            _discoveryGeneration++;
            _discoveryCancellation?.Cancel();
            timer1.Stop();
            _scanSemaphore?.Dispose();
            _scanSemaphore = null;
        }

        private void lblUPnPServer_Click(object sender, EventArgs e)
        {

        }

        private void label2_Click(object sender, EventArgs e)
        {

        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void lblClientPort_Click(object sender, EventArgs e)
        {

        }

        private void comboTime_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void lblTime_Click(object sender, EventArgs e)
        {

        }

        private void lblName_Click(object sender, EventArgs e)
        {

        }

        private void txtName_TextChanged(object sender, EventArgs e)
        {

        }

        private void numPublicPort_ValueChanged(object sender, EventArgs e)
        {
            UpdateMappingName();
        }

        private void chkTCP_CheckedChanged(object sender, EventArgs e)
        {
            KeepOneProtocolSelected(chkTCP);
        }

        private void chkUDP_CheckedChanged(object sender, EventArgs e)
        {
            KeepOneProtocolSelected(chkUDP);
        }

        private void KeepOneProtocolSelected(CheckBox changedCheckBox)
        {
            if (!chkTCP.Checked && !chkUDP.Checked)
            {
                changedCheckBox.Checked = true;
                return;
            }

            UpdateMappingName();
        }

        private void numClientPort_ValueChanged(object sender, EventArgs e)
        {
            UpdateMappingName();
        }

        private void comboCilentIP_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void lblClientIP_Click(object sender, EventArgs e)
        {

        }

        private void lblPublicPort_Click(object sender, EventArgs e)
        {

        }
    }
}
