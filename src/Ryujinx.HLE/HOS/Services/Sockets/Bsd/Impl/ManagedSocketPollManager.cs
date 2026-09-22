using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Proxy;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl
{
    class ManagedSocketPollManager : IPollManager
    {
        private static ManagedSocketPollManager _instance;

        public static ManagedSocketPollManager Instance
        {
            get
            {
                _instance ??= new ManagedSocketPollManager();

                return _instance;
            }
        }

        public bool IsCompatible(PollEvent evnt)
        {
            return evnt.FileDescriptor is ManagedSocket;
        }

        // A socket with a non-blocking connect still in progress is neither
        // ready nor failed. Reporting Error/Disconnected for it makes titles
        // abandon dials that would have completed (D2R-services stall: the
        // guest reads SO_ERROR 0, gets Disconnected from poll, and never
        // writes its TLS hello). Only a real failure (pending SO_ERROR != 0)
        // counts as failed; anything else keeps waiting.
        private static bool HasFailed(ISocketImpl socket)
        {
            if (socket is DefaultSocket dsocket)
            {
                try
                {
                    return (int)dsocket.BaseSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) != 0;
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
                {
                    return true;
                }
            }

            return true;
        }

        public LinuxError Poll(List<PollEvent> events, int timeoutMilliseconds, out int updatedCount)
        {
            List<ISocketImpl> readEvents = [];
            List<ISocketImpl> writeEvents = [];
            List<ISocketImpl> errorEvents = [];

            updatedCount = 0;

            foreach (PollEvent evnt in events)
            {
                if (evnt.FileDescriptor is ManagedSocket ms)
                {
                    bool isValidEvent = evnt.Data.InputEvents == 0;

                    errorEvents.Add(ms.Socket);

                    if ((evnt.Data.InputEvents & PollEventTypeMask.Input) != 0)
                    {
                        readEvents.Add(ms.Socket);

                        isValidEvent = true;
                    }

                    if ((evnt.Data.InputEvents & PollEventTypeMask.UrgentInput) != 0)
                    {
                        readEvents.Add(ms.Socket);

                        isValidEvent = true;
                    }

                    if ((evnt.Data.InputEvents & PollEventTypeMask.Output) != 0)
                    {
                        writeEvents.Add(ms.Socket);

                        isValidEvent = true;
                    }

                    if (!isValidEvent)
                    {
                        Logger.Warning?.Print(LogClass.ServiceBsd, $"Unsupported Poll input event type: {evnt.Data.InputEvents}");
                        return LinuxError.EINVAL;
                    }
                }
            }

            try
            {
                int actualTimeoutMicroseconds = timeoutMilliseconds == -1 ? -1 : timeoutMilliseconds * 1000;

                SocketHelpers.Select(readEvents, writeEvents, errorEvents, actualTimeoutMicroseconds);
            }
            catch (SocketException exception)
            {
                return WinSockHelper.ConvertError((WsaError)exception.ErrorCode);
            }

            foreach (PollEvent evnt in events)
            {
                if (evnt.FileDescriptor is ManagedSocket ms)
                {
                    ISocketImpl socket = ms.Socket;

                    PollEventTypeMask outputEvents = evnt.Data.OutputEvents & ~evnt.Data.InputEvents;

                    if (errorEvents.Contains(ms.Socket) && HasFailed(ms.Socket))
                    {
                        outputEvents |= PollEventTypeMask.Error;

                        // POSIX may report an ICMP port-unreachable error for a UDP
                        // hole-punch probe. A datagram socket is never "connected"
                        // in the managed sense, but that is not a peer disconnect
                        // and must not be exposed to Pia as POLLHUP: during NAT
                        // traversal the first probes routinely land on a
                        // still-closed port, and treating that as a disconnect
                        // kills the mesh join (Mario Golf: join dies, host hangs,
                        // joiner gets EndParticipation). Ported from
                        // NextendoNetwork/Ryujinx-Nextendo PR #28.
                        if (socket.SocketType == SocketType.Stream &&
                            (!socket.Connected || !socket.IsBound))
                        {
                            outputEvents |= PollEventTypeMask.Disconnected;
                        }
                    }

                    if (readEvents.Contains(ms.Socket))
                    {
                        if ((evnt.Data.InputEvents & PollEventTypeMask.Input) != 0)
                        {
                            outputEvents |= PollEventTypeMask.Input;
                        }
                    }

                    if (writeEvents.Contains(ms.Socket))
                    {
                        outputEvents |= PollEventTypeMask.Output;
                    }

                    evnt.Data.OutputEvents = outputEvents;

                    if (ms.PendingRemoteEndPoint != null)
                    {
                        Logger.Info?.Print(LogClass.ServiceBsd,
                            $"Poll(pending {ms.PendingRemoteEndPoint}): in={evnt.Data.InputEvents} out={outputEvents} timeout={timeoutMilliseconds}ms");
                    }
                }
            }

            updatedCount = readEvents.Count + writeEvents.Count + errorEvents.Count;

            return LinuxError.SUCCESS;
        }

        public LinuxError Select(List<PollEvent> events, int timeout, out int updatedCount)
        {
            List<ISocketImpl> readEvents = [];
            List<ISocketImpl> writeEvents = [];
            List<ISocketImpl> errorEvents = [];

            updatedCount = 0;

            foreach (PollEvent pollEvent in events)
            {
                if (pollEvent.FileDescriptor is ManagedSocket ms)
                {
                    if (pollEvent.Data.InputEvents.HasFlag(PollEventTypeMask.Input))
                    {
                        readEvents.Add(ms.Socket);
                    }

                    if (pollEvent.Data.InputEvents.HasFlag(PollEventTypeMask.Output))
                    {
                        writeEvents.Add(ms.Socket);
                    }

                    if (pollEvent.Data.InputEvents.HasFlag(PollEventTypeMask.Error))
                    {
                        errorEvents.Add(ms.Socket);
                    }
                }
            }

            SocketHelpers.Select(readEvents, writeEvents, errorEvents, timeout);

            updatedCount = readEvents.Count + writeEvents.Count + errorEvents.Count;

            foreach (PollEvent pollEvent in events)
            {
                if (pollEvent.FileDescriptor is ManagedSocket ms)
                {
                    if (readEvents.Contains(ms.Socket))
                    {
                        pollEvent.Data.OutputEvents |= PollEventTypeMask.Input;
                    }

                    if (writeEvents.Contains(ms.Socket))
                    {
                        pollEvent.Data.OutputEvents |= PollEventTypeMask.Output;
                    }

                    if (errorEvents.Contains(ms.Socket))
                    {
                        if (HasFailed(ms.Socket))
                        {
                            pollEvent.Data.OutputEvents |= PollEventTypeMask.Error;
                        }
                    }
                }
            }

            return LinuxError.SUCCESS;
        }
    }
}
