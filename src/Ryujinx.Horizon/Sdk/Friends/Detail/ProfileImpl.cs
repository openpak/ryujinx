using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// One user as 10500 returns them (friends contract §B.3): the flat user of
    /// <c>GET /1.0.0/users?filter.id.$in=…</c>, with no relationship in it.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x100, Pack = 0x1)]
    struct ProfileImpl
    {
        [FieldOffset(0x00)] public NetworkServiceAccountId NetworkUserId;
        [FieldOffset(0x08)] public Nickname Nickname;
        [FieldOffset(0x30)] public ThumbnailUrlField ThumbnailUrl;
        [FieldOffset(0xD0)] public bool IsValid;
    }
}
