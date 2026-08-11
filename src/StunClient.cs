using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetInfoCheckerX
{
    public enum NatType
    {
        UdpBlocked,
        OpenInternet,
        FullCone,
        RestrictedCone,
        PortRestrictedCone,
        SymmetricUDPFirewall,
        Symmetric, // NAT4
        Unknown
    }

    public class StunResult
    {
        public NatType NatType { get; set; } = NatType.Unknown;
        public IPEndPoint PublicEndPoint { get; set; }  // 我的外网地址
        public IPEndPoint LocalEndPoint { get; set; }   // 我的内网地址
        public IPEndPoint ChangedEndPoint { get; set; } // 服务器告诉我的备用地址
        public IPEndPoint ResponseEndPoint { get; set; } // 实际响应来源地址
        public string ErrorMessage { get; set; }        // 异常/错误信息（非超时类错误）
    }

    public class StunClient
    {
        // 消息类型定义
        private const ushort BindingRequest = 0x0001;
        private const ushort BindingResponse = 0x0101;

        // 属性类型定义
        private const int AttributeMappedAddress = 0x0001;
        private const int AttributeResponseAddress = 0x0002;
        private const int AttributeChangeRequest = 0x0003;
        private const int AttributeSourceAddress = 0x0004;
        private const int AttributeChangedAddress = 0x0005; // 关键：服务器的备用地址
        private const int AttributeXorMappedAddress = 0x0020;
        private const int AttributeOtherAddress = 0x802C; // RFC5780: OTHER-ADDRESS

        private static byte[] BuildLegacyTransactionId(byte[] tx12)
        {
            if (tx12 == null || tx12.Length != 12) return null;
            byte[] tx16 = new byte[16];
            tx16[0] = 0x21;
            tx16[1] = 0x12;
            tx16[2] = 0xA4;
            tx16[3] = 0x42;
            Buffer.BlockCopy(tx12, 0, tx16, 4, 12);
            return tx16;
        }

        public static StunResult Query(Socket socket, IPEndPoint serverEndpoint, bool changeIp, bool changePort, int timeoutMs = 2000)
        {
            try
            {
                // --- 1. 发送请求 ---
                byte[] fullTransactionId = Guid.NewGuid().ToByteArray();
                // STUN事务ID是16字节，但实际只用12字节
                byte[] transactionId = new byte[12];
                Array.Copy(fullTransactionId, 0, transactionId, 0, 12);
                // 调试：记录发送的目标
                Console.WriteLine($"[STUN] 发送请求到 {serverEndpoint}，changeIp={changeIp}, changePort={changePort}, timeout={timeoutMs}ms");

                List<byte> sendBuffer = new List<byte>();

                // STUN 消息头 (RFC5389格式)
                sendBuffer.AddRange(new byte[] { 0x00, 0x01 }); // Binding Request
                sendBuffer.AddRange(new byte[] { 0x00, 0x00 }); // Length - 先填0，后面计算

                // Magic Cookie (RFC 5389)
                sendBuffer.AddRange(new byte[] { 0x21, 0x12, 0xA4, 0x42 });
                sendBuffer.AddRange(transactionId); // 12字节事务ID

                List<byte> attributes = new List<byte>();

                if (changeIp || changePort)
                {
                    // Change Request 属性 (RFC3489)
                    attributes.AddRange(new byte[] { 0x00, 0x03 }); // Change Request
                    attributes.AddRange(new byte[] { 0x00, 0x04 }); // Length = 4
                    byte flag = 0;
                    if (changeIp) flag |= 0x04;
                    if (changePort) flag |= 0x02;
                    attributes.AddRange(new byte[] { 0x00, 0x00, 0x00, flag });
                }

                // 更新长度字段
                ushort length = (ushort)attributes.Count;
                byte[] lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)length));
                sendBuffer[2] = lengthBytes[0];
                sendBuffer[3] = lengthBytes[1];

                sendBuffer.AddRange(attributes);

                socket.SendTo(sendBuffer.ToArray(), serverEndpoint);

                // --- 2. 接收响应 ---
                byte[] receiveBuffer = new byte[1024];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                // 同一个 Socket 会收到历史包：必须循环直到拿到"事务ID匹配"的响应
                while (DateTime.UtcNow < deadline)
                {
                    int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remainingMs <= 0) break;
                    socket.ReceiveTimeout = remainingMs;

                    EndPoint senderRemote = (socket.AddressFamily == AddressFamily.InterNetworkV6)
                        ? new IPEndPoint(IPAddress.IPv6Any, 0)
                        : new IPEndPoint(IPAddress.Any, 0);

                    int len = socket.ReceiveFrom(receiveBuffer, ref senderRemote);
                    Console.WriteLine($"[STUN] 收到来自 {senderRemote} 的响应，长度: {len}");

                    StunResult result = ParseResponse(receiveBuffer, len, transactionId);
                    if (result != null)
                    {
                        result.ResponseEndPoint = senderRemote as IPEndPoint;
                        result.LocalEndPoint = socket.LocalEndPoint as IPEndPoint;
                        return result;
                    }

                    Console.WriteLine("[STUN] 收到非本次事务响应，继续等待...");
                }

                Console.WriteLine($"[STUN] 请求超时(无匹配事务): {serverEndpoint}");
                return null;
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.TimedOut)
            {
                Console.WriteLine($"[STUN] 请求超时: {serverEndpoint}");
                return null; // 超时表示无响应
            }
            catch (SocketException sex)
            {
                Console.WriteLine($"[STUN] Socket错误 [{sex.SocketErrorCode}]: {sex.Message} → {serverEndpoint}");
                return new StunResult { ErrorMessage = sex.Message };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STUN] 请求异常: {ex.Message}");
                return new StunResult { ErrorMessage = ex.Message };
            }
        }

        // RFC3489 经典查询（无 Magic Cookie）
        public static StunResult Query3489(Socket socket, IPEndPoint serverEndpoint, bool changeIp, bool changePort, int timeoutMs = 2000)
        {
            try
            {
                byte[] transactionId = Guid.NewGuid().ToByteArray(); // 16 bytes
                List<byte> sendBuffer = new List<byte>();

                sendBuffer.AddRange(new byte[] { 0x00, 0x01 }); // Binding Request
                sendBuffer.AddRange(new byte[] { 0x00, 0x00 }); // Length placeholder
                sendBuffer.AddRange(transactionId);              // RFC3489 transaction ID (16)

                List<byte> attributes = new List<byte>();
                if (changeIp || changePort)
                {
                    attributes.AddRange(new byte[] { 0x00, 0x03 });
                    attributes.AddRange(new byte[] { 0x00, 0x04 });
                    byte flag = 0;
                    if (changeIp) flag |= 0x04;
                    if (changePort) flag |= 0x02;
                    attributes.AddRange(new byte[] { 0x00, 0x00, 0x00, flag });
                }

                ushort length = (ushort)attributes.Count;
                byte[] lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)length));
                sendBuffer[2] = lengthBytes[0];
                sendBuffer[3] = lengthBytes[1];
                sendBuffer.AddRange(attributes);

                socket.SendTo(sendBuffer.ToArray(), serverEndpoint);

                byte[] receiveBuffer = new byte[1024];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                while (DateTime.UtcNow < deadline)
                {
                    int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remainingMs <= 0) break;
                    socket.ReceiveTimeout = remainingMs;

                    EndPoint senderRemote = (socket.AddressFamily == AddressFamily.InterNetworkV6)
                        ? new IPEndPoint(IPAddress.IPv6Any, 0)
                        : new IPEndPoint(IPAddress.Any, 0);

                    int len = socket.ReceiveFrom(receiveBuffer, ref senderRemote);
                    Console.WriteLine($"[STUN-3489] 收到来自 {senderRemote} 的响应，长度: {len}");

                    StunResult result = null;
                    if (len >= 20)
                    {
                        // 兼容 RFC5389 格式响应：现代 STUN 服务器可能以 RFC5389 格式回复 RFC3489 请求
                        uint respCookie = (uint)((receiveBuffer[4] << 24) | (receiveBuffer[5] << 16) | (receiveBuffer[6] << 8) | receiveBuffer[7]);
                        if (respCookie == 0x2112A442)
                        {
                            // RFC5389 格式响应：Magic Cookie 存在，比较 12 字节事务 ID
                            byte[] txId12 = new byte[12];
                            Buffer.BlockCopy(transactionId, 4, txId12, 0, 12);
                            result = ParseResponse(receiveBuffer, len, txId12);
                        }
                        else
                        {
                            // RFC3489 格式响应：无 Magic Cookie，比较完整 16 字节事务 ID
                            result = ParseRFC3489Response(receiveBuffer, len, transactionId);
                        }
                    }
                    if (result != null)
                    {
                        result.ResponseEndPoint = senderRemote as IPEndPoint;
                        result.LocalEndPoint = socket.LocalEndPoint as IPEndPoint;
                        return result;
                    }

                    Console.WriteLine("[STUN-3489] 收到非本次事务响应，继续等待...");
                }

                Console.WriteLine($"[STUN-3489] 请求超时(无匹配事务): {serverEndpoint}");
                return null;
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.TimedOut)
            {
                Console.WriteLine($"[STUN-3489] 请求超时: {serverEndpoint}");
                return null;
            }
            catch (SocketException sex)
            {
                Console.WriteLine($"[STUN-3489] Socket错误 [{sex.SocketErrorCode}]: {sex.Message} → {serverEndpoint}");
                return new StunResult { ErrorMessage = sex.Message };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STUN-3489] 请求异常: {ex.Message}");
                return new StunResult { ErrorMessage = ex.Message };
            }
        }

        private static StunResult ParseResponse(byte[] data, int length, byte[] originalTransactionId)
        {
            if (length < 20)
                return null;

            // 验证消息类型
            ushort messageType = (ushort)((data[0] << 8) | data[1]);
            if (messageType != BindingResponse)
            {
                return null;
            }

            // 验证Magic Cookie
            uint magicCookie = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
            if (magicCookie != 0x2112A442)
            {
                // 兼容 RFC3489（无 Magic Cookie）响应
                return ParseRFC3489Response(data, length, BuildLegacyTransactionId(originalTransactionId));
            }

            // 验证事务ID (比较12字节)
            for (int i = 8; i < 20; i++)
            {
                if (data[i] != originalTransactionId[i - 8])
                    return null;
            }

            StunResult result = new StunResult();
            int offset = 20;

            while (offset + 4 <= length)
            {
                int attrType = (data[offset] << 8) | data[offset + 1];
                int attrLen = (data[offset + 2] << 8) | data[offset + 3];
                offset += 4;

                if (offset + attrLen > length)
                    break;

                // --- 地址类型属性 ---
                if (attrType == AttributeMappedAddress ||
                    attrType == AttributeXorMappedAddress ||
                    attrType == AttributeChangedAddress ||
                    attrType == AttributeOtherAddress) // RFC5780: OTHER-ADDRESS
                {
                    if (attrLen < 8)
                    {
                        offset += attrLen;
                        continue;
                    }

                    byte family = data[offset + 1];

                    IPAddress ipAddress = null;
                    int port = (data[offset + 2] << 8) | data[offset + 3];

                    if (family == 0x01) // IPv4
                    {
                        if (attrLen < 8) { offset += attrLen; continue; }
                        byte[] ipBytes = new byte[4];
                        Buffer.BlockCopy(data, offset + 4, ipBytes, 0, 4);

                        // 如果是XOR编码的属性，需要解码
                        if (attrType == AttributeXorMappedAddress)
                        {
                            // XOR decoding for RFC5389
                            // 修复：直接异或 Magic Cookie 的高16位 (0x2112)，不要右移把它变没了
                            port ^= 0x2112;

                            ipBytes[0] ^= 0x21;
                            ipBytes[1] ^= 0x12;
                            ipBytes[2] ^= 0xA4;
                            ipBytes[3] ^= 0x42;
                        }
                        ipAddress = new IPAddress(ipBytes);
                    }
                    else if (family == 0x02) // IPv6 【新增：IPv6 支持】
                    {
                        if (attrLen < 20) { offset += attrLen; continue; }
                        byte[] ipBytes = new byte[16];
                        Buffer.BlockCopy(data, offset + 4, ipBytes, 0, 16);

                        // 如果是XOR编码的属性，需要解码
                        if (attrType == AttributeXorMappedAddress)
                        {
                            // XOR decoding for RFC5389: Port XOR Magic Cookie High 16 bits
                            port ^= (0x2112); // Magic Cookie的高16位是 0x2112

                            // IP XOR Magic Cookie (4 bytes) + Transaction ID (12 bytes)
                            // 修复：需要按照RFC5389规定进行XOR解码
                            // 前4字节与Magic Cookie (0x2112A442) XOR
                            ipBytes[0] ^= 0x21;
                            ipBytes[1] ^= 0x12;
                            ipBytes[2] ^= 0xA4;
                            ipBytes[3] ^= 0x42;

                            // 接下来的12字节与Transaction ID XOR
                            // 但data中的transactionId是12字节的，而originalTransactionId也是12字节
                            // 我们需要确保有足够的originalTransactionId字节
                            if (originalTransactionId != null && originalTransactionId.Length >= 12)
                            {
                                for (int i = 0; i < 12; i++)
                                {
                                    if (i + 4 < ipBytes.Length)
                                    {
                                        ipBytes[i + 4] ^= originalTransactionId[i];
                                    }
                                }
                            }
                        }
                        ipAddress = new IPAddress(ipBytes);
                    }
                    else // 不支持的地址族
                    {
                        offset += attrLen;
                        continue;
                    }

                    if (ipAddress == null) // 再次检查
                    {
                        offset += attrLen;
                        continue;
                    }

                    IPEndPoint ep = new IPEndPoint(ipAddress, port);

                    if (attrType == AttributeXorMappedAddress)
                        result.PublicEndPoint = ep;
                    else if (attrType == AttributeMappedAddress && result.PublicEndPoint == null)
                        result.PublicEndPoint = ep;
                    else if (attrType == AttributeChangedAddress || attrType == AttributeOtherAddress)
                        result.ChangedEndPoint = ep;
                }

                // --- 属性 4 字节对齐 ---
                offset += attrLen;
                if ((attrLen % 4) != 0)
                    offset += (4 - (attrLen % 4));
            }

            return result;
        }

        // 解析RFC3489格式的响应（没有Magic Cookie）
        private static StunResult ParseRFC3489Response(byte[] data, int length, byte[] expectedTransactionId16 = null)
        {
            if (length < 20)
                return null;

            ushort messageType = (ushort)((data[0] << 8) | data[1]);
            if (messageType != BindingResponse)
                return null;

            // RFC3489: 4字节头 + 16字节事务ID
            if (expectedTransactionId16 != null && expectedTransactionId16.Length == 16)
            {
                for (int i = 0; i < 16; i++)
                {
                    if (data[4 + i] != expectedTransactionId16[i])
                        return null;
                }
            }

            StunResult result = new StunResult();
            int offset = 20;

            while (offset + 4 <= length)
            {
                int attrType = (data[offset] << 8) | data[offset + 1];
                int attrLen = (data[offset + 2] << 8) | data[offset + 3];
                offset += 4;

                if (offset + attrLen > length)
                    break;

                // --- 地址类型属性 ---
                if (attrType == AttributeMappedAddress ||
                    attrType == AttributeChangedAddress)
                {
                    if (attrLen < 8)
                    {
                        offset += attrLen;
                        continue;
                    }

                    byte family = data[offset + 1];
                    if (family != 0x01) // 只处理IPv4
                    {
                        offset += attrLen;
                        continue;
                    }

                    int port = (data[offset + 2] << 8) | data[offset + 3];

                    byte[] ipBytes = new byte[4];
                    Buffer.BlockCopy(data, offset + 4, ipBytes, 0, 4);

                    IPEndPoint ep = new IPEndPoint(new IPAddress(ipBytes), port);

                    if (attrType == AttributeMappedAddress)
                        result.PublicEndPoint = ep;
                    else if (attrType == AttributeChangedAddress)
                        result.ChangedEndPoint = ep;
                }

                // --- 属性 4 字节对齐 ---
                offset += attrLen;
                if ((attrLen % 4) != 0)
                    offset += (4 - (attrLen % 4));
            }

            return result;
        }

        // 在 StunClient 类中添加辅助方法
        public static IPEndPoint GetAlternateServerEndpoint(IPEndPoint originalServer, StunResult result, string testType = "RFC5780")
        {
            if (result?.ChangedEndPoint == null)
                return null;

            IPEndPoint alternate = result.ChangedEndPoint;

            // 关键修复：如果备用地址端口无效（如3479无响应），尝试使用原始端口
            // 这在某些服务器配置不正确时特别有用
            if (testType == "RFC5780" || testType == "RFC5389")
            {
                // RFC5780/5389: OTHER-ADDRESS 端口应该有效
                // 但某些服务器配置错误，返回的端口可能不监听
                // 如果连续多次测试失败，可以尝试原始端口
                return alternate;
            }
            else
            {
                // RFC3489: CHANGED-ADDRESS
                return alternate;
            }
        }


        /// <summary>
        /// 通用的 STUN 查询方法，根据协议类型选择实现
        /// </summary>

        public static async Task<StunResult> QueryAsync(string protocol, Socket socket, IPEndPoint serverEndpoint, bool changeIp, bool changePort, IPEndPoint localEndPoint = null, CancellationToken cancellationToken = default)
        {
            protocol = protocol.ToUpper();

            if (protocol == "TCP")
            {
                return await QueryTcpAsync(serverEndpoint, changeIp, changePort, localEndPoint, cancellationToken).ConfigureAwait(false);
            }
            else if (protocol == "TLS")
            {
                return await QueryTlsAsync(serverEndpoint, changeIp, changePort, localEndPoint, cancellationToken).ConfigureAwait(false);
            }
            else if (protocol == "UDP")
            {
                // 对于 UDP，使用传入的 socket
                return await Task.Run(() =>
                {
                    return Query(socket, serverEndpoint, changeIp, changePort);
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentException("不支持的协议: " + protocol);
            }
        }

        /// <summary>
        /// TCP 协议的 STUN 查询方法
        /// </summary>
        public static async Task<StunResult> QueryTcpAsync(IPEndPoint serverEndpoint, bool changeIp, bool changePort, IPEndPoint localEndPoint = null, CancellationToken cancellationToken = default, int timeoutMs = 2000)
        {
            TcpClient tcpClient = null;
            NetworkStream stream = null;
            CancellationTokenSource timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(Math.Max(1, timeoutMs));
            CancellationToken queryToken = timeoutCancellation.Token;

            try
            {
                tcpClient = localEndPoint != null
                    ? new TcpClient(localEndPoint)
                    : new TcpClient(serverEndpoint.AddressFamily);
                if (localEndPoint != null)
                    Console.WriteLine($"[TCP] 已绑定到本地端点: {localEndPoint}");

                tcpClient.SendTimeout = timeoutMs;
                tcpClient.ReceiveTimeout = timeoutMs;

                Console.WriteLine($"[TCP] 尝试连接到 {serverEndpoint}");
                var connectTask = tcpClient.ConnectAsync(serverEndpoint.Address, serverEndpoint.Port);
                var timeoutTask = Task.Delay(Timeout.Infinite, queryToken);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Console.WriteLine($"[TCP] 连接超时: {serverEndpoint}");
                    tcpClient.Close();
                    return null;
                }

                try { await connectTask.ConfigureAwait(false); }
                catch (Exception connectEx)
                {
                    Console.WriteLine($"[TCP] 连接异常: {connectEx.Message}");
                    return null;
                }

                if (!tcpClient.Connected)
                {
                    Console.WriteLine($"[TCP] 连接失败: {serverEndpoint}");
                    return null;
                }

                Console.WriteLine($"[TCP] 连接成功: {serverEndpoint}");
                stream = tcpClient.GetStream();

                byte[] fullTransactionId = Guid.NewGuid().ToByteArray();
                byte[] transactionId = new byte[12];
                Array.Copy(fullTransactionId, 0, transactionId, 0, 12);

                List<byte> sendBuffer = new List<byte>();
                sendBuffer.AddRange(new byte[] { 0x00, 0x01 });
                sendBuffer.AddRange(new byte[] { 0x00, 0x00 });
                sendBuffer.AddRange(new byte[] { 0x21, 0x12, 0xA4, 0x42 });
                sendBuffer.AddRange(transactionId);

                List<byte> attributes = new List<byte>();
                if (changeIp || changePort)
                {
                    attributes.AddRange(new byte[] { 0x00, 0x03 });
                    attributes.AddRange(new byte[] { 0x00, 0x04 });
                    byte flag = 0;
                    if (changeIp) flag |= 0x04;
                    if (changePort) flag |= 0x02;
                    attributes.AddRange(new byte[] { 0x00, 0x00, 0x00, flag });
                }

                ushort length = (ushort)attributes.Count;
                byte[] lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)length));
                sendBuffer[2] = lengthBytes[0];
                sendBuffer[3] = lengthBytes[1];
                sendBuffer.AddRange(attributes);

                await stream.WriteAsync(sendBuffer.ToArray(), 0, sendBuffer.Count, queryToken).ConfigureAwait(false);
                await stream.FlushAsync(queryToken).ConfigureAwait(false);

                byte[] receiveBuffer = new byte[1024];
                int totalReceived = 0;
                int bytesRead;

                while (totalReceived < 20)
                {
                    bytesRead = await stream.ReadAsync(receiveBuffer, totalReceived, 20 - totalReceived, queryToken).ConfigureAwait(false);
                    if (bytesRead == 0) return null;
                    totalReceived += bytesRead;
                }

                ushort messageLength = (ushort)((receiveBuffer[2] << 8) | receiveBuffer[3]);
                int totalMessageLength = 20 + messageLength;

                while (totalReceived < totalMessageLength)
                {
                    int bytesToRead = Math.Min(totalMessageLength - totalReceived, receiveBuffer.Length - totalReceived);
                    bytesRead = await stream.ReadAsync(receiveBuffer, totalReceived, bytesToRead, queryToken).ConfigureAwait(false);
                    if (bytesRead == 0) return null;
                    totalReceived += bytesRead;
                }

                var result = ParseResponse(receiveBuffer, totalReceived, transactionId);
                if (result != null)
                {
                    result.ResponseEndPoint = serverEndpoint;
                    result.LocalEndPoint = tcpClient.Client.LocalEndPoint as IPEndPoint;
                }
                return result;
            }
            catch (SocketException sex)
            {
                Console.WriteLine($"[TCP] Socket错误 [{sex.SocketErrorCode}]: {sex.Message} → {serverEndpoint}");
                return new StunResult { ErrorMessage = sex.Message };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[TCP] 请求超时: {serverEndpoint}");
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP] 异常: {ex.Message}");
                return new StunResult { ErrorMessage = ex.Message };
            }
            finally
            {
                stream?.Close();
                try { tcpClient?.Client?.Close(0); } catch { }
                tcpClient?.Close();
                timeoutCancellation.Dispose();
            }
        }

        /// <summary>
        /// TLS 协议的 STUN 查询方法
        /// </summary>
        public static async Task<StunResult> QueryTlsAsync(
            IPEndPoint serverEndpoint,
            bool changeIp,
            bool changePort,
            IPEndPoint localEndPoint = null,
            CancellationToken cancellationToken = default,
            string tlsServerName = null,
            int timeoutMs = 2000)
        {
            TcpClient tcpClient = null;
            SslStream sslStream = null;
            CancellationTokenSource timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(Math.Max(1, timeoutMs));
            CancellationToken queryToken = timeoutCancellation.Token;

            try
            {
                tcpClient = localEndPoint != null
                    ? new TcpClient(localEndPoint)
                    : new TcpClient(serverEndpoint.AddressFamily);
                if (localEndPoint != null)
                    Console.WriteLine($"[TLS] 已绑定到本地端点: {localEndPoint}");

                tcpClient.SendTimeout = timeoutMs;
                tcpClient.ReceiveTimeout = timeoutMs;

                var connectTask = tcpClient.ConnectAsync(serverEndpoint.Address, serverEndpoint.Port);
                var timeoutTask = Task.Delay(Timeout.Infinite, queryToken);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Console.WriteLine($"[TLS] 连接超时: {serverEndpoint}");
                    tcpClient.Close();
                    return null;
                }

                try { await connectTask.ConfigureAwait(false); }
                catch (Exception connectEx)
                {
                    Console.WriteLine($"[TLS] 连接异常: {connectEx.Message}");
                    return null;
                }

                if (!tcpClient.Connected)
                {
                    Console.WriteLine($"[TLS] 连接失败: {serverEndpoint}");
                    return null;
                }

                Console.WriteLine($"[TLS] TCP连接成功: {serverEndpoint}");

                sslStream = new SslStream(tcpClient.GetStream(), false,
                    (sender, certificate, chain, sslPolicyErrors) => true);

                try
                {
                    string sni = string.IsNullOrWhiteSpace(tlsServerName)
                        ? serverEndpoint.Address.ToString()
                        : tlsServerName;
                    Task authenticateTask = sslStream.AuthenticateAsClientAsync(sni);
                    Task authenticateTimeoutTask = Task.Delay(Timeout.Infinite, queryToken);
                    Task authenticateCompletedTask = await Task.WhenAny(authenticateTask, authenticateTimeoutTask).ConfigureAwait(false);
                    if (authenticateCompletedTask == authenticateTimeoutTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Console.WriteLine($"[TLS] TLS握手超时: {serverEndpoint}");
                        return null;
                    }
                    await authenticateTask.ConfigureAwait(false);
                    Console.WriteLine($"[TLS] TLS握手成功");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception authEx)
                {
                    Console.WriteLine($"[TLS] TLS握手失败: {authEx.Message}");
                    return null;
                }

                byte[] fullTransactionId = Guid.NewGuid().ToByteArray();
                byte[] transactionId = new byte[12];
                Array.Copy(fullTransactionId, 0, transactionId, 0, 12);

                List<byte> sendBuffer = new List<byte>();
                sendBuffer.AddRange(new byte[] { 0x00, 0x01 });
                sendBuffer.AddRange(new byte[] { 0x00, 0x00 });
                sendBuffer.AddRange(new byte[] { 0x21, 0x12, 0xA4, 0x42 });
                sendBuffer.AddRange(transactionId);

                List<byte> attributes = new List<byte>();
                if (changeIp || changePort)
                {
                    attributes.AddRange(new byte[] { 0x00, 0x03 });
                    attributes.AddRange(new byte[] { 0x00, 0x04 });
                    byte flag = 0;
                    if (changeIp) flag |= 0x04;
                    if (changePort) flag |= 0x02;
                    attributes.AddRange(new byte[] { 0x00, 0x00, 0x00, flag });
                }

                ushort length = (ushort)attributes.Count;
                byte[] lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)length));
                sendBuffer[2] = lengthBytes[0];
                sendBuffer[3] = lengthBytes[1];
                sendBuffer.AddRange(attributes);

                await sslStream.WriteAsync(sendBuffer.ToArray(), 0, sendBuffer.Count, queryToken).ConfigureAwait(false);
                await sslStream.FlushAsync(queryToken).ConfigureAwait(false);

                byte[] receiveBuffer = new byte[1024];
                int totalReceived = 0;
                int bytesRead;

                while (totalReceived < 20)
                {
                    bytesRead = await sslStream.ReadAsync(receiveBuffer, totalReceived, 20 - totalReceived, queryToken).ConfigureAwait(false);
                    if (bytesRead == 0) return null;
                    totalReceived += bytesRead;
                }

                ushort messageLength = (ushort)((receiveBuffer[2] << 8) | receiveBuffer[3]);
                int totalMessageLength = 20 + messageLength;

                while (totalReceived < totalMessageLength)
                {
                    int bytesToRead = Math.Min(totalMessageLength - totalReceived, receiveBuffer.Length - totalReceived);
                    bytesRead = await sslStream.ReadAsync(receiveBuffer, totalReceived, bytesToRead, queryToken).ConfigureAwait(false);
                    if (bytesRead == 0) return null;
                    totalReceived += bytesRead;
                }

                var result = ParseResponse(receiveBuffer, totalReceived, transactionId);
                if (result != null)
                {
                    result.ResponseEndPoint = serverEndpoint;
                    result.LocalEndPoint = tcpClient.Client.LocalEndPoint as IPEndPoint;
                }
                return result;
            }
            catch (SocketException sex)
            {
                Console.WriteLine($"[TLS] Socket错误 [{sex.SocketErrorCode}]: {sex.Message} → {serverEndpoint}");
                return new StunResult { ErrorMessage = sex.Message };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[TLS] 请求超时: {serverEndpoint}");
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TLS] 异常: {ex.Message}");
                return new StunResult { ErrorMessage = ex.Message };
            }
            finally
            {
                sslStream?.Close();
                try { tcpClient?.Client?.Close(0); } catch { }
                tcpClient?.Close();
                timeoutCancellation.Dispose();
            }
        }
    }

}
