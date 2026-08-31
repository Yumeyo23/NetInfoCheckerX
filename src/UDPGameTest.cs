using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class UDPGameTest : Form
    {
        private sealed class LocalEndItem
        {
            public string Text;
            public IPAddress Address;
            public override string ToString() { return Text; }
        }

        private sealed class DisplaySnapshot
        {
            public double Delay;
            public double AverageDelay;
            public double HighOnePercentDelay;
            public UdpGameDirectionSnapshot Up = new UdpGameDirectionSnapshot();
            public UdpGameDirectionSnapshot Down = new UdpGameDirectionSnapshot();
            public uint Tick;
        }

        private readonly object _statsLock = new object();
        private readonly object _shardTasksLock = new object();
        private readonly List<double> _allRtts = new List<double>(20000);
        private readonly List<Task> _shardWriteTasks = new List<Task>();
        private readonly Random _random = new Random();
        private static int _activeTests;
        private Socket _socket;
        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private Task _sendTask;
        private TaskCompletionSource<bool> _welcomeSource;
        private UdpGameDirectionTracker _downstreamTracker;
        private UDPGameTestChart _chart;
        private System.Windows.Forms.Timer _uiTimer;
        private Stopwatch _sessionWatch;
        private uint _sessionId;
        private uint _nonce;
        private uint _upPacketSequence;
        private uint _upTickSequence;
        private uint _lastDownTick;
        private long _lastEchoClientTime;
        private double _currentRtt;
        private double _upMiss;
        private double _upLoss;
        private double _upJitter;
        private bool _active;
        private bool _closing;
        private bool _normalStopRequested;
        private bool _chartDisabled;
        private bool _chartSessionDisabled;
        private bool _timerPeriodActive;
        private volatile bool _reconnecting;
        private bool _countdownTitleEnabled;
        private int _lastDisplayedRemainingSeconds = -1;
        private long _lastDownstreamReceiveMicroseconds;
        private int _load;
        private int _bufferTicks;
        private DateTime _startedAt;
        private string _startTimeStr;
        private string _fileTargetToken;
        private int _shardIndex;
        private int _lastShardSec;
        private double _baselineTps;
        private int _nextCheckSec;
        private double _scheduleDelaySum;
        private int _scheduleDelayCount;
        private int _loopIterationCount;
        private int _delayDegradationCount;
        private int _rateDegradationCount;
        private double _baselineIterations;
        private long _lastUiTickTimestamp;
        private bool _registeredActiveTest;
        private readonly string _baseWindowTitle;
        private Task _runtimeWarmupTask;
        private PrivateFontCollection _privateFonts;
        private Font _outputFont;
        private bool _privateFontLeaseAcquired;

        private const uint FontResourcePrivate = 0x0010;
        private const int WmFontChange = 0x001D;
        private const string CascadiaMonoFamilyName = "Cascadia Mono";
        private const int TestDurationSeconds = 300;
        private const long ServerSilenceBeforeReconnectMicroseconds = 1000000;
        private const int ReconnectTimeoutMilliseconds = 10000;
        private const int PerformanceBaselineSeconds = 8;
        private const int PerformanceCheckIntervalSeconds = 4;
        private static readonly object PrivateFontSync = new object();
        private static string _registeredFontPath;
        private static int _registeredFontUsers;
        private static bool _privateFontRegistered;

        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);

        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);

        private string IniPath { get { return Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini"); } }
        private const string IniSection = "UDPGameTest";

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr window, int message, int wParam, int lParam);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveFontResourceEx(string fileName, uint flags, IntPtr reserved);

        public UDPGameTest()
        {
            InitializeComponent();
            _baseWindowTitle = Text;
            btnStart.Click += btnStart_Click;
            FormClosing += UDPGameTest_FormClosing;
            chkDelay.CheckedChanged += ChartVisibilityChanged;
            chkJitterUp.CheckedChanged += ChartVisibilityChanged;
            chkJitterDown.CheckedChanged += ChartVisibilityChanged;
            chkMissLoss.CheckedChanged += ChartVisibilityChanged;
            lblTarget.MouseDown += lblTarget_MouseDown;
            btnSave.Click += btnSave_Click;
        }

        private async void PingUDPGame_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            ApplyHighDpiOutputFont();
            ApplyAccentColor();
            btnStart.Enabled = false;
            comboLoad.SelectedIndex = 0;
            comboBuffering.SelectedIndex = 0;
            PopulateLocalEndpoints();
            LoadSettings();
            _runtimeWarmupTask = Task.Run(() => UdpGameRuntimeWarmup.Run(typeof(UDPGameTest)));

            _uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _uiTimer.Tick += UiTimer_Tick;
            ResetLabels();

            if (AppSettings.DisablePingLineChart)
            {
                _chartDisabled = true;
            }
            else if (ChartDependenciesAvailable())
            {
                _chart = new UDPGameTestChart();
                _chart.Location = new Point(Left, Bottom + 8);
                _chart.Show();
            }
            else
            {
                _chartDisabled = true;
            }

            try { await _runtimeWarmupTask; } catch { }
            if (_closing || IsDisposed) return;

            CleanupTempFiles();
            richTextBox1.Clear();
            AppendColorText("      ==== 欢迎使用 延迟测试-UDP游戏模拟 ❤ 网络综合查询器X by Yumeyo ====", Global.Yumeyo2, true);
            AppendColorText("本功能基于CS抓包+模拟抓包结果实现，请先阅读下列提示：", Color.LightSkyBlue, true);
            AppendColorText("          🔰查询器X不内置服务器，请自备服务器运行服务端🔰", Color.Yellow, true);
            AppendColorText("        ❤协议：UDP，Tick：64，发包方式/计算逻辑等，已尽量参照Valve官方设定", Color.LightGreen, true);
            AppendColorText("        ❤采用随机数据填充，模拟游戏同等负载，测试结果仍可能与实际游戏有出入", Color.White, true);
            AppendColorText("    🚀 各参数介绍可鼠标悬停查看，测试结果仅供参考，具体以实际游戏为准 ❤\n", Color.Gold, true);
            AppendColorText("    ❤ 延迟颜色对照表", Color.LightSkyBlue, true);
            AppendColorMap();
            CloudControl.UsedTimesCounter("PingUDPGame");
            btnStart.Enabled = true;
        }

        private void ApplyAccentColor()
        {
            LabelLikeTextBox[] accentLabels =
            {
                labelLikeTextBox1,
                labelLikeTextBox2,
                labelLikeTextBox3,
                labelLikeTextBox4,
                labelLikeTextBox6,
                labelLikeTextBox7,
                labelLikeTextBox8
            };

            foreach (LabelLikeTextBox label in accentLabels)
            {
                if (label != null) label.ForeColor = Global.Yumeyo2;
            }
        }

        private bool ChartDependenciesAvailable()
        {
            string directory = Application.StartupPath;
            if (File.Exists(Path.Combine(directory, "ScottPlot.WinForms.dll")) &&
                File.Exists(Path.Combine(directory, "ScottPlot.dll"))) return true;

            MessageBox.Show("未找到绘制统计图所需的 ScottPlot 依赖，本窗口的统计图功能已禁用。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void ApplyHighDpiOutputFont()
        {
            float dpi = DeviceDpi;
            try
            {
                using (Graphics graphics = CreateGraphics()) dpi = Math.Max(dpi, graphics.DpiX);
            }
            catch { }
            if (dpi <= 96F) return;

            Font selectedFont = null;
            string fontPath = Path.Combine(Application.StartupPath, "CascadiaMono.ttf");
            if (File.Exists(fontPath))
            {
                PrivateFontCollection fontCollection = null;
                try
                {
                    fontCollection = new PrivateFontCollection();
                    fontCollection.AddFontFile(fontPath);
                    FontFamily family = fontCollection.Families.FirstOrDefault(item =>
                        string.Equals(item.Name, CascadiaMonoFamilyName, StringComparison.OrdinalIgnoreCase));
                    if (family != null && TryAcquirePrivateFont(fontPath))
                    {
                        _privateFontLeaseAcquired = true;
                        SendMessage(richTextBox1.Handle, WmFontChange, 0, 0);
                        selectedFont = new Font(family, 9F, FontStyle.Regular, GraphicsUnit.Point);
                        _privateFonts = fontCollection;
                        fontCollection = null;
                    }
                }
                catch
                {
                    if (_privateFontLeaseAcquired)
                    {
                        ReleasePrivateFont();
                        _privateFontLeaseAcquired = false;
                    }
                }
                finally
                {
                    if (fontCollection != null) fontCollection.Dispose();
                }
            }

            if (selectedFont == null && IsFontFamilyInstalled(CascadiaMonoFamilyName))
            {
                try { selectedFont = new Font(CascadiaMonoFamilyName, 9F, FontStyle.Regular, GraphicsUnit.Point); }
                catch { }
            }

            if (selectedFont != null)
            {
                _outputFont = selectedFont;
                richTextBox1.Font = selectedFont;
                ApplyOutputSelectionFont();
            }
        }

        private void ApplyOutputSelectionFont()
        {
            if (_outputFont == null || richTextBox1.IsDisposed) return;
            try
            {
                richTextBox1.SelectionLength = 0;
                richTextBox1.SelectionFont = _outputFont;
            }
            catch { }
        }

        private static bool IsFontFamilyInstalled(string familyName)
        {
            try
            {
                using (InstalledFontCollection installedFonts = new InstalledFontCollection())
                {
                    return installedFonts.Families.Any(item =>
                        string.Equals(item.Name, familyName, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { return false; }
        }

        private static bool TryAcquirePrivateFont(string fontPath)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(fontPath); }
            catch { return false; }

            lock (PrivateFontSync)
            {
                if (_privateFontRegistered)
                {
                    if (!string.Equals(_registeredFontPath, fullPath, StringComparison.OrdinalIgnoreCase)) return false;
                    _registeredFontUsers++;
                    return true;
                }
                if (AddFontResourceEx(fullPath, FontResourcePrivate, IntPtr.Zero) <= 0) return false;
                _registeredFontPath = fullPath;
                _registeredFontUsers = 1;
                _privateFontRegistered = true;
                return true;
            }
        }

        private static void ReleasePrivateFont()
        {
            lock (PrivateFontSync)
            {
                if (!_privateFontRegistered || _registeredFontUsers <= 0) return;
                _registeredFontUsers--;
                if (_registeredFontUsers != 0) return;
                if (RemoveFontResourceEx(_registeredFontPath, FontResourcePrivate, IntPtr.Zero))
                {
                    _registeredFontPath = null;
                    _privateFontRegistered = false;
                }
            }
        }

        private void ReleaseOutputFont()
        {
            try
            {
                if (!richTextBox1.IsDisposed && _outputFont != null) richTextBox1.Font = Font;
            }
            catch { }
            try { if (_outputFont != null) _outputFont.Dispose(); } catch { }
            _outputFont = null;
            try { if (_privateFonts != null) _privateFonts.Dispose(); } catch { }
            _privateFonts = null;
            if (_privateFontLeaseAcquired)
            {
                ReleasePrivateFont();
                _privateFontLeaseAcquired = false;
            }
        }

        private void SaveSettings()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(txtServerIP.Text))
                    WritePrivateProfileString(IniSection, "Server", txtServerIP.Text.Trim(), IniPath);
                WritePrivateProfileString(IniSection, "Port", txtServerPort.Text.Trim(), IniPath);
                WritePrivateProfileString(IniSection, "LocalAddress", null, IniPath);
                if (comboLoad.SelectedItem != null)
                    WritePrivateProfileString(IniSection, "Load", comboLoad.SelectedItem.ToString(), IniPath);
                if (comboBuffering.SelectedItem != null)
                    WritePrivateProfileString(IniSection, "Buffering", comboBuffering.SelectedItem.ToString(), IniPath);
                WritePrivateProfileString(IniSection, "ChartDelay", chkDelay.Checked.ToString().ToLowerInvariant(), IniPath);
                WritePrivateProfileString(IniSection, "ChartJitterUp", chkJitterUp.Checked.ToString().ToLowerInvariant(), IniPath);
                WritePrivateProfileString(IniSection, "ChartJitterDown", chkJitterDown.Checked.ToString().ToLowerInvariant(), IniPath);
                WritePrivateProfileString(IniSection, "ChartMissLoss", chkMissLoss.Checked.ToString().ToLowerInvariant(), IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                string value;
                value = ReadSetting("Server");
                if (!string.IsNullOrWhiteSpace(value)) txtServerIP.Text = value;
                value = ReadSetting("Port");
                if (!string.IsNullOrWhiteSpace(value)) txtServerPort.Text = value;
                SelectComboText(comboLoad, ReadSetting("Load"));
                SelectComboText(comboBuffering, ReadSetting("Buffering"));
                LoadCheckBox(chkDelay, ReadSetting("ChartDelay"));
                LoadCheckBox(chkJitterUp, ReadSetting("ChartJitterUp"));
                LoadCheckBox(chkJitterDown, ReadSetting("ChartJitterDown"));
                LoadCheckBox(chkMissLoss, ReadSetting("ChartMissLoss"));
            }
            catch { }
        }

        private string ReadSetting(string key)
        {
            StringBuilder buffer = new StringBuilder(256);
            GetPrivateProfileString(IniSection, key, "", buffer, buffer.Capacity, IniPath);
            return buffer.ToString();
        }

        private static void SelectComboText(ComboBox combo, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (string.Equals(combo.Items[i].ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private static void LoadCheckBox(CheckBox checkBox, string value)
        {
            bool parsed;
            if (bool.TryParse(value, out parsed)) checkBox.Checked = parsed;
        }

        private void PopulateLocalEndpoints()
        {
            comboLocalEnd.DropDownStyle = ComboBoxStyle.DropDownList;
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add(new LocalEndItem { Text = "0.0.0.0 (Any)", Address = IPAddress.Any });
            comboLocalEnd.Items.Add(new LocalEndItem { Text = ":: (IPv6 Any)", Address = IPAddress.IPv6Any });
            foreach (NicAddressInfo nic in NicHelper.GetUsableIPAddresses())
                comboLocalEnd.Items.Add(new LocalEndItem { Text = nic.DisplayText, Address = nic.Address });
            comboLocalEnd.SelectedIndex = 0;
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (_active)
            {
                StopTest("用户停止测试", true);
                return;
            }
            await StartTestAsync();
        }

        private async Task StartTestAsync()
        {
            int port;
            if (!int.TryParse(txtServerPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("请输入 1-65535 之间的服务器端口。", "端口无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string host = txtServerIP.Text.Trim().Trim('[', ']');
            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("请输入服务器 IP 或域名。", "缺少服务器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnStart.Enabled = false;
            try { if (_runtimeWarmupTask != null) await _runtimeWarmupTask; } catch { }
            btnStart.Enabled = true;
            if (_closing || IsDisposed) return;

            CleanupTempFiles();

            _active = true;
            _normalStopRequested = false;
            _reconnecting = false;
            _countdownTitleEnabled = !(Global.isYumeyo && Global.isUnlimitedTime);
            _lastDisplayedRemainingSeconds = -1;
            _cts = new CancellationTokenSource();
            EnsureChartAvailable();
            _chartSessionDisabled = _chartDisabled || _chart == null || _chart.IsDisposed ||
                                    _chart.WindowState == FormWindowState.Minimized;
            btnStart.Text = "停止";
            SetInputsEnabled(false);
            richTextBox1.Clear();

            IPAddress target;
            try
            {
                target = await ResolveTargetAsync(host, _cts.Token);
                if (target == null) throw new SocketException((int)SocketError.HostNotFound);
                txtServerIP.Text = target.ToString();
            }
            catch (Exception ex)
            {
                if (!_normalStopRequested) AppendColorText("[DNS] 解析失败：" + ex.Message, Color.OrangeRed, true);
                StopTest(null, false);
                return;
            }
            if (!_active) return;

            EnsureSelectedNICValid();

            LocalEndItem localItem = comboLocalEnd.SelectedItem as LocalEndItem;
            IPAddress localAddress = localItem == null ? IPAddress.Any : localItem.Address;
            if (localAddress.AddressFamily != target.AddressFamily)
            {
                AppendColorText(string.Format("所选本地地址 [{0}] 与目标 [{1}] 的协议版本不一致。", localAddress, target), Color.OrangeRed, true);
                StopTest(null, false);
                return;
            }

            int selectedLoad;
            _load = comboLoad.SelectedItem != null && int.TryParse(comboLoad.SelectedItem.ToString(), out selectedLoad)
                ? selectedLoad
                : 1;
            _bufferTicks = comboBuffering.SelectedIndex < 0 ? 0 : comboBuffering.SelectedIndex;
            ResetStatistics();
            PrintHeader(target, port);

            try
            {
                IPEndPoint actualLocalEndPoint;
                _socket = CreateConnectedSocket(target, port, localAddress, out actualLocalEndPoint);
                AppendLocalEndpoint(actualLocalEndPoint);
                _socket.ReceiveTimeout = 500;
                _welcomeSource = new TaskCompletionSource<bool>();
                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
                await PerformHandshakeAsync(_cts.Token);
                if (!_welcomeSource.Task.IsCompleted || !_welcomeSource.Task.Result)
                    throw new TimeoutException("服务器在 3 秒内没有响应握手。请确认服务端已启动且 UDP 端口可达。");

                _startedAt = DateTime.Now;
                _startTimeStr = _startedAt.ToString("yyyyMMdd_HHmmss");
                _fileTargetToken = MakeSafeFileNamePart(new IPEndPoint(target, port).ToString());
                if (!_registeredActiveTest)
                {
                    Interlocked.Increment(ref _activeTests);
                    _registeredActiveTest = true;
                }
                _sessionWatch = Stopwatch.StartNew();
                Interlocked.Exchange(ref _lastDownstreamReceiveMicroseconds, UdpGameProtocol.NowMicroseconds());
                UpdateCountdownTitle(0);
                TimeBeginPeriod(1);
                _timerPeriodActive = true;
                _sendTask = Task.Run(() => SendLoop(_cts.Token));
                if (_chart != null && !_chart.IsDisposed)
                {
                    _chart.SetInfo(target + ":" + port, _load, _bufferTicks);
                    _chart.SetSeriesVisibility(chkDelay.Checked, chkJitterUp.Checked, chkJitterDown.Checked, chkMissLoss.Checked);
                }
                _uiTimer.Start();
            }
            catch (Exception ex)
            {
                if (!_normalStopRequested) AppendColorText("[连接] 启动失败：" + ex.Message, Color.OrangeRed, true);
                StopTest(null, false);
            }
        }

        private static Socket CreateConnectedSocket(IPAddress target, int port, IPAddress selectedLocalAddress,
            out IPEndPoint actualLocalEndPoint)
        {
            Socket socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                bool automaticLocalAddress = selectedLocalAddress.Equals(IPAddress.Any) ||
                                             selectedLocalAddress.Equals(IPAddress.IPv6Any);
                if (!automaticLocalAddress)
                    socket.Bind(new IPEndPoint(selectedLocalAddress, 0));

                socket.Connect(new IPEndPoint(target, port));
                actualLocalEndPoint = socket.LocalEndPoint as IPEndPoint;
                if (actualLocalEndPoint == null || actualLocalEndPoint.Address.Equals(IPAddress.Any) ||
                    actualLocalEndPoint.Address.Equals(IPAddress.IPv6Any))
                    throw new SocketException((int)SocketError.AddressNotAvailable);

                if (!automaticLocalAddress && !AddressesEqual(actualLocalEndPoint.Address, selectedLocalAddress))
                    throw new InvalidOperationException(string.Format("Socket 实际绑定到 {0}，与所选地址 {1} 不一致。",
                        actualLocalEndPoint.Address, selectedLocalAddress));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static bool AddressesEqual(IPAddress left, IPAddress right)
        {
            if (left == null || right == null || left.AddressFamily != right.AddressFamily) return false;
            return left.GetAddressBytes().SequenceEqual(right.GetAddressBytes()) &&
                   (left.AddressFamily != AddressFamily.InterNetworkV6 || left.ScopeId == right.ScopeId);
        }

        private void AppendLocalEndpoint(IPEndPoint endpoint)
        {
            for (int i = 0; i < comboLocalEnd.Items.Count; i++)
            {
                LocalEndItem item = comboLocalEnd.Items[i] as LocalEndItem;
                if (item != null && AddressesEqual(item.Address, endpoint.Address))
                {
                    comboLocalEnd.SelectedIndex = i;
                    break;
                }
            }
            AppendColorText(string.Format("  使用接口: {0} 负载:{1} 缓冲:{2}tick [UDP/64tick] | NICX By Yumeyo",
                endpoint.Address, _load, _bufferTicks), Color.LightSkyBlue, true);
            AppendColorText("", Color.White, true);
            AppendColorText("时间(Tick)             延迟    ↑ 错过  丢失  抖动  ↓ 错过  丢失  抖动", Color.LightGreen, true);
            AppendColorText("--------------------------------------------------------------------------------", Color.DimGray, true);
        }

        private void EnsureSelectedNICValid()
        {
            LocalEndItem selected = comboLocalEnd.SelectedItem as LocalEndItem;
            if (selected == null) return;
            if (selected.Address.Equals(IPAddress.Any) || selected.Address.Equals(IPAddress.IPv6Any)) return;

            IPAddress selectedAddress = selected.Address;
            PopulateLocalEndpoints();
            for (int i = 0; i < comboLocalEnd.Items.Count; i++)
            {
                LocalEndItem item = comboLocalEnd.Items[i] as LocalEndItem;
                if (item != null && AddressesEqual(item.Address, selectedAddress))
                {
                    comboLocalEnd.SelectedIndex = i;
                    return;
                }
            }
            comboLocalEnd.SelectedIndex = 0;
        }

        private void EnsureChartAvailable()
        {
            if (_chartDisabled || (_chart != null && !_chart.IsDisposed)) return;
            _chart = new UDPGameTestChart();
            _chart.Location = new Point(Left, Bottom + 8);
            _chart.Show();
        }

        private async Task<IPAddress> ResolveTargetAsync(string host, CancellationToken token)
        {
            IPAddress direct;
            if (IPAddress.TryParse(host, out direct)) return direct;

            AppendColorText("[DNS] 正在解析域名：" + host, Color.Yellow, true);
            IPAddress[] addresses = await Task.Run(() => Dns.GetHostAddresses(host));
            token.ThrowIfCancellationRequested();
            IPAddress[] unique = addresses
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6)
                .GroupBy(ip => ip.ToString()).Select(g => g.First()).ToArray();
            AppendColorText("[DNS] 解析结果：", Color.Yellow, true);
            foreach (IPAddress address in unique) AppendColorText("  -> " + address, Color.White, true);

            LocalEndItem local = comboLocalEnd.SelectedItem as LocalEndItem;
            AddressFamily family = local == null ? AddressFamily.InterNetwork : local.Address.AddressFamily;
            IPAddress chosen = unique.FirstOrDefault(ip => ip.AddressFamily == family) ?? unique.FirstOrDefault();
            if (chosen != null) AppendColorText("[DNS] 已自动选择并填入：" + chosen, Global.Yumeyo2, true);
            return chosen;
        }

        private async Task PerformHandshakeAsync(CancellationToken token)
        {
            for (int attempt = 0; attempt < 12 && !_welcomeSource.Task.IsCompleted; attempt++)
            {
                SendHello();
                await Task.WhenAny(_welcomeSource.Task, Task.Delay(250, token));
            }
        }

        private void SendHello()
        {
            UdpGamePacket hello = new UdpGamePacket
            {
                Type = UdpGameMessageType.Hello,
                Load = (byte)_load,
                BufferTicks = (byte)_bufferTicks,
                Nonce = _nonce,
                SendMicroseconds = UdpGameProtocol.NowMicroseconds()
            };
            byte[] bytes = UdpGameProtocol.CreatePacket(hello, UdpGameProtocol.HeaderSize, null);
            _socket.Send(bytes);
        }

        private void ReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[2048];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int length = _socket.Receive(buffer);
                    long receivedAt = UdpGameProtocol.NowMicroseconds();
                    UdpGamePacket packet;
                    if (!UdpGameProtocol.TryParse(buffer, length, out packet)) continue;

                    if (packet.Type == UdpGameMessageType.Welcome && packet.Nonce == _nonce)
                    {
                        _sessionId = packet.SessionId;
                        Interlocked.Exchange(ref _lastDownstreamReceiveMicroseconds, receivedAt);
                        _welcomeSource.TrySetResult(true);
                        continue;
                    }
                    if (packet.Type != UdpGameMessageType.DownstreamData || packet.SessionId != _sessionId || _sessionId == 0) continue;
                    Interlocked.Exchange(ref _lastDownstreamReceiveMicroseconds, receivedAt);
                    _downstreamTracker.Add(packet, receivedAt);
                    lock (_statsLock)
                    {
                        _lastDownTick = packet.TickSequence;
                        _upMiss = packet.UpMissPercent;
                        _upLoss = packet.UpLossPercent;
                        _upJitter = packet.UpJitterMs;
                        if (packet.EchoClientMicroseconds > 0 && packet.EchoClientMicroseconds != _lastEchoClientTime)
                        {
                            _lastEchoClientTime = packet.EchoClientMicroseconds;
                            long serverQueue = packet.ServerReceiveMicroseconds > 0
                                ? Math.Max(0, packet.SendMicroseconds - packet.ServerReceiveMicroseconds)
                                : 0;
                            double rtt = Math.Max(0, receivedAt - packet.EchoClientMicroseconds - serverQueue) / 1000.0;
                            _currentRtt = rtt;
                            _allRtts.Add(rtt);
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (token.IsCancellationRequested || ex.SocketErrorCode == SocketError.Interrupted || ex.SocketErrorCode == SocketError.OperationAborted) break;
                    if (ex.SocketErrorCode != SocketError.TimedOut)
                    {
                        if (IsReconnectableSocketError(ex.SocketErrorCode))
                        {
                            RequestReconnectFromWorker();
                            Thread.Sleep(20);
                            continue;
                        }
                        RequestStopFromWorker("接收中断：" + ex.Message);
                        break;
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) RequestStopFromWorker("接收中断：" + ex.Message);
                    break;
                }
            }
        }

        private void SendLoop(CancellationToken token)
        {
            long interval = Math.Max(1, Stopwatch.Frequency / UdpGameProtocol.TickRate);
            long next = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                next += interval;
                try
                {
                    if (!_reconnecting && _sessionId != 0)
                    {
                        long now = UdpGameProtocol.NowMicroseconds();
                        UdpGamePacket packet = new UdpGamePacket
                        {
                            Type = UdpGameMessageType.UpstreamData,
                            SessionId = _sessionId,
                            PacketSequence = ++_upPacketSequence,
                            TickSequence = ++_upTickSequence,
                            PartIndex = 0,
                            PartCount = 1,
                            SendMicroseconds = now,
                            Load = (byte)_load,
                            BufferTicks = (byte)_bufferTicks
                        };
                        int size = UdpGameProtocol.GetUpstreamPacketSize(_load, _random);
                        _socket.Send(UdpGameProtocol.CreatePacket(packet, size, _random));
                    }
                }
                catch (SocketException ex)
                {
                    if (token.IsCancellationRequested) break;
                    if (IsReconnectableSocketError(ex.SocketErrorCode))
                    {
                        RequestReconnectFromWorker();
                    }
                    else
                    {
                        RequestStopFromWorker("发送中断：" + ex.Message);
                        break;
                    }
                }
                catch (ObjectDisposedException) { break; }

                long afterSend = Stopwatch.GetTimestamp();
                if (afterSend - next > interval * 4) next = afterSend;

                while (!token.IsCancellationRequested)
                {
                    long remaining = next - Stopwatch.GetTimestamp();
                    if (remaining <= 0) break;
                    double milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
                    if (milliseconds > 2) Thread.Sleep(Math.Max(1, (int)milliseconds - 1));
                    else Thread.SpinWait(80);
                }
            }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (!_active || _sessionWatch == null) return;
            RecordUiLoopPerformance();
            CheckDegradation();
            if (!_active || _sessionWatch == null) return;
            double elapsed = _sessionWatch.Elapsed.TotalSeconds;
            lblTime.Text = ((int)elapsed).ToString() + "s";
            UpdateCountdownTitle(elapsed);

            if (_countdownTitleEnabled && elapsed >= TestDurationSeconds)
            {
                StopTest("已完成 5 分钟测试", true);
                return;
            }

            long lastReceived = Interlocked.Read(ref _lastDownstreamReceiveMicroseconds);
            long silentFor = UdpGameProtocol.NowMicroseconds() - lastReceived;
            if (!_reconnecting && lastReceived > 0 && silentFor >= ServerSilenceBeforeReconnectMicroseconds)
                StartReconnect();

            if (_reconnecting)
            {
                ShowReconnectingState();
                return;
            }

            DisplaySnapshot snapshot = GetDisplaySnapshot();
            lblDelay.Text = FormatMilliseconds(snapshot.Delay);
            lblDelayAvg.Text = FormatMilliseconds(snapshot.AverageDelay);
            lblDelayHi1.Text = FormatMilliseconds(snapshot.HighOnePercentDelay);
            lblMissUp.Text = FormatPercent(snapshot.Up.MissPercent);
            lblLossUp.Text = FormatPercent(snapshot.Up.LossPercent);
            lblJitterUp.Text = FormatMilliseconds(snapshot.Up.JitterMs);
            lblMissDown.Text = FormatPercent(snapshot.Down.MissPercent);
            lblLossDown.Text = FormatPercent(snapshot.Down.LossPercent);
            lblJitterDown.Text = FormatMilliseconds(snapshot.Down.JitterMs);
            if (!_chartSessionDisabled && _chart != null && !_chart.IsDisposed &&
                _chart.WindowState != FormWindowState.Minimized)
            {
                _chart.AddDataPoint(elapsed, snapshot.Delay, snapshot.Up.JitterMs, snapshot.Down.JitterMs,
                    snapshot.Up.HasImpairment, snapshot.Down.HasImpairment);
            }
            AppendSampleLine(snapshot);
        }

        private void UpdateCountdownTitle(double elapsedSeconds)
        {
            if (!_countdownTitleEnabled) return;
            int remaining = Math.Max(0, TestDurationSeconds - (int)elapsedSeconds);
            if (remaining == _lastDisplayedRemainingSeconds) return;
            _lastDisplayedRemainingSeconds = remaining;
            Text = _baseWindowTitle + " (" + remaining + ")";
        }

        private void ShowReconnectingState()
        {
            lblDelay.Text = "重连中";
            lblDelayAvg.Text = lblDelayHi1.Text = "-";
            lblMissUp.Text = lblLossUp.Text = lblJitterUp.Text = "-";
            lblMissDown.Text = lblLossDown.Text = lblJitterDown.Text = "-";
        }

        private void StartReconnect()
        {
            if (!_active || _reconnecting || _sessionWatch == null || _cts == null || _cts.IsCancellationRequested) return;
            long lastReceived = Interlocked.Read(ref _lastDownstreamReceiveMicroseconds);
            if (lastReceived <= 0 ||
                UdpGameProtocol.NowMicroseconds() - lastReceived < ServerSilenceBeforeReconnectMicroseconds) return;
            _reconnecting = true;
            ShowReconnectingState();
            AppendColorText("[重连] 服务器响应中断，正在尝试重新连接...", Color.Yellow, true);
            _ = ReconnectAsync(_cts.Token);
        }

        private async Task ReconnectAsync(CancellationToken token)
        {
            _sessionId = 0;
            _nonce = CreateNonce();
            TaskCompletionSource<bool> welcomeSource = new TaskCompletionSource<bool>();
            _welcomeSource = welcomeSource;

            Stopwatch reconnectWatch = Stopwatch.StartNew();
            while (_active && !token.IsCancellationRequested &&
                   reconnectWatch.ElapsedMilliseconds < ReconnectTimeoutMilliseconds &&
                   !welcomeSource.Task.IsCompleted)
            {
                try
                {
                    SendHello();
                }
                catch (SocketException ex)
                {
                    if (!IsReconnectableSocketError(ex.SocketErrorCode))
                    {
                        _reconnecting = false;
                        StopTest("重连中断：" + ex.Message, true);
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    await Task.WhenAny(welcomeSource.Task, Task.Delay(250, token));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (!_active || token.IsCancellationRequested) return;
            if (!welcomeSource.Task.IsCompleted || !welcomeSource.Task.Result)
            {
                _reconnecting = false;
                StopTest("服务器连接中断，10 秒内重连失败", true);
                return;
            }

            lock (_statsLock)
            {
                _currentRtt = _upMiss = _upLoss = _upJitter = 0;
                _lastDownTick = 0;
                _lastEchoClientTime = 0;
            }
            _upPacketSequence = 0;
            _upTickSequence = 0;
            _downstreamTracker = new UdpGameDirectionTracker(_bufferTicks);
            Interlocked.Exchange(ref _lastDownstreamReceiveMicroseconds, UdpGameProtocol.NowMicroseconds());
            _reconnecting = false;
            AppendColorText("[重连] 已重新连接服务器，测试继续。", Color.Lime, true);
        }

        private static bool IsReconnectableSocketError(SocketError error)
        {
            return error == SocketError.ConnectionReset ||
                   error == SocketError.ConnectionRefused ||
                   error == SocketError.HostDown ||
                   error == SocketError.HostUnreachable ||
                   error == SocketError.NetworkDown ||
                   error == SocketError.NetworkReset ||
                   error == SocketError.NetworkUnreachable ||
                   error == SocketError.NoBufferSpaceAvailable;
        }

        private DisplaySnapshot GetDisplaySnapshot()
        {
            long now = UdpGameProtocol.NowMicroseconds();
            UdpGameDirectionSnapshot down = _downstreamTracker == null
                ? new UdpGameDirectionSnapshot()
                : _downstreamTracker.Snapshot(now);
            lock (_statsLock)
            {
                double average = _allRtts.Count == 0 ? 0 : _allRtts.Average();
                double highOne = 0;
                if (_allRtts.Count > 0)
                {
                    int count = Math.Max(1, (int)Math.Ceiling(_allRtts.Count * 0.01));
                    highOne = _allRtts.OrderByDescending(v => v).Take(count).Average();
                }
                return new DisplaySnapshot
                {
                    Delay = _currentRtt,
                    AverageDelay = average,
                    HighOnePercentDelay = highOne,
                    Tick = _lastDownTick,
                    Up = new UdpGameDirectionSnapshot
                    {
                        MissPercent = _upMiss,
                        LossPercent = _upLoss,
                        JitterMs = _upJitter,
                        HasImpairment = _upMiss > 0 || _upLoss > 0
                    },
                    Down = down
                };
            }
        }

        private void AppendSampleLine(DisplaySnapshot snapshot)
        {
            int second = _sessionWatch == null ? 0 : (int)_sessionWatch.Elapsed.TotalSeconds;
            Color prefix = second % 2 == 0 ? Global.Yumeyo2 : ColorTranslator.FromHtml("#ffa5cf");
            AppendColorText(string.Format("[{0:HH:mm:ss.fff}]({1}) ", DateTime.Now, snapshot.Tick), prefix, false);
            string body = string.Format("延迟={0:0.0}ms  ↑ {1:0.0}% {2:0.0}% {3:0.0}ms  ↓ {4:0.0}% {5:0.0}% {6:0.0}ms",
                snapshot.Delay, snapshot.Up.MissPercent, snapshot.Up.LossPercent, snapshot.Up.JitterMs,
                snapshot.Down.MissPercent, snapshot.Down.LossPercent, snapshot.Down.JitterMs);
            AppendColorText(body, GetDelayColor(snapshot.Delay), true);
        }

        private void PrintHeader(IPAddress target, int port)
        {
            AppendColorText(string.Format(">> [UDP游戏模拟] 目标: {0} | {1:yyyy-MM-dd HH:mm:ss}",
                new IPEndPoint(target, port), DateTime.Now), Color.Yellow, true);
        }

        private static Color GetDelayColor(double delay)
        {
            if (delay <= 10) return Color.Lime;
            if (delay <= 20) return Color.MediumSpringGreen;
            if (delay <= 30) return Color.FromArgb(185, 210, 50);
            if (delay <= 40) return Color.Gold;
            if (delay <= 50) return Color.Orange;
            if (delay <= 100) return Color.Tomato;
            return Color.OrangeRed;
        }

        private void AppendColorText(string text, Color color, bool newLine)
        {
            if (_closing || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, Color, bool>(AppendColorText), text, color, newLine); } catch { }
                return;
            }
            richTextBox1.SelectionStart = richTextBox1.TextLength;
            richTextBox1.SelectionLength = 0;
            if (_outputFont != null)
            {
                try { richTextBox1.SelectionFont = _outputFont; } catch { }
            }
            richTextBox1.SelectionColor = color;
            richTextBox1.AppendText(newLine ? text + Environment.NewLine : text);
            richTextBox1.ScrollToCaret();
        }

        private void AppendColorMap()
        {
            Color[] colors =
            {
                Color.Lime, Color.MediumSpringGreen, Color.FromArgb(185, 210, 50),
                Color.Gold, Color.Orange, Color.Tomato, Color.OrangeRed
            };
            string[] labels = { "     ≤10ms ", " 20ms  ", " 30ms  ", " 40ms  ", " 50ms  ", " 100ms ", " >100ms" };
            string[] arrows = { "     >>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>", ">>>>>>>" };

            AppendColorText("    ===========================================================", Global.Yumeyo2, true);
            for (int i = 0; i < labels.Length; i++)
            {
                AppendColorText(labels[i], colors[i], false);
                if (i < labels.Length - 1) AppendColorText("|", Color.Gray, false);
            }
            richTextBox1.AppendText("\n");

            for (int i = 0; i < arrows.Length; i++)
            {
                AppendColorText(arrows[i], colors[i], false);
                if (i < arrows.Length - 1) AppendColorText(" ", Color.Black, false);
            }
            richTextBox1.AppendText("\n");
            AppendColorText("    ===========================================================", Global.Yumeyo2, true);
        }

        private void CleanupTempFiles()
        {
            if (Volatile.Read(ref _activeTests) > 0) return;
            try
            {
                Task[] pendingWrites;
                lock (_shardTasksLock) pendingWrites = _shardWriteTasks.ToArray();
                if (pendingWrites.Length > 0) Task.WaitAll(pendingWrites, 1000);
                foreach (string file in Directory.GetFiles(Application.StartupPath, "NICX_UDPGame_Temp_*.txt"))
                    File.Delete(file);
                lock (_shardTasksLock) _shardWriteTasks.Clear();
            }
            catch { }
        }

        private void RecordUiLoopPerformance()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastUiTickTimestamp != 0)
            {
                double actualIntervalMs = (now - _lastUiTickTimestamp) * 1000.0 / Stopwatch.Frequency;
                double delayMs = Math.Max(0, actualIntervalMs - _uiTimer.Interval);
                _scheduleDelaySum += delayMs;
                _scheduleDelayCount++;
            }
            _lastUiTickTimestamp = now;
            _loopIterationCount++;
        }

        private void AutoSaveAndClear()
        {
            if (string.IsNullOrEmpty(_startTimeStr) || string.IsNullOrEmpty(_fileTargetToken)) return;

            int shardIndex = ++_shardIndex;
            string fileName = string.Format("NICX_UDPGame_Temp_{0}_{1}_{2}.txt",
                shardIndex, _fileTargetToken, _startTimeStr);
            string filePath = Path.Combine(Application.StartupPath, fileName);
            string text = richTextBox1.Text;

            Task writeTask = Task.Run(() =>
            {
                try
                {
                    string shardFooter = "测试记录分片" + shardIndex + "\n";
                    File.WriteAllText(filePath, text + shardFooter, Encoding.UTF8);
                }
                catch { }
            });
            lock (_shardTasksLock) _shardWriteTasks.Add(writeTask);

            richTextBox1.ResetText();
            AppendColorText("接测试记录分片" + shardIndex, Color.Gray, true);

            _rateDegradationCount = 0;
            _delayDegradationCount = 0;
            _scheduleDelaySum = 0;
            _scheduleDelayCount = 0;
            _loopIterationCount = 0;
            _lastUiTickTimestamp = Stopwatch.GetTimestamp();
            int sec = _sessionWatch == null ? 0 : (int)_sessionWatch.Elapsed.TotalSeconds;
            _nextCheckSec = ((sec / PerformanceCheckIntervalSeconds) + 2) * PerformanceCheckIntervalSeconds;
        }

        private void CheckDegradation()
        {
            if (_sessionWatch == null) return;
            int currentSec = (int)_sessionWatch.Elapsed.TotalSeconds;

            // 与 PingPP 一致：每 301 秒自动分片，不受输出行数影响。
            if (currentSec - _lastShardSec >= 301)
            {
                AutoSaveAndClear();
                _lastShardSec = currentSec;
                return;
            }

            // 启动阶段先积累更稳定的基准，避免 JIT、首次绘图或 GC 抖动造成误分片。
            if (_baselineTps == 0 && _nextCheckSec == 0 && currentSec >= PerformanceBaselineSeconds)
            {
                if (_scheduleDelayCount > 0)
                    _baselineTps = _scheduleDelaySum / _scheduleDelayCount;
                _baselineIterations = _loopIterationCount * PerformanceCheckIntervalSeconds / (double)currentSec;
                _nextCheckSec = currentSec + PerformanceCheckIntervalSeconds;
                _scheduleDelaySum = 0;
                _scheduleDelayCount = 0;
                _loopIterationCount = 0;
            }
            else if (_nextCheckSec > 0 && currentSec >= _nextCheckSec)
            {
                bool degraded = false;
                if (_scheduleDelayCount > 0)
                {
                    double averageDelay = _scheduleDelaySum / _scheduleDelayCount;
                    if (_baselineTps > 0.01 && averageDelay > _baselineTps * 4.0 && averageDelay > 10.0)
                        degraded = true;
                }

                if (degraded)
                    _delayDegradationCount++;
                else
                    _delayDegradationCount = 0;

                if (_baselineIterations > 0 && _loopIterationCount < _baselineIterations * 0.70)
                    _rateDegradationCount++;
                else
                    _rateDegradationCount = 0;

                _scheduleDelaySum = 0;
                _scheduleDelayCount = 0;
                _loopIterationCount = 0;

                if (_delayDegradationCount >= 2 || _rateDegradationCount >= 3)
                    AutoSaveAndClear();
                else
                    _nextCheckSec += PerformanceCheckIntervalSeconds;
            }
        }

        private async void btnSave_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_startTimeStr) || string.IsNullOrEmpty(richTextBox1.Text))
            {
                MessageBox.Show("当前没有测试记录可以保存", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Task[] pendingWrites;
            lock (_shardTasksLock) pendingWrites = _shardWriteTasks.ToArray();
            if (pendingWrites.Length > 0) await Task.WhenAll(pendingWrites);
            if (_closing || IsDisposed) return;

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "请选择保存测试结果的位置";
                dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dialog.FileName = string.Format("NICX_UDPGameTest_{0}_{1}.txt", _fileTargetToken, _startTimeStr);

                if (dialog.ShowDialog() != DialogResult.OK) return;
                try
                {
                    StringBuilder output = new StringBuilder();
                    output.AppendLine("=== 欢迎使用 延迟测试-UDP游戏模拟 ❤ 网络综合查询器X by Yumeyo ===");
                    output.AppendLine();

                    string pattern = string.Format("NICX_UDPGame_Temp_*_{0}_{1}.txt", _fileTargetToken, _startTimeStr);
                    string[] shardFiles = Directory.GetFiles(Application.StartupPath, pattern);
                    Array.Sort(shardFiles, (left, right) =>
                        ExtractShardNumber(left).CompareTo(ExtractShardNumber(right)));

                    foreach (string file in shardFiles)
                    {
                        string content = File.ReadAllText(file, Encoding.UTF8);
                        int lastNewline = content.TrimEnd('\r', '\n').LastIndexOf('\n');
                        if (lastNewline >= 0)
                        {
                            string lastLine = content.Substring(lastNewline + 1).Trim();
                            if (lastLine.StartsWith("测试记录分片", StringComparison.Ordinal))
                                content = content.Substring(0, lastNewline + 1);
                        }
                        output.Append(content);
                    }

                    string currentText = richTextBox1.Text;
                    int firstNewline = currentText.IndexOf('\n');
                    if (firstNewline >= 0)
                    {
                        string firstLine = currentText.Substring(0, firstNewline).Trim();
                        if (firstLine.StartsWith("接测试记录分片", StringComparison.Ordinal))
                            currentText = currentText.Substring(firstNewline + 1);
                    }
                    output.Append(currentText);
                    if (!currentText.EndsWith("\n", StringComparison.Ordinal)) output.AppendLine();

                    output.AppendLine();
                    output.AppendLine("=== 感谢使用 延迟测试-UDP游戏模拟 ❤ 网络综合查询器X by Yumeyo ===");
                    output.AppendLine("======== 导出于 NetInfoCheckerX by Yumeyo ========");

                    File.WriteAllText(dialog.FileName, output.ToString(), Encoding.UTF8);
                    MessageBox.Show("保存[" + dialog.FileName + "]成功!", "保存成功了",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static int ExtractShardNumber(string path)
        {
            string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
            int number;
            return parts.Length > 3 && int.TryParse(parts[3], out number) ? number : 0;
        }

        private static string MakeSafeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
            return new string(chars);
        }

        private void ResetStatistics()
        {
            lock (_statsLock)
            {
                _allRtts.Clear();
                _currentRtt = _upMiss = _upLoss = _upJitter = 0;
                _lastDownTick = 0;
                _lastEchoClientTime = 0;
            }
            _sessionId = 0;
            _upPacketSequence = 0;
            _upTickSequence = 0;
            _nonce = CreateNonce();
            _reconnecting = false;
            Interlocked.Exchange(ref _lastDownstreamReceiveMicroseconds, 0);
            _startTimeStr = null;
            _fileTargetToken = null;
            _shardIndex = 0;
            _lastShardSec = 0;
            _baselineTps = 0;
            _nextCheckSec = 0;
            _scheduleDelaySum = 0;
            _scheduleDelayCount = 0;
            _loopIterationCount = 0;
            _delayDegradationCount = 0;
            _rateDegradationCount = 0;
            _baselineIterations = 0;
            _lastUiTickTimestamp = 0;
            _downstreamTracker = new UdpGameDirectionTracker(_bufferTicks);
            ResetLabels();
        }

        private static uint CreateNonce()
        {
            uint nonce = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);
            return nonce == 0 ? 1U : nonce;
        }

        private void ResetLabels()
        {
            lblDelay.Text = lblDelayAvg.Text = lblDelayHi1.Text = "-";
            lblMissUp.Text = lblLossUp.Text = lblJitterUp.Text = "-";
            lblMissDown.Text = lblLossDown.Text = lblJitterDown.Text = "-";
            lblTime.Text = "-";
        }

        private static string FormatMilliseconds(double value) { return value.ToString("0.0") + "ms"; }
        private static string FormatPercent(double value) { return value.ToString("0.0") + "%"; }

        private void SetInputsEnabled(bool enabled)
        {
            comboLocalEnd.Enabled = enabled;
            txtServerIP.Enabled = enabled;
            txtServerPort.Enabled = enabled;
            comboLoad.Enabled = enabled;
            comboBuffering.Enabled = enabled;
            btnSave.Enabled = enabled;
        }

        private void ChartVisibilityChanged(object sender, EventArgs e)
        {
            if (_chart != null && !_chart.IsDisposed)
                _chart.SetSeriesVisibility(chkDelay.Checked, chkJitterUp.Checked, chkJitterDown.Checked, chkMissLoss.Checked);
        }

        private void lblTarget_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            if (_active) StopTest(null, false);
            SaveSettings();
            Point currentLocation = Location;
            Size currentSize = Size;

            UDPGameTest newForm = new UDPGameTest
            {
                StartPosition = FormStartPosition.Manual,
                Location = currentLocation,
                Size = currentSize
            };
            newForm.Show();
            Close();
            Dispose();
        }

        private void RequestStopFromWorker(string reason)
        {
            if (_closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action<string, bool>(StopTest), reason, true); } catch { }
        }

        private void RequestReconnectFromWorker()
        {
            if (_closing || IsDisposed || !IsHandleCreated || _sessionWatch == null || _reconnecting) return;
            Interlocked.Exchange(ref _lastDownstreamReceiveMicroseconds,
                UdpGameProtocol.NowMicroseconds() - ServerSilenceBeforeReconnectMicroseconds);
            try { BeginInvoke(new Action(StartReconnect)); } catch { }
        }

        private void StopTest(string reason, bool appendReason)
        {
            if (!_active) return;
            _normalStopRequested = true;
            _active = false;
            _reconnecting = false;
            if (_registeredActiveTest)
            {
                Interlocked.Decrement(ref _activeTests);
                _registeredActiveTest = false;
            }
            if (_uiTimer != null) _uiTimer.Stop();

            try
            {
                if (_socket != null && _sessionId != 0)
                {
                    UdpGamePacket goodbye = new UdpGamePacket { Type = UdpGameMessageType.Goodbye, SessionId = _sessionId };
                    _socket.Send(UdpGameProtocol.CreatePacket(goodbye, UdpGameProtocol.HeaderSize, null));
                }
            }
            catch { }
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_socket != null) _socket.Close(); } catch { }
            _socket = null;
            if (_sessionWatch != null) _sessionWatch.Stop();
            _sessionWatch = null;
            if (_timerPeriodActive)
            {
                TimeEndPeriod(1);
                _timerPeriodActive = false;
            }

            if (appendReason && !string.IsNullOrEmpty(reason))
                AppendColorText("[结束] " + reason, Color.Yellow, true);
            if (_countdownTitleEnabled) Text = _baseWindowTitle;
            btnStart.Text = "开测";
            SetInputsEnabled(true);
        }

        private void UDPGameTest_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            _closing = true;
            StopTest(null, false);
            CleanupTempFiles();
            if (_chart != null && !_chart.IsDisposed) _chart.Shutdown();
            ReleaseOutputFont();
        }
    }
}
