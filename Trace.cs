using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IP2Region.Net.XDB;


namespace NetInfoCheckerX
{
    public partial class Trace : Form
    {
        private CancellationTokenSource cts; // 取消令牌
        private bool isRunning = false;      // 运行状态标识
        private PrivateFontCollection _privateFonts;
        private Font _traceOutputFont;
        private bool _privateFontLeaseAcquired;

        // PrivateFontCollection 只让 GDI+ 看到字体，RichTextBox 底层的
        // RichEdit 仍会按字体名称向 GDI 查找。因此还需要把 TTF
        // 注册为“仅当前进程可见”的 GDI 字体资源。
        private const uint FontResourcePrivate = 0x0010;
        private const int WmFontChange = 0x001D;
        private const string CascadiaMonoFamilyName = "Cascadia Mono";
        private static readonly object TracePrivateFontSync = new object();
        private static string _registeredTraceFontPath;
        private static int _registeredTraceFontUsers;
        private static bool _tracePrivateFontRegistered;

        // 防火墙逻辑变量
        private bool isManualChanged = false;     // 标记本次运行是否手动改过状态
        private bool initialFirewallOn;           // 记录进入窗口时的初始防火墙状态
        private bool initialRuleExisted;          // 记录进入窗口时的初始规则状态
        private string ruleName = "NICX_ICMP_Unlock";
        private System.Windows.Forms.Timer flashTimer; // 闪烁计时器

        // 增加两个布尔值，用于缓存状态，避免重复弹窗/查询
        private bool _lastFwStatus;
        private bool _lastRuleStatus;

        // 新增：当前窗口的唯一标识符，防止多开窗口时串扰
        private ushort _instanceIdentifier;
        private int _udpProbeSequence = Environment.TickCount & 0xFFFF;
        private ConcurrentDictionary<string, string> _geoCache = new ConcurrentDictionary<string, string>();
        private int _activeGeoOnlineIndex = 0;
        // 在线增强最多同时 3 个请求，并在整个进程内平滑限制为每秒最多启动 5 个。
        // 请求超时完全由各 Provider 使用的 HttpHelper 负责。
        private const int GeoMaxStartsPerSecond = 5;
        private SemaphoreSlim _geoEnrichSemaphore = new SemaphoreSlim(3, 3);
        private static readonly SemaphoreSlim GeoRateGate = new SemaphoreSlim(1, 1);
        private static long _nextGeoRequestTimestamp;
        private ConcurrentDictionary<string, byte> _enrichPending = new ConcurrentDictionary<string, byte>();
        private Dictionary<int, string> _hopGeoOriginal = new Dictionary<int, string>();
        private Dictionary<string, int> _ipToHop = new Dictionary<string, int>();
        private string _mtrRuntimeNotice;


        // INI 读写
        private static int WritePrivateProfileString(string section, string key, string value, string filePath)
            => IniFileHelper.WritePrivateProfileString(section, key, value, filePath);
        private static int GetPrivateProfileString(string section, string key, string defaultValue,
            StringBuilder buffer, int size, string filePath)
            => IniFileHelper.GetPrivateProfileString(section, key, defaultValue, buffer, size, filePath);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AddFontResourceEx(string fileName, uint flags,
            IntPtr reserved);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveFontResourceEx(string fileName, uint flags,
            IntPtr reserved);

        // WinDivert 2.x 原生接口。项目固定为 x64，运行目录需同时包含
        // WinDivert.dll 与 WinDivert64.sys。
        private const ulong WinDivertFlagSniff = 0x0001;
        private const ulong WinDivertFlagRecvOnly = 0x0004;
        private const ulong WinDivertFlagNoInstall = 0x0010;
        private const int WinDivertLayerNetwork = 0;
        private const int WinDivertLayerReflect = 4;
        private const int WinDivertShutdownRecv = 0x1;
        private const int WinDivertShutdownBoth = 0x3;
        private const int WinDivertAddressSize = 80;
        private static readonly IntPtr InvalidWinDivertHandle = new IntPtr(-1);
        private static readonly ConcurrentDictionary<int, WinDivertTraceSession>
            ActiveWinDivertSessions =
                new ConcurrentDictionary<int, WinDivertTraceSession>();
        private static int _nextWinDivertSessionId;
        private static int _winDivertLifecycleHooked;
        private static int _winDivertProcessCleanupStarted;

        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStop = 0x0020;
        private const uint DeleteAccess = 0x00010000;
        private const uint ServiceControlStop = 0x00000001;
        private const uint ServiceStopped = 0x00000001;

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr WinDivertOpen(string filter, int layer,
            short priority, ulong flags);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertRecv(IntPtr handle, [Out] byte[] packet,
            uint packetLength, out uint receivedLength, [Out] byte[] address);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertSend(IntPtr handle, [In] byte[] packet,
            uint packetLength, out uint sentLength, [In] byte[] address);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertShutdown(IntPtr handle, int how);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertClose(IntPtr handle);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinDivertHelperCalcChecksums(
            [In, Out] byte[] packet, uint packetLength, [In, Out] byte[] address,
            ulong flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName,
            string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr serviceControlManager,
            string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr service, uint control,
            ref ServiceStatus serviceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr service,
            ref ServiceStatus serviceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr serviceHandle);

        private string IniPath => Path.Combine(Application.StartupPath, "NetInfoCheckerX.ini");
        private const string IniSection = "Trace";

        // ip2region v4 搜索器（仅 IPv4）
        private Searcher _ip2regionSearcherV4;
        private Searcher _ip2regionSearcherV6;

        public Trace()
        {
            InitializeComponent();
            EnsureWinDivertLifecycleHooks();
            this.MinimumSize = this.Size;
            // 初始化一个随机的标识符 (使用时间戳和随机数混合)
            _instanceIdentifier = (ushort)(DateTime.Now.Ticks % 60000 + new Random().Next(100, 5000));
        }

        private static void EnsureWinDivertLifecycleHooks()
        {
            if (Interlocked.Exchange(ref _winDivertLifecycleHooked, 1) != 0) return;
            Application.ApplicationExit += (sender, args) =>
                ReleaseWinDivertForProcessExit();
            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
                ReleaseWinDivertForProcessExit();
        }

        private static void ReleaseWinDivertForProcessExit()
        {
            if (Interlocked.Exchange(ref _winDivertProcessCleanupStarted, 1) != 0)
                return;

            foreach (WinDivertTraceSession session in ActiveWinDivertSessions.Values.ToArray())
                session.RequestStop();

            // WinDivertClose 只释放用户态 handle；官方驱动默认仍会留在内核中。
            // 仅当没有其他进程的 WinDivert handle 时才卸载，避免干扰其他软件。
            TryUnloadWinDivertDriver();
        }

        private static void StopWinDivertSessionsOwnedBy(Trace owner)
        {
            foreach (WinDivertTraceSession session in ActiveWinDivertSessions.Values.ToArray())
            {
                if (ReferenceEquals(session.Owner, owner)) session.RequestStop();
            }
        }

        private static bool HasExternalWinDivertHandles()
        {
            IntPtr reflectHandle = InvalidWinDivertHandle;
            try
            {
                int processId = Process.GetCurrentProcess().Id;
                string filter = $"processId != {processId} and event == OPEN";
                reflectHandle = WinDivertOpen(filter, WinDivertLayerReflect, 0,
                    WinDivertFlagSniff | WinDivertFlagRecvOnly | WinDivertFlagNoInstall);
                if (reflectHandle == IntPtr.Zero || reflectHandle == InvalidWinDivertHandle)
                    return true; // 无法确认时选择不卸载。

                // REFLECT 会先排入当前已存在的 handle；停止接收新事件后清点队列。
                WinDivertShutdown(reflectHandle, WinDivertShutdownRecv);
                byte[] filterObject = new byte[4096];
                byte[] address = new byte[WinDivertAddressSize];
                return WinDivertRecv(reflectHandle, filterObject,
                    (uint)filterObject.Length, out _, address);
            }
            catch
            {
                return true;
            }
            finally
            {
                if (reflectHandle != IntPtr.Zero &&
                    reflectHandle != InvalidWinDivertHandle)
                {
                    try { WinDivertClose(reflectHandle); } catch { }
                }
            }
        }

        private static void TryUnloadWinDivertDriver()
        {
            if (!ActiveWinDivertSessions.IsEmpty) return;
            if (HasExternalWinDivertHandles())
            {
                Debug.WriteLine("[WinDivert-Cleanup] 检测到其他进程仍在使用 WinDivert，跳过卸载。");
                return;
            }

            IntPtr manager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;
            try
            {
                manager = OpenSCManager(null, null, ScManagerConnect);
                if (manager == IntPtr.Zero) return;
                service = OpenService(manager, "WinDivert",
                    ServiceQueryStatus | ServiceStop | DeleteAccess);
                if (service == IntPtr.Zero) return;

                var status = new ServiceStatus();
                if (!QueryServiceStatus(service, ref status)) return;
                if (status.CurrentState != ServiceStopped)
                {
                    ControlService(service, ServiceControlStop, ref status);
                    Stopwatch wait = Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 2000)
                    {
                        Thread.Sleep(50);
                        if (!QueryServiceStatus(service, ref status)) return;
                        if (status.CurrentState == ServiceStopped) break;
                    }
                }

                if (status.CurrentState == ServiceStopped &&
                    ActiveWinDivertSessions.IsEmpty)
                {
                    bool deleted = DeleteService(service);
                    Debug.WriteLine(deleted
                        ? "[WinDivert-Cleanup] 驱动服务已停止并删除。"
                        : "[WinDivert-Cleanup] 驱动已停止，删除服务失败：" +
                          Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WinDivert-Cleanup] " + ex.Message);
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (manager != IntPtr.Zero) CloseServiceHandle(manager);
            }
        }

        private void SaveSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(comboTargetIP.Text))
                    WritePrivateProfileString(IniSection, "TargetIP", comboTargetIP.Text, IniPath);
                WritePrivateProfileString(IniSection, "Hops", txtHops.Text, IniPath);
                WritePrivateProfileString(IniSection, "Delay", txtDelay.Text, IniPath);
                WritePrivateProfileString(IniSection, "Port", txtTargetPort.Text, IniPath);
                WritePrivateProfileString(IniSection, "GEO", checkGEO.Checked.ToString().ToLower(), IniPath);
                WritePrivateProfileString(IniSection, "MTR", checkMTR.Checked.ToString().ToLower(), IniPath);
                string proto = radioICMP.Checked ? "ICMP" : (radioTCP.Checked ? "TCP" : "UDP");
                WritePrivateProfileString(IniSection, "Protocol", proto, IniPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var sb = new StringBuilder(256);
                GetPrivateProfileString(IniSection, "TargetIP", "", sb, sb.Capacity, IniPath);
                string target = sb.ToString();
                if (!string.IsNullOrEmpty(target) && comboTargetIP.Items.Count > 0)
                {
                    int idx = -1;
                    for (int i = 0; i < comboTargetIP.Items.Count; i++)
                        if (comboTargetIP.Items[i].ToString() == target) { idx = i; break; }
                    if (idx >= 0) comboTargetIP.SelectedIndex = idx;
                    else comboTargetIP.Text = target;
                }
                string val;
                GetPrivateProfileString(IniSection, "Hops", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtHops.Text = val;
                GetPrivateProfileString(IniSection, "Delay", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtDelay.Text = val;
                GetPrivateProfileString(IniSection, "Port", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) txtTargetPort.Text = val;
                GetPrivateProfileString(IniSection, "GEO", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkGEO.Checked = val.ToLower() == "true";
                GetPrivateProfileString(IniSection, "MTR", "", sb, sb.Capacity, IniPath);
                if (!string.IsNullOrEmpty(val = sb.ToString())) checkMTR.Checked = val.ToLower() == "true";
                GetPrivateProfileString(IniSection, "Protocol", "", sb, sb.Capacity, IniPath);
                string proto = sb.ToString();
                if (proto == "TCP") radioTCP.Checked = true;
                else if (proto == "UDP") radioUDP.Checked = true;
                else if (proto == "ICMP") radioICMP.Checked = true;
            }
            catch { }
        }

        // 修改后的 UpdateWDFUI
        private void UpdateWDFUI(bool useCache = false)
        {
            if (flashTimer != null) flashTimer.Stop();
            btnWDF.Font = new Font(btnWDF.Font, FontStyle.Regular);

            // 如果是初始化加载，直接用缓存；如果是点击后刷新，再实时查
            bool isFirewallOn = useCache ? _lastFwStatus : IsFirewallEnabled();
            bool hasRule = useCache ? _lastRuleStatus : IsICMPRuleExisted();

            if (!isFirewallOn)
            {
                btnWDF.Text = "防火关";
                btnWDF.ForeColor = Color.White;
            }
            else if (hasRule)
            {
                btnWDF.Text = "已放行";
                btnWDF.ForeColor = Color.Lime;
            }
            else
            {
                btnWDF.Text = "防火开";
                btnWDF.ForeColor = Color.Yellow;
                StartBtnFlash();
            }
            UpdateWindowTitleStatus();
        }
        private void UpdateWindowTitleStatus()
        {
            // 获取防火墙简要状态
            bool isOn = IsFirewallEnabled();
            bool hasRule = IsICMPRuleExisted();
            string wdfStatus = !isOn ? "防火关" : (hasRule ? "已放行" : "防火开");

            // 更新窗口标题文字
            this.Text = $"Trace+ ✧ NetInfoCheckerX | 权限:{Global.UACLevel} {wdfStatus}";
        }

        // 开启闪烁效果的方法
        private void StartBtnFlash()
        {
            if (flashTimer == null)
            {
                flashTimer = new System.Windows.Forms.Timer();
                flashTimer.Interval = 500;
                flashTimer.Tick += (s, e) =>
                {
                    if (btnWDF.IsDisposed) return;
                    // 来回切换粗体和常规体
                    btnWDF.Font = new Font(btnWDF.Font, btnWDF.Font.Bold ? FontStyle.Regular : FontStyle.Bold);
                };
            }
            flashTimer.Start();
        }
        private bool IsFirewallEnabled()
        {
            // 1. 获取输出（依然保留梦酱之前的双编码逻辑）
            string output = GetNetshOutput("advfirewall show allprofiles state", Encoding.UTF8);
            if (!IsOutputValid(output))
                output = GetNetshOutput("advfirewall show allprofiles state", Encoding.GetEncoding(936));

            // 2. 按照夢酱的思路：切除前三行
            // StringSplitOptions.None 保留空行，确保行数计算准确
            string[] lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // 如果行数够多，我们就跳过前3行，把剩下的拼回来
            string cleanOutput = "";
            if (lines.Length > 4)
            {
                cleanOutput = string.Join("\n", lines.Skip(4));
            }
            else
            {
                cleanOutput = output; // 行数太少就不切了
            }

            // 调试用（确认切完后是什么）：
            //MessageBox.Show("清理后的内容：\n" + cleanOutput);

            // 3. 在清理后的内容里进行比对
            return cleanOutput.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cleanOutput.Contains("启用") ||
                   cleanOutput.Contains("开启");
        }

        private bool IsICMPRuleExisted()
        {
            try
            {
                // 1. 同样先走 UTF-8 尝试
                string output = GetNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"", Encoding.UTF8);

                // 2. 如果 UTF-8 拿到的东西看起来完全不对（比如连 ruleName 都没有，或者全是问号）
                // 我们尝试 GB2312
                if (!output.Contains(ruleName))
                {
                    string legacyOutput = GetNetshOutput($"advfirewall firewall show rule name=\"{ruleName}\"", Encoding.GetEncoding(936));
                    if (legacyOutput.Contains(ruleName)) output = legacyOutput;
                }

                // 4. 调试输出（梦酱测试完可以关掉）
                //MessageBox.Show("规则内容：\n" + output);

                // IPv4/IPv6 两条同名规则都存在，才认为 Trace 已完整放行。
                return output.Contains(ruleName) &&
                       output.IndexOf("ICMPv4", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       output.IndexOf("ICMPv6", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // 辅助：判断输出是否有效（包含防火墙特有的状态词）
        private bool IsOutputValid(string text)
        {
            string[] keywords = { "ON", "OFF", "启用", "禁用", "开启", "关闭", "State", "状态" };
            return keywords.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // 辅助：统一获取命令行输出
        private string GetNetshOutput(string args, Encoding enc)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = enc
                };
                using (Process p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd();
                    // 如果返回的是空的或者是提示“未找到”，尝试用默认编码再扫一遍
                    if (string.IsNullOrWhiteSpace(s)) return "";
                    return s;
                }
            }
            catch { return ""; }
        }

        private async Task RunNetshCmd(string args)
        {
            await Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("netsh", args)
                    {
                        Verb = "runas",
                        CreateNoWindow = true,
                        UseShellExecute = true
                    };
                    Process.Start(psi)?.WaitForExit();
                }
                catch { }
            });
        }
        // 自动刷新网卡：当系统网卡变化导致选中网卡不存在时，刷新列表并恢复默认
        private void EnsureSelectedNICValid()
        {
            string selectedText = comboLocalEnd.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            if (selectedText.Contains("Any") || selectedText.Contains("系统默认") ||
                selectedText.Contains("ICMP兼容模式") || selectedText.StartsWith("0.0.0.0") ||
                selectedText.StartsWith("::")) return;

            // 刷新 IPv4/IPv6 网卡列表。
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                {
                    comboLocalEnd.Items.Add(nicAddress.DisplayText);
                }
            }
            catch { }

            // 尝试恢复原选中项
            bool found = false;
            foreach (var item in comboLocalEnd.Items)
            {
                if (item.ToString() == selectedText)
                {
                    comboLocalEnd.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found && comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;
        }

        private async void Trace_Load(object sender, EventArgs e)
        {
            // 1. 先做那些”秒开”的基础 UI 初始化
            comboLocalEnd.Items.Clear();
            comboLocalEnd.Items.Add("0.0.0.0 (Any)");
            comboLocalEnd.Items.Add(":: (IPv6 Any)");
            comboLocalEnd.Items.Add("系统默认 (ICMP兼容模式)");
            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;
            // 窗口载入时立即检查 Trace 相关依赖，让用户在开测前了解降级风险。
            CheckIpSearcherDependencies();
            CheckWinDivertDependencies();
            ApplyHighDpiOutputFont();
            AppendColorText("✧ 正在检查系统环境，请稍候... ✧\n", Color.White, true);

            // 2. 后台初始化 IP 数据库和防火墙状态
            await Task.Run(() =>
            {
                InitIp2Region();
                initialFirewallOn = _lastFwStatus = IsFirewallEnabled();
                initialRuleExisted = _lastRuleStatus = IsICMPRuleExisted();
            });

            // 3. UI 线程：填充网卡列表
            try
            {
                foreach (NicAddressInfo nicAddress in NicHelper.GetUsableIPAddresses())
                {
                    comboLocalEnd.Items.Add(nicAddress.DisplayText);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取网卡列表失败: " + ex.Message);
            }

            if (comboLocalEnd.Items.Count > 0) comboLocalEnd.SelectedIndex = 0;

            radioICMP.CheckedChanged += UpdateProtocolTip;
            radioTCP.CheckedChanged += UpdateProtocolTip;
            radioUDP.CheckedChanged += UpdateProtocolTip;

            // 3. 后台干完活了，回到 UI 线程更新界面
            UpdateWDFUI(true);
            UpdateProtocolTip(null, null); // 刷新协议提示文字
            // 开发调试服务器列表
            CloudControl.LoadTraceServers(comboTargetIP);
            CloudControl.ApplyDevTitle(this);
            LoadSettings();
            CloudControl.UsedTimesCounter("TracePP");
        }
        private void InitIp2Region()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string v4Path = Path.Combine(baseDir, "ip2region.v4.xdb");
                string v6Path = Path.Combine(baseDir, "ip2region.v6.xdb");

                // 初始化 IPv4 搜索器
                if (File.Exists(v4Path))
                {
                    _ip2regionSearcherV4 = new Searcher(CachePolicy.Content, v4Path);
                }
                else
                {
                    AppendColorText("ip2region.v4.xdb 未找到\n", Global.Yumeyo2, false);
                }

                // 新增：初始化 IPv6 搜索器
                if (File.Exists(v6Path))
                {
                    _ip2regionSearcherV6 = new Searcher(CachePolicy.Content, v6Path);
                }
                else
                {
                    AppendColorText("ip2region.v6.xdb 未找到\n", Global.Yumeyo2, false);
                }
            }
            catch (Exception ex)
            {
                AppendColorText("ip2region 初始化失敗：" + ex.Message + "\n", Global.Yumeyo2, false);
            }
        }

