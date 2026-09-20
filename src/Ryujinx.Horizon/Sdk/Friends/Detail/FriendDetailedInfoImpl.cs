using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// One friend's profile page, as 20102 and 20107 return it (friends contract §B.3).
    ///
    /// The play-log block at +0xE0 and the route block at +0x4C0 are left zero: the block's
    /// stride is known but not the fields inside an entry, and the route block's layout is an
    /// inference. Their valid flags stay clear, which is how the module marks "nothing here".
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x800, Pack = 0x1)]
    struct FriendDetailedInfoImpl
    {
        [FieldOffset(0x000)] public Uid UserId;
        [FieldOffset(0x010)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x018)] public Nickname Nickname;
        [FieldOffset(0x040)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x0E0)] public PlayLogBlock PlayLog;
        [FieldOffset(0x400)] public bool IsPlayLogValid;

        /// <summary>The channels table (§A.1), 1-based; 0 when the server named none.</summary>
        [FieldOffset(0x4B8)] public uint Channel;

        [FieldOffset(0x520)] public bool IsValid;
    }
}
