using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// A user with their play log, as 20500 and 30500 return them (friends contract §B.3):
    /// <see cref="ProfileImpl"/> plus the 20 × 0x28 play-log block at +0xD0.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x400, Pack = 0x1)]
    struct ProfileExtraImpl
    {
        [FieldOffset(0x000)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x008)] public Nickname Nickname;
        [FieldOffset(0x030)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x0D0)] public PlayLogBlock PlayLog;
        [FieldOffset(0x3F0)] public bool IsValid;
    }

    /// <summary>The same with acdIndex in every play-log entry (20502 / 30501): 20 × 0x30.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x4A8, Pack = 0x1)]
    struct ProfileExtraImplV2
    {
        [FieldOffset(0x000)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x008)] public Nickname Nickname;
        [FieldOffset(0x030)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x0D0)] public PlayLogBlockV2 PlayLog;
        [FieldOffset(0x490)] public bool IsValid;
    }
}
