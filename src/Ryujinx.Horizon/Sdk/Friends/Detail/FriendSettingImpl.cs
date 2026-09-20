using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>The per-friend flags as 20110 returns them (friends contract §B.3).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x40, Pack = 0x1)]
    struct FriendSettingImpl
    {
        [FieldOffset(0x00)] public Uid UserId;
        [FieldOffset(0x10)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x18)] public bool IsFavourite;
        [FieldOffset(0x19)] public bool IsNew;
        [FieldOffset(0x1A)] public bool IsOnlineNotification;
    }

    /// <summary>The same plus the private note (20111).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x80, Pack = 0x1)]
    struct FriendSettingImplV2
    {
        [FieldOffset(0x00)] public Uid UserId;
        [FieldOffset(0x10)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x18)] public FriendNoteField FriendNote;
        [FieldOffset(0x69)] public bool IsFavourite;
        [FieldOffset(0x6A)] public bool IsNew;
        [FieldOffset(0x6B)] public bool IsOnlineNotification;
    }
}
