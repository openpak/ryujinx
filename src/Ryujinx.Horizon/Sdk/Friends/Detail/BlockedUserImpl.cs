using Ryujinx.Common.Memory;
using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// One blocked user as 20400 returns them (friends contract §B.3): the block's target,
    /// why it was made, and the title it was made from.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x200, Pack = 0x1)]
    struct BlockedUserImpl
    {
        [FieldOffset(0x000)] public Uid UserId;
        [FieldOffset(0x010)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x018)] public Nickname Nickname;
        [FieldOffset(0x040)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x0E0)] public uint Reason;
        [FieldOffset(0x0E8)] public ulong ApplicationId;
        [FieldOffset(0x0F0)] public ulong PresenceGroupId;
        [FieldOffset(0x0F8)] public RouteNameField RouteName;
        [FieldOffset(0x138)] public Array8<byte> RouteLanguage;
        [FieldOffset(0x140)] public long CreatedAt;
        [FieldOffset(0x148)] public bool IsValid;
    }

    /// <summary>The same with the route acdIndex at +0xF4, everything after it shifted by 8 (20402).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x200, Pack = 0x1)]
    struct BlockedUserImplV2
    {
        [FieldOffset(0x000)] public Uid UserId;
        [FieldOffset(0x010)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x018)] public Nickname Nickname;
        [FieldOffset(0x040)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x0E0)] public uint Reason;
        [FieldOffset(0x0E8)] public ulong ApplicationId;
        [FieldOffset(0x0F4)] public byte AcdIndex;
        [FieldOffset(0x0F8)] public ulong PresenceGroupId;
        [FieldOffset(0x100)] public RouteNameField RouteName;
        [FieldOffset(0x140)] public Array8<byte> RouteLanguage;
        [FieldOffset(0x148)] public long CreatedAt;
        [FieldOffset(0x150)] public bool IsValid;
    }
}
