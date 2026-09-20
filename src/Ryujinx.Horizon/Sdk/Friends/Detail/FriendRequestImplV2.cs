using Ryujinx.Common.Memory;
using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// One friend request as the guest reads it (20202, friends contract §B.3): the V2
    /// shape, with the V2 route ApplicationInfo (<c>appId</c>, <c>acdIndex</c> at +0xC,
    /// <c>groupId</c>) at +0xF8 and everything after it shifted by 8.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x200, Pack = 0x1)]
    struct FriendRequestImplV2
    {
        [FieldOffset(0x000)] public Uid UserId;
        [FieldOffset(0x010)] public ulong RequestId;
        [FieldOffset(0x018)] public ulong OtherUserId;
        [FieldOffset(0x020)] public Nickname Nickname;
        [FieldOffset(0x048)] public ThumbnailHolder ThumbnailUrl;
        [FieldOffset(0x0E8)] public uint ListType;
        [FieldOffset(0x0EC)] public uint Channel;
        [FieldOffset(0x0F0)] public uint State;
        [FieldOffset(0x0F8)] public ulong RouteApplicationId;
        [FieldOffset(0x100)] public uint RouteUnknown;
        [FieldOffset(0x104)] public byte RouteAcdIndex;
        [FieldOffset(0x108)] public ulong RoutePresenceGroupId;
        [FieldOffset(0x110)] public Array64<byte> RouteName;
        [FieldOffset(0x150)] public Array8<byte> RouteLanguage;
        [FieldOffset(0x158)] public long CreatedAt;
        [FieldOffset(0x160)] public bool IsRead;
        [FieldOffset(0x161)] public bool IsValid;
        [FieldOffset(0x168)] public RouteUnion Union;

        [InlineArray(0xA0)]
        public struct ThumbnailHolder
        {
            public byte Value;
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x98, Pack = 0x1)]
        public struct RouteUnion
        {
            [FieldOffset(0x00)] public Array16<byte> CatalogId;
            [FieldOffset(0x00)] public Array32<byte> MiiName;
            [FieldOffset(0x20)] public Array16<byte> MiiImageUrlParam;
        }
    }
}
