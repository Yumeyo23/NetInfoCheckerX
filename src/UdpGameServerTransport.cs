using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetInfoCheckerX
{
    // 通配监听仍须从请求的目的 IP 回包，不能让系统重新选择临时地址。
    internal sealed class UdpGameServerTransport : IDisposable
    {
        internal sealed class ReplyRoute
        {
            internal readonly byte[] RemoteAddress;
            internal readonly byte[] ControlData;

            internal ReplyRoute(byte[] remoteAddress, byte[] controlData)
            {
                RemoteAddress = remoteAddress;
                ControlData = controlData;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBuffer
        {
            internal uint Length;
            internal IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            internal IntPtr Name;
            internal int NameLength;
            internal IntPtr Buffers;
            internal uint BufferCount;
            internal NativeBuffer Control;
            internal uint Flags;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SendMessageDelegate(IntPtr socket, ref NativeMessage message,
            uint flags, out uint sent, IntPtr overlapped, IntPtr completionRoutine);

        [DllImport("ws2_32.dll")]
        private static extern int WSAGetLastError();

        private readonly Socket socket;
        private readonly SendMessageDelegate sendMessage;
        private readonly object sendLock = new object();
        private bool disposed;

        internal AddressFamily AddressFamily { get { return socket.AddressFamily; } }

        internal UdpGameServerTransport(Socket socket)
        {
            this.socket = socket;
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
            if (socket.AddressFamily == AddressFamily.InterNetwork)
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);

            byte[] functionPointer = new byte[IntPtr.Size];
            socket.IOControl(IOControlCode.GetExtensionFunctionPointer,
                new Guid("a441e712-754f-43ca-84a7-0dee44cf606d").ToByteArray(), functionPointer);
            IntPtr address = IntPtr.Size == 8
                ? new IntPtr(BitConverter.ToInt64(functionPointer, 0))
                : new IntPtr(BitConverter.ToInt32(functionPointer, 0));
            if (address == IntPtr.Zero) throw new SocketException((int)SocketError.OperationNotSupported);
            sendMessage = (SendMessageDelegate)Marshal.GetDelegateForFunctionPointer(address, typeof(SendMessageDelegate));
        }

        internal int Receive(byte[] buffer, ref EndPoint remote, out IPPacketInformation local)
        {
            SocketFlags flags = SocketFlags.None;
            return socket.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref remote, out local);
        }

        internal ReplyRoute CreateReplyRoute(IPEndPoint remote, IPPacketInformation local)
        {
            IPAddress source = local.Address;
            if (source == null || source.Equals(IPAddress.Any) || source.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("无法识别客户端访问的本机 IP，未发送握手回复。");
            if (source.IsIPv4MappedToIPv6) source = source.MapToIPv4();
            if (socket.AddressFamily == AddressFamily.InterNetworkV6 && remote.AddressFamily == AddressFamily.InterNetwork)
                remote = new IPEndPoint(remote.Address.MapToIPv6(), remote.Port);

            SocketAddress endpoint = remote.Serialize();
            byte[] destination = new byte[endpoint.Size];
            for (int i = 0; i < destination.Length; i++) destination[i] = endpoint[i];

            byte[] sourceBytes = source.GetAddressBytes();
            int headerSize = IntPtr.Size + 8; // SIZE_T cmsg_len, INT cmsg_level, INT cmsg_type
            int packetInfoSize = sourceBytes.Length + 4; // 地址后紧随 ULONG interface index
            int length = headerSize + packetInfoSize;
            byte[] control = new byte[Align(length)];
            byte[] size = IntPtr.Size == 8 ? BitConverter.GetBytes((long)length) : BitConverter.GetBytes(length);
            Buffer.BlockCopy(size, 0, control, 0, size.Length);
            int level = source.AddressFamily == AddressFamily.InterNetwork ? (int)SocketOptionLevel.IP : (int)SocketOptionLevel.IPv6;
            Buffer.BlockCopy(BitConverter.GetBytes(level), 0, control, IntPtr.Size, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)SocketOptionName.PacketInformation), 0, control, IntPtr.Size + 4, 4);
            Buffer.BlockCopy(sourceBytes, 0, control, headerSize, sourceBytes.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(local.Interface), 0, control, headerSize + sourceBytes.Length, 4);
            return new ReplyRoute(destination, control);
        }

        internal int Send(byte[] bytes, ReplyRoute route)
        {
            // ReceiveMessageFrom 与发送可以并行；关闭需等待同步原生发送完成。
            lock (sendLock)
            {
                if (disposed) throw new ObjectDisposedException(nameof(UdpGameServerTransport));
                int bufferSize = Marshal.SizeOf(typeof(NativeBuffer));
                IntPtr native = Marshal.AllocHGlobal(bufferSize + Align(route.RemoteAddress.Length) + route.ControlData.Length);
                GCHandle pinned = default(GCHandle);
                try
                {
                    IntPtr name = IntPtr.Add(native, bufferSize);
                    IntPtr control = IntPtr.Add(name, Align(route.RemoteAddress.Length));
                    Marshal.Copy(route.RemoteAddress, 0, name, route.RemoteAddress.Length);
                    Marshal.Copy(route.ControlData, 0, control, route.ControlData.Length);
                    pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    Marshal.StructureToPtr(new NativeBuffer
                    {
                        Length = (uint)bytes.Length,
                        Buffer = pinned.AddrOfPinnedObject()
                    }, native, false);
                    var message = new NativeMessage
                    {
                        Name = name,
                        NameLength = route.RemoteAddress.Length,
                        Buffers = native,
                        BufferCount = 1,
                        Control = new NativeBuffer { Length = (uint)route.ControlData.Length, Buffer = control }
                    };
                    uint sent;
                    if (sendMessage(socket.Handle, ref message, 0, out sent, IntPtr.Zero, IntPtr.Zero) != 0)
                        throw new SocketException(WSAGetLastError());
                    return checked((int)sent);
                }
                finally
                {
                    if (pinned.IsAllocated) pinned.Free();
                    Marshal.FreeHGlobal(native);
                    GC.KeepAlive(socket);
                }
            }
        }

        private static int Align(int length)
        {
            return (length + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
        }

        public void Dispose()
        {
            lock (sendLock)
            {
                if (disposed) return;
                disposed = true;
                socket.Dispose();
            }
        }
    }
}
