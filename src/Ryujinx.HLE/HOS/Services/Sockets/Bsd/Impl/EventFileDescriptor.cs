using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl
{
    class EventFileDescriptor : IFileDescriptor
    {
        // Raised after any successful write to any event fd. The Bsd server subscribes: a write
        // is the wakeup itself, and the deferred poll that waits on this fd must be re-checked
        // promptly rather than at the mercy of the next unrelated IPC.
        public static event Action OnAnyWrite;

        private ulong _value;
        private readonly EventFdFlags _flags;

        // type is not Lock due to Monitor class usage
        private readonly object _lock = new();

        public bool Blocking { get => !_flags.HasFlag(EventFdFlags.NonBlocking); set => throw new NotSupportedException(); }

        public ManualResetEvent WriteEvent { get; }
        public ManualResetEvent ReadEvent { get; }

        public EventFileDescriptor(ulong value, EventFdFlags flags)
        {
            // FIXME: We should support blocking operations.
            // Right now they can't be supported because it would cause the
            // service to lock up as we only have one thread processing requests.
            flags |= EventFdFlags.NonBlocking;

            _value = value;
            _flags = flags;

            WriteEvent = new ManualResetEvent(false);
            ReadEvent = new ManualResetEvent(false);
            UpdateEventStates();
        }

        public int Refcount { get; set; }

        public void Dispose()
        {
            WriteEvent.Dispose();
            ReadEvent.Dispose();
        }

        private void ResetEventStates()
        {
            WriteEvent.Reset();
            ReadEvent.Reset();
        }

        private void UpdateEventStates()
        {
            if (_value > 0)
            {
                ReadEvent.Set();
            }

            if (_value != uint.MaxValue - 1)
            {
                WriteEvent.Set();
            }
        }

        public LinuxError Read(out int readSize, Span<byte> buffer)
        {
            if (buffer.Length < sizeof(ulong))
            {
                readSize = 0;

                return LinuxError.EINVAL;
            }

            lock (_lock)
            {
                ResetEventStates();

                ref ulong count = ref MemoryMarshal.Cast<byte, ulong>(buffer)[0];

                if (_value == 0)
                {
                    if (Blocking)
                    {
                        while (_value == 0)
                        {
                            Monitor.Wait(_lock);
                        }
                    }
                    else
                    {
                        readSize = 0;

                        UpdateEventStates();

                        Logger.Debug?.Print(LogClass.ServiceBsd, $"[EventFd] Read while empty (EAGAIN)");

                        return LinuxError.EAGAIN;
                    }
                }

                readSize = sizeof(ulong);

                if (_flags.HasFlag(EventFdFlags.Semaphore))
                {
                    --_value;

                    count = 1;
                }
                else
                {
                    count = _value;

                    _value = 0;
                }

                UpdateEventStates();

                Logger.Debug?.Print(LogClass.ServiceBsd, $"[EventFd] Read: {count}");

                return LinuxError.SUCCESS;
            }
        }

        public LinuxError Write(out int writeSize, ReadOnlySpan<byte> buffer)
        {
            if (!MemoryMarshal.TryRead(buffer, out ulong count) || count == ulong.MaxValue)
            {
                writeSize = 0;

                Logger.Debug?.Print(LogClass.ServiceBsd, $"[EventFd] Write rejected: buffer does not carry a count");

                return LinuxError.EINVAL;
            }

            lock (_lock)
            {
                ResetEventStates();

                if (_value > _value + count)
                {
                    if (Blocking)
                    {
                        Monitor.Wait(_lock);
                    }
                    else
                    {
                        writeSize = 0;

                        UpdateEventStates();

                        Logger.Debug?.Print(LogClass.ServiceBsd, $"[EventFd] Write overflow while non-blocking (EAGAIN)");

                        return LinuxError.EAGAIN;
                    }
                }

                writeSize = sizeof(ulong);

                _value += count;
                Monitor.Pulse(_lock);

                UpdateEventStates();

                // A write is the wakeup itself: whoever signs a poller's death or its next step
                // does it here, so it is worth one line in the log.
                Logger.Debug?.Print(LogClass.ServiceBsd, $"[EventFd] Write: count {count}, value now {_value}");

                OnAnyWrite?.Invoke();

                return LinuxError.SUCCESS;
            }
        }
    }
}
