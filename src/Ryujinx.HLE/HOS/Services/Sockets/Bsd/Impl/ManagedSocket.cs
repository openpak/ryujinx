using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Proxy;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl
{
    class ManagedSocket : ISocket
    {
        public int Refcount { get; set; }

        public AddressFamily AddressFamily => Socket.AddressFamily;

        public SocketType SocketType => Socket.SocketType;

        public ProtocolType ProtocolType => Socket.ProtocolType;

        public bool Blocking { get => Socket.Blocking; set => Socket.Blocking = value; }

        public nint Handle => nint.Zero;

        public IPEndPoint RemoteEndPoint => Socket.RemoteEndPoint as IPEndPoint;

        public IPEndPoint LocalEndPoint => Socket.LocalEndPoint as IPEndPoint;

        public ISocketImpl Socket { get; }

        public ManagedSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, string lanInterfaceId)
        {
            Socket = SocketHelpers.CreateSocket(addressFamily, socketType, protocolType, lanInterfaceId);
            Refcount = 1;
        }

        private ManagedSocket(ISocketImpl socket)
        {
            Socket = socket;
            Refcount = 1;
        }

        private static SocketFlags ConvertBsdSocketFlags(BsdSocketFlags bsdSocketFlags)
        {
            SocketFlags socketFlags = SocketFlags.None;

            if (bsdSocketFlags.HasFlag(BsdSocketFlags.Oob))
            {
                socketFlags |= SocketFlags.OutOfBand;
            }

            if (bsdSocketFlags.HasFlag(BsdSocketFlags.Peek))
            {
                socketFlags |= SocketFlags.Peek;
            }

            if (bsdSocketFlags.HasFlag(BsdSocketFlags.DontRoute))
            {
                socketFlags |= SocketFlags.DontRoute;
            }

            if (bsdSocketFlags.HasFlag(BsdSocketFlags.Trunc))
            {
                socketFlags |= SocketFlags.Truncated;
            }

            if (bsdSocketFlags.HasFlag(BsdSocketFlags.CTrunc))
            {
                socketFlags |= SocketFlags.ControlDataTruncated;
            }

            bsdSocketFlags &= ~(BsdSocketFlags.Oob |
                BsdSocketFlags.Peek |
                BsdSocketFlags.DontRoute |
                BsdSocketFlags.DontWait |
                BsdSocketFlags.Trunc |
                BsdSocketFlags.CTrunc);

            if (bsdSocketFlags != BsdSocketFlags.None)
            {
                Logger.Warning?.Print(LogClass.ServiceBsd, $"Unsupported socket flags: {bsdSocketFlags}");
            }

            return socketFlags;
        }

        public LinuxError Accept(out ISocket newSocket)
        {
            try
            {
                newSocket = new ManagedSocket(Socket.Accept());

                IPEndPoint remoteEndPoint = newSocket.RemoteEndPoint;
                bool isPrivateIp = remoteEndPoint.Address.ToString().StartsWith("192.168.");
                Logger.Info?.PrintMsg(LogClass.ServiceBsd,
                    isPrivateIp
                        ? $"Accepted connection from {ProtocolType}/{remoteEndPoint.Address}:{remoteEndPoint.Port}"
                        : $"Accepted connection from {ProtocolType}/***:{remoteEndPoint.Port}");

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                newSocket = null;

                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        public LinuxError Bind(IPEndPoint localEndPoint)
        {
            Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Socket binding to: {ProtocolType}/{localEndPoint.Port}");

            // A bind to port 0 pins nothing: the kernel picks the source port at connect or send
            // time regardless. Titles bind explicitly before connecting on a socket whose family
            // we may have answered differently than they asked (an IPv6 gRPC socket over an IPv4
            // answer, for one), and failing that optional bind aborts the whole connection — so
            // it is treated as the no-op it is.
            if (localEndPoint.Port == 0)
            {
                return LinuxError.SUCCESS;
            }

            // Titles rebind the same fixed source port across retries (the NPLN resolver does it
            // on every attempt). Without reuse the second bind fails while the first socket is
            // still being torn down, and the retry dies of EADDRINUSE before it sends anything.
            // Best effort in every direction: an option the platform refuses is logged and
            // ignored, never allowed to reach the guest as a crash.
            if (ProtocolType == ProtocolType.Udp)
            {
                try
                {
                    Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                }
                catch (Exception exception)
                {
                    Logger.Debug?.Print(LogClass.ServiceBsd, $"Could not set SO_REUSEADDR: {exception.Message}");
                }
            }

            try
            {
                Socket.Bind(localEndPoint);

                return LinuxError.SUCCESS;
            }
            catch (Exception exception)
            {
                if (exception is not SocketException socketException || socketException.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Bind failed: {exception.Message}");
                }

                return exception is SocketException se
                    ? WinSockHelper.ConvertError((WsaError)se.ErrorCode)
                    : LinuxError.EINVAL;
            }
        }

        public void Close()
        {
            Socket.Close();
        }

        public LinuxError Connect(IPEndPoint remoteEndPoint)
        {
            bool isLDNPrivateIP = remoteEndPoint.Address.ToString().StartsWith("192.168.");
            if (isLDNPrivateIP)
            {
                Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Connecting to: {ProtocolType}/{remoteEndPoint.Address}:{remoteEndPoint.Port}");
            }
            else
            {
                Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Connecting to: {ProtocolType}/***:{remoteEndPoint.Port}");
            }

            try
            {
                Socket.Connect(remoteEndPoint);

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                if (!Blocking && exception.ErrorCode == (int)WsaError.WSAEWOULDBLOCK)
                {
                    return LinuxError.EINPROGRESS;
                }
                else
                {
                    if (exception.SocketErrorCode != SocketError.WouldBlock)
                    {
                        Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                    }

                    return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
                }
            }
        }

        public void Disconnect()
        {
            Logger.Info?.Print(LogClass.ServiceBsd, "Socket disconnecting");
            Socket.Disconnect(true);
        }

        public void Dispose()
        {
            Logger.Info?.Print(LogClass.ServiceBsd, "Socket closed");
            Socket.Close();
            Socket.Dispose();
        }

        public LinuxError Listen(int backlog)
        {
            try
            {
                Socket.Listen(backlog);

                Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Socket listening: {ProtocolType}/{(Socket.LocalEndPoint as IPEndPoint).Port}");

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        public bool Poll(int microSeconds, SelectMode mode)
        {
            return Socket.Poll(microSeconds, mode);
        }

        public LinuxError Shutdown(BsdSocketShutdownFlags how)
        {
            try
            {
                Socket.Shutdown((SocketShutdown)how);

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        private bool _hasEmittedBlockingWarning;

        public LinuxError Receive(out int receiveSize, Span<byte> buffer, BsdSocketFlags flags)
        {
            LinuxError result;

            bool shouldBlockAfterOperation = false;

            try
            {
                if (Blocking && flags.HasFlag(BsdSocketFlags.DontWait))
                {
                    Blocking = false;
                    shouldBlockAfterOperation = true;
                }

                if (Blocking && !_hasEmittedBlockingWarning)
                {
                    Logger.Warning?.PrintMsg(LogClass.ServiceBsd, "Blocking socket operations are not yet working properly. Expect network errors.");
                    _hasEmittedBlockingWarning = true;
                }

                receiveSize = Socket.Receive(buffer, ConvertBsdSocketFlags(flags));

                result = LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                receiveSize = -1;

                result = WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }

            if (shouldBlockAfterOperation)
            {
                Blocking = true;
            }

            return result;
        }

        public LinuxError ReceiveFrom(out int receiveSize, Span<byte> buffer, int size, BsdSocketFlags flags, out IPEndPoint remoteEndPoint)
        {
            remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            LinuxError result;

            bool shouldBlockAfterOperation = false;

            try
            {
                EndPoint temp = new IPEndPoint(IPAddress.Any, 0);

                if (Blocking && flags.HasFlag(BsdSocketFlags.DontWait))
                {
                    Blocking = false;
                    shouldBlockAfterOperation = true;
                }

                if (Blocking && !_hasEmittedBlockingWarning)
                {
                    Logger.Warning?.PrintMsg(LogClass.ServiceBsd, "Blocking socket operations are not yet working properly. Expect network errors.");
                    _hasEmittedBlockingWarning = true;
                }

                if (!Socket.IsBound)
                {
                    receiveSize = -1;

                    return LinuxError.EOPNOTSUPP;
                }

                receiveSize = Socket.ReceiveFrom(buffer[..size], ConvertBsdSocketFlags(flags), ref temp);

                remoteEndPoint = (IPEndPoint)temp;
                result = LinuxError.SUCCESS;

                Logger.Debug?.Print(LogClass.ServiceBsd,
                    $"ReceiveFrom: {receiveSize} bytes from {remoteEndPoint}");
            }
            catch (SocketException exception)
            {
                // WouldBlock included: a probe reply that never arrives must not be silent, or a
                // title waiting on its own NAT check looks like a server that never answered.
                Logger.Debug?.Print(LogClass.ServiceBsd,
                    $"ReceiveFrom failed: {exception.SocketErrorCode}");

                receiveSize = -1;

                result = WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }

            if (shouldBlockAfterOperation)
            {
                Blocking = true;
            }

            return result;
        }

        public LinuxError Send(out int sendSize, ReadOnlySpan<byte> buffer, BsdSocketFlags flags)
        {
            try
            {
                sendSize = Socket.Send(buffer, ConvertBsdSocketFlags(flags));

                Logger.Debug?.Print(LogClass.ServiceBsd, $"Send: {sendSize} bytes on connected socket");

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                // WouldBlock included: a send that never happens must not be silent, or a title
                // that stalls waiting on its own packet looks like a server that never answered.
                Logger.Debug?.Print(LogClass.ServiceBsd,
                    $"Send failed: {exception.SocketErrorCode} ({buffer.Length} bytes held back)");

                sendSize = -1;

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        public LinuxError SendTo(out int sendSize, ReadOnlySpan<byte> buffer, int size, BsdSocketFlags flags, IPEndPoint remoteEndPoint)
        {
            try
            {
                sendSize = Socket.SendTo(buffer[..size], ConvertBsdSocketFlags(flags), remoteEndPoint);

                Logger.Debug?.Print(LogClass.ServiceBsd,
                    $"SendTo: {sendSize} bytes to {remoteEndPoint}");

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                // Same rule as Send: a datagram that never leaves, for any reason, is one line
                // in the log rather than a title that connects to nothing.
                Logger.Debug?.Print(LogClass.ServiceBsd,
                    $"SendTo {remoteEndPoint} failed: {exception.SocketErrorCode} ({size} bytes held back)");

                sendSize = -1;

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        public LinuxError GetSocketOption(BsdSocketOption option, SocketOptionLevel level, Span<byte> optionValue)
        {
            try
            {
                LinuxError result = WinSockHelper.ValidateSocketOption(option, level, write: false);

                if (result != LinuxError.SUCCESS)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Invalid GetSockOpt Option: {option} Level: {level}");

                    return result;
                }

                if (!WinSockHelper.TryConvertSocketOption(option, level, out SocketOptionName optionName))
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Unsupported GetSockOpt Option: {option} Level: {level}");
                    optionValue.Clear();

                    return LinuxError.EOPNOTSUPP;
                }

                byte[] tempOptionValue = new byte[optionValue.Length];

                Socket.GetSocketOption(level, optionName, tempOptionValue);

                tempOptionValue.AsSpan().CopyTo(optionValue);

                // The connect-completion check reads SO_ERROR right after a poll says the socket
                // is writable; what this returns decides whether the title writes its first byte
                // or hangs up. Worth seeing in the log.
                if (optionName == SocketOptionName.Error)
                {
                    int socketError = optionValue.Length >= 4 ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(optionValue) : -1;

                    Logger.Info?.Print(LogClass.ServiceBsd, $"[Bsd] SO_ERROR read: {socketError}");
                }

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        public LinuxError SetSocketOption(BsdSocketOption option, SocketOptionLevel level, ReadOnlySpan<byte> optionValue)
        {
            try
            {
                LinuxError result = WinSockHelper.ValidateSocketOption(option, level, write: true);

                if (result != LinuxError.SUCCESS)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Invalid SetSockOpt Option: {option} Level: {level}");

                    return result;
                }

                if (!WinSockHelper.TryConvertSocketOption(option, level, out SocketOptionName optionName))
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Unsupported SetSockOpt Option: {option} Level: {level}");

                    return LinuxError.EOPNOTSUPP;
                }

                int value = optionValue.Length >= 4 ? MemoryMarshal.Read<int>(optionValue) : MemoryMarshal.Read<byte>(optionValue);

                if (level == SocketOptionLevel.Socket && option == BsdSocketOption.SoLinger)
                {
                    int value2 = 0;

                    if (optionValue.Length >= 8)
                    {
                        value2 = MemoryMarshal.Read<int>(optionValue[4..]);
                    }

                    Socket.SetSocketOption(level, SocketOptionName.Linger, new LingerOption(value != 0, value2));
                }
                else
                {
                    Socket.SetSocketOption(level, optionName, value);
                }

                return LinuxError.SUCCESS;
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        public LinuxError Read(out int readSize, Span<byte> buffer)
        {
            return Receive(out readSize, buffer, BsdSocketFlags.None);
        }

        public LinuxError Write(out int writeSize, ReadOnlySpan<byte> buffer)
        {
            return Send(out writeSize, buffer, BsdSocketFlags.None);
        }

        private bool CanSupportMMsgHdr(BsdMMsgHdr message)
        {
            for (int i = 0; i < message.Messages.Length; i++)
            {
                if (message.Messages[i].Name != null ||
                    message.Messages[i].Control != null)
                {
                    return false;
                }
            }

            return true;
        }

        private static ArraySegment<byte>[] ConvertMessagesToBuffer(BsdMMsgHdr message)
        {
            int segmentCount = 0;
            int index = 0;

            foreach (BsdMsgHdr msgHeader in message.Messages)
            {
                segmentCount += msgHeader.Iov.Length;
            }

            ArraySegment<byte>[] buffers = new ArraySegment<byte>[segmentCount];

            foreach (BsdMsgHdr msgHeader in message.Messages)
            {
                foreach (byte[] iov in msgHeader.Iov)
                {
                    buffers[index++] = new ArraySegment<byte>(iov);
                }

                // Clear the length
                msgHeader.Length = 0;
            }

            return buffers;
        }

        private static void UpdateMessages(out int vlen, BsdMMsgHdr message, int transferedSize)
        {
            int bytesLeft = transferedSize;
            int index = 0;

            while (bytesLeft > 0)
            {
                // First ensure we haven't finished all buffers
                if (index >= message.Messages.Length)
                {
                    break;
                }

                BsdMsgHdr msgHeader = message.Messages[index];

                int possiblyTransferedBytes = 0;

                foreach (byte[] iov in msgHeader.Iov)
                {
                    possiblyTransferedBytes += iov.Length;
                }

                int storedBytes;

                if (bytesLeft > possiblyTransferedBytes)
                {
                    storedBytes = possiblyTransferedBytes;
                    index++;
                }
                else
                {
                    storedBytes = bytesLeft;
                }

                msgHeader.Length = (uint)storedBytes;
                bytesLeft -= storedBytes;
            }

            Debug.Assert(bytesLeft == 0);

            vlen = index + 1;
        }

        // TODO: Find a way to support passing the timeout somehow without changing the socket ReceiveTimeout.
        public LinuxError RecvMMsg(out int vlen, BsdMMsgHdr message, BsdSocketFlags flags, TimeVal timeout)
        {
            vlen = 0;

            if (message.Messages.Length == 0)
            {
                return LinuxError.SUCCESS;
            }

            if (!CanSupportMMsgHdr(message))
            {
                Logger.Warning?.Print(LogClass.ServiceBsd, "Unsupported BsdMMsgHdr");

                return LinuxError.EOPNOTSUPP;
            }

            if (message.Messages.Length == 0)
            {
                return LinuxError.SUCCESS;
            }

            try
            {
                int receiveSize = (Socket as DefaultSocket).BaseSocket.Receive(ConvertMessagesToBuffer(message), ConvertBsdSocketFlags(flags), out SocketError socketError);

                if (receiveSize > 0)
                {
                    UpdateMessages(out vlen, message, receiveSize);
                }

                return WinSockHelper.ConvertError((WsaError)socketError);
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }

        public LinuxError SendMMsg(out int vlen, BsdMMsgHdr message, BsdSocketFlags flags)
        {
            vlen = 0;

            if (message.Messages.Length == 0)
            {
                return LinuxError.SUCCESS;
            }

            if (!CanSupportMMsgHdr(message))
            {
                Logger.Warning?.Print(LogClass.ServiceBsd, "Unsupported BsdMMsgHdr");

                return LinuxError.EOPNOTSUPP;
            }

            if (message.Messages.Length == 0)
            {
                return LinuxError.SUCCESS;
            }

            try
            {
                int sendSize = (Socket as DefaultSocket).BaseSocket.Send(ConvertMessagesToBuffer(message), ConvertBsdSocketFlags(flags), out SocketError socketError);

                if (sendSize > 0)
                {
                    UpdateMessages(out vlen, message, sendSize);
                }

                return WinSockHelper.ConvertError((WsaError)socketError);
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Socket Exception: {exception}");
                }

                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }
        }
    }
}
