using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Am
{
    /// <summary>
    /// The running application's friend invitation storage channel: what an accepted invitation
    /// leaves for the game.
    ///
    /// On a console qlaunch pushes it with IApplicationAccessor 180 when the person accepts; here
    /// the host's "Join" does. The game polls IApplicationFunctions 141 (or waits on 140's event)
    /// and reads each storage as [Uid 0x10][application_data raw bytes] — the bytes the sender's
    /// game passed, not the base64 the server keeps them as.
    /// </summary>
    public class FriendInvitationChannel
    {
        private readonly Queue<byte[]> _storages = new();
        private readonly Lock _lock = new();

        internal KEvent Event { get; }

        internal FriendInvitationChannel(KernelContext context)
        {
            Event = new KEvent(context);
        }

        /// <summary>Queue one invitation for the game and wake whoever waits on the event.</summary>
        public void Push(UserId userId, ReadOnlySpan<byte> applicationData)
        {
            byte[] storage = new byte[0x10 + applicationData.Length];

            BitConverter.TryWriteBytes(storage.AsSpan(0, 8), userId.High);
            BitConverter.TryWriteBytes(storage.AsSpan(8, 8), userId.Low);
            applicationData.CopyTo(storage.AsSpan(0x10));

            lock (_lock)
            {
                _storages.Enqueue(storage);

                Event.ReadableEvent.Signal();
            }
        }

        /// <summary>
        /// The oldest storage, or false when there is none. A pop clears the event; it stays
        /// signalled only while something is still queued, so a title waiting on it wakes once per
        /// invitation rather than spinning on a channel it has already emptied.
        /// </summary>
        internal bool TryPop(out byte[] storage)
        {
            lock (_lock)
            {
                Event.ReadableEvent.Clear();

                bool popped = _storages.TryDequeue(out storage);

                if (_storages.Count > 0)
                {
                    Event.ReadableEvent.Signal();
                }

                return popped;
            }
        }
    }
}
