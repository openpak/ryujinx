using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// The caller's own presence slot as 20600 returns it (friends contract §B.3). The state is
    /// the console's own — 0 INACTIVE, 1 ONLINE, 2 PLAYING — not a friend's status enum.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0xE0, Pack = 0x1)]
    struct UserPresenceViewImpl
    {
        [FieldOffset(0x00)] public ulong ApplicationId;
        [FieldOffset(0x08)] public ulong PresenceGroupId;
        [FieldOffset(0x10)] public long LastUpdateTimestamp;
        [FieldOffset(0x18)] public uint State;
        [FieldOffset(0x20)] public AppKeyValueStorageHolder AppKeyValueStorage;

        [InlineArray(0xC0)]
        public struct AppKeyValueStorageHolder
        {
            public byte Value;
        }
    }
}
