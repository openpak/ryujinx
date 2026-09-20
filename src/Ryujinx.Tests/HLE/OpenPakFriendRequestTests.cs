using NUnit.Framework;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.Friends.Detail;
using Ryujinx.OpenPak;
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The guest's friend-request structs (20201/20202, friends contract §B.3) and the
    /// mapping from a parsed request box item onto them: sizes, offsets and field values.
    /// </summary>
    public class OpenPakFriendRequestTests
    {
        private static BaasRequest Request => new(0xa1, 3, 1, 0x11cf3b1423e1183f, "Leia", "https://example/1/a.jpg")
        {
            CreatedAt = 1788000000,
            Read = true,
            Route = new BaasRoute(0x0100a5a020d5e000, 1, 0x0100a5a020d5e000,
                "0100a5a020d5e0000100a5a020d5e000", "Seen", "en-US", null, null),
        };

        private static string Utf8(ReadOnlySpan<byte> field)
        {
            int length = field.IndexOf((byte)0);

            return Encoding.UTF8.GetString(field[..(length < 0 ? field.Length : length)]);
        }

        [Test]
        public void StructsAre0x200()
        {
            Assert.That(Marshal.SizeOf<FriendRequestImpl>(), Is.EqualTo(0x200));
            Assert.That(Marshal.SizeOf<FriendRequestImplV2>(), Is.EqualTo(0x200));
        }

        [Test]
        public void V1Offsets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.UserId)).ToInt64(), Is.EqualTo(0x000));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.RequestId)).ToInt64(), Is.EqualTo(0x010));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.OtherUserId)).ToInt64(), Is.EqualTo(0x018));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.Nickname)).ToInt64(), Is.EqualTo(0x020));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.ThumbnailUrl)).ToInt64(), Is.EqualTo(0x048));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.ListType)).ToInt64(), Is.EqualTo(0x0E8));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.Channel)).ToInt64(), Is.EqualTo(0x0EC));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.State)).ToInt64(), Is.EqualTo(0x0F0));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.RouteApplicationId)).ToInt64(), Is.EqualTo(0x0F8));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.RoutePresenceGroupId)).ToInt64(), Is.EqualTo(0x100));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.RouteName)).ToInt64(), Is.EqualTo(0x108));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.RouteLanguage)).ToInt64(), Is.EqualTo(0x148));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.CreatedAt)).ToInt64(), Is.EqualTo(0x150));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.IsRead)).ToInt64(), Is.EqualTo(0x158));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.IsValid)).ToInt64(), Is.EqualTo(0x159));
                Assert.That(Marshal.OffsetOf<FriendRequestImpl>(nameof(FriendRequestImpl.Union)).ToInt64(), Is.EqualTo(0x160));
            });
        }

        [Test]
        public void V2Offsets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.RouteApplicationId)).ToInt64(), Is.EqualTo(0x0F8));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.RouteAcdIndex)).ToInt64(), Is.EqualTo(0x104));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.RoutePresenceGroupId)).ToInt64(), Is.EqualTo(0x108));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.RouteName)).ToInt64(), Is.EqualTo(0x110));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.RouteLanguage)).ToInt64(), Is.EqualTo(0x150));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.CreatedAt)).ToInt64(), Is.EqualTo(0x158));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.IsRead)).ToInt64(), Is.EqualTo(0x160));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.IsValid)).ToInt64(), Is.EqualTo(0x161));
                Assert.That(Marshal.OffsetOf<FriendRequestImplV2>(nameof(FriendRequestImplV2.Union)).ToInt64(), Is.EqualTo(0x168));
            });
        }

        [Test]
        public void MapsARequest()
        {
            FriendRequestImpl impl = OpenPakFriends.ToRequestImpl(Request, new Uid(7, 8), listType: 2);

            Assert.Multiple(() =>
            {
                Assert.That(impl.UserId, Is.EqualTo(new Uid(7, 8)));
                Assert.That(impl.RequestId, Is.EqualTo(0xa1));
                Assert.That(impl.OtherUserId, Is.EqualTo(0x11cf3b1423e1183f));
                Assert.That(impl.Nickname.ToString(), Is.EqualTo("Leia"));
                Assert.That(Utf8(MemoryMarshal.CreateSpan(ref impl.ThumbnailUrl.Value, 0xA0)),
                    Is.EqualTo("https://example/1/a.jpg"));
                Assert.That(impl.ListType, Is.EqualTo(2));
                Assert.That(impl.Channel, Is.EqualTo(3));
                Assert.That(impl.State, Is.EqualTo(1));
                Assert.That(impl.RouteApplicationId, Is.EqualTo(0x0100a5a020d5e000));
                Assert.That(impl.RoutePresenceGroupId, Is.EqualTo(0x0100a5a020d5e000));
                Assert.That(Utf8(impl.RouteName.AsSpan()), Is.EqualTo("Seen"));
                Assert.That(Utf8(impl.RouteLanguage.AsSpan()), Is.EqualTo("en-US"));
                Assert.That(impl.CreatedAt, Is.EqualTo(1788000000));
                Assert.That(impl.IsRead, Is.True);
                Assert.That(impl.IsValid, Is.True);
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(impl.Union.CatalogId.AsSpan()),
                    Is.EqualTo(0x0100a5a020d5e000));
            });
        }

        [Test]
        public void MapsARequestV2()
        {
            FriendRequestImplV2 impl = OpenPakFriends.ToRequestImplV2(Request, new Uid(7, 8), listType: 1);

            Assert.Multiple(() =>
            {
                Assert.That(impl.RequestId, Is.EqualTo(0xa1));
                Assert.That(impl.ListType, Is.EqualTo(1));
                Assert.That(impl.RouteAcdIndex, Is.EqualTo(1));
                Assert.That(Utf8(impl.RouteName.AsSpan()), Is.EqualTo("Seen"));
                Assert.That(impl.CreatedAt, Is.EqualTo(1788000000));
                Assert.That(impl.IsRead, Is.True);
                Assert.That(impl.IsValid, Is.True);
            });
        }

        [Test]
        public void MiiUnionWhenNoCatalog()
        {
            BaasRequest request = Request with
            {
                Route = new BaasRoute(0, 0, 0, null, null, null, "Mii", "param"),
            };

            FriendRequestImpl impl = OpenPakFriends.ToRequestImpl(request, Uid.Null, listType: 2);

            Assert.Multiple(() =>
            {
                Assert.That(Utf8(impl.Union.MiiName.AsSpan()), Is.EqualTo("Mii"));
                Assert.That(Utf8(impl.Union.MiiImageUrlParam.AsSpan()), Is.EqualTo("param"));
            });
        }

        [Test]
        public void NullRouteGivesZeros()
        {
            BaasRequest request = Request with { Route = null };

            FriendRequestImpl impl = OpenPakFriends.ToRequestImpl(request, Uid.Null, listType: 2);

            Assert.Multiple(() =>
            {
                Assert.That(impl.RouteApplicationId, Is.EqualTo(0));
                Assert.That(Utf8(impl.RouteName.AsSpan()), Is.Empty);
                Assert.That(impl.Union.CatalogId.AsSpan().ToArray(), Has.All.EqualTo(0));
            });
        }
    }
}
