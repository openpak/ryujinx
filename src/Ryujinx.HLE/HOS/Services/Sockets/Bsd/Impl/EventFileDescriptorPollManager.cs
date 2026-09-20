using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl
{
    class EventFileDescriptorPollManager : IPollManager
    {
        private static EventFileDescriptorPollManager _instance;

        public static EventFileDescriptorPollManager Instance
        {
            get
            {
                _instance ??= new EventFileDescriptorPollManager();

                return _instance;
            }
        }

        public bool IsCompatible(PollEvent evnt)
        {
            return evnt.FileDescriptor is EventFileDescriptor;
        }

        public LinuxError Poll(List<PollEvent> events, int timeoutMilliseconds, out int updatedCount)
        {
            updatedCount = 0;

            List<ManualResetEvent> waiters = [];

            for (int i = 0; i < events.Count; i++)
            {
                PollEvent evnt = events[i];

                EventFileDescriptor socket = (EventFileDescriptor)evnt.FileDescriptor;

                bool isValidEvent = false;

                if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Input) ||
                    evnt.Data.InputEvents.HasFlag(PollEventTypeMask.UrgentInput))
                {
                    waiters.Add(socket.ReadEvent);

                    isValidEvent = true;
                }

                if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Output))
                {
                    waiters.Add(socket.WriteEvent);

                    isValidEvent = true;
                }

                if (!isValidEvent)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd, $"Unsupported Poll input event type: {evnt.Data.InputEvents}");

                    return LinuxError.EINVAL;
                }
            }

            int index = WaitHandle.WaitAny(waiters.ToArray(), timeoutMilliseconds);

            if (index != WaitHandle.WaitTimeout)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    PollEventTypeMask outputEvents = 0;

                    PollEvent evnt = events[i];

                    EventFileDescriptor socket = (EventFileDescriptor)evnt.FileDescriptor;

                    if (socket.ReadEvent.WaitOne(0))
                    {
                        if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Input))
                        {
                            outputEvents |= PollEventTypeMask.Input;
                        }

                        if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.UrgentInput))
                        {
                            outputEvents |= PollEventTypeMask.UrgentInput;
                        }
                    }

                    if ((evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Output))
                        && socket.WriteEvent.WaitOne(0))
                    {
                        outputEvents |= PollEventTypeMask.Output;
                    }

                    if (outputEvents != 0)
                    {
                        evnt.Data.OutputEvents = outputEvents;

                        updatedCount++;
                    }
                }
            }
            else
            {
                return LinuxError.ETIMEDOUT;
            }

            return LinuxError.SUCCESS;
        }

        // Readiness only, never a wait: IClient.Select owns the waiting, because blocking the
        // single Bsd thread on an event fd deadlocks (the EventFdWrite that would end the wait is
        // an IPC on that same thread). Returning EOPNOTSUPP here instead left nn::websocket's
        // worker unable to ever see its wakeup fd: it never drained it and its select(1 s) over
        // that fd alone returned instantly, so the worker free-ran at ~18k IPC/s and never drove
        // the socket it had just dialled.
        public LinuxError Select(List<PollEvent> events, int timeout, out int updatedCount)
        {
            updatedCount = 0;

            foreach (PollEvent evnt in events)
            {
                EventFileDescriptor eventFd = (EventFileDescriptor)evnt.FileDescriptor;

                PollEventTypeMask outputEvents = 0;

                if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Input) && eventFd.ReadEvent.WaitOne(0))
                {
                    outputEvents |= PollEventTypeMask.Input;
                }

                if (evnt.Data.InputEvents.HasFlag(PollEventTypeMask.Output) && eventFd.WriteEvent.WaitOne(0))
                {
                    outputEvents |= PollEventTypeMask.Output;
                }

                if (outputEvents != 0)
                {
                    evnt.Data.OutputEvents = outputEvents;

                    updatedCount++;
                }
            }

            return LinuxError.SUCCESS;
        }
    }
}
