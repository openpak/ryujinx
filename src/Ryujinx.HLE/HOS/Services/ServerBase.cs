using Microsoft.IO;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Ipc;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon;
using Ryujinx.Horizon.Common;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services
{
    class ServerBase : IDisposable
    {
        // Must be the maximum value used by services (highest one know is the one used by nvservices = 0x8000).
        // Having a size that is too low will cause failures as data copy will fail if the receiving buffer is
        // not large enough.
        private const int PointerBufferSize = 0x8000;

        // Deferred Bsd Poll support. A poll that would block parks itself and the request lands
        // here; every pass of the server loop re-runs parked polls non-blocking and replies to the
        // ones that are ready or overdue. The single Bsd thread never sits inside a wait that only
        // the next eventfd write could end.
        private sealed class DeferredPoll
        {
            public int SessionHandle;
            public IpcMessage Request;
            public ulong RecvListAddr;
            public long DeadlineMs;
            public byte[] InputSnapshot;
        }

        private readonly List<DeferredPoll> _deferredPolls = [];
        private readonly object _deferredPollsLock = new();
        private ulong _heapAddr;
        private ulong _tlsAddress;

        private static uint[] DefaultCapabilities => [
            (((uint)KScheduler.CpuCoresCount - 1) << 24) + (((uint)KScheduler.CpuCoresCount - 1) << 16) + 0x63F7u,
            0x1FFFFFCF,
            0x207FFFEF,
            0x47E0060F,
            0x0048BFFF,
            0x01007FFF,
        ];

        // The amount of time Dispose() will wait to Join() the thread executing the ServerLoop()
        private static readonly TimeSpan _threadJoinTimeout = TimeSpan.FromSeconds(3);

        private readonly KernelContext _context;
        private KProcess _selfProcess;
        private KThread _selfThread;
        private KEvent _wakeEvent;
        private int _wakeHandle = 0;

        private readonly ReaderWriterLockSlim _handleLock = new();
        private readonly Dictionary<int, IpcService> _sessions = new();
        private readonly Dictionary<int, Func<IpcService>> _ports = new();

        private readonly RecyclableMemoryStream _requestDataStream;
        private readonly BinaryReader _requestDataReader;

        private readonly RecyclableMemoryStream _responseDataStream;
        private readonly BinaryWriter _responseDataWriter;

        private int _isDisposed = 0;

        public ManualResetEvent InitDone { get; }
        public string Name { get; }
        public Func<IpcService> SmObjectFactory { get; }

        public ServerBase(KernelContext context, string name, Func<IpcService> smObjectFactory = null)
        {
            _context = context;

            _requestDataStream = MemoryStreamManager.Shared.GetStream();
            _requestDataReader = new BinaryReader(_requestDataStream);

            _responseDataStream = MemoryStreamManager.Shared.GetStream();
            _responseDataWriter = new BinaryWriter(_responseDataStream);

            InitDone = new ManualResetEvent(false);
            Name = name;
            SmObjectFactory = smObjectFactory;

            // Only the Bsd server defers polls, so only it needs to know that an event fd was
            // written: that write is the wakeup of a parked poll, and the loop must re-check it
            // now instead of after whichever unrelated IPC happens to come next.
            if (name == "Bsd")
            {
                Sockets.Bsd.Impl.EventFileDescriptor.OnAnyWrite += SignalWake;
            }

            const ProcessCreationFlags Flags =
                ProcessCreationFlags.EnableAslr |
                ProcessCreationFlags.AddressSpace64Bit |
                ProcessCreationFlags.Is64Bit |
                ProcessCreationFlags.PoolPartitionSystem;

            ProcessCreationInfo creationInfo = new(Name, 1, 0, 0x8000000, 1, Flags, 0, 0);

            KernelStatic.StartInitialProcess(context, creationInfo, DefaultCapabilities, 44, () =>
            {
                var currentThread = KernelStatic.GetCurrentThread();
                currentThread.HostThread.Name = $"{{{Name}}}";

                Main();
            });
        }

        private void AddPort(int serverPortHandle, Func<IpcService> objectFactory)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);

                _ports.Add(serverPortHandle, objectFactory);
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitWriteLock();
                }
            }
        }

        public void AddSessionObj(KServerSession serverSession, IpcService obj)
        {
            // Ensure that the sever loop is running.
            InitDone.WaitOne();

            _selfProcess.HandleTable.GenerateHandle(serverSession, out int serverSessionHandle);

            AddSessionObj(serverSessionHandle, obj);
        }

        public void AddSessionObj(int serverSessionHandle, IpcService obj)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);

                _sessions.Add(serverSessionHandle, obj);
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitWriteLock();
                }
            }

            _wakeEvent.WritableEvent.Signal();
        }

        private IpcService GetSessionObj(int serverSessionHandle)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterReadLock(Timeout.Infinite);

                return _sessions[serverSessionHandle];
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitReadLock();
                }
            }
        }

        private bool RemoveSessionObj(int serverSessionHandle, out IpcService obj)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);

                return _sessions.Remove(serverSessionHandle, out obj);
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitWriteLock();
                }
            }
        }

        private void Main()
        {
            ServerLoop();
        }

        private void ServerLoop()
        {
            _selfProcess = KernelStatic.GetCurrentProcess();
            _selfThread = KernelStatic.GetCurrentThread();

            HorizonStatic.Register(
                default,
                _context.Syscall,
                _selfProcess.CpuMemory,
                _selfThread.ThreadContext,
                (int)_selfThread.ThreadContext.GetX(1));

            if (SmObjectFactory != null)
            {
                _context.Syscall.ManageNamedPort(out int serverPortHandle, "sm:", 50);

                AddPort(serverPortHandle, SmObjectFactory);
            }

            _wakeEvent = new KEvent(_context);
            Result result = _selfProcess.HandleTable.GenerateHandle(_wakeEvent.ReadableEvent, out _wakeHandle);

            InitDone.Set();

            ulong messagePtr = _selfThread.TlsAddress;
            _context.Syscall.SetHeapSize(out ulong heapAddr, 0x200000);

            // Kept for the deferred-poll reply path, which must re-arm the session's receive-list
            // descriptor from outside the normal reply flow.
            _heapAddr = heapAddr;
            _tlsAddress = messagePtr;

            _selfProcess.CpuMemory.Write(messagePtr + 0x0, 0);
            _selfProcess.CpuMemory.Write(messagePtr + 0x4, 2 << 10);
            _selfProcess.CpuMemory.Write(messagePtr + 0x8, heapAddr | ((ulong)PointerBufferSize << 48));
            int replyTargetHandle = 0;

            while (true)
            {
                // Re-check parked polls first: one whose socket or event fd is now ready replies
                // here, and one whose deadline has passed answers with a timeout.
                CompleteReadyDeferredPolls(ref replyTargetHandle);

                int portHandleCount;
                int handleCount;
                int[] handles;

                bool handleLockTaken = false;
                try
                {
                    handleLockTaken = _handleLock.TryEnterReadLock(Timeout.Infinite);

                    portHandleCount = _ports.Count;

                    handleCount = portHandleCount + _sessions.Count + 1;

                    handles = ArrayPool<int>.Shared.Rent(handleCount);

                    handles[0] = _wakeHandle;

                    _ports.Keys.CopyTo(handles, 1);

                    _sessions.Keys.CopyTo(handles, portHandleCount + 1);
                }
                finally
                {
                    if (handleLockTaken)
                    {
                        _handleLock.ExitReadLock();
                    }
                }

                Result rc = _context.Syscall.ReplyAndReceive(out int signaledIndex, handles.AsSpan(0, handleCount), replyTargetHandle, -1);

                _selfThread.HandlePostSyscall();

                if (!_selfThread.Context.Running)
                {
                    break;
                }

                replyTargetHandle = 0;

                if (rc == Result.Success && signaledIndex >= portHandleCount + 1)
                {
                    // We got a IPC request, process it, pass to the appropriate service if needed.
                    int signaledHandle = handles[signaledIndex];

                    if (Process(signaledHandle, heapAddr))
                    {
                        replyTargetHandle = signaledHandle;
                    }
                }
                else
                {
                    if (rc == Result.Success)
                    {
                        if (signaledIndex > 0)
                        {
                            // We got a new connection, accept the session to allow servicing future requests.
                            if (_context.Syscall.AcceptSession(out int serverSessionHandle, handles[signaledIndex]) == Result.Success)
                            {
                                bool handleWriteLockTaken = false;
                                try
                                {
                                    handleWriteLockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);
                                    IpcService obj = _ports[handles[signaledIndex]].Invoke();
                                    _sessions.Add(serverSessionHandle, obj);
                                }
                                finally
                                {
                                    if (handleWriteLockTaken)
                                    {
                                        _handleLock.ExitWriteLock();
                                    }
                                }
                            }
                        }
                        else
                        {
                            // The _wakeEvent signalled, which means we have a new session.
                            _wakeEvent.WritableEvent.Clear();
                        }
                    }
                    else if (rc == KernelResult.PortRemoteClosed && signaledIndex >= 0 && SmObjectFactory != null)
                    {
                        DestroySession(handles[signaledIndex]);
                    }

                    _selfProcess.CpuMemory.Write(messagePtr + 0x0, 0);
                    _selfProcess.CpuMemory.Write(messagePtr + 0x4, 2 << 10);
                    _selfProcess.CpuMemory.Write(messagePtr + 0x8, heapAddr | ((ulong)PointerBufferSize << 48));
                }

                ArrayPool<int>.Shared.Return(handles);
            }

            Dispose();
        }

        /// <summary>
        /// Answer parked polls whose socket is ready or whose deadline has passed.
        ///
        /// Each parked poll is re-run non-blocking against the snapshot of its pollfd array taken
        /// when it was parked — never against guest memory, where that array has long since been
        /// overwritten by other IPC. When a poll completes, the fresh response must carry the
        /// original request's pointer-buffer descriptors again: a response built from scratch has
        /// none, the kernel would copy nothing, and the game would see a poll that says "ready"
        /// with zeroed events — which is the poll storm that wedges gRPC for good.
        /// </summary>
        private void CompleteReadyDeferredPolls(ref int replyTargetHandle)
        {
            lock (_deferredPollsLock)
            {
                if (_deferredPolls.Count == 0)
                {
                    return;
                }
            }

            // Re-checks also write into the shared IPC pointer buffer. Deliver
            // the previous reply before reusing its output memory.
            if (replyTargetHandle != 0)
            {
                _context.Syscall.ReplyAndReceive(out _, ReadOnlySpan<int>.Empty, replyTargetHandle, 0);
                _selfThread.HandlePostSyscall();
                replyTargetHandle = 0;
            }

            lock (_deferredPollsLock)
            {
                for (int i = _deferredPolls.Count - 1; i >= 0; i--)
                {
                    DeferredPoll deferred = _deferredPolls[i];

                    IpcMessage response = new() { Type = IpcMessageType.CmifResponse };

                    _requestDataStream.SetLength(0);
                    _requestDataStream.Write(deferred.Request.RawData);
                    _requestDataStream.Position = 0;
                    _responseDataStream.SetLength(0);

                    ServiceCtx context = new(
                        _context.Device,
                        _selfProcess,
                        _selfProcess.CpuMemory,
                        _selfThread,
                        deferred.Request,
                        response,
                        _requestDataReader,
                        _responseDataWriter)
                    {
                        PollForceNonBlocking = true,
                        PollInputSnapshot = deferred.InputSnapshot,
                    };

                    GetSessionObj(deferred.SessionHandle).CallCmifMethod(context);

                    bool deadlineElapsed = PerformanceCounter.ElapsedMilliseconds >= deferred.DeadlineMs;

                    if (context.PollResult > 0 || deadlineElapsed)
                    {
                        response.RawData = _responseDataStream.ToArray();

                        for (int b = 0; b < deferred.Request.RecvListBuff.Count; b++)
                        {
                            IpcRecvListBuffDesc buf = deferred.Request.RecvListBuff[b];

                            if (buf.Position != 0 && buf.Size != 0)
                            {
                                response.PtrBuff.Add(new IpcPtrBuffDesc(buf.Position, (uint)b, buf.Size));
                            }
                        }

                        // Deliver each poll before another re-check overwrites
                        // the shared output buffer used by this response.
                        RecyclableMemoryStream responseStream = response.GetStream(
                            (long)_tlsAddress,
                            deferred.RecvListAddr | ((ulong)PointerBufferSize << 48));

                        _selfProcess.CpuMemory.Write(_tlsAddress, responseStream.GetReadOnlySequence());
                        MemoryStreamManager.Shared.ReleaseStream(responseStream);
                        _context.Syscall.ReplyAndReceive(out _, ReadOnlySpan<int>.Empty, deferred.SessionHandle, 0);
                        _selfThread.HandlePostSyscall();

                        _deferredPolls.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// Write a neutral CMIF message header into the thread's TLS. Deferring skips the normal
        /// reply, and that reply is what re-publishes the session's receive-list descriptor; this
        /// re-arms it explicitly so the next request carrying pointer buffers does not fail its
        /// kernel copy.
        /// </summary>
        private void RearmReceiveList()
        {
            ulong messagePtr = _tlsAddress;

            _selfProcess.CpuMemory.Write(messagePtr + 0x0, 0);
            _selfProcess.CpuMemory.Write(messagePtr + 0x4, 2 << 10);
            _selfProcess.CpuMemory.Write(messagePtr + 0x8, _heapAddr | ((ulong)PointerBufferSize << 48));
        }

        private void SignalWake()
        {
            _wakeEvent?.WritableEvent.Signal();
        }

        private void DestroySession(int serverSessionHandle)
        {
            _context.Syscall.CloseHandle(serverSessionHandle);

            if (RemoveSessionObj(serverSessionHandle, out IpcService session))
            {
                (session as IDisposable)?.Dispose();
            }
        }

        private bool Process(int serverSessionHandle, ulong recvListAddr)
        {
            IpcMessage request = ReadRequest();

            IpcMessage response = new();

            ulong tempAddr = recvListAddr;
            int sizesOffset = request.RawData.Length - ((request.RecvListBuff.Count * 2 + 3) & ~3);

            bool noReceive = true;

            for (int i = 0; i < request.ReceiveBuff.Count; i++)
            {
                noReceive &= (request.ReceiveBuff[i].Position == 0);
            }

            if (noReceive)
            {
                response.PtrBuff.EnsureCapacity(request.RecvListBuff.Count);

                for (int i = 0; i < request.RecvListBuff.Count; i++)
                {
                    ulong size = (ulong)BinaryPrimitives.ReadInt16LittleEndian(request.RawData.AsSpan(sizesOffset + i * 2, 2));

                    response.PtrBuff.Add(new IpcPtrBuffDesc(tempAddr, (uint)i, size));

                    request.RecvListBuff[i] = new IpcRecvListBuffDesc(tempAddr, size);

                    tempAddr += size;
                }
            }

            bool shouldReply = true;
            bool isTipcCommunication = false;

            _requestDataStream.SetLength(0);
            _requestDataStream.Write(request.RawData);
            _requestDataStream.Position = 0;

            if (request.Type is IpcMessageType.CmifRequest or
                IpcMessageType.CmifRequestWithContext)
            {
                response.Type = IpcMessageType.CmifResponse;

                _responseDataStream.SetLength(0);

                ServiceCtx context = new(
                    _context.Device,
                    _selfProcess,
                    _selfProcess.CpuMemory,
                    _selfThread,
                    request,
                    response,
                    _requestDataReader,
                    _responseDataWriter);

                GetSessionObj(serverSessionHandle).CallCmifMethod(context);

                // The Bsd poll handler parks a poll that would block: the request is registered by
                // the loop and answered when it is ready. Skipping the reply here is not optional
                // bookkeeping — the skipped response write is what leaves the session's receive-
                // list descriptor armed for the next request, and the freed thread is what lets
                // the eventfd write that will end the wait be processed at all.
                if (context.PollDeferRequested)
                {
                    lock (_deferredPollsLock)
                    {
                        _deferredPolls.Add(new DeferredPoll
                        {
                            SessionHandle = serverSessionHandle,
                            Request = request,
                            RecvListAddr = recvListAddr,
                            DeadlineMs = context.PollDeadlineMs,
                            InputSnapshot = context.PollInputSnapshot,
                        });
                    }

                    RearmReceiveList();

                    return false;
                }

                response.RawData = _responseDataStream.ToArray();
            }
            else if (request.Type is IpcMessageType.CmifControl or
                     IpcMessageType.CmifControlWithContext)
            {
#pragma warning disable IDE0059 // Remove unnecessary value assignment
                uint magic = (uint)_requestDataReader.ReadUInt64();
#pragma warning restore IDE0059
                uint cmdId = (uint)_requestDataReader.ReadUInt64();

                switch (cmdId)
                {
                    case 0:
                        FillHipcResponse(response, 0, GetSessionObj(serverSessionHandle).ConvertToDomain());
                        break;

                    case 3:
                        FillHipcResponse(response, 0, PointerBufferSize);
                        break;

                    // TODO: Whats the difference between IpcDuplicateSession/Ex?
                    case 2:
                    case 4:
                        {
                            _ = _requestDataReader.ReadInt32();

                            _context.Syscall.CreateSession(out int dupServerSessionHandle, out int dupClientSessionHandle, false, 0);

                            bool writeLockTaken = false;
                            try
                            {
                                writeLockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);
                                _sessions[dupServerSessionHandle] = _sessions[serverSessionHandle];
                            }
                            finally
                            {
                                if (writeLockTaken)
                                {
                                    _handleLock.ExitWriteLock();
                                }
                            }

                            response.HandleDesc = IpcHandleDesc.MakeMove(dupClientSessionHandle);

                            FillHipcResponse(response, 0);

                            break;
                        }

                    default:
                        throw new NotImplementedException(cmdId.ToString());
                }
            }
            else if (request.Type is IpcMessageType.CmifCloseSession or IpcMessageType.TipcCloseSession)
            {
                DestroySession(serverSessionHandle);
                shouldReply = false;
            }
            // If the type is past 0xF, we are using TIPC
            else if (request.Type > IpcMessageType.TipcCloseSession)
            {
                isTipcCommunication = true;

                // Response type is always the same as request on TIPC.
                response.Type = request.Type;

                _responseDataStream.SetLength(0);

                ServiceCtx context = new(
                    _context.Device,
                    _selfProcess,
                    _selfProcess.CpuMemory,
                    _selfThread,
                    request,
                    response,
                    _requestDataReader,
                    _responseDataWriter);

                GetSessionObj(serverSessionHandle).CallTipcMethod(context);

                response.RawData = _responseDataStream.ToArray();

                RecyclableMemoryStream responseStream = response.GetStreamTipc();
                _selfProcess.CpuMemory.Write(_selfThread.TlsAddress, responseStream.GetReadOnlySequence());
                MemoryStreamManager.Shared.ReleaseStream(responseStream);
            }
            else
            {
                throw new NotImplementedException(request.Type.ToString());
            }

            if (!isTipcCommunication)
            {
                RecyclableMemoryStream responseStream = response.GetStream((long)_selfThread.TlsAddress, recvListAddr | ((ulong)PointerBufferSize << 48));
                _selfProcess.CpuMemory.Write(_selfThread.TlsAddress, responseStream.GetReadOnlySequence());
                MemoryStreamManager.Shared.ReleaseStream(responseStream);
            }

            return shouldReply;
        }

        private IpcMessage ReadRequest()
        {
            const int MessageSize = 0x100;

            using SpanOwner<byte> reqDataOwner = SpanOwner<byte>.Rent(MessageSize);

            Span<byte> reqDataSpan = reqDataOwner.Span;

            _selfProcess.CpuMemory.Read(_selfThread.TlsAddress, reqDataSpan);

            IpcMessage request = new(reqDataSpan, (long)_selfThread.TlsAddress);

            return request;
        }

        private void FillHipcResponse(IpcMessage response, long result)
        {
            FillHipcResponse(response, result, ReadOnlySpan<byte>.Empty);
        }

        private void FillHipcResponse(IpcMessage response, long result, int value)
        {
            Span<byte> span = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            FillHipcResponse(response, result, span);
        }

        private void FillHipcResponse(IpcMessage response, long result, ReadOnlySpan<byte> data)
        {
            response.Type = IpcMessageType.CmifResponse;

            _responseDataStream.SetLength(0);

            _responseDataStream.Write(IpcMagic.Sfco);
            _responseDataStream.Write(result);

            _responseDataStream.Write(data);

            response.RawData = _responseDataStream.ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _selfThread != null)
            {
                if (_selfThread.HostThread.ManagedThreadId != Environment.CurrentManagedThreadId && _selfThread.HostThread.Join(_threadJoinTimeout) == false)
                {
                    Logger.Warning?.Print(LogClass.Service, $"The ServerBase thread didn't terminate within {_threadJoinTimeout:g}, waiting longer.");

                    _selfThread.HostThread.Join(Timeout.Infinite);
                }

                if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
                {
                    _selfProcess.HandleTable.CloseHandle(_wakeHandle);

                    foreach (IpcService service in _sessions.Values)
                    {
                        (service as IDisposable)?.Dispose();

                        service.DestroyAtExit();
                    }

                    _sessions.Clear();
                    _ports.Clear();
                    _handleLock.Dispose();

                    _requestDataReader.Dispose();
                    _requestDataStream.Dispose();
                    _responseDataWriter.Dispose();
                    _responseDataStream.Dispose();

                    InitDone.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