        private void ApplyHighDpiOutputFont()
        {
            float dpi = DeviceDpi;
            try
            {
                using (Graphics graphics = CreateGraphics())
                    dpi = Math.Max(dpi, graphics.DpiX);
            }
            catch { }

            Font selectedFont = null;
            string fontPath = Path.Combine(Application.StartupPath, "CascadiaMono.ttf");
            bool hasLocalFont = File.Exists(fontPath);

            // 100% 缩放（96 DPI）保留设计器中的新宋体；只有缩放
            // 高于 100% 时才尝试应用 Cascadia Mono。
            if (dpi <= 96F) return;

            // 优先使用主程序旁的 TTF。AddFontResourceEx(FR_PRIVATE) 只在
            // 本进程内注册，不会安装到 Windows 字体目录。
            if (hasLocalFont)
            {
                PrivateFontCollection fontCollection = null;
                try
                {
                    fontCollection = new PrivateFontCollection();
                    fontCollection.AddFontFile(fontPath);
                    FontFamily family = fontCollection.Families.FirstOrDefault(item =>
                        string.Equals(item.Name, CascadiaMonoFamilyName,
                            StringComparison.OrdinalIgnoreCase));

                    if (family != null && TryAcquireTracePrivateFont(fontPath))
                    {
                        _privateFontLeaseAcquired = true;
                        // RichEdit 可能已在 InitializeComponent 中创建，通知它
                        // 重新检查进程内可用的字体。
                        SendMessage(richTextBox1.Handle, WmFontChange, 0, 0);
                        selectedFont = new Font(family, 9F, FontStyle.Regular,
                            GraphicsUnit.Point);
                        _privateFonts = fontCollection;
                        fontCollection = null;
                        Debug.WriteLine("Trace 输出字体：进程私有 CascadiaMono.ttf");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Trace 加载私有字体失败：" + ex.Message);
                    if (_privateFontLeaseAcquired)
                    {
                        ReleaseTracePrivateFont();
                        _privateFontLeaseAcquired = false;
                    }
                }
                finally
                {
                    fontCollection?.Dispose();
                }
            }

            // 本地 TTF 不可用时，才尝试已安装的系统字体。必须先
            // 精确确认字体族存在，因为 new Font("不存在的名称")
            // 不会报错，而是会静默回退到默认字体。
            if (selectedFont == null && IsFontFamilyInstalled(CascadiaMonoFamilyName))
            {
                try
                {
                    selectedFont = new Font(CascadiaMonoFamilyName, 9F,
                        FontStyle.Regular, GraphicsUnit.Point);
                    Debug.WriteLine("Trace 输出字体：系统 Cascadia Mono");
                }
                catch { }
            }

            if (selectedFont != null)
            {
                _traceOutputFont = selectedFont;
                richTextBox1.Font = selectedFont;
                ApplyTraceOutputSelectionFont();
                toolTip1.SetToolTip(richTextBox1,
                    "可使用Ctrl+滚轮缩放字体大小\n当前输出字体: " +
                    selectedFont.FontFamily.Name +
                    (_privateFontLeaseAcquired ? " (程序目录)" : " (系统)"));
            }
        }

        private void ApplyTraceOutputSelectionFont()
        {
            if (_traceOutputFont == null || richTextBox1.IsDisposed) return;

            try
            {
                richTextBox1.SelectionLength = 0;
                richTextBox1.SelectionFont = _traceOutputFont;
            }
            catch { }
        }

        private static bool IsFontFamilyInstalled(string familyName)
        {
            try
            {
                using (var installedFonts = new InstalledFontCollection())
                {
                    return installedFonts.Families.Any(item =>
                        string.Equals(item.Name, familyName,
                            StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { return false; }
        }

        private static bool TryAcquireTracePrivateFont(string fontPath)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(fontPath); }
            catch { return false; }

            lock (TracePrivateFontSync)
            {
                if (_tracePrivateFontRegistered)
                {
                    if (!string.Equals(_registeredTraceFontPath, fullPath,
                        StringComparison.OrdinalIgnoreCase)) return false;

                    _registeredTraceFontUsers++;
                    return true;
                }

                if (AddFontResourceEx(fullPath, FontResourcePrivate, IntPtr.Zero) <= 0)
                    return false;

                _registeredTraceFontPath = fullPath;
                _registeredTraceFontUsers = 1;
                _tracePrivateFontRegistered = true;
                return true;
            }
        }

        private static void ReleaseTracePrivateFont()
        {
            lock (TracePrivateFontSync)
            {
                if (!_tracePrivateFontRegistered || _registeredTraceFontUsers <= 0)
                    return;

                _registeredTraceFontUsers--;
                if (_registeredTraceFontUsers != 0) return;

                if (RemoveFontResourceEx(_registeredTraceFontPath,
                    FontResourcePrivate, IntPtr.Zero))
                {
                    _registeredTraceFontPath = null;
                    _tracePrivateFontRegistered = false;
                }
            }
        }

        private void ReleaseTraceOutputFont()
        {
            // 先让 RichEdit 不再引用私有字体，再释放 GDI+/GDI 资源。
            try
            {
                if (!richTextBox1.IsDisposed && _traceOutputFont != null)
                    richTextBox1.Font = this.Font;
            }
            catch { }

            try { _traceOutputFont?.Dispose(); } catch { }
            _traceOutputFont = null;
            try { _privateFonts?.Dispose(); } catch { }
            _privateFonts = null;

            if (_privateFontLeaseAcquired)
            {
                ReleaseTracePrivateFont();
                _privateFontLeaseAcquired = false;
            }
        }
        /// <summary>
        /// 使用 ip2region 查询并返回格式化结果（Country/Province/City/Isp）
        /// 优化：不显示 "0" / "-" / "Reserved" 字段；若是内网/保留地址，返回中文友好提示。
        /// 若没有任何可用字段，返回 "未知"（你也可以改为空字符串以不显示）
        /// </summary>
        private string GetIpLocationString(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return string.Empty;

            if (!IPAddress.TryParse(ip, out var ipAddr))
                return "未知";

            // 1) 先處理私有/保留地址 (目前夢酱的邏輯主要是 V4，V6 的特殊地址通常數據庫會包含)
            string reservedLabel = GetPrivateOrReservedLabel(ipAddr);
            if (!string.IsNullOrEmpty(reservedLabel))
                return reservedLabel;

            try
            {
                string region = "";

                // 判斷 IP 類型並選擇對應的搜索器
                if (ipAddr.AddressFamily == AddressFamily.InterNetwork) // IPv4
                {
                    if (_ip2regionSearcherV4 == null) return "V4數據庫未加載";
                    region = _ip2regionSearcherV4.Search(ip);
                }
                else if (ipAddr.AddressFamily == AddressFamily.InterNetworkV6) // IPv6
                {
                    if (_ip2regionSearcherV6 == null) return "V6數據庫未加載";
                    region = _ip2regionSearcherV6.Search(ip);
                }
                else
                {
                    return "未知協議";
                }

                if (string.IsNullOrWhiteSpace(region)) return "未知";

                // 接下來的格式化邏輯（Split 和 Join）保持不變，因為 ip2region 的格式是一樣的
                var parts = region.Split('|');
                var fields = new List<string>();
                foreach (var part in parts)
                {
                    string clean = NormalizeField(part);
                    if (!string.IsNullOrEmpty(clean)) fields.Add(clean);
                }

                return fields.Count == 0 ? "未知" : string.Join("/", fields);
            }
            catch
            {
                return "查詢失敗";
            }
        }

        /// <summary>
        /// 规范化单个字段：去掉 "0", "0/0", "-", "Reserved", "reserved" 等占位值并 Trim。
        /// 若是保留/占位，返回空字符串。
        /// </summary>
        private string NormalizeField(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();

            // 常见无效占位值
            if (s == "0" || s == "0.0" || s == "0/0" || s == "-")
                return "";
            if (s.Equals("Reserved", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("保留", StringComparison.OrdinalIgnoreCase))
                return "";

            // 有些 ip2region 写成 "CN" 在 country；你可以保留或映射（这里保留原样）
            return s;
        }

        /// <summary>
        /// 判断是否是私有/保留/特殊地址并返回友好中文说明（如果匹配则返回非空字符串）
        /// 返回示例： "内网地址", "CGNAT(运营商私网)", "回环地址", "链路本地", "多播地址", "保留地址"
        /// 如果不是私有/保留地址，返回 null 或 空串。
        /// </summary>
        private string GetPrivateOrReservedLabel(IPAddress ipAddr)
        {
            if (ipAddr == null) return null;
            return IanaReservedIP.Check(ipAddr.ToString());
        }
        private void AppendColorText(string text, Color color, bool addNewLine = false,
            Font fontOverride = null)
        {
            if (this.IsDisposed || richTextBox1.IsDisposed) return;
            richTextBox1.SelectionStart = richTextBox1.Text.Length;
            richTextBox1.SelectionLength = 0;

            // RichEdit 会在异步 await、选区替换或 Clear() 后丢失当前
            // 字符格式。每次写入都显式指定字体，避免运行中途
            // 回退到设计器的微软雅黑。归属地等小字号可通过
            // fontOverride 保留它们自己的字号。
            Font outputFont = fontOverride ?? _traceOutputFont;
            if (outputFont != null)
            {
                try { richTextBox1.SelectionFont = outputFont; } catch { }
            }

            richTextBox1.SelectionColor = color;
            richTextBox1.AppendText(addNewLine ? text + Environment.NewLine : text);
            richTextBox1.ScrollToCaret();
        }

        private void UpdateProtocolTip(object sender, EventArgs e)
        {
            if (sender is RadioButton rb && !rb.Checked) return;

            string protocol = GetSelectedProtocol();
            richTextBox1.Clear();
            Color themeColor = Global.Yumeyo2;

            if (protocol == "ICMP")
            {
                AppendColorText("     ==== 欢迎使用 Trace + ❤ 网络综合查询器X by Yumeyo ====", themeColor, true);
                AppendColorText("当前选中 ICMP 协议，请先阅读下列提示：", Color.Lime, true);
                AppendColorText("    🔰 Trace+ 采用Socket模拟实现, 以实现网卡选择, \n", Color.Pink, true);
                AppendColorText("    1.需要 >>关闭防火墙/放行查询器X的ICMP<< 才能测试, 否则一跳也看不到 💦", Color.Yellow, true);
                AppendColorText("                  └─ 点击网卡右边[防火墙]按钮，快速操作", Color.Yellow, true);
                AppendColorText("    2.如有问题可 >>管理员权限运行<< 再尝试 💦", Color.Orange, true);
                AppendColorText("                 └─ 右键左上角[网卡]白字，快速操作\n", Color.Orange, true);
                AppendColorText("    🔰 此处归属地仅供参考，有疑惑可复制IP用“手动查询-IP地址”确认属地 ❤", Color.LightSkyBlue, true);
                AppendColorText("    🔰 IPv4/IPv6 均支持；指定网卡时请选择与目标相同地址族的本机 IP\n", Color.LightGreen, true);
                AppendColorText("🚀注意: 因测试原理，所有第三方Trace都可能互相干扰,", Color.Gold, true);
                AppendColorText("    建议同时只运行一个Trace测试，包括但不限于查询器X和同类软件! ", Color.Gold, true);
                comboLocalEnd.Enabled = true;
                txtTargetPort.Enabled = false;
            }
            else
            {
                AppendColorText("     ==== 欢迎使用 Trace + ❤ 网络综合查询器X by Yumeyo ====", themeColor, true);
                AppendColorText($"当前选中 {protocol} 协议，请先阅读下列提示：", Color.Lime, true);
                AppendColorText("    🔰 Trace+ 采用Socket模拟实现, 以实现网卡选择, \n", Color.Pink, true);
                AppendColorText($"    1. {protocol} 必须 >>管理员权限运行<< 💦", Color.Yellow, true);
                AppendColorText("                   └─ 右键左上角[网卡]白字，快速操作", Color.Yellow, true);
                AppendColorText("    2. 还要 >>关闭防火墙/放行查询器X的ICMP<< 才能测试 💦", Color.Yellow, true);
                AppendColorText("                  └─ 点击网卡右边[防火墙]按钮，快速操作\n", Color.Yellow, true);
                AppendColorText("    🔰 此处归属地仅供参考，有疑惑可复制IP用“手动查询-IP地址”确认属地 ❤", Color.LightSkyBlue, true);
                AppendColorText("    🔰 IPv4/IPv6 均支持；指定网卡时请选择与目标相同地址族的本机 IP\n", Color.LightGreen, true);
                AppendColorText("🚀注意: 因测试原理，所有第三方Trace都可能互相干扰,", Color.Gold, true);
                AppendColorText("    建议同时只运行一个Trace测试，包括但不限于查询器X和同类软件! ", Color.Gold, true);
                comboLocalEnd.Enabled = true;
                txtTargetPort.Enabled = true;
                // 切换协议时自动填入默认端口
                if (protocol == "TCP")
                    txtTargetPort.Text = "80";
                else if (protocol == "UDP")
                    txtTargetPort.Text = "53";
            }
        }

        private string GetSelectedProtocol()
        {
            if (radioTCP.Checked) return "TCP";
            if (radioUDP.Checked) return "UDP";
            return "ICMP";
        }

        private static bool IsCurrentProcessElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity)
                        .IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        private void SetUIState(bool running)
        {
            bool enableControls = !running;
            comboLocalEnd.Enabled = enableControls;
            txtHops.Enabled = enableControls;
            txtDelay.Enabled = enableControls;
            comboTargetIP.Enabled = enableControls;
            radioTCP.Enabled = enableControls;
            radioUDP.Enabled = enableControls;
            radioICMP.Enabled = enableControls;
            checkGEO.Enabled = enableControls;
            checkMTR.Enabled = enableControls;
            txtTargetPort.Enabled = !running && !radioICMP.Checked;
            btnWDF.Enabled = enableControls;
            btnSave.Enabled = enableControls;

            if (running) btnStartTrace.Text = "停止";
            else btnStartTrace.Text = "开测";
        }

        private async void btnStartTrace_Click(object sender, EventArgs e)
        {
            // 自动刷新网卡（若当前选中的网卡已不存在）
            EnsureSelectedNICValid();

            if (isRunning)
            {
                if (cts != null) cts.Cancel();
                return;
            }
            if ((radioTCP.Checked || radioUDP.Checked) && comboLocalEnd.Text.Contains("ICMP兼容模式"))
            {
                richTextBox1.Clear();
                AppendColorText("\n\nTCP/UDP Test 需绑定指定网卡，ICMP兼容模式下不支持。\n请选择本机 IP 网卡或切换到 ICMP 协议。\n", Color.Yellow, true);
                SetUIState(false);
                return;
            }

            // 新增：协议与环境前置检查
            bool isNotAdmin = !IsCurrentProcessElevated();

            // 情况 A: TCP/UDP 模式但非管理员
            if ((radioTCP.Checked || radioUDP.Checked) && isNotAdmin)
            {
                DialogResult drUac = MessageBox.Show(
                    "查询器X的 TCP/UDP Trace 需【以管理员身份运行】。\n\n" +
                    "【确认】立刻以管理员身份重启（当前输入的内容不会保留）\n" +
                    "【取消】稍后自行操作\n\n" +
                    "也可通过右键窗口左上角“网卡”白字尝试提权",
                    "权限不够了", MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation);

                if (drUac == DialogResult.OK)
                {
                    if (isRunning && cts != null) cts.Cancel();
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = Application.ExecutablePath;
                    startInfo.WorkingDirectory = Environment.CurrentDirectory;
                    startInfo.Verb = "runas";

                    try
                    {
                        Process.Start(startInfo);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("提权失败: " + ex.Message, "提权已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                return; // 终止本次开测
            }

            // 情况 B: 未关闭防火墙
            if (!comboLocalEnd.Text.Contains("ICMP兼容模式"))
            {
                bool fwOn = IsFirewallEnabled();
                bool ruleOk = IsICMPRuleExisted();
                if (fwOn && !ruleOk)
                {
                    MessageBox.Show(
                        "Trace+ 需【关闭防火墙】或【放行查询器X】才能正常使用，\n" +
                        "当前还未设置任意一种放行规则。\n\n" +
                        "请点击网卡右边【防火开】按钮, 选择放行方式\n" +
                        "或选择【ICMP兼容模式】网卡, 可发起测试, 但不支持识别/指定网卡",
                        "遇到问题了", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    return; // 终止本次开测
                }
            }

            string input = comboTargetIP.Text.Trim().ToLower();
            if (input.StartsWith("http://")) input = input.Substring(7);
            else if (input.StartsWith("https://")) input = input.Substring(8);
            if (input.Contains("/")) input = input.Split('/')[0];
            input = Regex.Replace(input, @"[^a-z0-9\.\:\-_]", "");

            if (string.IsNullOrEmpty(input))
            {
                SystemSounds.Beep.Play();
                return;
            }

            comboTargetIP.Text = input;
            richTextBox1.Clear();
            string inputTarget = input;
            SetUIState(true);
            IPAddress finalTargetIp = null;

            try
            {
                if (IPAddress.TryParse(inputTarget, out IPAddress directIp))
                {
                    finalTargetIp = directIp;
                }
                else
                {
                    if (this.IsDisposed) return;
                    AppendColorText($"[DNS]正在解析域名 {inputTarget} ...\n", Color.Yellow, true);

                    try
                    {
                        IPAddress[] resolved = await Dns.GetHostAddressesAsync(inputTarget);
                        List<IPAddress> addressList = resolved
                            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork ||
                                         ip.AddressFamily == AddressFamily.InterNetworkV6)
                            .Distinct()
                            .ToList();

                        if (addressList.Count == 0) throw new Exception("未解析到 IPv4/IPv6 地址");

                        comboTargetIP.Items.Clear();
                        comboTargetIP.Items.Add(inputTarget);
                        foreach (var ip in addressList) comboTargetIP.Items.Add(ip.ToString());

                        comboTargetIP.DroppedDown = true;
                        if (comboTargetIP.Items.Count == 2)
                        {
                            comboTargetIP.SelectedIndex = 1;
                            AppendColorText($"\n[DNS]解析到 {addressList.Count} 个目标 IP。", Color.Yellow, true);
                            AppendColorText($"[DNS]已经选择了，再次点击“开测”。\n", Color.Yellow, true);
                        }
                        else
                        {
                            AppendColorText($"\n[DNS]解析到 {addressList.Count} 个目标 IP。", Color.Yellow, true);
                            AppendColorText($"[DNS]请选择一个IP后，点击“开测”。\n", Color.Yellow, true);
                        }

                        isRunning = false;
                        SetUIState(false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppendColorText($"[DNS]解析出错：{ex.Message}\n", Color.Yellow, true);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (this.IsDisposed) return;
                AppendColorText($"[DNS]解析出错：{ex.Message}\n", Color.Yellow, true);
                return;
            }
            finally
            {
                if (finalTargetIp == null) SetUIState(false);
            }

            if (finalTargetIp == null) return;

            isRunning = true;
            cts = new CancellationTokenSource();
            // TraceGEO 是开发者专用的在线增强提供方选择。
            // 不能仅依赖 About 页隐藏控件，运行时也必须校验权限。
            _activeGeoOnlineIndex = Global.isYumeyo
                ? ReadTraceGEOIndexFromIni()
                : 0;
            _geoCache.Clear();
            _enrichPending.Clear();
            _hopGeoOriginal.Clear();
            _ipToHop.Clear();
            _mtrRuntimeNotice = null;
            CancellationToken token = cts.Token;

            if (!int.TryParse(txtHops.Text, out int maxHops) || maxHops < 1 || maxHops > 255)
            {
                maxHops = 30;
                txtHops.Text = maxHops.ToString();
            }
            if (!int.TryParse(txtDelay.Text, out int maxDelayMs) || maxDelayMs < 1 || maxDelayMs > 60000)
            {
                maxDelayMs = 500;
                txtDelay.Text = maxDelayMs.ToString();
            }

            string selectedMethod = GetSelectedProtocol();
            if (!int.TryParse(txtTargetPort.Text, out int targetPort) || targetPort < 1 || targetPort > 65535)
            {
                targetPort = (selectedMethod == "TCP") ? 80 : 53;
                if (selectedMethod != "ICMP") txtTargetPort.Text = targetPort.ToString();
            }

            try
            {
                if (this.IsDisposed) return;
                // --- 网卡解析逻辑 ---
                IPAddress localExportIp;
                string userSelectIp = "";
                this.Invoke(new Action(() => { userSelectIp = comboLocalEnd.Text; }));

                if (userSelectIp.Contains(" ")) userSelectIp = userSelectIp.Split(' ')[0];

                bool compatibilityMode = userSelectIp.Contains("ICMP兼容模式");
                bool anyV4 = userSelectIp == "0.0.0.0";
                bool anyV6 = userSelectIp == "::";

                if (compatibilityMode || anyV4 || anyV6)
                {
                    if (!compatibilityMode &&
                        ((anyV4 && finalTargetIp.AddressFamily != AddressFamily.InterNetwork) ||
                         (anyV6 && finalTargetIp.AddressFamily != AddressFamily.InterNetworkV6)))
                    {
                        throw new InvalidOperationException(
                            $"所选接口 {userSelectIp} 与目标 {finalTargetIp} 的地址族不一致，请选择匹配的 IPv4/IPv6 网卡。");
                    }
                    localExportIp = GetLocalExportIP(finalTargetIp);
                    string detectedIpStr = localExportIp.ToString();
                    for (int i = 0; i < comboLocalEnd.Items.Count; i++)
                    {
                        if (comboLocalEnd.Items[i].ToString().StartsWith(detectedIpStr))
                        {
                            comboLocalEnd.SelectedIndex = i;
                            break;
                        }
                    }
                }
                else
                {
                    if (!IPAddress.TryParse(userSelectIp, out localExportIp)) localExportIp = GetLocalExportIP(finalTargetIp);
                }

                if (localExportIp.AddressFamily != finalTargetIp.AddressFamily)
                {
                    throw new InvalidOperationException(
                        $"本机接口 {localExportIp} 与目标 {finalTargetIp} 的地址族不一致，请重新选择网卡。");
                }

                if (this.IsDisposed) return;

                string finalPort = String.Empty;
                if (selectedMethod == "TCP" || selectedMethod == "UDP")
                {
                    finalPort = $"端口 {txtTargetPort.Text}";
                }
                else
                {
                    finalPort = String.Empty;
                }
                // --- 统一输出提示信息 ---
                string portInfo = (selectedMethod == "TCP" || selectedMethod == "UDP") ? $" 端口:{txtTargetPort.Text}" : "";
                AppendColorText($">> [Trace+] 目标: {finalTargetIp} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", Color.Lime, false);
                if (comboLocalEnd.Text.Contains("ICMP兼容模式"))
                {
                    AppendColorText($"   使用接口: {comboLocalEnd.Text} 跳数:{maxHops} 超时:{maxDelayMs}ms 协议:{selectedMethod}{portInfo} | NICX By Yumeyo\n\n", Color.LightSkyBlue, false);
                }
                else
                {
                    AppendColorText($"   使用接口: {localExportIp} 跳数:{maxHops} 超时:{maxDelayMs}ms 协议:{selectedMethod}{portInfo} | NICX By Yumeyo\n\n", Color.LightSkyBlue, false);
                }

                if (checkMTR.Checked)
                {
                    if (compatibilityMode && selectedMethod == "ICMP")
                        await RunNativeMtrTrace(finalTargetIp, localExportIp, maxHops, maxDelayMs, token);
                    else
                        await RunMtrTrace(finalTargetIp, localExportIp, maxHops, maxDelayMs, selectedMethod, targetPort, token);
                }
                else if (selectedMethod == "ICMP")
                {
                    if (comboLocalEnd.Text.Contains("ICMP兼容模式"))
                    {
                        await RunNativeIcmpTrace(finalTargetIp, maxHops, maxDelayMs, token);
                    }
                    else
                    {
                        await RunIcmpTrace(finalTargetIp, localExportIp, maxHops, maxDelayMs, token);
                    }
                }
                else
                {
                    await RunSocketTrace(finalTargetIp, localExportIp, maxHops, maxDelayMs, selectedMethod, targetPort, token);
                }
            }
            catch (OperationCanceledException)
            {
                AppendColorText(" ■ 用户手动停止测试", Color.Yellow, true);
            }
            catch (Exception ex)
            {
                if (!this.IsDisposed)
                    AppendColorText($"\n\n    执行出错: {ex.Message}", Color.Orange, false);
            }
            finally
            {
                if (!this.IsDisposed)
                {
                    isRunning = false;
                    SetUIState(false);
                }
                if (cts != null) cts.Dispose();
            }
        }

        // 梦酱专属：原生 ICMP 兼容模式 Trace
        private async Task RunNativeIcmpTrace(IPAddress targetIp, int maxHops, int timeout, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool isV6 = targetIp.AddressFamily == AddressFamily.InterNetworkV6;
            var results = new ConcurrentDictionary<int, HopResult>();

            async Task ProbeHop(int ttl, CancellationToken hopToken)
            {
                var result = new HopResult(ttl);
                using (Ping pingSender = new Ping())
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (hopToken.IsCancellationRequested) break;
                        if (i > 0) await Task.Delay(40, hopToken);
                        PingOptions options = new PingOptions(ttl, true);
                        byte[] buffer = Encoding.ASCII.GetBytes("YumeyoNICX_Trace_Packet");
                        Stopwatch sw = Stopwatch.StartNew();
                        try
                        {
                            PingReply reply = await pingSender.SendPingAsync(targetIp, timeout, buffer, options);
                            sw.Stop();
                            if (reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired)
                            {
                                if (result.ReplyAddress == null && geoChecked)
                                    result.GeoInfo = ResolveGeoInfo(reply.Address.ToString(), hopToken);
                                result.ReplyAddress = reply.Address;
                                result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                if (reply.Status == IPStatus.Success) result.TargetReached = true;
                            }
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            sw.Stop();
                            result.RTTs[i] = -2;
                        }
                    }
                }
                results[ttl] = result;
            }

            var tasks = new List<Task>();
            for (int ttl = 1; ttl <= maxHops; ttl++)
            { int ct = ttl; tasks.Add(Task.Run(() => ProbeHop(ct, token), token)); }
            var allDone = Task.WhenAll(tasks);
            var globalTimer = Stopwatch.StartNew();
            int hopTimeout = timeout * 4 + 200;//最长等待时间 原生
            int missingStreak = 0;
            const int minPacingMs = 50; //显示缓冲时间
            bool reachedTarget = false;
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                if (this.IsDisposed || token.IsCancellationRequested) break;
                int effectiveWait;
                if (allDone.IsCompleted)
                    effectiveWait = 0;
                else if (missingStreak > 0)
                {
                    int futureDone = 0;
                    for (int f = ttl + 1; f <= Math.Min(ttl + 6, maxHops); f++)
                        if (results.ContainsKey(f)) futureDone++;
                    if (futureDone >= 3) effectiveWait = 400;
                    else if (futureDone >= 1) effectiveWait = Math.Min(700, hopTimeout);
                    else effectiveWait = Math.Max(400, hopTimeout - missingStreak * 250);
                }
                else effectiveWait = hopTimeout;
                effectiveWait = Math.Min(effectiveWait, Math.Max(0, hopTimeout - (int)globalTimer.ElapsedMilliseconds));
                var iterStart = Stopwatch.StartNew();
                var waited = Stopwatch.StartNew();
                while (!results.TryGetValue(ttl, out _) && waited.ElapsedMilliseconds < effectiveWait
                       && !allDone.IsCompleted && !token.IsCancellationRequested)
                    await Task.Delay(20);
                HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                DisplaySingleHop(hop, geoChecked, isV6);
                if (hop.TargetReached) { reachedTarget = true; break; }
                missingStreak = hop.HasAnyResponse ? 0 : missingStreak + 1;
                int iterMs = (int)iterStart.ElapsedMilliseconds;
                if (iterMs < minPacingMs)
                    await Task.Delay(minPacingMs - iterMs);
            }
            try { await allDone; } catch { }
            if (geoChecked) await WaitForEnrichmentsAsync(token);
            if (reachedTarget)
                AppendColorText("\nTrace 完成.\n", Color.Lime, false);
        }

        private async Task RunNativeMtrTrace(IPAddress targetIp, IPAddress localIp, int maxHops,
            int timeout, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            var stats = new ConcurrentDictionary<int, MtrHopStats>();
            for (int ttl = 1; ttl <= maxHops; ttl++)
                stats[ttl] = new MtrHopStats { TTL = ttl };

            string targetLabel = targetIp.ToString();
            int round = 0;
            int effectiveMaxHops = maxHops;
            int confirmedTargetHop = 0;
            bool targetEverReached = false;
            byte[] buffer = Encoding.ASCII.GetBytes("YumeyoNICX_Trace_Packet");

            while (!token.IsCancellationRequested && !IsDisposed)
            {
                round++;
                int roundTargetHop = 0;
                for (int ttl = 1; ttl <= effectiveMaxHops; ttl++)
                {
                    token.ThrowIfCancellationRequested();
                    MtrHopStats stat = stats[ttl];
                    stat.Sent++;
                    using (var ping = new Ping())
                    {
                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            PingReply reply = await ping.SendPingAsync(targetIp, timeout, buffer,
                                new PingOptions(ttl, true));
                            stopwatch.Stop();
                            if (reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired)
                            {
                                string ipText = reply.Address.ToString();
                                stat.ReplyAddress = reply.Address;
                                stat.Received++;
                                stat.RTTs.Add(stopwatch.Elapsed.TotalMilliseconds);
                                if (!stat.IpAppearCount.ContainsKey(ipText)) stat.IpAppearCount[ipText] = 0;
                                stat.IpAppearCount[ipText]++;
                                if (!stat.AllIPs.Any(ip => ip.Equals(reply.Address)))
                                {
                                    stat.AllIPs.Add(reply.Address);
                                    stat.FirstSeenRound[ipText] = round;
                                }
                                if (geoChecked)
                                {
                                    if (!stat.IpGeoCache.TryGetValue(ipText, out string geo))
                                    {
                                        geo = GetLocalGeoInfo(ipText);
                                        stat.IpGeoCache[ipText] = geo;
                                    }
                                    stat.GeoInfo = geo;
                                    if (string.IsNullOrEmpty(IanaReservedIP.Check(ipText)))
                                    {
                                        int capturedTtl = ttl;
                                        _ = EnrichGeoOnlineAsync(ipText, capturedTtl, stats, token);
                                    }
                                }
                                if (reply.Status == IPStatus.Success) roundTargetHop = ttl;
                            }
                        }
                        catch (PingException ex)
                        {
                            Debug.WriteLine($"[Native-MTR] TTL={ttl}: {ex.Message}");
                        }
                    }

                    DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout, "ICMP",
                        round, geoChecked, targetEverReached, ttl);
                    if (roundTargetHop > 0) break;
                    if (ttl < effectiveMaxHops) await Task.Delay(100, token);
                }

                if (roundTargetHop > 0 && confirmedTargetHop == 0)
                {
                    confirmedTargetHop = roundTargetHop;
                    effectiveMaxHops = roundTargetHop;
                }
                if (roundTargetHop > 0) targetEverReached = true;
                DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout, "ICMP",
                    round, geoChecked, targetEverReached, effectiveMaxHops);
                await Task.Delay(800, token);
            }
        }

        // ==========================================
        // 第一部分：校验和计算
        // ==========================================
        private string GetLocalGeoInfo(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return string.Empty;

            if (!IPAddress.TryParse(ip, out _))
                return "未知";

            string reservedLabel = IanaReservedIP.Check(ip);
            if (!string.IsNullOrEmpty(reservedLabel))
                return reservedLabel;

            try
            {
                // Trace 的默认本地库与主界面/手动查询共用同一入口：
                // 有效 DLC 存在时优先使用 DLC，否则由该方法回退至
                // IP2Region + GeoCN。这里不受全局隐私模式影响。
                GeoResult localResult = Api2.GetLocalDBGeoAsync(ip, CancellationToken.None)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                string formatted = FormatOnlineGeoResult(localResult);
                return string.IsNullOrWhiteSpace(formatted) ? "未知" : formatted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GEO-Local] 本地数据库查询失败 ip={ip}: {ex.Message}");
                return "查询失败";
            }
        }

        private static string NormalizeOnlineGeoPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string trimmed = Regex.Replace(value.Trim(), @"\s+", " ");
            if (trimmed.StartsWith("@", StringComparison.Ordinal)) return null;

            // 不把仅由分隔符、占位符或 API 错误文字组成的内容当作成功结果。
            string compact = Regex.Replace(trimmed,
                @"[\s/\\|,;:()\[\]{}<>_\-@.]+", string.Empty);
            if (string.IsNullOrEmpty(compact)) return null;

            string lower = compact.ToLowerInvariant();
            string[] invalidValues =
            {
                "null", "undefined", "unknown", "none", "na", "n/a",
                "未知", "无", "失败", "error", "as", "asn"
            };
            if (invalidValues.Any(item => lower == item)) return null;

            string[] failureMarkers =
            {
                "查询失败", "请求失败", "解析失败", "解析出错", "未查询到结果",
                "数据库不存在", "格式不支持", "timed out", "timeout"
            };
            if (failureMarkers.Any(marker =>
                    trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                return null;

            return trimmed;
        }

        private static string FormatOnlineGeoResult(GeoResult geoResult)
        {
            if (geoResult == null) return null;
            string location = NormalizeOnlineGeoPart(geoResult.Loc);
            string asInfo = NormalizeOnlineGeoPart(geoResult.AS);
            if (location == null) return asInfo;
            if (asInfo == null || string.Equals(location, asInfo,
                    StringComparison.OrdinalIgnoreCase)) return location;
            return location + " " + asInfo;
        }

        private string GetOnlineGeoOrLocalFallback(string ip, GeoResult geoResult,
            out bool usedOnlineResult)
        {
            string online = FormatOnlineGeoResult(geoResult);
            usedOnlineResult = !string.IsNullOrWhiteSpace(online);
            return usedOnlineResult ? online : GetLocalGeoInfo(ip);
        }

        private string CacheLocalGeoFallback(string ip)
        {
            string fallback = GetLocalGeoInfo(ip);
            if (!string.IsNullOrWhiteSpace(fallback)) _geoCache[ip] = fallback;
            return fallback;
        }

        private static async Task WaitForGeoRequestSlotAsync(CancellationToken token)
        {
            await GeoRateGate.WaitAsync(token);
            try
            {
                long intervalTicks = Math.Max(1L,
                    Stopwatch.Frequency / GeoMaxStartsPerSecond);
                while (true)
                {
                    long now = Stopwatch.GetTimestamp();
                    long remainingTicks = _nextGeoRequestTimestamp - now;
                    if (remainingTicks <= 0)
                    {
                        _nextGeoRequestTimestamp = now + intervalTicks;
                        return;
                    }

                    int delayMs = Math.Max(1, (int)Math.Ceiling(
                        remainingTicks * 1000.0 / Stopwatch.Frequency));
                    await Task.Delay(delayMs, token);
                }
            }
            finally
            {
                GeoRateGate.Release();
            }
        }

        private string ResolveGeoInfo(string ip, CancellationToken token)
        {
            string geo = GetLocalGeoInfo(ip);
            bool isReserved = !string.IsNullOrEmpty(IanaReservedIP.Check(ip));
            Debug.WriteLine($"[GEO-Trace] 本地 ip={ip} reserved={isReserved} geo={geo}");
            if (!isReserved)
                EnrichGeoCacheAsync(ip, token);
            return geo;
        }

        private bool EnrichGeoCacheAsync(string ip, CancellationToken token)
        {
            if (!CanUseOnlineGeoEnhancement() || _geoCache.ContainsKey(ip)) return false;
            if (!_enrichPending.TryAdd(ip, 0)) return false;

            Debug.WriteLine($"[GEO-Trace] 发起查询 ip={ip}");
            _ = Task.Run(async () =>
            {
                bool semaphoreEntered = false;
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    await _geoEnrichSemaphore.WaitAsync(token);
                    semaphoreEntered = true;
                    if (!CanUseOnlineGeoEnhancement()) return;
                    if (_geoCache.ContainsKey(ip)) { Debug.WriteLine($"[GEO-Trace] 跳过(已缓存) ip={ip}"); return; }
                    var provider = Api2.GeoCN_Providers[_activeGeoOnlineIndex];
                    await WaitForGeoRequestSlotAsync(token);
                    GeoResult geoResult = await provider.GetGeoTaskIgnoringPrivacy(ip, token);
                    sw.Stop();
                    string enriched = GetOnlineGeoOrLocalFallback(ip, geoResult,
                        out bool usedOnlineResult);
                    if (!string.IsNullOrWhiteSpace(enriched)) _geoCache[ip] = enriched;
                    if (usedOnlineResult)
                        Debug.WriteLine($"[GEO-Trace] 完成 ip={ip} => {enriched} 耗时={sw.Elapsed.TotalSeconds:F1}s");
                    else
                        Debug.WriteLine($"[GEO-Trace] 在线结果无效，使用本地库 ip={ip} => {enriched} 耗时={sw.Elapsed.TotalSeconds:F1}s");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    sw.Stop();
                    string fallback = CacheLocalGeoFallback(ip);
                    Debug.WriteLine($"[GEO-Trace] 在线查询超时，使用本地库 ip={ip} => {fallback}");
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"[GEO-Trace] 查询已取消 ip={ip}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    string fallback = token.IsCancellationRequested
                        ? null
                        : CacheLocalGeoFallback(ip);
                    Debug.WriteLine($"[GEO-Trace] 在线查询异常，使用本地库 ip={ip} => {fallback}: {ex.Message}");
                }
                finally
                {
                    if (semaphoreEntered) _geoEnrichSemaphore.Release();
                    _enrichPending.TryRemove(ip, out _);
                    if (!IsDisposed && IsHandleCreated &&
                        _geoCache.TryGetValue(ip, out string enriched) &&
                        _ipToHop.TryGetValue(ip, out int hop))
                    {
                        try
                        {
                            BeginInvoke((Action)(() =>
                        {
                            if (_hopGeoOriginal.TryGetValue(hop, out string oldGeo) && enriched != oldGeo)
                            {
                                try
                                {
                                    int pos = richTextBox1.Find(oldGeo, 0, RichTextBoxFinds.None);
                                    if (pos >= 0)
                                    {
                                        richTextBox1.Select(pos, oldGeo.Length);
                                        richTextBox1.SelectedText = enriched;
                                        _hopGeoOriginal[hop] = enriched;
                                    }
                                }
                                catch { }
                            }
                        }));
                        }
                        catch { }
                    }
                }
            });
            return true;
        }

        private async Task WaitForEnrichmentsAsync(CancellationToken token)
        {
            if (!CanUseOnlineGeoEnhancement()) return;

            // 第一次扫描：为所有仍显示本地库格式的IP启动在线查询
            bool startedAny = false;
            foreach (var kvp in _ipToHop)
            {
                string ip = kvp.Key;
                int hop = kvp.Value;
                if (_hopGeoOriginal.ContainsKey(hop) &&
                    string.IsNullOrEmpty(IanaReservedIP.Check(ip)))
                {
                    startedAny |= EnrichGeoCacheAsync(ip, token);
                }
            }

            bool hasPending = _enrichPending.Count > 0;
            if (hasPending || startedAny)
            {
                if (hasPending)
                    AppendColorText("\n正在查询地理位置...", Global.Yumeyo2, false);
                while (_enrichPending.Count > 0)
                    await Task.Delay(100, token);
            }

            // 同步应用所有已完成的在线查询结果到 RichTextBox
            foreach (var kvp in _ipToHop)
            {
                string ip = kvp.Key;
                int hop = kvp.Value;
                if (_geoCache.TryGetValue(ip, out string enriched)
                    && _hopGeoOriginal.TryGetValue(hop, out string oldGeo)
                    && enriched != oldGeo)
                {
                    try
                    {
                        int pos = richTextBox1.Find(oldGeo, 0, RichTextBoxFinds.None);
                        if (pos >= 0)
                        {
                            richTextBox1.Select(pos, oldGeo.Length);
                            richTextBox1.SelectedText = enriched;
                            _hopGeoOriginal[hop] = enriched;
                        }
                    }
                    catch { }
                }
            }
        }

        private static void ApplyEnrichedGeoToAllStats(string ip, string enriched,
            ConcurrentDictionary<int, MtrHopStats> stats)
        {
            foreach (var kvp in stats)
            {
                if (kvp.Value.ReplyAddress?.ToString() == ip)
                {
                    kvp.Value.GeoInfo = enriched;
                    if (kvp.Value.IpGeoCache.ContainsKey(ip))
                        kvp.Value.IpGeoCache[ip] = enriched;
                }
            }
        }

        private async Task EnrichGeoOnlineAsync(string ip, int ttl,
            ConcurrentDictionary<int, MtrHopStats> stats, CancellationToken token)
        {
            if (!CanUseOnlineGeoEnhancement()) return;
            if (_geoCache.TryGetValue(ip, out string cached))
            {
                ApplyEnrichedGeoToAllStats(ip, cached, stats);
                return;
            }
            if (!_enrichPending.TryAdd(ip, 0)) return;

            Debug.WriteLine($"[GEO-MTR] 发起查询 ttl={ttl} ip={ip}");
            bool semaphoreEntered = false;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                await _geoEnrichSemaphore.WaitAsync(token);
                semaphoreEntered = true;
                if (!CanUseOnlineGeoEnhancement()) return;
                if (_geoCache.TryGetValue(ip, out cached))
                {
                    ApplyEnrichedGeoToAllStats(ip, cached, stats);
                    Debug.WriteLine($"[GEO-MTR] 跳过(已缓存) ttl={ttl} ip={ip}");
                    return;
                }
                var provider = Api2.GeoCN_Providers[_activeGeoOnlineIndex];
                await WaitForGeoRequestSlotAsync(token);
                GeoResult geoResult = await provider.GetGeoTaskIgnoringPrivacy(ip, token);
                sw.Stop();
                string enriched = GetOnlineGeoOrLocalFallback(ip, geoResult,
                    out bool usedOnlineResult);
                if (!string.IsNullOrWhiteSpace(enriched))
                {
                    _geoCache[ip] = enriched;
                    ApplyEnrichedGeoToAllStats(ip, enriched, stats);
                }
                if (usedOnlineResult)
                    Debug.WriteLine($"[GEO-MTR] 完成 ttl={ttl} ip={ip} => {enriched} 耗时={sw.Elapsed.TotalSeconds:F1}s");
                else
                    Debug.WriteLine($"[GEO-MTR] 在线结果无效，使用本地库 ttl={ttl} ip={ip} => {enriched} 耗时={sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                sw.Stop();
                string fallback = CacheLocalGeoFallback(ip);
                if (!string.IsNullOrWhiteSpace(fallback))
                    ApplyEnrichedGeoToAllStats(ip, fallback, stats);
                Debug.WriteLine($"[GEO-MTR] 在线查询超时，使用本地库 ttl={ttl} ip={ip} => {fallback}");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[GEO-MTR] 查询已取消 ttl={ttl} ip={ip}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                string fallback = token.IsCancellationRequested
                    ? null
                    : CacheLocalGeoFallback(ip);
                if (!string.IsNullOrWhiteSpace(fallback))
                    ApplyEnrichedGeoToAllStats(ip, fallback, stats);
                Debug.WriteLine($"[GEO-MTR] 在线查询异常，使用本地库 ttl={ttl} ip={ip} => {fallback}: {ex.Message}");
            }
            finally
            {
                if (semaphoreEntered) _geoEnrichSemaphore.Release();
                _enrichPending.TryRemove(ip, out _);
            }
        }

        private int ReadTraceGEOIndexFromIni()
        {
            if (!Global.isYumeyo) return 0;

            try
            {
                var sb = new StringBuilder(16);
                GetPrivateProfileString("Trace", "TraceGEO", "0", sb, sb.Capacity, IniPath);
                if (int.TryParse(sb.ToString(), out int idx) && idx > 0 && idx < Api2.GeoCN_Providers.Count)
                    return idx;
            }
            catch { }
            return 0;
        }

        private bool CanUseOnlineGeoEnhancement()
        {
            return Global.isYumeyo &&
                _activeGeoOnlineIndex > 0 &&
                _activeGeoOnlineIndex < Api2.GeoCN_Providers.Count;
        }

        private static ushort ComputeChecksumRange(byte[] data, int offset, int length)
        {
            uint sum = 0;
            int index = offset;
            int count = length;
            while (count > 1)
            {
                sum += (uint)((data[index] << 8) | data[index + 1]);
                index += 2;
                count -= 2;
            }
            if (count > 0) sum += (uint)(data[index] << 8);
            sum = (sum >> 16) + (sum & 0xffff);
            sum += (sum >> 16);
            return (ushort)(~sum);
        }

        private static ushort ComputeFoldedChecksumSum(byte[] data, int offset, int length)
        {
            uint sum = 0;
            int end = offset + length;
            for (int i = offset; i < end; i += 2)
            {
                ushort word = (ushort)(data[i] << 8);
                if (i + 1 < end) word |= data[i + 1];
                sum += word;
                while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            }
            return (ushort)sum;
        }

        private static ushort AddOnesComplement(ushort left, ushort right)
        {
            uint sum = (uint)left + right;
            sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)sum;
        }

        private static void ApplyParisChecksumCompensation(byte[] checksumData,
            int compensationOffset, ushort desiredChecksum)
        {
            checksumData[compensationOffset] = 0;
            checksumData[compensationOffset + 1] = 0;
            ushort currentSum = ComputeFoldedChecksumSum(checksumData, 0, checksumData.Length);
            ushort desiredSum = (ushort)~desiredChecksum;
            ushort compensation = AddOnesComplement(desiredSum, (ushort)~currentSum);
            checksumData[compensationOffset] = (byte)(compensation >> 8);
            checksumData[compensationOffset + 1] = (byte)compensation;
        }

        // 构造完整 IP+UDP 包，用于 raw socket 发送，避免 Windows 将 ICMP 错误
        // 关联到 UDP socket 而导致 raw ICMP receiver 收不到响应
        // 当目标端口为 53(DNS) 时，构造合法 DNS 查询 payload 以通过 DPI 检测
        private static readonly byte[] DnsQueryPayload = new byte[] {
            0x00, 0x01, // Transaction ID
            0x01, 0x00, // Flags: standard query, RD
            0x00, 0x01, // Questions: 1
            0x00, 0x00, // Answer RRs
            0x00, 0x00, // Authority RRs
            0x00, 0x00, // Additional RRs
            // Query: "test.local" type A, class IN
            0x04, 0x74, 0x65, 0x73, 0x74, // "test"
            0x05, 0x6c, 0x6f, 0x63, 0x61, 0x6c, // "local"
            0x00, // root label
            0x00, 0x01, // Type A
            0x00, 0x01  // Class IN
        };

        private ushort NextUdpProbeId()
        {
            return (ushort)(Interlocked.Increment(ref _udpProbeSequence) & 0xFFFF);
        }

        private static int GetUdpProbeSourcePort(int basePort, int ttl)
        {
            const int minPort = 1025;
            const int portRange = 65535 - minPort + 1;
            int normalized = Math.Max(0, basePort - minPort);
            return minPort + ((normalized + Math.Max(1, ttl)) % portRange);
        }

        private static byte[] GetUdpPayload(int dstPort, ushort probeId)
        {
            if (dstPort == 53)
            {
                byte[] dnsPayload = (byte[])DnsQueryPayload.Clone();
                dnsPayload[0] = (byte)(probeId >> 8);
                dnsPayload[1] = (byte)(probeId & 0xFF);
                return dnsPayload;
            }

            // 非 DNS 端口使用可识别的中性 payload，不触发 DNS DPI。
            return new byte[] {
                0x4E, 0x49, 0x43, 0x58, // "NICX"
                (byte)(probeId >> 8), (byte)(probeId & 0xFF),
                0x54, 0x52 // "TR"
            };
        }

        private static byte[] BuildUdpTracePacket(IPAddress srcIp, IPAddress dstIp,
            int srcPort, int dstPort, int ttl, ushort probeId)
        {
            byte[] payload = GetUdpPayload(dstPort, probeId);
            int udpLength = 8 + payload.Length;
            int totalLength = 20 + udpLength;
            byte[] packet = new byte[totalLength];
            byte[] src = srcIp.GetAddressBytes();
            byte[] dst = dstIp.GetAddressBytes();

            if (src.Length != 4 || dst.Length != 4)
                throw new ArgumentException("Raw UDP Trace 仅支持 IPv4。");

            // --- IP Header (20 bytes) ---
            packet[0] = 0x45; // Version=4, IHL=5 (20 bytes)
            packet[1] = 0x00; // DSCP/ECN = default
            packet[2] = (byte)(totalLength >> 8);
            packet[3] = (byte)(totalLength & 0xFF);
            packet[4] = (byte)(probeId >> 8);
            packet[5] = (byte)(probeId & 0xFF);
            packet[6] = 0x00; packet[7] = 0x00; // Flags=0, Fragment=0
            packet[8] = (byte)ttl;
            packet[9] = 17; // Protocol = UDP
            packet[10] = 0x00; packet[11] = 0x00; // Checksum placeholder
            Buffer.BlockCopy(src, 0, packet, 12, 4);
            Buffer.BlockCopy(dst, 0, packet, 16, 4);
            // Compute IP header checksum
            ushort ipCksum = ComputeChecksumRange(packet, 0, 20);
            packet[10] = (byte)(ipCksum >> 8);
            packet[11] = (byte)(ipCksum & 0xFF);

            // --- UDP Header (8 bytes) at offset 20 ---
            packet[20] = (byte)(srcPort >> 8);
            packet[21] = (byte)(srcPort & 0xFF);
            packet[22] = (byte)(dstPort >> 8);
            packet[23] = (byte)(dstPort & 0xFF);
            packet[24] = (byte)(udpLength >> 8);
            packet[25] = (byte)(udpLength & 0xFF);
            Buffer.BlockCopy(payload, 0, packet, 28, payload.Length);

            // Compute UDP checksum over pseudo-header + UDP header + payload.
            byte[] udpCksumData = new byte[12 + udpLength];
            Buffer.BlockCopy(src, 0, udpCksumData, 0, 4);
            Buffer.BlockCopy(dst, 0, udpCksumData, 4, 4);
            udpCksumData[8] = 0;
            udpCksumData[9] = 17; // Protocol = UDP
            udpCksumData[10] = (byte)(udpLength >> 8);
            udpCksumData[11] = (byte)(udpLength & 0xFF);
            // UDP header (with checksum field = 0)
            Buffer.BlockCopy(packet, 20, udpCksumData, 12, udpLength);
            ushort udpCksum = ComputeChecksumRange(udpCksumData, 0, udpCksumData.Length);
            if (udpCksum == 0) udpCksum = 0xFFFF;
            packet[26] = (byte)(udpCksum >> 8);
            packet[27] = (byte)(udpCksum & 0xFF);

            return packet;
        }

        private static Socket CreateRawUdpSocket(IPAddress localIp)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(localIp, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                socket.ReceiveBufferSize = 65536;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static Socket CreateIPv4CaptureSocket(IPAddress localIp)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            try
            {
                socket.Bind(new IPEndPoint(localIp, 0));
                socket.IOControl(IOControlCode.ReceiveAll,
                    new byte[] { 1, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 });
                socket.ReceiveBufferSize = 65536;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private Task StartRawUdpResponseReceiver(Socket rawUdpSocket, IPAddress targetIp, int targetPort,
            ConcurrentDictionary<int, TaskCompletionSource<IPAddress>> waiterStore,
            ConcurrentDictionary<int, ushort> transactionStore, CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] buffer = new byte[8192];
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                rawUdpSocket.ReceiveTimeout = 500;

                while (!token.IsCancellationRequested && !this.IsDisposed)
                {
                    try
                    {
                        int len = rawUdpSocket.ReceiveFrom(buffer, ref remoteEP);
                        if (len < 28 || (buffer[0] >> 4) != 4 || buffer[9] != 17) continue;

                        int ipHeaderLength = (buffer[0] & 0x0F) * 4;
                        if (ipHeaderLength < 20 || len < ipHeaderLength + 8) continue;

                        IPAddress peer = ((IPEndPoint)remoteEP).Address;
                        if (!peer.Equals(targetIp)) continue;

                        int udpOffset = ipHeaderLength;
                        int sourcePort = (buffer[udpOffset] << 8) | buffer[udpOffset + 1];
                        int localPort = (buffer[udpOffset + 2] << 8) | buffer[udpOffset + 3];
                        if (sourcePort != targetPort || !waiterStore.ContainsKey(localPort)) continue;

                        // DNS 应答还需匹配 Transaction ID 和 QR 位，避免同机其他 DNS 流量串入。
                        if (targetPort == 53)
                        {
                            int dnsOffset = udpOffset + 8;
                            if (len < dnsOffset + 4 || !transactionStore.TryGetValue(localPort, out ushort expectedId))
                                continue;
                            ushort responseId = (ushort)((buffer[dnsOffset] << 8) | buffer[dnsOffset + 1]);
                            bool isResponse = (buffer[dnsOffset + 2] & 0x80) != 0;
                            if (!isResponse || responseId != expectedId) continue;
                        }

                        if (waiterStore.TryRemove(localPort, out var waiter))
                            waiter.TrySetResult(targetIp);
                    }
                    catch (SocketException ex)
                    {
                        if (token.IsCancellationRequested) break;
                        if (ex.SocketErrorCode == SocketError.TimedOut ||
                            ex.SocketErrorCode == SocketError.WouldBlock ||
                            ex.SocketErrorCode == SocketError.Interrupted ||
                            ex.SocketErrorCode == SocketError.ConnectionReset ||
                            ex.SocketErrorCode == SocketError.NetworkReset ||
                            ex.SocketErrorCode == SocketError.HostUnreachable ||
                            ex.SocketErrorCode == SocketError.NetworkUnreachable)
                            continue;

                        Debug.WriteLine($"[UDP-Trace] Raw UDP receive stopped: {ex.SocketErrorCode} {ex.Message}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                            Debug.WriteLine($"[UDP-Trace] Raw UDP receive stopped: {ex.Message}");
                        break;
                    }
                }
            }, token);
        }

        // ==========================================
        // 第二部分：构造 ICMP 报文 (使用 InstanceID)
        // ==========================================
        private byte[] CreateIcmpPacket(ushort seqNum)
        {
            byte[] packet = new byte[32];
            packet[0] = 8; // Type: Echo Request
            packet[1] = 0; // Code: 0

            // 关键修改：使用当前窗口唯一的 _instanceIdentifier 作为 ID
            Buffer.BlockCopy(BitConverter.GetBytes(_instanceIdentifier), 0, packet, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(seqNum), 0, packet, 6, 2);

            byte[] payload = Encoding.ASCII.GetBytes("YumeyoTraceX-Paris");
            Buffer.BlockCopy(payload, 0, packet, 8, Math.Min(payload.Length, 22));

            // 序列号用于匹配探针；末尾 2 字节补偿其变化，使所有 ICMPv4
            // 探针保持相同校验和，避免部分 ECMP 设备将其视为不同流。
            const ushort desiredChecksum = 0xBEEF;
            ApplyParisChecksumCompensation(packet, packet.Length - 2, desiredChecksum);
            ushort checksum = ComputeChecksumRange(packet, 0, packet.Length);
            packet[2] = (byte)(checksum >> 8);
            packet[3] = (byte)checksum;
            return packet;
        }

        private static ushort ReadUInt16Network(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static uint ReadUInt32Network(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }

        private static bool AddressMatches(byte[] buffer, int offset, IPAddress address)
        {
            byte[] expected = address.GetAddressBytes();
            if (offset < 0 || buffer.Length < offset + expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (buffer[offset + i] != expected[i]) return false;
            return true;
        }

        private static bool TryGetIcmpV4Offset(byte[] buffer, int length, out int icmpOffset)
        {
            icmpOffset = 0;
            if (length < 28 || (buffer[0] >> 4) != 4) return false;
            int headerLength = (buffer[0] & 0x0F) * 4;
            if (headerLength < 20 || length < headerLength + 8 || buffer[9] != 1) return false;
            icmpOffset = headerLength;
            return true;
        }

        private static bool TryParseIcmpV4EchoReference(byte[] buffer, int length, IPAddress targetIp,
            out ushort identifier, out ushort sequence, out bool echoReply)
        {
            identifier = 0;
            sequence = 0;
            echoReply = false;
            if (!TryGetIcmpV4Offset(buffer, length, out int icmpOffset)) return false;
            byte type = buffer[icmpOffset];
            if (type == 0)
            {
                identifier = BitConverter.ToUInt16(buffer, icmpOffset + 4);
                sequence = BitConverter.ToUInt16(buffer, icmpOffset + 6);
                echoReply = true;
                return true;
            }
            if (type != 3 && type != 11) return false;

            int innerOffset = icmpOffset + 8;
            if (length < innerOffset + 20 || (buffer[innerOffset] >> 4) != 4) return false;
            int innerHeaderLength = (buffer[innerOffset] & 0x0F) * 4;
            if (innerHeaderLength < 20 || buffer[innerOffset + 9] != 1 ||
                length < innerOffset + innerHeaderLength + 8 ||
                !AddressMatches(buffer, innerOffset + 16, targetIp)) return false;

            int echoOffset = innerOffset + innerHeaderLength;
            identifier = BitConverter.ToUInt16(buffer, echoOffset + 4);
            sequence = BitConverter.ToUInt16(buffer, echoOffset + 6);
            return true;
        }

        private static bool TryParseIcmpV4TransportReference(byte[] buffer, int length,
            IPAddress targetIp, byte expectedProtocol, int targetPort, out int sourcePort)
        {
            sourcePort = 0;
            if (!TryGetIcmpV4Offset(buffer, length, out int icmpOffset)) return false;
            byte type = buffer[icmpOffset];
            if (type != 3 && type != 11) return false;

            int innerOffset = icmpOffset + 8;
            if (length < innerOffset + 20 || (buffer[innerOffset] >> 4) != 4) return false;
            int innerHeaderLength = (buffer[innerOffset] & 0x0F) * 4;
            if (innerHeaderLength < 20 || buffer[innerOffset + 9] != expectedProtocol ||
                length < innerOffset + innerHeaderLength + 4 ||
                !AddressMatches(buffer, innerOffset + 16, targetIp)) return false;

            int transportOffset = innerOffset + innerHeaderLength;
            sourcePort = ReadUInt16Network(buffer, transportOffset);
            return ReadUInt16Network(buffer, transportOffset + 2) == targetPort;
        }

        private static bool TryParseDirectTcpV4Response(byte[] buffer, int length,
            IPAddress targetIp, int targetPort, out int localPort)
        {
            localPort = 0;
            if (length < 40 || (buffer[0] >> 4) != 4 || buffer[9] != 6 ||
                !AddressMatches(buffer, 12, targetIp)) return false;
            int ipHeaderLength = (buffer[0] & 0x0F) * 4;
            if (ipHeaderLength < 20 || length < ipHeaderLength + 20) return false;
            if (ReadUInt16Network(buffer, ipHeaderLength) != targetPort) return false;
            byte flags = buffer[ipHeaderLength + 13];
            if ((flags & 0x04) == 0 && (flags & 0x12) != 0x12) return false;
            localPort = ReadUInt16Network(buffer, ipHeaderLength + 2);
            return true;
        }

        private static bool IsMatchingIcmpV4TransportResponse(byte[] buffer, int length,
            string protocol, ushort probeId, int sourcePort, int targetPort,
            ushort expectedUdpChecksum, IPAddress targetIp)
        {
            if (!TryGetIcmpV4Offset(buffer, length, out int icmpOffset)) return false;
            byte type = buffer[icmpOffset];
            if (type != 3 && type != 11) return false;

            int innerOffset = icmpOffset + 8;
            if (length < innerOffset + 20 || (buffer[innerOffset] >> 4) != 4 ||
                !AddressMatches(buffer, innerOffset + 16, targetIp)) return false;
            int innerHeaderLength = (buffer[innerOffset] & 0x0F) * 4;
            byte expectedProtocol = protocol == "TCP" ? (byte)6 : (byte)17;
            int transportOffset = innerOffset + innerHeaderLength;
            if (innerHeaderLength < 20 || buffer[innerOffset + 9] != expectedProtocol ||
                length < transportOffset + 8 ||
                ReadUInt16Network(buffer, transportOffset) != sourcePort ||
                ReadUInt16Network(buffer, transportOffset + 2) != targetPort) return false;

            if (protocol == "TCP")
            {
                uint expectedSequence = ((uint)probeId << 16) | (uint)sourcePort;
                return ReadUInt32Network(buffer, transportOffset + 4) == expectedSequence;
            }
            return ReadUInt16Network(buffer, transportOffset + 6) == expectedUdpChecksum;
        }

        private static bool IsMatchingDirectIPv4TransportResponse(byte[] buffer, int length,
            IPAddress remoteAddress, string protocol, ushort probeId, int sourcePort,
            int targetPort, IPAddress targetIp)
        {
            if (!remoteAddress.Equals(targetIp) || length < 28 ||
                (buffer[0] >> 4) != 4 || !AddressMatches(buffer, 12, targetIp)) return false;
            int ipHeaderLength = (buffer[0] & 0x0F) * 4;
            byte expectedProtocol = protocol == "TCP" ? (byte)6 : (byte)17;
            if (ipHeaderLength < 20 || buffer[9] != expectedProtocol ||
                length < ipHeaderLength + 8 ||
                ReadUInt16Network(buffer, ipHeaderLength) != targetPort ||
                ReadUInt16Network(buffer, ipHeaderLength + 2) != sourcePort) return false;

            if (protocol == "TCP")
            {
                if (length < ipHeaderLength + 20) return false;
                byte flags = buffer[ipHeaderLength + 13];
                bool accepted = (flags & 0x04) != 0 || (flags & 0x12) == 0x12;
                if (!accepted) return false;
                if ((flags & 0x10) == 0) return false;
                uint expectedAck = unchecked((((uint)probeId << 16) |
                    (uint)sourcePort) + 1u);
                return ReadUInt32Network(buffer, ipHeaderLength + 8) == expectedAck;
            }

            int payloadOffset = ipHeaderLength + 8;
            if (targetPort == 53)
            {
                return length >= payloadOffset + 4 &&
                       ReadUInt16Network(buffer, payloadOffset) == probeId &&
                       (buffer[payloadOffset + 2] & 0x80) != 0;
            }
            return IsMatchingUdpProbePayload(buffer, length, payloadOffset, probeId);
        }

        private static bool IsMatchingUdpProbePayload(byte[] buffer, int length,
            int payloadOffset, ushort probeId)
        {
            byte[] expected = GetUdpPayload(54, probeId);
            if (length < payloadOffset + expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (buffer[payloadOffset + i] != expected[i]) return false;
            return true;
        }

        private sealed class WinDivertUnavailableException : Exception
        {
            public WinDivertUnavailableException(string message) : base(message) { }
            public WinDivertUnavailableException(string message, Exception inner)
                : base(message, inner) { }
        }

        private sealed class WinDivertPendingProbe
        {
            public readonly int SourcePort;
            public readonly ushort ProbeId;
            public readonly ushort ExpectedUdpChecksum;
            public readonly TaskCompletionSource<IPAddress> Completion;

            public WinDivertPendingProbe(int sourcePort, ushort probeId,
                ushort expectedUdpChecksum)
            {
                SourcePort = sourcePort;
                ProbeId = probeId;
                ExpectedUdpChecksum = expectedUdpChecksum;
                Completion = new TaskCompletionSource<IPAddress>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private sealed class WinDivertTraceSession
        {
            private readonly Trace _owner;
            private readonly IPAddress _targetIp;
            private readonly IPAddress _localIp;
            private readonly string _protocol;
            private readonly int _targetPort;
            private readonly bool _isV6;
            private readonly IntPtr _handle;
            private readonly Socket _portReservation;
            private readonly CancellationTokenSource _receiveCts = new CancellationTokenSource();
            private readonly ConcurrentDictionary<int, WinDivertPendingProbe> _pending =
                new ConcurrentDictionary<int, WinDivertPendingProbe>();
            private readonly Task _receiveTask;
            private readonly int _sessionId;
            private int _pendingKey;
            private int _stopRequested;
            private int _cleanupCompleted;

            public int SourcePort { get; private set; }
            public Trace Owner => _owner;

            private WinDivertTraceSession(Trace owner, IPAddress targetIp,
                IPAddress localIp, string protocol, int targetPort, int sourcePort,
                Socket portReservation, IntPtr handle)
            {
                _owner = owner;
                _targetIp = targetIp;
                _localIp = localIp;
                _protocol = protocol;
                _targetPort = targetPort;
                _isV6 = targetIp.AddressFamily == AddressFamily.InterNetworkV6;
                SourcePort = sourcePort;
                _portReservation = portReservation;
                _handle = handle;
                _sessionId = Interlocked.Increment(ref _nextWinDivertSessionId);
                ActiveWinDivertSessions[_sessionId] = this;
                _receiveTask = Task.Run(ReceiveLoop);
            }

            public static WinDivertTraceSession Open(Trace owner, IPAddress targetIp,
                IPAddress localIp, string protocol, int targetPort)
            {
                string dllPath = Path.Combine(Application.StartupPath, "WinDivert.dll");
                string driverPath = Path.Combine(Application.StartupPath, "WinDivert64.sys");
                if (!File.Exists(dllPath) || !File.Exists(driverPath))
                {
                    throw new WinDivertUnavailableException(
                        "程序目录缺少必要驱动 (WinDivert.dll 或 WinDivert64.sys)，无法进行测试。");
                }

                bool isV6 = targetIp.AddressFamily == AddressFamily.InterNetworkV6;
                string transport = protocol == "TCP" ? "tcp" : "udp";
                string network = isV6 ? "ipv6" : "ip";
                string icmp = isV6 ? "icmpv6" : "icmp";
                string filter = $"inbound and {network} and ({icmp} or " +
                    $"({transport} and {transport}.SrcPort == {targetPort}))";

                Socket reservation = null;
                IntPtr handle = InvalidWinDivertHandle;
                try
                {
                    reservation = ReserveStableSourcePort(localIp, targetIp, protocol,
                        targetPort, out int sourcePort);
                    // SNIFF 只复制匹配报文，不会截获或要求本程序重新注入系统流量。
                    handle = WinDivertOpen(filter, WinDivertLayerNetwork, 0,
                        WinDivertFlagSniff);
                    if (handle == IntPtr.Zero || handle == InvalidWinDivertHandle)
                    {
                        int error = Marshal.GetLastWin32Error();
                        string reason;
                        switch (error)
                        {
                            case 2: reason = "未找到 WinDivert64.sys"; break;
                            case 5: reason = "需要管理员权限"; break;
                            case 577: reason = "驱动签名无效或被系统安全策略阻止"; break;
                            case 654: reason = "系统中残留了不兼容版本的 WinDivert 驱动"; break;
                            case 1060: reason = "WinDivert 驱动服务不存在"; break;
                            case 1275: reason = "WinDivert 驱动被系统或安全软件阻止"; break;
                            case 1753: reason = "Base Filtering Engine 服务未运行"; break;
                            default: reason = "Win32 错误 " + error; break;
                        }
                        throw new WinDivertUnavailableException(
                            "无法启动 WinDivert：" + reason + "。");
                    }

                    return new WinDivertTraceSession(owner, targetIp, localIp,
                        protocol, targetPort, sourcePort, reservation, handle);
                }
                catch (DllNotFoundException ex)
                {
                    reservation?.Dispose();
                    throw new WinDivertUnavailableException(
                        "无法加载 WinDivert.dll；请确认它位于程序目录且为 x64 版本。", ex);
                }
                catch (BadImageFormatException ex)
                {
                    reservation?.Dispose();
                    throw new WinDivertUnavailableException(
                        "WinDivert.dll 位数不匹配；本程序需要 x64 版本。", ex);
                }
                catch (EntryPointNotFoundException ex)
                {
                    reservation?.Dispose();
                    throw new WinDivertUnavailableException(
                        "WinDivert.dll 版本不兼容；需要 WinDivert 2.x。", ex);
                }
                catch
                {
                    if (handle != IntPtr.Zero && handle != InvalidWinDivertHandle)
                        try { WinDivertClose(handle); } catch { }
                    reservation?.Dispose();
                    throw;
                }
            }

            private static Socket ReserveStableSourcePort(IPAddress localIp,
                IPAddress targetIp, string protocol, int targetPort, out int sourcePort)
            {
                uint hash = 2166136261;
                foreach (byte b in localIp.GetAddressBytes()) hash = (hash ^ b) * 16777619;
                foreach (byte b in targetIp.GetAddressBytes()) hash = (hash ^ b) * 16777619;
                hash = (hash ^ (byte)(protocol == "TCP" ? 6 : 17)) * 16777619;
                hash = (hash ^ (byte)(targetPort >> 8)) * 16777619;
                hash = (hash ^ (byte)targetPort) * 16777619;

                const int minPort = 40000;
                const int portCount = 20000;
                int preferred = minPort + (int)(hash % portCount);
                for (int offset = 0; offset < portCount; offset++)
                {
                    int candidate = minPort + ((preferred - minPort + offset) % portCount);
                    var socket = new Socket(localIp.AddressFamily,
                        protocol == "TCP" ? SocketType.Stream : SocketType.Dgram,
                        protocol == "TCP" ? ProtocolType.Tcp : ProtocolType.Udp);
                    try
                    {
                        socket.ExclusiveAddressUse = true;
                        socket.Bind(new IPEndPoint(localIp, candidate));
                        sourcePort = candidate;
                        return socket;
                    }
                    catch (SocketException)
                    {
                        socket.Dispose();
                    }
                }
                throw new InvalidOperationException("无法为 Trace 保留稳定的 TCP/UDP 源端口。");
            }

            private void ReceiveLoop()
            {
                byte[] packet = new byte[65575];
                byte[] address = new byte[WinDivertAddressSize];
                while (!_receiveCts.IsCancellationRequested)
                {
                    uint receivedLength;
                    bool received = WinDivertRecv(_handle, packet, (uint)packet.Length,
                        out receivedLength, address);
                    if (!received)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (_receiveCts.IsCancellationRequested || error == 6 || error == 232)
                            return;
                        throw new InvalidOperationException(
                            "WinDivert 接收循环异常终止，Win32 错误 " + error + "。");
                    }

                    int length = (int)receivedLength;
                    int version = length > 0 ? packet[0] >> 4 : 0;
                    if ((_isV6 && (version != 6 || length < 40)) ||
                        (!_isV6 && (version != 4 || length < 20))) continue;
                    int addressLength = _isV6 ? 16 : 4;
                    int sourceOffset = _isV6 ? 8 : 12;
                    byte[] sourceBytes = new byte[addressLength];
                    Buffer.BlockCopy(packet, sourceOffset, sourceBytes, 0, addressLength);
                    IPAddress responder = new IPAddress(sourceBytes);

                    foreach (KeyValuePair<int, WinDivertPendingProbe> item in _pending.ToArray())
                    {
                        WinDivertPendingProbe pending = item.Value;
                        bool directMatch = _isV6
                            ? IsMatchingDirectIPv6TransportResponse(packet, length,
                                responder, _protocol, pending.ProbeId,
                                pending.SourcePort, _targetPort, _targetIp)
                            : IsMatchingDirectIPv4TransportResponse(packet, length,
                                responder, _protocol, pending.ProbeId,
                                pending.SourcePort, _targetPort, _targetIp);
                        bool icmpMatch = !directMatch && (_isV6
                            ? _owner.IsMatchingIcmpV6Response(packet, length, _protocol,
                                pending.ProbeId, pending.SourcePort, _targetPort,
                                pending.ExpectedUdpChecksum, _targetIp)
                            : IsMatchingIcmpV4TransportResponse(packet, length, _protocol,
                                pending.ProbeId, pending.SourcePort, _targetPort,
                                pending.ExpectedUdpChecksum, _targetIp));
                        if (directMatch || icmpMatch)
                            pending.Completion.TrySetResult(responder);
                    }
                }
            }

            public async Task<ProbeAttemptResult> ProbeAsync(int ttl, int timeout,
                ushort probeId, CancellationToken token)
            {
                var result = new ProbeAttemptResult();
                Stopwatch stopwatch = new Stopwatch();
                int pendingKey = 0;
                try
                {
                    byte[] packet = BuildWinDivertTransportPacket(_localIp, _targetIp,
                        _protocol, SourcePort, _targetPort, ttl, probeId);
                    byte[] address = CreateWinDivertOutboundAddress(_isV6);
                    if (!WinDivertHelperCalcChecksums(packet, (uint)packet.Length,
                            address, 0))
                    {
                        throw new InvalidOperationException("WinDivert 无法计算传输层校验和，错误 " +
                            Marshal.GetLastWin32Error() + "。");
                    }

                    int transportOffset = _isV6 ? 40 : 20;
                    ushort udpChecksum = _protocol == "UDP"
                        ? ReadUInt16Network(packet, transportOffset + 6)
                        : (ushort)0;
                    var pending = new WinDivertPendingProbe(SourcePort, probeId, udpChecksum);
                    pendingKey = Interlocked.Increment(ref _pendingKey);
                    if (!_pending.TryAdd(pendingKey, pending))
                        throw new InvalidOperationException("无法登记 WinDivert 探针。");

                    stopwatch.Start();
                    if (!WinDivertSend(_handle, packet, (uint)packet.Length,
                            out uint sentLength, address) || sentLength != packet.Length)
                    {
                        throw new InvalidOperationException("WinDivert 注入探针失败，错误 " +
                            Marshal.GetLastWin32Error() + "。");
                    }

                    Task completed = await Task.WhenAny(pending.Completion.Task,
                        _receiveTask, Task.Delay(timeout, token));
                    stopwatch.Stop();
                    token.ThrowIfCancellationRequested();
                    if (completed == _receiveTask)
                    {
                        try { await _receiveTask; }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "WinDivert 接收线程已停止：" + ex.GetBaseException().Message, ex);
                        }
                        throw new InvalidOperationException("WinDivert 接收线程意外停止。");
                    }
                    if (completed == pending.Completion.Task)
                    {
                        result.Address = await pending.Completion.Task;
                        result.RoundTripTime = stopwatch.Elapsed.TotalMilliseconds;
                        result.TargetReached = result.Address.Equals(_targetIp);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    string family = _isV6 ? "IPv6" : "IPv4";
                    Debug.WriteLine($"[WinDivert-{family}] TTL={ttl} {_protocol}: {ex.Message}");
                    throw new InvalidOperationException(
                        $"WinDivert {family} {_protocol} 探针处理失败：{ex.Message}", ex);
                }
                finally
                {
                    if (pendingKey != 0) _pending.TryRemove(pendingKey, out _);
                }
                return result;
            }

            public void RequestStop()
            {
                if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
                try { _receiveCts.Cancel(); } catch { }
                try { WinDivertShutdown(_handle, WinDivertShutdownBoth); } catch { }
                try { WinDivertClose(_handle); } catch { }
                try { _portReservation?.Dispose(); } catch { }
                ActiveWinDivertSessions.TryRemove(_sessionId, out _);
            }

            public async Task StopAsync()
            {
                RequestStop();
                try { await _receiveTask; } catch { }
                if (Interlocked.Exchange(ref _cleanupCompleted, 1) == 0)
                {
                    try { _receiveCts.Dispose(); } catch { }
                }
            }
        }

        private static byte[] CreateWinDivertOutboundAddress(bool isV6)
        {
            byte[] address = new byte[WinDivertAddressSize];
            // WINDIVERT_ADDRESS 位域：Outbound=bit17，IPv6=bit20。
            uint flags = 1u << 17;
            if (isV6) flags |= 1u << 20;
            Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, address, 8, 4);
            return address;
        }

        private static byte[] BuildWinDivertTransportPacket(IPAddress source, IPAddress destination,
            string protocol, int sourcePort, int destinationPort, int hopLimit, ushort probeId)
        {
            bool tcp = protocol == "TCP";
            bool isV6 = source.AddressFamily == AddressFamily.InterNetworkV6;
            if (destination.AddressFamily != source.AddressFamily)
                throw new ArgumentException("源地址和目标地址的地址族不一致。");
            byte[] payload = tcp ? new byte[0] : GetUdpPayload(destinationPort, probeId);
            int transportLength = tcp ? 20 : 8 + payload.Length;
            int ipHeaderLength = isV6 ? 40 : 20;
            byte[] packet = new byte[ipHeaderLength + transportLength];

            if (isV6)
            {
                packet[0] = 0x60;
                packet[4] = (byte)(transportLength >> 8);
                packet[5] = (byte)transportLength;
                packet[6] = tcp ? (byte)6 : (byte)17;
                packet[7] = (byte)hopLimit;
                Buffer.BlockCopy(source.GetAddressBytes(), 0, packet, 8, 16);
                Buffer.BlockCopy(destination.GetAddressBytes(), 0, packet, 24, 16);
            }
            else
            {
                int totalLength = packet.Length;
                packet[0] = 0x45;
                packet[2] = (byte)(totalLength >> 8);
                packet[3] = (byte)totalLength;
                // Identification 和 TOS 固定，避免它们成为设备的额外散列输入。
                packet[6] = 0x40; // Don't Fragment
                packet[8] = (byte)hopLimit;
                packet[9] = tcp ? (byte)6 : (byte)17;
                Buffer.BlockCopy(source.GetAddressBytes(), 0, packet, 12, 4);
                Buffer.BlockCopy(destination.GetAddressBytes(), 0, packet, 16, 4);
            }

            int offset = ipHeaderLength;
            packet[offset] = (byte)(sourcePort >> 8);
            packet[offset + 1] = (byte)sourcePort;
            packet[offset + 2] = (byte)(destinationPort >> 8);
            packet[offset + 3] = (byte)destinationPort;
            if (tcp)
            {
                uint sequence = ((uint)probeId << 16) | (uint)sourcePort;
                packet[offset + 4] = (byte)(sequence >> 24);
                packet[offset + 5] = (byte)(sequence >> 16);
                packet[offset + 6] = (byte)(sequence >> 8);
                packet[offset + 7] = (byte)sequence;
                packet[offset + 12] = 0x50; // TCP header length = 5 (20 bytes)
                packet[offset + 13] = 0x02; // SYN
                packet[offset + 14] = 0xFA; // Window = 64240
                packet[offset + 15] = 0xF0;
            }
            else
            {
                packet[offset + 4] = (byte)(transportLength >> 8);
                packet[offset + 5] = (byte)transportLength;
                Buffer.BlockCopy(payload, 0, packet, offset + 8, payload.Length);
            }
            return packet;
        }

        private static void SetIPv6HopLimit(Socket socket, int hopLimit)
        {
            // Windows 的 IPV6_UNICAST_HOPS 对应数值 4（IpTimeToLive）。
            // SocketOptionName.HopLimit 对应 IPV6_HOPLIMIT(21)，是接收辅助信息选项，
            // 用它设置发送跳数会被 Windows 静默接受但不会限制出站报文。
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, hopLimit);
        }

        private byte[] CreateIcmpV6Packet(ushort sequence, IPAddress source, IPAddress destination)
        {
            byte[] packet = new byte[32];
            packet[0] = 128; // ICMPv6 Echo Request
            packet[1] = 0;
            packet[4] = (byte)(_instanceIdentifier >> 8);
            packet[5] = (byte)(_instanceIdentifier & 0xFF);
            packet[6] = (byte)(sequence >> 8);
            packet[7] = (byte)(sequence & 0xFF);
            byte[] payload = Encoding.ASCII.GetBytes("YumeyoTraceX-Paris");
            Buffer.BlockCopy(payload, 0, packet, 8, Math.Min(payload.Length, 22));

            byte[] pseudo = new byte[40 + packet.Length];
            Buffer.BlockCopy(source.GetAddressBytes(), 0, pseudo, 0, 16);
            Buffer.BlockCopy(destination.GetAddressBytes(), 0, pseudo, 16, 16);
            int upperLength = packet.Length;
            pseudo[32] = (byte)(upperLength >> 24);
            pseudo[33] = (byte)(upperLength >> 16);
            pseudo[34] = (byte)(upperLength >> 8);
            pseudo[35] = (byte)upperLength;
            pseudo[39] = 58; // Next Header = ICMPv6
            Buffer.BlockCopy(packet, 0, pseudo, 40, packet.Length);
            const ushort desiredChecksum = 0xBEEF;
            ApplyParisChecksumCompensation(pseudo, pseudo.Length - 2, desiredChecksum);
            packet[packet.Length - 2] = pseudo[pseudo.Length - 2];
            packet[packet.Length - 1] = pseudo[pseudo.Length - 1];
            ushort checksum = ComputeChecksumRange(pseudo, 0, pseudo.Length);
            packet[2] = (byte)(checksum >> 8);
            packet[3] = (byte)(checksum & 0xFF);
            return packet;
        }

        private static int GetIcmpV6Offset(byte[] buffer, int length)
        {
            // Windows raw ICMPv6 sockets may return either the ICMPv6 payload alone
            // or an outer IPv6 header followed by ICMPv6.
            if (length >= 48 && (buffer[0] >> 4) == 6 && buffer[6] == 58)
                return 40;
            return 0;
        }

        private static bool TryGetQuotedIPv6Payload(byte[] buffer, int length, int icmpOffset,
            IPAddress targetIp, out int payloadOffset, out byte nextHeader)
        {
            payloadOffset = 0;
            nextHeader = 0;
            int innerOffset = icmpOffset + 8;
            if (length < innerOffset + 40 || (buffer[innerOffset] >> 4) != 6) return false;
            if (!AddressMatches(buffer, innerOffset + 24, targetIp)) return false;

            nextHeader = buffer[innerOffset + 6];
            // NICX 发送的 IPv6 探针不包含扩展头，因此上层头紧跟固定 40 字节头。
            payloadOffset = innerOffset + 40;
            return true;
        }

        private bool IsMatchingIcmpV6Response(byte[] buffer, int length, string protocol,
            ushort sequence, int sourcePort, int targetPort, ushort expectedUdpChecksum,
            IPAddress targetIp)
        {
            int icmpOffset = GetIcmpV6Offset(buffer, length);
            if (length < icmpOffset + 8) return false;
            byte type = buffer[icmpOffset];

            if (protocol == "ICMP" && type == 129)
            {
                return ReadUInt16Network(buffer, icmpOffset + 4) == _instanceIdentifier &&
                       ReadUInt16Network(buffer, icmpOffset + 6) == sequence;
            }

            // Destination Unreachable / Packet Too Big / Time Exceeded / Parameter Problem.
            if (type < 1 || type > 4) return false;
            if (!TryGetQuotedIPv6Payload(buffer, length, icmpOffset, targetIp,
                    out int payloadOffset, out byte nextHeader)) return false;

            if (protocol == "ICMP")
            {
                if (nextHeader != 58 || length < payloadOffset + 8) return false;
                return ReadUInt16Network(buffer, payloadOffset + 4) == _instanceIdentifier &&
                       ReadUInt16Network(buffer, payloadOffset + 6) == sequence;
            }

            byte expectedHeader = protocol == "TCP" ? (byte)6 : (byte)17;
            if (nextHeader != expectedHeader || length < payloadOffset + 4) return false;
            if (ReadUInt16Network(buffer, payloadOffset) != sourcePort ||
                ReadUInt16Network(buffer, payloadOffset + 2) != targetPort) return false;
            if (protocol == "TCP")
            {
                if (length < payloadOffset + 8) return false;
                uint expectedSequence = ((uint)sequence << 16) | (uint)sourcePort;
                return ReadUInt32Network(buffer, payloadOffset + 4) == expectedSequence;
            }
            if (length < payloadOffset + 8) return false;
            return ReadUInt16Network(buffer, payloadOffset + 6) == expectedUdpChecksum;
        }

        private static bool IsMatchingDirectIPv6TransportResponse(byte[] buffer, int length,
            IPAddress remoteAddress, string protocol, ushort probeId, int sourcePort,
            int targetPort, IPAddress targetIp)
        {
            if (protocol == "ICMP" || !remoteAddress.Equals(targetIp)) return false;
            byte expectedHeader = protocol == "TCP" ? (byte)6 : (byte)17;
            int transportOffset = 0;
            // WinDivert 网络层通常包含外层 IPv6 头；也兼容仅有传输层数据的格式。
            if (length >= 44 && (buffer[0] >> 4) == 6)
            {
                if (buffer[6] != expectedHeader || !AddressMatches(buffer, 8, targetIp)) return false;
                transportOffset = 40;
            }
            if (length < transportOffset + 4) return false;
            if (ReadUInt16Network(buffer, transportOffset) != targetPort ||
                ReadUInt16Network(buffer, transportOffset + 2) != sourcePort) return false;

            if (protocol == "TCP")
            {
                if (length < transportOffset + 14) return false;
                byte flags = buffer[transportOffset + 13];
                bool accepted = (flags & 0x04) != 0 || // RST：目标端口关闭
                                ((flags & 0x12) == 0x12); // SYN+ACK：目标端口开放
                if (!accepted) return false;
                if ((flags & 0x10) == 0 || length < transportOffset + 12) return false;
                uint expectedAck = unchecked((((uint)probeId << 16) |
                    (uint)sourcePort) + 1u);
                return ReadUInt32Network(buffer, transportOffset + 8) == expectedAck;
            }

            if (targetPort == 53)
            {
                int dnsOffset = transportOffset + 8;
                return length >= dnsOffset + 4 &&
                       ReadUInt16Network(buffer, dnsOffset) == probeId &&
                       (buffer[dnsOffset + 2] & 0x80) != 0;
            }
            return IsMatchingUdpProbePayload(buffer, length,
                transportOffset + 8, probeId);
        }

        private Task StartSingleIcmpV6Receiver(Socket receiver, string protocol, ushort sequence,
            int sourcePort, int targetPort, IPAddress targetIp,
            TaskCompletionSource<IPAddress> completion, CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] buffer = new byte[8192];
                EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);
                receiver.ReceiveTimeout = 100;
                while (!token.IsCancellationRequested && !completion.Task.IsCompleted)
                {
                    try
                    {
                        int length = receiver.ReceiveFrom(buffer, ref remote);
                        IPAddress remoteAddress = ((IPEndPoint)remote).Address;
                        if (IsMatchingDirectIPv6TransportResponse(buffer, length, remoteAddress,
                                protocol, sequence, sourcePort, targetPort, targetIp))
                        {
                            completion.TrySetResult(targetIp);
                            return;
                        }
                        if (IsMatchingIcmpV6Response(buffer, length, protocol, sequence,
                                sourcePort, targetPort, 0, targetIp))
                        {
                            completion.TrySetResult(((IPEndPoint)remote).Address);
                            return;
                        }
                    }
                    catch (SocketException ex)
                    {
                        if (token.IsCancellationRequested) return;
                        if (ex.SocketErrorCode == SocketError.TimedOut ||
                            ex.SocketErrorCode == SocketError.WouldBlock ||
                            ex.SocketErrorCode == SocketError.Interrupted) continue;
                        return;
                    }
                    catch (ObjectDisposedException) { return; }
                    catch { return; }
                }
            });
        }

        private static Task<IPAddress> StartTcpTargetDetection(Socket socket, IPEndPoint target)
        {
            var completion = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
            socket.ConnectAsync(target).ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    completion.TrySetResult(target.Address);
                    return;
                }

                if (task.IsFaulted)
                {
                    // 观察异常，避免未观察任务异常。不能仅凭 ConnectionRefused 判定到达：
                    // Windows 也可能把中间设备/低 Hop Limit 导致的失败映射为该错误。
                    Debug.WriteLine($"[TCP-Trace] Connect failed: {task.Exception?.GetBaseException().Message}");
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return completion.Task;
        }

        private async Task<ProbeAttemptResult> ProbeIcmpV6Once(IPAddress targetIp,
            IPAddress localIp, int ttl, int timeout, ushort sequence,
            CancellationToken token)
        {
            var attempt = new ProbeAttemptResult();
            Socket icmpReceiver = null;
            CancellationTokenSource receiveCts = null;
            Task icmpReceiveTask = Task.CompletedTask;
            try
            {
                icmpReceiver = new Socket(AddressFamily.InterNetworkV6,
                    SocketType.Raw, ProtocolType.IcmpV6);
                icmpReceiver.Bind(new IPEndPoint(localIp, 0));
                icmpReceiver.ReceiveBufferSize = 65536;
                receiveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var icmpCompletion = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
                icmpReceiveTask = StartSingleIcmpV6Receiver(icmpReceiver, "ICMP", sequence,
                    0, 0, targetIp, icmpCompletion, receiveCts.Token);

                Stopwatch stopwatch = Stopwatch.StartNew();
                SetIPv6HopLimit(icmpReceiver, ttl);
                byte[] packet = CreateIcmpV6Packet(sequence, localIp, targetIp);
                icmpReceiver.SendTo(packet, new IPEndPoint(targetIp, 0));

                Task delay = Task.Delay(timeout, token);
                Task completed = await Task.WhenAny(icmpCompletion.Task, delay);
                stopwatch.Stop();
                token.ThrowIfCancellationRequested();

                if (completed == icmpCompletion.Task)
                {
                    attempt.Address = await icmpCompletion.Task;
                    attempt.RoundTripTime = stopwatch.Elapsed.TotalMilliseconds;
                    attempt.TargetReached = attempt.Address.Equals(targetIp);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                attempt.Error = true;
                Debug.WriteLine($"[IPv6-Trace] TTL={ttl} ICMP: {ex.Message}");
            }
            finally
            {
                receiveCts?.Cancel();
                try { icmpReceiver?.Dispose(); } catch { }
                try { await icmpReceiveTask; } catch { }
                receiveCts?.Dispose();
            }
            return attempt;
        }

        private async Task<HopResult> ProbeIPv6Hop(IPAddress targetIp, IPAddress localIp,
            int ttl, int timeout, int probeCount, bool geoChecked,
            CancellationToken probeToken, CancellationToken geoToken)
        {
            var hop = new HopResult(ttl);
            for (int probe = 0; probe < probeCount; probe++)
            {
                probeToken.ThrowIfCancellationRequested();
                if (probe > 0) await Task.Delay(40, probeToken);

                ushort sequence = NextUdpProbeId();
                ProbeAttemptResult attempt = await ProbeIcmpV6Once(targetIp, localIp,
                    ttl, timeout, sequence, probeToken);
                if (attempt.Address != null)
                {
                    if (hop.ReplyAddress == null && geoChecked)
                        hop.GeoInfo = ResolveGeoInfo(attempt.Address.ToString(), geoToken);
                    hop.ReplyAddress = attempt.Address;
                    hop.RTTs[probe] = attempt.RoundTripTime;
                    hop.TargetReached |= attempt.TargetReached;
                }
                else if (attempt.Error)
                {
                    hop.RTTs[probe] = -2;
                }
            }
            return hop;
        }

        private async Task<HopResult> ProbeWinDivertHop(WinDivertTraceSession session,
            int ttl, int timeout, int probeCount, bool geoChecked,
            CancellationToken probeToken, CancellationToken geoToken)
        {
            var hop = new HopResult(ttl);
            for (int probe = 0; probe < probeCount; probe++)
            {
                probeToken.ThrowIfCancellationRequested();
                if (probe > 0) await Task.Delay(40, probeToken);

                ushort probeId = NextUdpProbeId();
                ProbeAttemptResult attempt = await session.ProbeAsync(ttl, timeout,
                    probeId, probeToken);
                if (attempt.Address != null)
                {
                    if (hop.ReplyAddress == null && geoChecked)
                        hop.GeoInfo = ResolveGeoInfo(attempt.Address.ToString(), geoToken);
                    hop.ReplyAddress = attempt.Address;
                    hop.RTTs[probe] = attempt.RoundTripTime;
                    hop.TargetReached |= attempt.TargetReached;
                }
                else if (attempt.Error)
                {
                    hop.RTTs[probe] = -2;
                }
            }
            return hop;
        }

        private async Task RunIcmpV6Trace(IPAddress targetIp, IPAddress localIp, int maxHops,
            int timeout, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool reachedTarget = false;
            var hopTasks = new Dictionary<int, Task<HopResult>>();
            using (var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                // 各 TTL 以 100ms 间隔进入流水线，显示仍按 TTL 排序。
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    int capturedTtl = ttl;
                    int delay = (ttl - 1) * 100;
                    hopTasks[ttl] = Task.Run(async () =>
                    {
                        if (delay > 0) await Task.Delay(delay, pipelineCts.Token);
                        return await ProbeIPv6Hop(targetIp, localIp, capturedTtl, timeout,
                            4, geoChecked, pipelineCts.Token, token);
                    }, pipelineCts.Token);
                }

                try
                {
                    for (int ttl = 1; ttl <= maxHops; ttl++)
                    {
                        token.ThrowIfCancellationRequested();
                        HopResult hop = await hopTasks[ttl];
                        DisplaySingleHop(hop, geoChecked, isV6: true);
                        if (hop.TargetReached)
                        {
                            reachedTarget = true;
                            pipelineCts.Cancel();
                            break;
                        }
                    }
                }
                finally
                {
                    pipelineCts.Cancel();
                    try { await Task.WhenAll(hopTasks.Values); } catch { }
                }
            }
            if (geoChecked) await WaitForEnrichmentsAsync(token);
            if (reachedTarget) AppendColorText("\nTrace 完成.\n", Color.Lime, false);
        }

        private async Task RunWinDivertSocketTrace(IPAddress targetIp, IPAddress localIp, int maxHops,
            int timeout, string protocol, int targetPort, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool reachedTarget = false;
            var hopTasks = new Dictionary<int, Task<HopResult>>();
            WinDivertTraceSession session = WinDivertTraceSession.Open(this,
                targetIp, localIp, protocol, targetPort);
            try
            {
                using (var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    for (int ttl = 1; ttl <= maxHops; ttl++)
                    {
                        int capturedTtl = ttl;
                        int delay = (ttl - 1) * 100;
                        hopTasks[ttl] = Task.Run(async () =>
                        {
                            if (delay > 0) await Task.Delay(delay, pipelineCts.Token);
                            return await ProbeWinDivertHop(session, capturedTtl,
                                timeout, 3, geoChecked, pipelineCts.Token, token);
                        }, pipelineCts.Token);
                    }

                    try
                    {
                        for (int ttl = 1; ttl <= maxHops; ttl++)
                        {
                            token.ThrowIfCancellationRequested();
                            HopResult hop = await hopTasks[ttl];
                            DisplaySingleHop(hop, geoChecked,
                                isV6: targetIp.AddressFamily == AddressFamily.InterNetworkV6,
                                probeCount: 3);
                            if (hop.TargetReached)
                            {
                                reachedTarget = true;
                                pipelineCts.Cancel();
                                break;
                            }
                        }
                    }
                    finally
                    {
                        pipelineCts.Cancel();
                        try { await Task.WhenAll(hopTasks.Values); } catch { }
                    }
                }
            }
            finally
            {
                await session.StopAsync();
            }

            if (geoChecked) await WaitForEnrichmentsAsync(token);
            if (reachedTarget)
                AppendColorText("\nTrace 完成.\n", Color.Lime, false);
        }

        // ==========================================
        // 终极整合版 RunIcmpTrace (多窗口防串扰版)
        // ==========================================
        private async Task RunIcmpTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, CancellationToken token)
        {
            if (targetIp.AddressFamily == AddressFamily.InterNetworkV6)
            {
                await RunIcmpV6Trace(targetIp, localIp, maxHops, timeout, token);
                return;
            }

            bool geoChecked = checkGEO.Checked;
            var results = new ConcurrentDictionary<int, HopResult>();
            var seqTcsStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();

            using (Socket receiver = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            {
                receiver.Bind(new IPEndPoint(localIp, 0));
                receiver.ReceiveBufferSize = 65536;

                var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var receiveTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[1024];
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    receiver.ReceiveTimeout = 500;
                    while (!receiveCts.Token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                            if (TryParseIcmpV4EchoReference(rcvBuffer, len, targetIp,
                                    out ushort rcvId, out ushort rcvSeq, out bool echoReply))
                            {
                                if (echoReply && !((IPEndPoint)remoteEP).Address.Equals(targetIp)) continue;
                                if (rcvId == _instanceIdentifier && seqTcsStore.TryRemove(rcvSeq, out var tcs))
                                    tcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                            }
                        }
                        catch (SocketException) { continue; }
                        catch { break; }
                    }
                }, receiveCts.Token);

                async Task ProbeHop(int ttl, CancellationToken hopToken)
                {
                    var result = new HopResult(ttl);
                    try
                    {
                        using (Socket sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                        {
                            sendSocket.Bind(new IPEndPoint(localIp, 0));
                            sendSocket.Ttl = (short)ttl;
                            for (int i = 0; i < 4; i++)
                            {
                                if (hopToken.IsCancellationRequested) break;
                                if (i > 0) await Task.Delay(40, hopToken);
                                ushort seq = NextUdpProbeId();
                                byte[] req = CreateIcmpPacket(seq);
                                var tcs = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
                                seqTcsStore[seq] = tcs;
                                Stopwatch sw = Stopwatch.StartNew();
                                try
                                {
                                    sendSocket.SendTo(req, new IPEndPoint(targetIp, 0));
                                    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, hopToken));
                                    sw.Stop();
                                    seqTcsStore.TryRemove(seq, out _);
                                    if (done == tcs.Task)
                                    {
                                        IPAddress addr = await tcs.Task;
                                        if (result.ReplyAddress == null && geoChecked)
                                            result.GeoInfo = ResolveGeoInfo(addr.ToString(), hopToken);
                                        result.ReplyAddress = addr;
                                        result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                        if (addr.Equals(targetIp))
                                            result.TargetReached = true;
                                    }
                                }
                                catch (Exception ex) when (!(ex is OperationCanceledException))
                                {
                                    sw.Stop();
                                    result.RTTs[i] = -2;
                                    seqTcsStore.TryRemove(seq, out _);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    results[ttl] = result;
                }

                // 并发启动所有跳
                var hopTasks = new Dictionary<int, Task>();
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    int ct = ttl;
                    hopTasks[ttl] = Task.Run(() => ProbeHop(ct, token), token);
                }

                // 顺序收集显示：等待每跳的Task完成，不会因为启发式超时而跳过未完成的跳
                int hopTimeout = timeout * 4 + 200;
                bool reachedTarget = false;
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    if (token.IsCancellationRequested || this.IsDisposed) break;

                    Task hopTask = hopTasks[ttl];
                    try { await Task.WhenAny(hopTask, Task.Delay(hopTimeout, token)); } catch { }

                    HopResult hop = results.TryGetValue(ttl, out var h) ? h : new HopResult(ttl);
                    DisplaySingleHop(hop, geoChecked);
                    if (hop.TargetReached) { reachedTarget = true; break; }
                }

                receiveCts.Cancel();
                try { await receiveTask; } catch { }
                receiveCts.Dispose();

                if (geoChecked) await WaitForEnrichmentsAsync(token);
                if (reachedTarget)
                    AppendColorText("\nTrace 完成.\n", Color.Lime, false);
            }
        }

        // ==========================================
        // TCP/UDP Trace实现
        // ==========================================
        private async Task RunSocketTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int customPort, CancellationToken token)
        {
            try
            {
                await RunWinDivertSocketTrace(targetIp, localIp, maxHops, timeout,
                    protocol, customPort, token);
            }
            catch (WinDivertUnavailableException ex)
                when (targetIp.AddressFamily == AddressFamily.InterNetwork)
            {
                AppendColorText("    *WinDivert驱动不可用, 使用查询器X原生方法实现\n",
                    Color.Orange, false);
                Debug.WriteLine("[WinDivert-Fallback] " + ex.Message);
                await RunNativeSocketTraceV4(targetIp, localIp, maxHops, timeout,
                    protocol, customPort, token);
            }
        }

        private async Task RunNativeSocketTraceV4(IPAddress targetIp, IPAddress localIp,
            int maxHops, int timeout, string protocol, int customPort,
            CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            bool isTcp = (protocol == "TCP");
            var results = new ConcurrentDictionary<int, HopResult>();
            var portTcsStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();
            var udpTransactionStore = new ConcurrentDictionary<int, ushort>();

            using (Socket receiver = isTcp
                ? CreateIPv4CaptureSocket(localIp)
                : new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            using (Socket rawUdpSocket = isTcp ? null : CreateRawUdpSocket(localIp))
            {
                if (!isTcp) receiver.Bind(new IPEndPoint(localIp, 0));
                receiver.ReceiveBufferSize = 65536;

                // 取一个本机可用端口作为探针源端口基值。
                int fixedSourcePort;
                using (var tmpSocket = new Socket(AddressFamily.InterNetwork,
                    isTcp ? SocketType.Stream : SocketType.Dgram,
                    isTcp ? ProtocolType.Tcp : ProtocolType.Udp))
                {
                    tmpSocket.Bind(new IPEndPoint(localIp, 0));
                    fixedSourcePort = ((IPEndPoint)tmpSocket.LocalEndPoint).Port;
                }

                var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                int expectedProtocol = isTcp ? 6 : 17;
                var receiveTask = Task.Run(() =>
                {
                    byte[] rcvBuffer = new byte[8192];
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    receiver.ReceiveTimeout = 500;
                    while (!receiveCts.Token.IsCancellationRequested && !this.IsDisposed)
                    {
                        try
                        {
                            int len = receiver.ReceiveFrom(rcvBuffer, ref remoteEP);
                            if (isTcp && TryParseDirectTcpV4Response(rcvBuffer, len, targetIp,
                                    customPort, out int directLocalPort) &&
                                portTcsStore.TryRemove(directLocalPort, out var directTcs))
                            {
                                directTcs.TrySetResult(targetIp);
                                continue;
                            }
                            if (TryParseIcmpV4TransportReference(rcvBuffer, len, targetIp,
                                    (byte)expectedProtocol, customPort, out int sourcePort) &&
                                portTcsStore.TryRemove(sourcePort, out var tcs))
                                tcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                        }
                        catch (SocketException) { continue; }
                        catch { break; }
                    }
                }, receiveCts.Token);
                Task udpReceiveTask = isTcp
                    ? Task.CompletedTask
                    : StartRawUdpResponseReceiver(rawUdpSocket, targetIp, customPort,
                        portTcsStore, udpTransactionStore, receiveCts.Token);

                async Task ProbeHop(int ttl, CancellationToken hopToken)
                {
                    var result = new HopResult(ttl);
                    try
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            if (hopToken.IsCancellationRequested) break;
                            if (i > 0) await Task.Delay(40, hopToken);
                            Socket tcpSenderSocket = null;
                            int myPort = GetUdpProbeSourcePort(fixedSourcePort, ttl);
                            try
                            {
                                if (isTcp)
                                {
                                    tcpSenderSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                                    tcpSenderSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                                    tcpSenderSocket.Bind(new IPEndPoint(localIp, myPort));
                                    tcpSenderSocket.Ttl = (short)ttl;
                                }

                                var icmpTcs = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
                                portTcsStore[myPort] = icmpTcs;
                                Stopwatch sw = Stopwatch.StartNew();

                                Task<IPAddress> tcpResultTask = null;
                                if (isTcp)
                                {
                                    tcpResultTask = StartTcpTargetDetection(
                                        tcpSenderSocket, new IPEndPoint(targetIp, customPort));
                                }
                                else
                                {
                                    ushort probeId = NextUdpProbeId();
                                    udpTransactionStore[myPort] = probeId;
                                    byte[] packet = BuildUdpTracePacket(localIp, targetIp,
                                        myPort, customPort, ttl, probeId);
                                    rawUdpSocket.SendTo(packet, new IPEndPoint(targetIp, customPort));
                                }

                                Task completed = tcpResultTask != null
                                    ? await Task.WhenAny(icmpTcs.Task, tcpResultTask, Task.Delay(timeout, hopToken))
                                    : await Task.WhenAny(icmpTcs.Task, Task.Delay(timeout, hopToken));

                                sw.Stop();
                                if (completed == icmpTcs.Task)
                                {
                                    IPAddress addr = await icmpTcs.Task;
                                    if (result.ReplyAddress == null && geoChecked)
                                        result.GeoInfo = ResolveGeoInfo(addr.ToString(), token);
                                    result.ReplyAddress = addr;
                                    result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                    if (addr.Equals(targetIp))
                                        result.TargetReached = true;
                                }
                                else if (tcpResultTask != null && completed == tcpResultTask)
                                {
                                    if (result.ReplyAddress == null && geoChecked)
                                        result.GeoInfo = ResolveGeoInfo(targetIp.ToString(), token);
                                    result.ReplyAddress = targetIp;
                                    result.RTTs[i] = sw.Elapsed.TotalMilliseconds;
                                    result.TargetReached = true;
                                }
                            }
                            catch (Exception ex) when (!(ex is OperationCanceledException))
                            {
                                result.RTTs[i] = -2;
                            }
                            finally
                            {
                                portTcsStore.TryRemove(myPort, out _);
                                udpTransactionStore.TryRemove(myPort, out _);
                                tcpSenderSocket?.Dispose();
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    results[ttl] = result;
                }

                // 各 TTL 以 100ms 间隔进入流水线；结果仍按 TTL 顺序显示。
                // 这样不会等待上一跳的 3 次超时后才开始下一跳。
                var hopTasks = new Dictionary<int, Task>();
                bool reachedTarget = false;
                using (var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    for (int ttl = 1; ttl <= maxHops; ttl++)
                    {
                        int capturedTtl = ttl;
                        int delay = (ttl - 1) * 100;
                        hopTasks[ttl] = Task.Run(async () =>
                        {
                            if (delay > 0) await Task.Delay(delay, pipelineCts.Token);
                            await ProbeHop(capturedTtl, pipelineCts.Token);
                        }, pipelineCts.Token);
                    }

                    try
                    {
                        for (int ttl = 1; ttl <= maxHops; ttl++)
                        {
                            if (token.IsCancellationRequested || this.IsDisposed) break;
                            await hopTasks[ttl];

                            HopResult hop = results.TryGetValue(ttl, out var h)
                                ? h
                                : new HopResult(ttl);
                            DisplaySingleHop(hop, geoChecked, probeCount: 3);
                            if (hop.TargetReached)
                            {
                                reachedTarget = true;
                                pipelineCts.Cancel();
                                break;
                            }
                        }
                    }
                    finally
                    {
                        pipelineCts.Cancel();
                        try { await Task.WhenAll(hopTasks.Values); } catch { }
                    }
                }

                receiveCts.Cancel();
                try { await receiveTask; } catch { }
                try { await udpReceiveTask; } catch { }
                receiveCts.Dispose();

                if (geoChecked) await WaitForEnrichmentsAsync(token);
                if (reachedTarget)
                    AppendColorText("\nTrace 完成.\n", Color.Lime, false);
            }
        }

        // ==========================================
        // MTR 模式：持续多轮探测 + 累计统计 + 实时刷新表格
        // ==========================================
        private async Task RunMtrTrace(IPAddress targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int targetPort, CancellationToken token)
        {
            if (protocol == "TCP" || protocol == "UDP")
            {
                try
                {
                    await RunMtrTraceManaged(targetIp, localIp, maxHops, timeout,
                        protocol, targetPort, token);
                    return;
                }
                catch (WinDivertUnavailableException ex)
                    when (targetIp.AddressFamily == AddressFamily.InterNetwork)
                {
                    _mtrRuntimeNotice = "    *WinDivert驱动不可用, 使用查询器X原生方法实现";
                    AppendColorText(_mtrRuntimeNotice + "\n", Color.Orange, false);
                    Debug.WriteLine("[WinDivert-MTR-Fallback] " + ex.Message);
                    // IPv4 继续执行下方原生 MTR。
                }
            }
            else if (targetIp.AddressFamily == AddressFamily.InterNetworkV6)
            {
                await RunMtrTraceManaged(targetIp, localIp, maxHops, timeout,
                    protocol, targetPort, token);
                return;
            }

            bool geoChecked = checkGEO.Checked;
            var stats = new ConcurrentDictionary<int, MtrHopStats>();
            for (int ttl = 1; ttl <= maxHops; ttl++)
                stats[ttl] = new MtrHopStats { TTL = ttl };

            string targetLabel = targetIp.ToString();
            int round = 0;
            int effectiveMaxHops = maxHops;
            int confirmedTargetHop = 0;
            bool targetEverReached = false;
            bool isFirstRound = true;

            using (Socket receiver = protocol == "TCP"
                ? CreateIPv4CaptureSocket(localIp)
                : new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            using (Socket rawUdpSocket = protocol == "UDP" ? CreateRawUdpSocket(localIp) : null)
            {
                if (protocol != "TCP") receiver.Bind(new IPEndPoint(localIp, 0));
                receiver.ReceiveBufferSize = 65536;

                // 取一个本机可用端口作为 TCP/UDP 探针源端口基值。
                int mtrFixedPort = 0;
                if (protocol != "ICMP")
                {
                    using (var tmpSocket = new Socket(AddressFamily.InterNetwork,
                        protocol == "TCP" ? SocketType.Stream : SocketType.Dgram,
                        protocol == "TCP" ? ProtocolType.Tcp : ProtocolType.Udp))
                    {
                        tmpSocket.Bind(new IPEndPoint(localIp, 0));
                        mtrFixedPort = ((IPEndPoint)tmpSocket.LocalEndPoint).Port;
                    }
                }

                while (!token.IsCancellationRequested && !this.IsDisposed)
                {
                    round++;
                    var roundResults = new ConcurrentDictionary<int, HopResult>();
                    // 接收循环：同时支持 ICMP (ID+seq) 和 TCP/UDP (源端口) 匹配
                    var roundSeqStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();
                    var roundPortStore = new ConcurrentDictionary<int, TaskCompletionSource<IPAddress>>();
                    var roundUdpTransactionStore = new ConcurrentDictionary<int, ushort>();
                    var roundCts = CancellationTokenSource.CreateLinkedTokenSource(token);

                    var receiveTask = Task.Run(() =>
                    {
                        byte[] buf = new byte[1024];
                        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                        receiver.ReceiveTimeout = 500;
                        while (!roundCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                int len = receiver.ReceiveFrom(buf, ref remoteEP);
                                if (protocol == "TCP" &&
                                    TryParseDirectTcpV4Response(buf, len, targetIp, targetPort,
                                        out int directLocalPort) &&
                                    roundPortStore.TryRemove(directLocalPort, out var directTcs))
                                {
                                    directTcs.TrySetResult(targetIp);
                                    continue;
                                }
                                if (protocol == "ICMP" &&
                                    TryParseIcmpV4EchoReference(buf, len, targetIp,
                                        out ushort rcvId, out ushort rcvSeq, out bool echoReply))
                                {
                                    if (echoReply && !((IPEndPoint)remoteEP).Address.Equals(targetIp)) continue;
                                    if (rcvId == _instanceIdentifier &&
                                        roundSeqStore.TryRemove(rcvSeq, out var stcs))
                                        stcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                                }
                                else if (protocol != "ICMP" &&
                                    TryParseIcmpV4TransportReference(buf, len, targetIp,
                                        protocol == "TCP" ? (byte)6 : (byte)17,
                                        targetPort, out int sourcePort) &&
                                    roundPortStore.TryRemove(sourcePort, out var ptcs))
                                    ptcs.TrySetResult(((IPEndPoint)remoteEP).Address);
                            }
                            catch (SocketException) { continue; }
                            catch { break; }
                        }
                    }, roundCts.Token);
                    Task udpReceiveTask = protocol == "UDP"
                        ? StartRawUdpResponseReceiver(rawUdpSocket, targetIp, targetPort,
                            roundPortStore, roundUdpTransactionStore, roundCts.Token)
                        : Task.CompletedTask;

                    // 每轮每跳 1 个探针，各 TTL 以 100ms 间隔进入流水线。
                    bool isIcmpProto = (protocol == "ICMP");
                    var lastDraw = Stopwatch.StartNew();
                    int roundTargetHop = 0;
                    var roundHopTasks = new Dictionary<int, Task>();

                    for (int ttl = 1; ttl <= effectiveMaxHops; ttl++)
                    {
                        int ct = ttl;
                        int delay = (ttl - 1) * 100;

                        roundHopTasks[ttl] = Task.Run(async () =>
                        {
                            if (delay > 0) await Task.Delay(delay, roundCts.Token);
                            var result = new HopResult(ct);
                            try
                            {
                                if (isIcmpProto)
                                {
                                    using (Socket sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
                                    {
                                        sendSocket.Bind(new IPEndPoint(localIp, 0));
                                        sendSocket.Ttl = (short)ct;
                                        if (roundCts.Token.IsCancellationRequested) { roundResults[ct] = result; return; }
                                        ushort seq = NextUdpProbeId();
                                        byte[] req = CreateIcmpPacket(seq);
                                        var tcs = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
                                        roundSeqStore[seq] = tcs;
                                        Stopwatch sw = Stopwatch.StartNew();
                                        try
                                        {
                                            sendSocket.SendTo(req, new IPEndPoint(targetIp, 0));
                                            var done = await Task.WhenAny(tcs.Task,
                                                Task.Delay(timeout, roundCts.Token));
                                            sw.Stop();
                                            roundSeqStore.TryRemove(seq, out _);
                                            if (done == tcs.Task)
                                            {
                                                IPAddress addr = await tcs.Task;
                                                if (geoChecked) result.GeoInfo = GetLocalGeoInfo(addr.ToString());
                                                result.ReplyAddress = addr;
                                                result.RTTs[0] = sw.Elapsed.TotalMilliseconds;
                                                if (addr.Equals(targetIp)) result.TargetReached = true;
                                            }
                                        }
                                        catch (Exception ex) when (!(ex is OperationCanceledException))
                                        {
                                            sw.Stop();
                                            result.RTTs[0] = -2;
                                            roundSeqStore.TryRemove(seq, out _);
                                        }
                                    }
                                }
                                else
                                {
                                    Socket tcpSendSocket = null;
                                    int myPort = GetUdpProbeSourcePort(mtrFixedPort, ct);
                                    try
                                    {
                                        if (protocol == "TCP")
                                        {
                                            tcpSendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                                            tcpSendSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                                            tcpSendSocket.Bind(new IPEndPoint(localIp, myPort));
                                            tcpSendSocket.Ttl = (short)ct;
                                        }

                                        if (roundCts.Token.IsCancellationRequested) { roundResults[ct] = result; return; }
                                        var tcs = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
                                        roundPortStore[myPort] = tcs;
                                        Stopwatch sw = Stopwatch.StartNew();

                                        Task<IPAddress> tcpResultTask = null;
                                        if (protocol == "TCP")
                                        {
                                            tcpResultTask = StartTcpTargetDetection(
                                                tcpSendSocket, new IPEndPoint(targetIp, targetPort));
                                        }
                                        else
                                        {
                                            ushort probeId = NextUdpProbeId();
                                            roundUdpTransactionStore[myPort] = probeId;
                                            byte[] packet = BuildUdpTracePacket(localIp, targetIp,
                                                myPort, targetPort, ct, probeId);
                                            rawUdpSocket.SendTo(packet, new IPEndPoint(targetIp, targetPort));
                                        }

                                        Task completed = tcpResultTask != null
                                            ? await Task.WhenAny(tcs.Task, tcpResultTask,
                                                Task.Delay(timeout, roundCts.Token))
                                            : await Task.WhenAny(tcs.Task,
                                                Task.Delay(timeout, roundCts.Token));
                                        sw.Stop();

                                        if (completed == tcs.Task)
                                        {
                                            IPAddress addr = await tcs.Task;
                                            if (geoChecked) result.GeoInfo = GetLocalGeoInfo(addr.ToString());
                                            result.ReplyAddress = addr;
                                            result.RTTs[0] = sw.Elapsed.TotalMilliseconds;
                                            if (addr.Equals(targetIp)) result.TargetReached = true;
                                        }
                                        else if (tcpResultTask != null && completed == tcpResultTask)
                                        {
                                            if (geoChecked) result.GeoInfo = GetLocalGeoInfo(targetIp.ToString());
                                            result.ReplyAddress = targetIp;
                                            result.RTTs[0] = sw.Elapsed.TotalMilliseconds;
                                            result.TargetReached = true;
                                        }
                                    }
                                    catch (Exception ex) when (!(ex is OperationCanceledException))
                                    {
                                        result.RTTs[0] = -2;
                                    }
                                    finally
                                    {
                                        roundPortStore.TryRemove(myPort, out _);
                                        roundUdpTransactionStore.TryRemove(myPort, out _);
                                        tcpSendSocket?.Dispose();
                                    }
                                }
                            }
                            catch (OperationCanceledException) { }
                            roundResults[ct] = result;
                        }, roundCts.Token);
                    }

                    for (int ttl = 1; ttl <= effectiveMaxHops; ttl++)
                    {
                        token.ThrowIfCancellationRequested();
                        int ct = ttl;
                        try { await roundHopTasks[ttl]; }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }

                        // 即时更新统计
                        if (roundResults.TryGetValue(ct, out var hop))
                        {
                            var stat = stats[ct];
                            stat.Sent += 1;
                            if (hop.HasAnyResponse)
                            {
                                var ip = hop.ReplyAddress;
                                stat.ReplyAddress = ip;
                                string ipStr = ip.ToString();
                                // 统计IP出现次数
                                if (!stat.IpAppearCount.ContainsKey(ipStr))
                                    stat.IpAppearCount[ipStr] = 0;
                                stat.IpAppearCount[ipStr]++;
                                if (!stat.AllIPs.Any(a => a.Equals(ip)))
                                {
                                    stat.AllIPs.Add(ip);
                                    if (!stat.FirstSeenRound.ContainsKey(ipStr))
                                        stat.FirstSeenRound[ipStr] = round;
                                    if (hop.GeoInfo != null)
                                        stat.IpGeoCache[ipStr] = hop.GeoInfo;
                                }
                                stat.GeoInfo = stat.IpGeoCache.ContainsKey(ipStr) ? stat.IpGeoCache[ipStr] : hop.GeoInfo;
                                for (int i = 0; i < 4; i++)
                                    if (hop.RTTs[i] >= 0) { stat.Received++; stat.RTTs.Add(hop.RTTs[i]); }
                                if (geoChecked && string.IsNullOrEmpty(IanaReservedIP.Check(ipStr)))
                                {
                                    int captureTtl = ct;
                                    string captureIp = ipStr;
                                    _ = EnrichGeoOnlineAsync(captureIp, captureTtl, stats, token);
                                }
                            }
                            if (hop.TargetReached && roundTargetHop == 0)
                                roundTargetHop = ct;
                        }

                        if (lastDraw.ElapsedMilliseconds >= 100)
                        {
                            int displayHops = isFirstRound ? ct : effectiveMaxHops;
                            DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout, protocol, round, geoChecked, targetEverReached, displayHops);
                            lastDraw.Restart();
                        }

                        if (roundTargetHop > 0)
                        {
                            roundCts.Cancel();
                            break;
                        }
                    }

                    if (roundTargetHop > 0 && confirmedTargetHop == 0)
                    {
                        confirmedTargetHop = roundTargetHop;
                        effectiveMaxHops = confirmedTargetHop;
                    }
                    if (roundTargetHop > 0) targetEverReached = true;
                    // 首轮必定显示最终表格；后续轮由实时刷新处理
                    if (isFirstRound || confirmedTargetHop > 0)
                        DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout, protocol, round, geoChecked, targetEverReached, effectiveMaxHops);
                    isFirstRound = false;

                    // 停止本轮接收循环
                    roundCts.Cancel();
                    try { await Task.WhenAll(roundHopTasks.Values); } catch { }
                    try { await receiveTask; } catch { }
                    try { await udpReceiveTask; } catch { }
                    roundCts.Dispose();

                    if (token.IsCancellationRequested) break;

                    // 轮间间隔
                    await Task.Delay(800, token);
                }
            }
        }

        private async Task RunMtrTraceManaged(IPAddress targetIp, IPAddress localIp, int maxHops,
            int timeout, string protocol, int targetPort, CancellationToken token)
        {
            bool geoChecked = checkGEO.Checked;
            string displayProtocol = protocol;
            var stats = new ConcurrentDictionary<int, MtrHopStats>();
            for (int ttl = 1; ttl <= maxHops; ttl++)
                stats[ttl] = new MtrHopStats { TTL = ttl };

            bool useWinDivert = protocol == "TCP" || protocol == "UDP";
            string targetLabel = targetIp.ToString();
            int round = 0;
            int effectiveMaxHops = maxHops;
            int confirmedTargetHop = 0;
            bool targetEverReached = false;
            bool firstRound = true;
            WinDivertTraceSession session = useWinDivert
                ? WinDivertTraceSession.Open(this, targetIp, localIp, protocol, targetPort)
                : null;

            try
            {
                while (!token.IsCancellationRequested && !IsDisposed)
                {
                    round++;
                    int roundTargetHop = 0;
                    var lastDraw = Stopwatch.StartNew();

                    // MTR 每轮将各 TTL 以 100ms 间隔送入流水线；每个探针独立等待
                    // 自己的响应/超时，不让前一跳的丢包把后续跳整体拖慢。
                    var roundProbeTasks = new Dictionary<int, Task<ProbeAttemptResult>>();
                    using (var roundCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        for (int ttl = 1; ttl <= effectiveMaxHops; ttl++)
                        {
                            int capturedTtl = ttl;
                            int delay = (ttl - 1) * 100;
                            roundProbeTasks[ttl] = Task.Run(async () =>
                            {
                                if (delay > 0) await Task.Delay(delay, roundCts.Token);
                                ushort sequence = NextUdpProbeId();
                                return useWinDivert
                                    ? await session.ProbeAsync(capturedTtl, timeout,
                                        sequence, roundCts.Token)
                                    : await ProbeIcmpV6Once(targetIp, localIp, capturedTtl,
                                        timeout, sequence, roundCts.Token);
                            }, roundCts.Token);
                        }

                        try
                        {
                            for (int ttl = 1; ttl <= effectiveMaxHops; ttl++)
                            {
                                token.ThrowIfCancellationRequested();
                                ProbeAttemptResult attempt = await roundProbeTasks[ttl];

                                MtrHopStats stat = stats[ttl];
                                stat.Sent++;
                                if (attempt.Address != null)
                                {
                                    string ipText = attempt.Address.ToString();
                                    stat.ReplyAddress = attempt.Address;
                                    stat.Received++;
                                    stat.RTTs.Add(attempt.RoundTripTime);
                                    if (!stat.IpAppearCount.ContainsKey(ipText)) stat.IpAppearCount[ipText] = 0;
                                    stat.IpAppearCount[ipText]++;
                                    if (!stat.AllIPs.Any(ip => ip.Equals(attempt.Address)))
                                    {
                                        stat.AllIPs.Add(attempt.Address);
                                        stat.FirstSeenRound[ipText] = round;
                                    }

                                    if (geoChecked)
                                    {
                                        if (!stat.IpGeoCache.TryGetValue(ipText, out string geo))
                                        {
                                            geo = GetLocalGeoInfo(ipText);
                                            stat.IpGeoCache[ipText] = geo;
                                        }
                                        stat.GeoInfo = geo;
                                        if (string.IsNullOrEmpty(IanaReservedIP.Check(ipText)))
                                        {
                                            int capturedTtl = ttl;
                                            _ = EnrichGeoOnlineAsync(ipText,
                                                capturedTtl, stats, token);
                                        }
                                    }

                                    if (attempt.TargetReached && roundTargetHop == 0)
                                        roundTargetHop = ttl;
                                }

                                if (lastDraw.ElapsedMilliseconds >= 100)
                                {
                                    int displayHops = firstRound ? ttl : effectiveMaxHops;
                                    DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout,
                                        displayProtocol, round, geoChecked, targetEverReached,
                                        displayHops);
                                    lastDraw.Restart();
                                }

                                if (roundTargetHop > 0)
                                {
                                    roundCts.Cancel();
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            roundCts.Cancel();
                            try { await Task.WhenAll(roundProbeTasks.Values); } catch { }
                        }
                    }

                    if (roundTargetHop > 0 && confirmedTargetHop == 0)
                    {
                        confirmedTargetHop = roundTargetHop;
                        effectiveMaxHops = roundTargetHop;
                    }
                    if (roundTargetHop > 0) targetEverReached = true;
                    if (firstRound || roundTargetHop > 0)
                        DrawMtrTable(stats, targetLabel, localIp, maxHops, timeout, displayProtocol,
                            round, geoChecked, targetEverReached, effectiveMaxHops);
                    firstRound = false;

                    await Task.Delay(800, token);
                }
            }
            finally
            {
                if (session != null) await session.StopAsync();
            }
        }

        private IPAddress GetLocalExportIP(IPAddress targetIp)
        {
            try
            {
                using (Socket socket = new Socket(targetIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(targetIp, 65530);
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            }
            catch (Exception ex)
            {
                string family = targetIp.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
                throw new InvalidOperationException($"无法找到通往目标的本机 {family} 出口地址：{ex.Message}", ex);
            }
        }

        private class HopResult
        {
            public int TTL;
            public IPAddress ReplyAddress;
            public bool TargetReached;
            public double[] RTTs = new double[4]; // >=0=ms, -1=timeout, -2=error
            public string GeoInfo; // 预缓存的归属地信息（线程池线程计算，避免 UI 线程磁盘 I/O）

            public bool HasAnyResponse => ReplyAddress != null;

            public HopResult(int ttl)
            {
                TTL = ttl;
                for (int i = 0; i < 4; i++) RTTs[i] = -1;
            }
        }

        private sealed class ProbeAttemptResult
        {
            public IPAddress Address;
            public bool TargetReached;
            public bool Error;
            public double RoundTripTime;
        }

        private class MtrHopStats
        {
            public int TTL;
            public IPAddress ReplyAddress;
            public string GeoInfo;
            public List<IPAddress> AllIPs = new List<IPAddress>();
            public Dictionary<string, string> IpGeoCache = new Dictionary<string, string>();
            public Dictionary<string, int> FirstSeenRound = new Dictionary<string, int>();
            public Dictionary<string, int> IpAppearCount = new Dictionary<string, int>();
            public int Sent;
            public int Received;
            public List<double> RTTs = new List<double>();
            public double LastRTT => RTTs.Count > 0 ? RTTs[RTTs.Count - 1] : double.NaN;

            public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
            public double AvgRTT => RTTs.Count == 0 ? double.NaN : RTTs.Average();
            public double BestRTT => RTTs.Count == 0 ? double.NaN : RTTs.Min();
            public double WorstRTT => RTTs.Count == 0 ? double.NaN : RTTs.Max();
        }

        /// <summary>
        /// 线程安全的归属地预计算（可在线程池线程调用，避免阻塞 UI）
        /// </summary>
        private string ComputeCachedGeoInfo(string ip)
        {
            return GetLocalGeoInfo(ip);
        }

        private void DisplaySingleHop(HopResult result, bool geoChecked, bool isV6 = false, int probeCount = 4)
        {
            if (this.IsDisposed || richTextBox1.IsDisposed) return;

            AppendColorText(result.TTL.ToString().PadLeft(3), Color.Yellow, false);
            AppendColorText("   ", Color.White, false);

            if (result.HasAnyResponse)
            {
                if (isV6)
                {
                    AppendColorText("  " + result.ReplyAddress.ToString() + "\n", Color.Yellow, false);
                    AppendColorText("               ", Color.White, false);
                }
                else
                {
                    AppendColorText("  " + result.ReplyAddress.ToString().PadRight(15), Color.Yellow, false);
                }
            }
            else if (isV6)
            {
                AppendColorText("\n  -            ", Color.Orange, false);
            }
            else
            {
                AppendColorText("  -              ", Color.Orange, false);
            }

            for (int i = 0; i < probeCount; i++)
            {
                double rtt = result.RTTs[i];
                if (rtt >= 0)
                    AppendColorText($"{rtt:F1} ms".PadLeft(10), Color.White, false);
                else if (rtt <= -1.5)
                    AppendColorText("       ERR", Color.Orange, false);
                else
                    AppendColorText("         *", Color.Orange, false);
            }

            if (result.HasAnyResponse)
            {
                AppendColorText("\n", Color.White, false);
                if (geoChecked)
                {
                    string combined = result.GeoInfo;
                    if (string.IsNullOrEmpty(combined))
                        combined = GetLocalGeoInfo(result.ReplyAddress.ToString());
                    var defaultFont2 = richTextBox1.Font;
                    using (var smallFont2 = new Font(defaultFont2.FontFamily, Math.Max(defaultFont2.Size - 1.5f, 7f)))
                    {
                        AppendColorText("             -> " + combined + "\n",
                            Global.Yumeyo2, false, smallFont2);
                        _hopGeoOriginal[result.TTL] = combined;
                        if (result.ReplyAddress != null)
                            _ipToHop[result.ReplyAddress.ToString()] = result.TTL;
                    }
                }
            }
            else
            {
                AppendColorText("   请求超时.\n", Color.Orange, false);
            }

            richTextBox1.ScrollToCaret();
        }

        private void DrawMtrTable(ConcurrentDictionary<int, MtrHopStats> stats, string targetIp, IPAddress localIp, int maxHops, int timeout, string protocol, int round, bool geoChecked, bool targetReached, int effectiveMaxHops)
        {
            if (this.IsDisposed || richTextBox1.IsDisposed) return;

            int firstLine = SendMessage(richTextBox1.Handle, EM_GETFIRSTVISIBLELINE, 0, 0);
            SendMessage(richTextBox1.Handle, WM_SETREDRAW, 0, 0);

            richTextBox1.Clear();

            AppendColorText($">> [MTR] 第 {round} 轮 | 目标: {targetIp} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", Color.Lime, false);
            AppendColorText($"   使用接口: {localIp} 跳数:{maxHops} 超时:{timeout}ms 协议:{protocol} | NICX By Yumeyo\n", Color.LightSkyBlue, false);
            if (!string.IsNullOrEmpty(_mtrRuntimeNotice))
                AppendColorText(_mtrRuntimeNotice + "\n", Color.Orange, false);

            bool isV6 = localIp.AddressFamily == AddressFamily.InterNetworkV6;
            int ipWidth = isV6 ? 39 : 20;
            AppendColorText("\n   # " + "IP".PadRight(ipWidth) + " Loss%   Sent  Rcvd   Last   Avg   Best   Worst\n", Color.Cyan, false);
            //AppendColorText("──── ──────────────── ────── ──── ──── ───── ───── ───── ─────\n", Color.Gray, false);

            var sorted = stats.Where(kv => kv.Key <= effectiveMaxHops).OrderBy(kv => kv.Key);
            foreach (var kvp in sorted)
            {
                var s = kvp.Value;

                AppendColorText(kvp.Key.ToString().PadLeft(4) + " ", Color.Yellow, false);

                if (s.ReplyAddress != null)
                {
                    bool isTarget = s.ReplyAddress.ToString() == targetIp;
                    AppendColorText(s.ReplyAddress.ToString().PadRight(ipWidth) + " ", isTarget ? Color.LightGreen : Color.Yellow, false);
                }
                else
                    AppendColorText("-".PadRight(ipWidth) + " ", Color.Orange, false);

                double loss = s.LossPercent;
                Color lossColor = loss >= 50 ? Color.FromArgb(255, 160, 140) : (loss > 0 ? Color.FromArgb(255, 255, 190) : Color.White);
                AppendColorText($"{loss:F1}%".PadLeft(6) + " ", lossColor, false);

                AppendColorText(s.Sent.ToString().PadLeft(5) + " ", Color.White, false);
                AppendColorText(s.Received.ToString().PadLeft(5) + " ", Color.White, false);

                AppendRttCell(s.LastRTT);
                AppendRttCellAvg(s.AvgRTT);
                AppendRttCellBest(s.BestRTT);
                AppendRttCellWorst(s.WorstRTT);

                AppendColorText("\n", Color.White, false);

                // 归属地用更小字号
                var defaultFont = richTextBox1.Font;
                using (var smallFont = new Font(defaultFont.FontFamily, Math.Max(defaultFont.Size - 1.5f, 7f)))
                {
                    if (geoChecked && s.GeoInfo != null)
                    {
                        AppendColorText("             -> " + s.GeoInfo,
                            Global.Yumeyo2, false, smallFont);
                        AppendColorText("\n", Color.White, false);
                    }

                    // 同一跳的其他 IP（-> 箭头与主 IP 对齐）
                    var altIPs = s.AllIPs.Where(ip => !ip.Equals(s.ReplyAddress)).ToList();
                    foreach (var altIp in altIPs)
                    {
                        string altIpStr = altIp.ToString();
                        s.FirstSeenRound.TryGetValue(altIpStr, out int firstRnd);
                        s.IpAppearCount.TryGetValue(altIpStr, out int appearCnt);
                        string roundTag = "";
                        if (appearCnt > 1 && firstRnd > 0)
                            roundTag = $" ({firstRnd}/{appearCnt})";
                        bool isTargetAlt = altIpStr == targetIp;
                        AppendColorText("      " + altIpStr.PadRight(ipWidth), isTargetAlt ? Color.LightGreen : Color.Yellow, false);
                        if (roundTag.Length > 0)
                            AppendColorText(roundTag, Color.Gray, false);
                        if (geoChecked && s.IpGeoCache.TryGetValue(altIp.ToString(), out var altGeo))
                        {
                            AppendColorText(" -> " + altGeo,
                                Global.Yumeyo2, false, smallFont);
                        }
                        AppendColorText("\n", Color.White, false);
                    }
                }
            }

            string status = targetReached ? "(目标已达)" : "";
            AppendColorText($"\n>> 第 {round} 轮完成 {status}, 按[停止]结束 | {Global.exeName}", Color.Lime, true);

            SendMessage(richTextBox1.Handle, WM_SETREDRAW, 1, 0);
            richTextBox1.Invalidate();

            // clamp 到有效行范围，避免截断后滚出空白
            SendMessage(richTextBox1.Handle, EM_LINESCROLL, 0, -9999);
            int lastChar = Math.Max(0, richTextBox1.TextLength - 1);
            int totalLines = richTextBox1.GetLineFromCharIndex(lastChar) + 1;
            int restoreLine = Math.Max(0, Math.Min(firstLine, totalLines - 1));
            if (restoreLine > 0)
                SendMessage(richTextBox1.Handle, EM_LINESCROLL, 0, restoreLine);
        }

        private void AppendRttCell(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.White, false);
        }

        private void AppendRttCellAvg(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.FromArgb(255, 255, 190), false);
        }

        private void AppendRttCellBest(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.Lime, false);
        }

        private void AppendRttCellWorst(double ms)
        {
            if (double.IsNaN(ms))
                AppendColorText("     - ", Color.Orange, false);
            else
                AppendColorText($"{ms,6:F1} ", Color.Red, false);
        }

        private void Trace_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            // 先确认窗口确实会关闭，避免用户在防火墙确认框点“取消”时误停测试。
            if (isManualChanged)
            {
                bool curOn = IsFirewallEnabled();
                bool curRule = IsICMPRuleExisted();
                string stateStr = !curOn ? "防火关" : (curRule ? "已放行" : "防火开");

                DialogResult dr = MessageBox.Show(
                    $"当前防火墙手动设为【{stateStr}】，正在退出Trace+。\n需要还原之前状态吗？\n\n" +
                    "【是】还原并退出 (可能会有多个UAC提示框, 请允许)\n" +
                    "【否】保持并退出\n" +
                    "【取消】手滑了，先不退出",
                    "需要还原吗",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    // 使用同步执行，确保命令跑完
                    // A. 还原防火墙总开关状态
                    string fwCommand = initialFirewallOn ? "advfirewall set allprofiles state on" : "advfirewall set allprofiles state off";
                    RunNetshSync(fwCommand);

                    // B. 还原规则状态 (不管防火墙开没开，规则都要对齐初始状态)
                    if (initialRuleExisted)
                    {
                        // 初始有规则 -> 确保现在也有 (先删再加，保底做法)
                        RunNetshSync($"advfirewall firewall delete rule name=\"{ruleName}\"");
                        RunNetshSync($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv4");
                        RunNetshSync($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv6");
                    }
                    else
                    {
                        // 初始没规则 -> 确保现在删掉
                        RunNetshSync($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    }
                }
                else if (dr == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return; // 梦酱注意：如果是取消，直接返回，不要执行后面的 Dispose
                }
            }

            // 停止属于当前窗口的测试与 WinDivert 会话。RequestStop 会立即关闭
            // 原生 handle，不依赖异步 Trace 方法稍后执行 finally。
            isRunning = false;
            SetUIState(false);
            try { cts?.Cancel(); } catch { }
            StopWinDivertSessionsOwnedBy(this);
            cts?.Dispose();
            cts = null;

            try
            {
                flashTimer?.Stop();
                flashTimer?.Dispose();
                flashTimer = null;
                _ip2regionSearcherV4?.Dispose();
                _ip2regionSearcherV6?.Dispose();
                ReleaseTraceOutputFont();
                _ip2regionSearcherV4 = null;
                _ip2regionSearcherV6 = null;
            }
            catch { }
        }

        // 梦酱专属辅助方法：同步运行 netsh
        private void RunNetshSync(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", arguments)
                {
                    // 关键点 1：必须为 true，否则下面的 runas 不起作用，也就不会弹 UAC
                    UseShellExecute = true,

                    // 关键点 2：申请管理员权限
                    Verb = "runas",

                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(psi))
                {
                    // 关键点 3：等它运行完。3000毫秒（3秒）足够 netsh 处理完了
                    p?.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                // 如果梦酱在弹出的 UAC 框点了“否”，会进到这里
                Debug.WriteLine(ex.Message);
            }
        }

        private void lblTarget_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (isRunning && cts != null) cts.Cancel();

                SaveSettings();

                Point currentLocation = this.Location;
                Size currentSize = this.Size;

                Trace newForm = new Trace();
                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = currentLocation;
                newForm.Size = currentSize;

                newForm.Show();
                this.Close();
                this.Dispose();
            }
        }
        private void lblLocalEnd_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                DialogResult result = MessageBox.Show(
                    "确定以管理员身份重启程序？\n(TCP/UDP Trace需管理员身份运行，如误点请取消)",
                    "提权确认框",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    if (isRunning && cts != null) cts.Cancel();
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = Application.ExecutablePath;
                    startInfo.WorkingDirectory = Environment.CurrentDirectory;
                    startInfo.Verb = "runas";

                    try
                    {
                        Process.Start(startInfo);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("提权失败: " + ex.Message, "提权已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }
        private void CheckIpSearcherDependencies()
        {
            string appPath = AppDomain.CurrentDomain.BaseDirectory;

            // 需要检查的文件清单：文件名 -> 用途说明
            Dictionary<string, string> requiredFiles = new Dictionary<string, string>
    {
        { "IP2Region.Net.dll", "用于 IP2RG 本地数据库核心库" },
        { "ip2region.v4.xdb", "用于 IP2RG IPv4 本地数据库" },
        { "ip2region.v6.xdb", "用于 IP2RG IPv6 本地数据库" },
        { "GeoCN.mmdb", "用于 MaxMind 本地数据库" },
        { "Microsoft.Bcl.AsyncInterfaces.dll", "用于 IP2RG 本地数据库依赖" },
        { "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Memory.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Numerics.Vectors.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Runtime.CompilerServices.Unsafe.dll", "用于 IP2RG 本地数据库依赖" },
        { "System.Threading.Tasks.Extensions.dll", "用于 IP2RG 本地数据库依赖" }
    };

            List<string> missingFiles = new List<string>();

            foreach (var item in requiredFiles)
            {
                string fullPath = Path.Combine(appPath, item.Key);
                if (!File.Exists(fullPath))
                {
                    missingFiles.Add($"{item.Key}（{item.Value}）");
                }
            }

            if (missingFiles.Count > 0)
            {
                checkGEO.Checked = false;
                checkGEO.Enabled = false;

                string msg =
                    "提示：缺少运行 IP 归属地查询所需的文件。\n\n" +
                    string.Join("\n", missingFiles.Select(f => "• " + f)) +
                    "\n\n你可正常进行 Trace 测试，但无法显示每一跳的 IP 归属地。\n" +
                    "如有需要，请重新解压程序、检查杀毒软件或检查相关数据库。";

                MessageBox.Show(
                    msg,
                    "IP2RG 归属地组件缺失",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
            else
            {
                checkGEO.Enabled = true;
            }

        }

        private void CheckWinDivertDependencies()
        {
            string appPath = AppDomain.CurrentDomain.BaseDirectory;
            string[] requiredFiles = { "WinDivert.dll", "WinDivert64.sys" };
            List<string> missingFiles = requiredFiles
                .Where(file => !File.Exists(Path.Combine(appPath, file)))
                .ToList();

            if (missingFiles.Count == 0) return;

            string msg =
                "提示：缺少 TCP/UDP Trace 所需的 WinDivert 驱动\n\n" +
                string.Join("\n", missingFiles.Select(file => "• " + file)) +
                "\n\n影响：\n" +
                "• IPv4 TCP/UDP 将自动使用查询器X原生方法，准确度可能略有降低；\n" +
                "• IPv6 TCP/UDP 无法测试；\n" +
                "• IPv4/IPv6 ICMP Trace 不受影响。\n\n" +
                "可尝试重新下载/解压查询器X/检查杀毒软件，确保依赖完整。";

            MessageBox.Show(
                msg,
                "WinDivert驱动缺失",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            // 1. 如果文本框是空的，就没必要保存啦
            if (string.IsNullOrEmpty(richTextBox1.Text))
            {
                MessageBox.Show("当前没有测试结果可以保存喵", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 2. 创建保存文件对话框
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "请选择保存测试结果的位置";
                sfd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                string pingType = String.Empty;
                if (radioICMP.Checked == true)
                {
                    pingType = radioICMP.Text;
                }
                if (radioTCP.Checked == true)
                {
                    pingType = radioTCP.Text;
                }
                if (radioUDP.Checked == true)
                {
                    pingType = radioUDP.Text;
                }

                // 默认文件名
                string saveTime = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string mtrPrefix = checkMTR.Checked ? "_MTR" : "";
                sfd.FileName = $"NICX_Trace{mtrPrefix}_{pingType}_{comboTargetIP.Text}_{saveTime}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // 3. 准备要保存的内容
                        StringBuilder sb = new StringBuilder();

                        sb.AppendLine($"=== 欢迎使用 Trace+ ❤ 网络综合查询器X by Yumeyo ===");
                        sb.AppendLine($"🔥 本次 Trace+ 输出详情: \n");
                        sb.AppendLine(richTextBox1.Text);
                        sb.AppendLine($"=== 感谢使用 Trace+ ❤ 网络综合查询器X by Yumeyo ===");
                        sb.AppendLine($"======== 导出于 NetInfoCheckerX by Yumeyo ========\n");

                        // 4. 写入文件
                        System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);

                        MessageBox.Show($"保存[{sfd.FileName}]成功!", "保存成功了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        private async void btnWDF_Click(object sender, EventArgs e)
        {
            bool isOn = IsFirewallEnabled();
            bool hasRule = IsICMPRuleExisted();

            if (!isOn)
            {
                // 状态 1：当前关闭
                if (MessageBox.Show("当前防火墙【关闭】。开启防火墙吗？", "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await RunNetshCmd("advfirewall set allprofiles state on");
                    isManualChanged = true;
                }
            }
            else if (hasRule)
            {
                // 状态 2：已放行
                if (MessageBox.Show("当前防火墙【开启】且【已放行】查询器X入站。删除放行规则吗？", "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await RunNetshCmd($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    isManualChanged = true;
                }
            }
            else
            {
                // 状态 3：开启未放行
                DialogResult dr = MessageBox.Show("当前防火墙【开启】且【未放行】查询器X，\n只可使用系统默认网卡(ICMP兼容模式)\n\n要解锁网卡选择功能, 请选择一个操作：\n【是】 关闭 防火墙\n【否】 添加 放行规则\n【取消】暂不修改",
                    "解锁方法选择", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    await RunNetshCmd("advfirewall set allprofiles state off");
                    isManualChanged = true;
                }
                else if (dr == DialogResult.No)
                {
                    // 添加前先删一次确保不重复
                    await RunNetshCmd($"advfirewall firewall delete rule name=\"{ruleName}\"");
                    await RunNetshCmd($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv4");
                    await RunNetshCmd($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=icmpv6");
                    isManualChanged = true;
                }
            }
            UpdateWDFUI();
        }
    }
}
