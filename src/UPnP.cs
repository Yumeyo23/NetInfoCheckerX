using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Open.Nat;

namespace NetInfoCheckerX
{
    public partial class UPnP : Form
    {
        private int _refreshCountdown = 300;
        private NatDevice _device;
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
                label1, label2, radioTCP, radioUDP
            })
            {
                control.BackColor = Color.Transparent;
                control.ForeColor = foreground;
            }

            foreach (TextBox textBox in new[] { txtClientPort, txtPublicPort, txtName })
            {
                textBox.BackColor = controlBackground;
                textBox.ForeColor = foreground;
                textBox.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

            foreach (ComboBox comboBox in new[] { comboCilentIP, comboTime, comboNIC })
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
            gridCopyMenu = new ContextMenuStrip();
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

        private async Task<NatDevice> GetNatDevice()
        {
            if (_device != null) return _device;

            string selectedIpStr = "";
            this.Invoke(new Action(() => { selectedIpStr = comboNIC.Text; }));
            if (string.IsNullOrEmpty(selectedIpStr)) return null;
            IPAddress selectedIp = IPAddress.Parse(selectedIpStr);
            IPAddress gatewayIp = GetGatewayIp(selectedIp, selectedIpStr);

            // 第一阶段：广播搜索与常用端口直连并行赛跑
            var quickStageCts = new CancellationTokenSource();

            var broadcastTask = Task.Run(async () =>
            {
                try
                {
                    var found = await DiscoverDeviceOnInterface(selectedIp, 1500, quickStageCts.Token);
                    if (found != null) quickStageCts.Cancel();
                    return found;
                }
                catch { return null; }
            });

            var commonPortsTask = Task.Run(async () =>
            {
                int[] commonPorts = { 1900, 2189, 2869, 5000, 5800, 5431, 80, 8080 };
                foreach (int port in commonPorts)
                {
                    if (quickStageCts.Token.IsCancellationRequested) break;
                    if (await QuickCheckPort(gatewayIp, port, selectedIp))
                    {
                        try
                        {
                            var device = await TryGetDeviceOnPort(gatewayIp, port, selectedIp);
                            if (device != null) { quickStageCts.Cancel(); return device; }
                        }
                        catch { }
                    }
                }
                return null;
            });

            var winner = await Task.WhenAny(broadcastTask, commonPortsTask);
            _device = await winner;
            if (_device == null) _device = (winner == broadcastTask) ? await commonPortsTask : await broadcastTask;

            // 第二阶段：全量端口扫描兜底（需用户授权）
            if (_device == null)
            {
                bool shouldFullScan = CheckFullScanConfig();
                if (shouldFullScan)
                {
                    _device = await RunFullScanLogic(gatewayIp, selectedIp);
                }
            }

            this.Invoke(new Action(() => { btnRefresh.Text = "刷新列表"; }));
            return _device;
        }

        private async Task<NatDevice> DiscoverDeviceOnInterface(
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

            using (var client = new UdpClient(AddressFamily.InterNetwork))
            {
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
                    await client.SendAsync(requestBytes, requestBytes.Length, multicastEndPoint);
                }

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    int remainingMs = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                    Task<UdpReceiveResult> receiveTask = client.ReceiveAsync();
                    Task delayTask = Task.Delay(remainingMs, token);
                    Task completedTask = await Task.WhenAny(receiveTask, delayTask);

                    if (completedTask != receiveTask)
                    {
                        token.ThrowIfCancellationRequested();
                        return null;
                    }

                    UdpReceiveResult response = await receiveTask;
                    string responseText = Encoding.UTF8.GetString(response.Buffer);
                    string location = GetSsdpHeaderValue(responseText, "LOCATION");
                    if (string.IsNullOrWhiteSpace(location) || !seenLocations.Add(location)) continue;

                    Uri locationUri;
                    if (!Uri.TryCreate(location, UriKind.Absolute, out locationUri)) continue;
                    if (locationUri.Scheme != Uri.UriSchemeHttp && locationUri.Scheme != Uri.UriSchemeHttps) continue;

                    NatDevice device = await Task.Run(() =>
                    {
                        try
                        {
                            object info = CallBuildUpnpNatDeviceInfo(localAddress, locationUri.ToString());
                            return info == null ? null : CreateUpnpDevice(info);
                        }
                        catch
                        {
                            return null;
                        }
                    });

                    if (device != null) return device;
                }
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

        private async Task<NatDevice> RunFullScanLogic(IPAddress gatewayIp, IPAddress localAddress)
        {
            var masterCts = new CancellationTokenSource();
            return await Task.Run(async () =>
            {
                using (var semaphore = new SemaphoreSlim(500))
                {
                    var scanTasks = new List<Task<NatDevice>>();
                    for (int port = 1024; port <= 65535; port++)
                    {
                        if (masterCts.IsCancellationRequested) break;
                        int targetPort = port;
                        await semaphore.WaitAsync();

                        var t = Task.Run(async () =>
                        {
                            try
                            {
                                if (masterCts.IsCancellationRequested) return null;
                                if (await QuickCheckPort(gatewayIp, targetPort, localAddress))
                                {
                                    var d = await TryGetDeviceOnPort(gatewayIp, targetPort, localAddress);
                                    if (d != null) { masterCts.Cancel(); return d; }
                                }
                            }
                            catch { }
                            finally
                            {
                                semaphore.Release();
                                if (targetPort % 100 == 0)
                                    this.BeginInvoke(new Action(() => { btnRefresh.Text = $"{targetPort}"; }));
                            }
                            return null;
                        });
                        scanTasks.Add(t);

                        if (scanTasks.Count > 1000)
                        {
                            var found = scanTasks.FirstOrDefault(st => st.IsCompleted && st.Result != null);
                            if (found != null) return found.Result;
                            scanTasks.RemoveAll(st => st.IsCompleted);
                        }
                    }
                    var results = await Task.WhenAll(scanTasks);
                    return results.FirstOrDefault(d => d != null);
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
                    var cts = new CancellationTokenSource(800);
                    var device = await Task.Run(() =>
                    {
                        try
                        {
                            var info = CallBuildUpnpNatDeviceInfo(localAddress, url);
                            if (info == null) return null;
                            return CreateUpnpDevice(info);
                        }
                        catch { return null; }
                    }, cts.Token);

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
            if (activeBtn != null) activeBtn.Enabled = true;
        }

        private async Task RefreshListInternal()
        {
            dataGridView1.Rows.Clear();
            try
            {
                var device = await GetNatDevice();
                if (device == null)
                {
                    dataGridView1.Rows.Add(null, "未发现UPnP设备", "请检查路由器", "是否开启UPnP", "或相关服务是否正常", "喵", "");
                    timer1.Stop();
                    return;
                }

                var mappings = await device.GetAllMappingsAsync();
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
                dataGridView1.Rows.Add(null, "读取异常：", ex.Message, null, "喵", null, "");
            }
        }

        #endregion

        #region 按钮事件

        private async void btnRefresh_Click(object sender, EventArgs e)
        {
            EnsureSelectedNICValid();

            try
            {
                btnRefresh.Enabled = false;
                timer1.Stop();
                btnRefresh.Text = "刷新中...";
                ToggleControls(false);

                await RefreshListInternal();

                btnRefresh.Text = "刷新完毕";
                ToggleControls(true, exceptButtons: true);
                ToggleControls(true);
                btnRefresh.Text = "刷新列表";
                ResetTimer();
                timer1.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("刷新时出错：" + ex.Message, "刷新失败了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            finally
            {
                btnRefresh.Enabled = true;
                btnRefresh.Text = "刷新列表";
            }
        }

        private async void btnCreate_Click(object sender, EventArgs e)
        {
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
                    Mapping newMap = new Mapping(
                        radioTCP.Checked ? Protocol.Tcp : Protocol.Udp,
                        IPAddress.Parse(savedClientIP),
                        int.Parse(txtClientPort.Text),
                        int.Parse(txtPublicPort.Text),
                        GetLifetimeInSeconds(comboTime.Text),
                        txtName.Text
                    );
                    await device.CreatePortMapAsync(newMap);
                    btnCreate.Text = "创建完毕";
                    timer1.Start();
                    await RefreshListInternal();

                    _suppressNameUpdate = true;
                    comboCilentIP.Text = savedClientIP;
                    _suppressNameUpdate = false;
                }
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

                int successCount = 0;
                foreach (DataGridViewRow row in dataGridView1.SelectedRows)
                {
                    if (row.Cells[0].Value != null && int.TryParse(row.Cells[0].Value.ToString(), out int seq))
                    {
                        if (seq > 0 && seq <= sortedList.Count)
                        {
                            try
                            {
                                await device.DeletePortMapAsync(sortedList[seq - 1]);
                                successCount++;
                            }
                            catch { /* 单条失败不中断 */ }
                        }
                    }
                    btnDel.Text = $"已删{successCount}条";
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

                var mappings = await device.GetAllMappingsAsync();
                int total = mappings.Count();
                int success = 0;
                int fail = 0;

                foreach (var m in mappings)
                {
                    try
                    {
                        await device.DeletePortMapAsync(m);
                        success++;
                    }
                    catch { fail++; }
                }

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
            _device = null;
        }

        private void UPnP_Load(object sender, EventArgs e)
        {
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
            txtClientPort.TextChanged += (s, ea) => UpdateMappingName();
            txtPublicPort.TextChanged += (s, ea) => UpdateMappingName();
            radioTCP.CheckedChanged += (s, ea) => UpdateMappingName();
            comboTime.SelectedIndexChanged += (s, ea) => UpdateMappingName();

            _suppressNameUpdate = false;
            UpdateMappingName();

            dataGridView1.Rows.Add("-", "请【刷新网卡】后，", "选择欲查看UPnP映射的网卡，", "再点击【获取映射】", "喵", "-", "-");
        }

        private void UpdateMappingName()
        {
            if (_suppressNameUpdate) return;

            string ip = comboCilentIP.Text;
            string intPort = txtClientPort.Text;
            string extPort = txtPublicPort.Text;
            string protocol = radioTCP.Checked ? "TCP" : "UDP";
            string time = comboTime.Text;

            txtName.Text = $"NICX_{ip}_{intPort}_{extPort}_{protocol}_{time}";
        }

        private void EnsureSelectedNICValid()
        {
            string selectedText = comboNIC.Text;
            if (string.IsNullOrEmpty(selectedText)) return;

            InitIpLists();

            bool found = false;
            foreach (var item in comboNIC.Items)
            {
                if (item.ToString() == selectedText)
                {
                    comboNIC.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found && comboNIC.Items.Count > 0) comboNIC.SelectedIndex = 0;
        }

        private void InitIpLists()
        {
            _suppressNameUpdate = true;

            comboNIC.Items.Clear();
            comboCilentIP.Items.Clear();
            foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses(includeIPv6: false))
            {
                comboNIC.Items.Add(nicAddress.AddressText);
                comboCilentIP.Items.Add(nicAddress.AddressText);
            }
            if (comboNIC.Items.Count > 0) { comboNIC.SelectedIndex = 0; comboCilentIP.SelectedIndex = 0; }

            _suppressNameUpdate = false;
        }

        private async void timer1_Tick(object sender, EventArgs e)
        {
            _refreshCountdown--;
            this.Text = $"UPnP控制台 ✧ NetInfoCheckerX | 自动刷新倒计时: {_refreshCountdown}秒";

            if (_refreshCountdown <= 0)
            {
                timer1.Stop();
                await RefreshListInternal();
                ResetTimer();
                timer1.Start();
            }
        }

        private void ResetTimer() => _refreshCountdown = 300;

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

        private void txtClientPort_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
                e.Handled = true;
        }

        private void txtPublicPort_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
                e.Handled = true;
        }

        private void txtName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnCreate.PerformClick();
            }
        }

        private void txtPublicPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnCreate.PerformClick();
            }
        }

        private void txtClientPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                btnCreate.PerformClick();
            }
        }

        private void UPnP_FormClosing(object sender, FormClosingEventArgs e)
        {
            timer1.Stop();
            _scanSemaphore?.Dispose();
            _scanSemaphore = null;
        }
    }
}
