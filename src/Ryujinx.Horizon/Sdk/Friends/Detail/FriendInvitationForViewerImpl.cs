using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// One waiting invitation as 22000 returns it (invitations doc §2a). The application data is
    /// the raw bytes the sender passed, already base64-decoded.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x500, Pack = 0x1)]
    struct FriendInvitationForViewerImpl
    {
        [FieldOffset(0x000)] public ulong InvitationId;
        [FieldOffset(0x008)] public ulong GroupId;
        [FieldOffset(0x010)] public ulong SenderId;
        [FieldOffset(0x018)] public ulong ApplicationId;
        [FieldOffset(0x020)] public ulong ApplicationGroupId;
        [FieldOffset(0x028)] public uint ApplicationDataSize;
        [FieldOffset(0x030)] public long CreatedAt;
        [FieldOffset(0x038)] public bool IsRead;
        [FieldOffset(0x039)] public bool ApplicationIdMatch;
        [FieldOffset(0x03A)] public bool IsValid;
        [FieldOffset(0x0E0)] public ApplicationDataHolder ApplicationData;

        [InlineArray(0x400)]
        public struct ApplicationDataHolder
        {
            public byte Value;
        }
    }

    /// <summary>The same with the acd index, everything after it shifted by 8 (22002).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x500, Pack = 0x1)]
    struct FriendInvitationForViewerImplV2
    {
        [FieldOffset(0x000)] public ulong InvitationId;
        [FieldOffset(0x008)] public ulong GroupId;
        [FieldOffset(0x010)] public ulong SenderId;
        [FieldOffset(0x018)] public ulong ApplicationId;
        [FieldOffset(0x020)] public uint Unknown;
        [FieldOffset(0x024)] public byte AcdIndex;
        [FieldOffset(0x028)] public ulong ApplicationGroupId;
        [FieldOffset(0x030)] public uint ApplicationDataSize;
        [FieldOffset(0x038)] public long CreatedAt;
        [FieldOffset(0x040)] public bool IsRead;
        [FieldOffset(0x041)] public bool ApplicationIdMatch;
        [FieldOffset(0x042)] public bool IsValid;
        [FieldOffset(0x0E0)] public FriendInvitationForViewerImpl.ApplicationDataHolder ApplicationData;
    }
}
