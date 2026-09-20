using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// The caller's own user as 20800 returns it (friends contract §B.3): the privacy switches,
    /// the friend code and the profile behind <c>GET /1.0.0/users/&lt;me&gt;</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x800, Pack = 0x1)]
    struct UserSettingImpl
    {
        [FieldOffset(0x000)] public Uid UserId;

        /// <summary>0 SELF, 1 FAVORITE_FRIENDS, 2 FRIENDS.</summary>
        [FieldOffset(0x010)] public uint PresencePermission;

        /// <summary>1 self, 2 favoriteFriends, 3 friends, 5 everyone.</summary>
        [FieldOffset(0x014)] public uint PlayLogPermission;

        [FieldOffset(0x018)] public bool FriendRequestReception;
        [FieldOffset(0x020)] public FriendCode FriendCode;
        [FieldOffset(0x040)] public long FriendCodeRegenerableAt;
        [FieldOffset(0x048)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x050)] public Nickname Nickname;
        [FieldOffset(0x078)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x118)] public PlayLogBlock PlayLog;
        [FieldOffset(0x438)] public bool IsValid;
    }

    /// <summary>
    /// The same through +0x118, with acdIndex in every play-log entry (20802): 20 × 0x30, so
    /// the valid flag lands at +0x4D8. The buffer is still 0x800.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x800, Pack = 0x1)]
    struct UserSettingImplV2
    {
        [FieldOffset(0x000)] public Uid UserId;
        [FieldOffset(0x010)] public uint PresencePermission;
        [FieldOffset(0x014)] public uint PlayLogPermission;
        [FieldOffset(0x018)] public bool FriendRequestReception;
        [FieldOffset(0x020)] public FriendCode FriendCode;
        [FieldOffset(0x040)] public long FriendCodeRegenerableAt;
        [FieldOffset(0x048)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x050)] public Nickname Nickname;
        [FieldOffset(0x078)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x118)] public PlayLogBlockV2 PlayLog;
        [FieldOffset(0x4D8)] public bool IsValid;
    }
}
