using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// One friend as a viewer port reads them (20105 / 20106, friends contract §B.3): the same
    /// friend as <see cref="FriendImpl"/> with the thumbnail URL in it, and the presence blob
    /// filled without the privacy filter.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x200, Pack = 0x1)]
    struct FriendForViewerImpl
    {
        [FieldOffset(0x000)] public Uid UserId;
        [FieldOffset(0x010)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x018)] public Nickname Nickname;
        [FieldOffset(0x040)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0x0E0)] public FriendPresenceImpl Presence;
        [FieldOffset(0x1C8)] public bool IsFavourite;
        [FieldOffset(0x1C9)] public bool IsNew;
        [FieldOffset(0x1D0)] public bool IsValid;
    }
}
