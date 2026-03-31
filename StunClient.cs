using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
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

        public static StunResult Query(Socket socket, IPEndPoint serverEndpoint, bool changeIp, bool changePort)
        {
            try
            {
                // --- 1. 发送请求 ---
                byte[] fullTransactionId = Guid.NewGuid().ToByteArray();
                // STUN事务ID是16字节，但实际只用12字节
                byte[] transactionId = new byte[12];
                Array.Copy(fullTransactionId, 0, transactionId, 0, 12);
                // 调试：记录发送的目标
                Console.WriteLine($"[STUN] 发送请求到 {serverEndpoint}，changeIp={changeIp}, changePort={changePort}");

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
                socket.ReceiveTimeout = 3000; // 3秒超时

                // 【修复：根据 Socket 的地址族动态选择 Any IP】
                EndPoint senderRemote = (socket.AddressFamily == AddressFamily.InterNetworkV6)
                    ? new IPEndPoint(IPAddress.IPv6Any, 0) // V6 Any Address
                    : new IPEndPoint(IPAddress.Any, 0);    // V4 Any Address

                int len = socket.ReceiveFrom(receiveBuffer, ref senderRemote);
                // 调试：记录响应来源
                Console.WriteLine($"[STUN] 收到来自 {senderRemote} 的响应，长度: {len}");

                // --- 3. 解析所有属性 ---
                return ParseResponse(receiveBuffer, len, transactionId);
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.TimedOut)
            {
                Console.WriteLine($"[STUN] 请求超时: {serverEndpoint}");
                return null; // 超时表示无响应
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STUN] 请求异常: {ex.Message}");
                return null;
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
                // 可能是RFC3489格式的响应，没有Magic Cookie
                // 在这种情况下，事务ID从第4字节开始
                return ParseRFC3489Response(data, length);
            }

            // 验证Magic Cookie
            uint magicCookie = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
            if (magicCookie != 0x2112A442)
                return null;

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

                    if (attrType == AttributeMappedAddress || attrType == AttributeXorMappedAddress)
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
        private static StunResult ParseRFC3489Response(byte[] data, int length)
        {
            if (length < 20)
                return null;

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
        public static async Task<StunResult> QueryTcpAsync(IPEndPoint serverEndpoint, bool changeIp, bool changePort, IPEndPoint localEndPoint = null, CancellationToken cancellationToken = default)
        {
            TcpClient tcpClient = null;
            NetworkStream stream = null;

            try
            {
                // 创建 TCP 客户端
                tcpClient = new TcpClient(serverEndpoint.AddressFamily);

                // === 修复点1：先绑定本地端点，再连接 ===
                if (localEndPoint != null)
                {
                    try
                    {
                        tcpClient.Client.Bind(localEndPoint);
                        Console.WriteLine($"[TCP] 已绑定到本地端点: {localEndPoint}");
                    }
                    catch (Exception bindEx)
                    {
                        Console.WriteLine($"[TCP] 绑定本地端点失败: {bindEx.Message}");
                        // 绑定失败时不立即返回，尝试直接连接
                    }
                }

                tcpClient.SendTimeout = 4000;
                tcpClient.ReceiveTimeout = 4000;

                Console.WriteLine($"[TCP] 尝试连接到 {serverEndpoint}");

                // 使用带超时的连接
                var connectTask = tcpClient.ConnectAsync(serverEndpoint.Address, serverEndpoint.Port);
                var timeoutTask = Task.Delay(4000);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    Console.WriteLine($"[TCP] 连接超时: {serverEndpoint}");
                    // 取消连接尝试
                    tcpClient.Close();
                    return null;
                }

                // 检查连接结果
                try
                {
                    await connectTask.ConfigureAwait(false);
                }
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

                // 生成事务ID
                byte[] fullTransactionId = Guid.NewGuid().ToByteArray();
                byte[] transactionId = new byte[12];
                Array.Copy(fullTransactionId, 0, transactionId, 0, 12);

                // 构建 STUN 请求消息
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

                // 发送 STUN 请求
                await stream.WriteAsync(sendBuffer.ToArray(), 0, sendBuffer.Count, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);

                // 接收响应
                byte[] receiveBuffer = new byte[1024];
                int totalReceived = 0;
                int bytesRead;

                // 先读取消息头 (20字节)
                while (totalReceived < 20)
                {
                    bytesRead = await stream.ReadAsync(receiveBuffer, totalReceived, 20 - totalReceived, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine($"[TCP] 连接已关闭，未收到完整响应头");
                        return null;
                    }
                    totalReceived += bytesRead;
                }

                // 从消息头中获取消息长度
                ushort messageLength = (ushort)((receiveBuffer[2] << 8) | receiveBuffer[3]);
                int totalMessageLength = 20 + messageLength;

                // 读取剩余的消息内容
                while (totalReceived < totalMessageLength)
                {
                    int bytesToRead = Math.Min(totalMessageLength - totalReceived, receiveBuffer.Length - totalReceived);
                    bytesRead = await stream.ReadAsync(receiveBuffer, totalReceived, bytesToRead, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine($"[TCP] 连接已关闭，未收到完整响应体");
                        return null;
                    }
                    totalReceived += bytesRead;
                }

                Console.WriteLine($"[TCP] 收到完整响应，长度: {totalReceived}");

                // 解析响应
                var result = ParseResponse(receiveBuffer, totalReceived, transactionId);
                if (result == null)
                {
                    Console.WriteLine($"[TCP] 响应解析失败");
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP] 异常: {ex.GetType().Name} - {ex.Message}");
                return null;
            }
            finally
            {
                stream?.Close();
                tcpClient?.Close();
            }
        }

        /// <summary>
        /// TLS 协议的 STUN 查询方法
        /// </summary>
        public static async Task<StunResult> QueryTlsAsync(IPEndPoint serverEndpoint, bool changeIp, bool changePort, IPEndPoint localEndPoint = null, CancellationToken cancellationToken = default)
        {
            TcpClient tcpClient = null;
            SslStream sslStream = null;

            try
            {
                // 创建 TCP 客户端
                tcpClient = new TcpClient(serverEndpoint.AddressFamily);

                Console.WriteLine($"[TLS] 尝试连接到 {serverEndpoint}，本地端点: {localEndPoint}");

                // 先绑定本地端点，再连接
                if (localEndPoint != null)
                {
                    try
                    {
                        tcpClient.Client.Bind(localEndPoint);
                        Console.WriteLine($"[TLS] 已绑定到本地端点: {localEndPoint}");
                    }
                    catch (Exception bindEx)
                    {
                        Console.WriteLine($"[TLS] 绑定本地端点失败: {bindEx.Message}");
                        // 绑定失败时不立即返回，尝试直接连接
                    }
                }

                // TLS 连接需要更多时间
                tcpClient.SendTimeout = 4000;    // 发送超时 4秒
                tcpClient.ReceiveTimeout = 4000; // 接收超时 4秒

                // 使用带超时的连接
                var connectTask = tcpClient.ConnectAsync(serverEndpoint.Address, serverEndpoint.Port);
                var timeoutTask = Task.Delay(4000); // 连接超时 4秒
                var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    Console.WriteLine($"[TLS] 连接超时: {serverEndpoint}");
                    tcpClient.Close();
                    return null;
                }

                // 检查连接结果
                try
                {
                    await connectTask.ConfigureAwait(false);
                }
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

                // 创建 SSL 流
                sslStream = new SslStream(tcpClient.GetStream(), false,
                    (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        // 接受所有证书 - 用于测试环境
                        Console.WriteLine($"[TLS] 证书验证: {certificate?.Subject}, 错误: {sslPolicyErrors}");
                        return true;
                    });

                // TLS 握手 - 使用兼容性方法
                try
                {
                    Console.WriteLine($"[TLS] 开始TLS握手...");

                    // 兼容性修复：使用旧的 AuthenticateAsClientAsync 重载
                    await sslStream.AuthenticateAsClientAsync(serverEndpoint.Address.ToString()).ConfigureAwait(false);

                    Console.WriteLine($"[TLS] TLS握手成功");
                    Console.WriteLine($"[TLS] SSL协议: {sslStream.SslProtocol}, 是否加密: {sslStream.IsEncrypted}, 是否认证: {sslStream.IsAuthenticated}");
                }
                catch (AuthenticationException authEx)
                {
                    Console.WriteLine($"[TLS] TLS认证失败: {authEx.Message}");
                    return null;
                }
                catch (Exception authEx)
                {
                    Console.WriteLine($"[TLS] TLS握手失败: {authEx.Message}");
                    return null;
                }

                // 生成事务ID
                byte[] fullTransactionId = Guid.NewGuid().ToByteArray();
                byte[] transactionId = new byte[12];
                Array.Copy(fullTransactionId, 0, transactionId, 0, 12);

                // 构建 STUN 请求消息
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

                // 发送 STUN 请求
                Console.WriteLine($"[TLS] 发送STUN请求，长度: {sendBuffer.Count}");
                await sslStream.WriteAsync(sendBuffer.ToArray(), 0, sendBuffer.Count, cancellationToken).ConfigureAwait(false);
                await sslStream.FlushAsync().ConfigureAwait(false);

                // 接收响应
                byte[] receiveBuffer = new byte[1024];
                int totalReceived = 0;
                int bytesRead;

                // 先读取消息头 (20字节)
                Console.WriteLine($"[TLS] 等待响应头...");
                DateTime headerTimeout = DateTime.Now.AddSeconds(10);
                while (totalReceived < 20)
                {
                    if (DateTime.Now > headerTimeout)
                    {
                        Console.WriteLine($"[TLS] 读取响应头超时");
                        return null;
                    }

                    bytesRead = await sslStream.ReadAsync(receiveBuffer, totalReceived, 20 - totalReceived, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine($"[TLS] 连接已关闭，未收到完整响应头");
                        return null;
                    }
                    totalReceived += bytesRead;
                }

                // 从消息头中获取消息长度
                ushort messageLength = (ushort)((receiveBuffer[2] << 8) | receiveBuffer[3]);
                int totalMessageLength = 20 + messageLength;

                Console.WriteLine($"[TLS] 消息总长度: {totalMessageLength} (头部: 20, 内容: {messageLength})");

                // 读取剩余的消息内容
                DateTime bodyTimeout = DateTime.Now.AddSeconds(10);
                while (totalReceived < totalMessageLength)
                {
                    if (DateTime.Now > bodyTimeout)
                    {
                        Console.WriteLine($"[TLS] 读取响应体超时");
                        return null;
                    }

                    int bytesToRead = Math.Min(totalMessageLength - totalReceived, receiveBuffer.Length - totalReceived);
                    bytesRead = await sslStream.ReadAsync(receiveBuffer, totalReceived, bytesToRead, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine($"[TLS] 连接已关闭，未收到完整响应体");
                        return null;
                    }
                    totalReceived += bytesRead;
                }

                Console.WriteLine($"[TLS] 收到完整响应，长度: {totalReceived}");

                // 解析响应
                var result = ParseResponse(receiveBuffer, totalReceived, transactionId);
                if (result == null)
                {
                    Console.WriteLine($"[TLS] 响应解析失败");
                }
                else
                {
                    Console.WriteLine($"[TLS] 响应解析成功: PublicEndPoint={result.PublicEndPoint}, ChangedEndPoint={result.ChangedEndPoint}");
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[TLS] 操作被取消");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TLS] 异常: {ex.GetType().Name} - {ex.Message}");
                return null;
            }
            finally
            {
                try
                {
                    sslStream?.Close();
                    sslStream?.Dispose();
                }
                catch { }

                try
                {
                    tcpClient?.Close();
                    tcpClient?.Dispose();
                }
                catch { }
            }
        }
    }
}
