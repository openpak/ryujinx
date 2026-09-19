using Ryujinx.Common.Memory;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// A friend's presence as the guest reads it (FriendImpl +0x40). Not the write-side
    /// <see cref="UserPresenceImpl"/>: the two share a size and the blob offset, and this one
    /// starts with the friend's title, not a Uid.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 0xE0)]
    struct FriendPresenceImpl
    {
        public ulong ApplicationId;
        public ulong PresenceGroupId;
        public long LastUpdateTimestamp;
        public PresenceStatus Status;
        public bool SamePresenceGroupApplication;
        public Array3<byte> Padding;
        public AppKeyValueStorageHolder AppKeyValueStorage;

        [InlineArray(0xC0)]
        public struct AppKeyValueStorageHolder
        {
            public byte Value;
        }

        public readonly override string ToString()
        {
            return $"{{ ApplicationId: {ApplicationId:x16}, PresenceGroupId: {PresenceGroupId:x16}, Status: {Status} }}";
        }
    }
}
