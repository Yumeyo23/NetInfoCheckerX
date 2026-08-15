using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetInfoCheckerX
{
    public partial class UDPGameTestServer : Form
    {
        private sealed class BindItem
        {
            public string Text;
            public IPAddress Address;
            public override string ToString() { return Text; }
        }

        private sealed class ClientSession
        {
            public uint Id;
            public uint Nonce;
            public EndPoint RemoteEndPoint;
            public int Load;
            public int BufferTicks;
            public long LastSeenMicroseconds;
            public long LatestClientSendMicroseconds;
            public long LatestServerReceiveMicroseconds;
            public bool HasUpstreamData;
            public uint DownPacketSequence;
            public uint DownTickSequence;
            public Random Random;
            public UdpGameDirectionTracker UpstreamTracker;
        }

        private readonly object _sessionsLock = new object();
        private readonly Dictionary<string, ClientSession> _sessions = new Dictionary<string, ClientSession>();
        private readonly Random _sessionRandom = new Random();
        private Socket _socket;
        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private Task _sendTask;
        private System.Windows.Forms.Timer _statusTimer;
        private bool _running;
        private long _bytesReceived;
        private long _bytesSent;
        private long _lastBytesReceived;
        private long _lastBytesSent;
        private Task _runtimeWarmupTask;

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);

        public UDPGameTestServer()
        {
            InitializeComponent();
            btnStart.Click += btnStart_Click;
            FormClosing += UDPGameTestServer_FormClosing;
        }

        private void ApplyServerTheme()
        {
            bool isLight = Global.isThemelight;
            Color windowBack = isLight ? Global.themeLight : Global.themeBlack;
            Color textBack = isLight ? Global.colorWhite : Global.themeBlack;
            Color baseContrastColor = isLight ? Color.Black : Color.White;
            Color yumeyoColor = isLight ? Global.Yumeyo : Global.Yumeyo2;
            Color btnDarkBack = Color.FromArgb(60, 60, 60);

            BackColor = windowBack;

            Label[] titleLabels = { label1, label5 };
            foreach (Label label in titleLabels)
            {
                if (label == null) continue;
                label.ForeColor = yumeyoColor;
                label.BackColor = Color.Transparent;
            }

            if (comboServer != null)
            {
                comboServer.ForeColor = baseContrastColor;
                comboServer.BackColor = textBack;
                comboServer.FlatStyle = isLight ? FlatStyle.Standard : FlatStyle.Flat;
            }

            if (txtPort != null)
            {
                txtPort.ForeColor = baseContrastColor;
                txtPort.BackColor = textBack;
                txtPort.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

            if (lblStatus != null)
            {
                lblStatus.ForeColor = baseContrastColor;
                lblStatus.BackColor = textBack;
                lblStatus.BorderStyle = isLight ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
            }

            if (btnStart != null)
            {
                if (isLight)
                {
                    btnStart.ForeColor = Color.Black;
                    btnStart.BackColor = SystemColors.Control;
                    btnStart.UseVisualStyleBackColor = true;
                    btnStart.FlatStyle = FlatStyle.Standard;
                }
                else
                {
                    btnStart.ForeColor = Color.White;
                    btnStart.BackColor = btnDarkBack;
                    btnStart.UseVisualStyleBackColor = false;
                    btnStart.FlatStyle = FlatStyle.Flat;
                    btnStart.FlatAppearance.BorderColor = Color.DimGray;
                    btnStart.FlatAppearance.MouseOverBackColor = yumeyoColor;
                }
            }

            if (pictureBox1 != null)
                pictureBox1.BackColor = Color.Transparent;
        }

        private void UDPGameTestServer_Load(object sender, EventArgs e)
        {
            this.MinimumSize = this.Size;
            ApplyServerTheme();
            comboServer.DropDownStyle = ComboBoxStyle.DropDownList;
            comboServer.Items.Clear();
            comboServer.Items.Add(new BindItem { Text = "Any (所有网卡)", Address = null });
            foreach (NicAddressInfo nic in NicHelper.GetUsableIPAddresses())
                comboServer.Items.Add(new BindItem { Text = nic.DisplayText, Address = nic.Address });
            comboServer.SelectedIndex = 0;
            CloudControl.UsedTimesCounter("PingUDPGameServer");
            lblStatus.Text = "服务未启动\r\n";
            _runtimeWarmupTask = Task.Run(() => UdpGameRuntimeWarmup.Run(typeof(UDPGameTestServer)));
            _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (_running) StopServer();
            else
            {
                btnStart.Enabled = false;
                try { if (_runtimeWarmupTask != null) await _runtimeWarmupTask; } catch { }
                btnStart.Enabled = true;
                if (!IsDisposed) StartServer();
            }
        }

        private void StartServer()
        {
            int port;
            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("请输入 1-65535 之间的端口", "端口无效了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            BindItem item = comboServer.SelectedItem as BindItem;
            IPAddress selectedAddress = item == null ? null : item.Address;
            try
            {
                _socket = CreateBoundSocket(selectedAddress, port);
                _socket.ReceiveTimeout = 500;
                _cts = new CancellationTokenSource();
                lock (_sessionsLock) _sessions.Clear();
                Interlocked.Exchange(ref _bytesReceived, 0);
                Interlocked.Exchange(ref _bytesSent, 0);
                _lastBytesReceived = _lastBytesSent = 0;
                _running = true;
                TimeBeginPeriod(1);
                btnStart.Text = "关服";
                comboServer.Enabled = false;
                txtPort.Enabled = false;
                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
                _sendTask = Task.Run(() => SendLoop(_cts.Token));
                lblStatus.Text = "服务已启动\r\n等待客户端连接...";
            }
            catch (Exception ex)
            {
                try { if (_socket != null) _socket.Close(); } catch { }
                _socket = null;
                MessageBox.Show("服务启动失败：\r\n" + ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static Socket CreateBoundSocket(IPAddress address, int port)
        {
            if (address != null)
            {
                Socket selected = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                selected.Bind(new IPEndPoint(address, port));
                return selected;
            }

            try
            {
                Socket dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                dual.DualMode = true;
                dual.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return dual;
            }
            catch (SocketException)
            {
                Socket ipv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                ipv4.Bind(new IPEndPoint(IPAddress.Any, port));
                return ipv4;
            }
        }

        private void ReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[2048];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    EndPoint remote = _socket.AddressFamily == AddressFamily.InterNetworkV6
                        ? new IPEndPoint(IPAddress.IPv6Any, 0)
                        : new IPEndPoint(IPAddress.Any, 0);
                    int length = _socket.ReceiveFrom(buffer, ref remote);
                    long receivedAt = UdpGameProtocol.NowMicroseconds();
                    Interlocked.Add(ref _bytesReceived, length);

                    UdpGamePacket packet;
                    if (!UdpGameProtocol.TryParse(buffer, length, out packet)) continue;
                    if (packet.Type == UdpGameMessageType.Hello)
                    {
                        HandleHello(packet, remote);
                        continue;
                    }

                    ClientSession session = GetSession(remote, packet.SessionId);
                    if (session == null) continue;
                    session.LastSeenMicroseconds = receivedAt;

                    if (packet.Type == UdpGameMessageType.Goodbye)
                    {
                        RemoveSession(remote, packet.SessionId);
                    }
                    else if (packet.Type == UdpGameMessageType.UpstreamData)
                    {
                        session.HasUpstreamData = true;
                        session.LatestClientSendMicroseconds = packet.SendMicroseconds;
                        session.LatestServerReceiveMicroseconds = receivedAt;
                        session.UpstreamTracker.Add(packet, receivedAt);
                    }
                }
                catch (SocketException ex)
                {
                    if (token.IsCancellationRequested || ex.SocketErrorCode == SocketError.Interrupted || ex.SocketErrorCode == SocketError.OperationAborted) break;
                    if (ex.SocketErrorCode != SocketError.TimedOut) Thread.Sleep(20);
                }
                catch (ObjectDisposedException) { break; }
                catch { if (!token.IsCancellationRequested) Thread.Sleep(20); }
            }
        }

        private void HandleHello(UdpGamePacket packet, EndPoint remote)
        {
            string key = EndPointKey(remote);
            ClientSession session;
            lock (_sessionsLock)
            {
                if (!_sessions.TryGetValue(key, out session) || session.Nonce != packet.Nonce)
                {
                    uint id = NextSessionId();
                    int load = Math.Max(1, Math.Min(4, (int)packet.Load));
                    int bufferTicks = Math.Max(0, Math.Min(2, (int)packet.BufferTicks));
                    session = new ClientSession
                    {
                        Id = id,
                        Nonce = packet.Nonce,
                        RemoteEndPoint = remote,
                        Load = load,
                        BufferTicks = bufferTicks,
                        LastSeenMicroseconds = UdpGameProtocol.NowMicroseconds(),
                        Random = new Random(unchecked((int)(id ^ packet.Nonce))),
                        UpstreamTracker = new UdpGameDirectionTracker(bufferTicks)
                    };
                    _sessions[key] = session;
                }
                else
                {
                    session.LastSeenMicroseconds = UdpGameProtocol.NowMicroseconds();
                }
            }

            UdpGamePacket welcome = new UdpGamePacket
            {
                Type = UdpGameMessageType.Welcome,
                SessionId = session.Id,
                Load = (byte)session.Load,
                BufferTicks = (byte)session.BufferTicks,
                Nonce = session.Nonce,
                SendMicroseconds = UdpGameProtocol.NowMicroseconds()
            };
            SendTo(UdpGameProtocol.CreatePacket(welcome, UdpGameProtocol.HeaderSize, null), session.RemoteEndPoint);
        }

        private void SendLoop(CancellationToken token)
        {
            long interval = Math.Max(1, System.Diagnostics.Stopwatch.Frequency / UdpGameProtocol.TickRate);
            long next = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                next += interval;
                ClientSession[] sessions = GetActiveSessions();
                foreach (ClientSession session in sessions)
                {
                    if (!session.HasUpstreamData) continue;
                    try { SendGameTick(session); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { return; }
                }

                long afterSend = System.Diagnostics.Stopwatch.GetTimestamp();
                if (afterSend - next > interval * 4) next = afterSend;

                while (!token.IsCancellationRequested)
                {
                    long remaining = next - System.Diagnostics.Stopwatch.GetTimestamp();
                    if (remaining <= 0) break;
                    double milliseconds = remaining * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (milliseconds > 2) Thread.Sleep(Math.Max(1, (int)milliseconds - 1));
                    else Thread.SpinWait(80);
                }
            }
        }

        private void SendGameTick(ClientSession session)
        {
            long sendAt = UdpGameProtocol.NowMicroseconds();
            UdpGameDirectionSnapshot up = session.UpstreamTracker.Snapshot(sendAt);
            int[] sizes = UdpGameProtocol.GetDownstreamPacketSizes(session.Load, session.Random);
            uint tick = ++session.DownTickSequence;
            for (int part = 0; part < sizes.Length; part++)
            {
                UdpGamePacket packet = new UdpGamePacket
                {
                    Type = UdpGameMessageType.DownstreamData,
                    SessionId = session.Id,
                    PacketSequence = ++session.DownPacketSequence,
                    TickSequence = tick,
                    PartIndex = (ushort)part,
                    PartCount = (ushort)sizes.Length,
                    SendMicroseconds = UdpGameProtocol.NowMicroseconds(),
                    EchoClientMicroseconds = session.LatestClientSendMicroseconds,
                    ServerReceiveMicroseconds = session.LatestServerReceiveMicroseconds,
                    UpMissPercent = (float)up.MissPercent,
                    UpLossPercent = (float)up.LossPercent,
                    UpJitterMs = (float)up.JitterMs,
                    Load = (byte)session.Load,
                    BufferTicks = (byte)session.BufferTicks
                };
                SendTo(UdpGameProtocol.CreatePacket(packet, sizes[part], session.Random), session.RemoteEndPoint);
            }
        }

        private void SendTo(byte[] bytes, EndPoint remote)
        {
            int sent = _socket.SendTo(bytes, remote);
            Interlocked.Add(ref _bytesSent, sent);
        }

        private ClientSession[] GetActiveSessions()
        {
            long now = UdpGameProtocol.NowMicroseconds();
            lock (_sessionsLock)
            {
                foreach (string key in _sessions.Where(kv => now - kv.Value.LastSeenMicroseconds > 5000000L).Select(kv => kv.Key).ToArray())
                    _sessions.Remove(key);
                return _sessions.Values.ToArray();
            }
        }

        private ClientSession GetSession(EndPoint remote, uint id)
        {
            ClientSession session;
            lock (_sessionsLock)
                return _sessions.TryGetValue(EndPointKey(remote), out session) && session.Id == id ? session : null;
        }

        private void RemoveSession(EndPoint remote, uint id)
        {
            string key = EndPointKey(remote);
            lock (_sessionsLock)
            {
                ClientSession session;
                if (_sessions.TryGetValue(key, out session) && session.Id == id) _sessions.Remove(key);
            }
        }

        private uint NextSessionId()
        {
            byte[] bytes = new byte[4];
            uint value;
            do { _sessionRandom.NextBytes(bytes); value = BitConverter.ToUInt32(bytes, 0); } while (value == 0);
            return value;
        }

        private static string EndPointKey(EndPoint endpoint)
        {
            IPEndPoint ip = endpoint as IPEndPoint;
            if (ip == null) return endpoint.ToString();
            IPAddress address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
            return address + "|" + ip.Port;
        }

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            if (!_running) return;
            long received = Interlocked.Read(ref _bytesReceived);
            long sent = Interlocked.Read(ref _bytesSent);
            long receiveRate = received - _lastBytesReceived;
            long sendRate = sent - _lastBytesSent;
            _lastBytesReceived = received;
            _lastBytesSent = sent;
            int online;
            lock (_sessionsLock) online = _sessions.Count;
            lblStatus.Text = string.Format("在线客户端：{0}\r\n↑ {1}/s    ↓ {2}/s\r\n请用查询器X“延迟测试-UDP游戏模拟(客户端)”连接并开启测试。", online, FormatRate(sendRate), FormatRate(receiveRate));
        }

        private static string FormatRate(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024d / 1024d).ToString("0.00") + " MB";
            return (bytes / 1024d).ToString("0.0") + " KB";
        }

        private void StopServer()
        {
            if (!_running) return;
            _running = false;
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_socket != null) _socket.Close(); } catch { }
            _socket = null;
            TimeEndPeriod(1);
            lock (_sessionsLock) _sessions.Clear();
            btnStart.Text = "开服";
            comboServer.Enabled = true;
            txtPort.Enabled = true;
            lblStatus.Text = "服务已停止\r\n";
        }

        private void UDPGameTestServer_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_statusTimer != null) _statusTimer.Stop();
            StopServer();
        }
    }
}
