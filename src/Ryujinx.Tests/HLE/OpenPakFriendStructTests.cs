using NUnit.Framework;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.Friends;
using Ryujinx.Horizon.Sdk.Friends.Detail;
using Ryujinx.OpenPak;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The guest's friends structs other than the request pair (friends contract §B.3,
    /// invitations doc §2): sizes, the offsets the audit pins, and what the mappers put in them.
    ///
    /// The sizes matter on their own: a struct with no size is an element stride of 1, and a
    /// guest buffer filled at the wrong stride is read as garbage.
    /// </summary>
    public class OpenPakFriendStructTests
    {
        private static string Utf8<T>(in T field) where T : struct
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in field), 1));

            int end = bytes.IndexOf((byte)0);

            return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
        }

        [Test]
        public void Sizes()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<ProfileImpl>(), Is.EqualTo(0x100));
                Assert.That(Marshal.SizeOf<ProfileExtraImpl>(), Is.EqualTo(0x400));
                Assert.That(Marshal.SizeOf<ProfileExtraImplV2>(), Is.EqualTo(0x4A8));
                Assert.That(Marshal.SizeOf<BlockedUserImpl>(), Is.EqualTo(0x200));
                Assert.That(Marshal.SizeOf<BlockedUserImplV2>(), Is.EqualTo(0x200));
                Assert.That(Marshal.SizeOf<UserSettingImpl>(), Is.EqualTo(0x800));
                Assert.That(Marshal.SizeOf<UserSettingImplV2>(), Is.EqualTo(0x800));
                Assert.That(Marshal.SizeOf<FriendSettingImpl>(), Is.EqualTo(0x40));
                Assert.That(Marshal.SizeOf<FriendSettingImplV2>(), Is.EqualTo(0x80));
                Assert.That(Marshal.SizeOf<FriendForViewerImpl>(), Is.EqualTo(0x200));
                Assert.That(Marshal.SizeOf<FriendDetailedInfoImpl>(), Is.EqualTo(0x800));
                Assert.That(Marshal.SizeOf<FriendInvitationForViewerImpl>(), Is.EqualTo(0x500));
                Assert.That(Marshal.SizeOf<FriendInvitationForViewerImplV2>(), Is.EqualTo(0x500));
                Assert.That(Marshal.SizeOf<FriendInvitationGroupImpl>(), Is.EqualTo(0x1400));
                Assert.That(Marshal.SizeOf<FriendInvitationGroupImplV2>(), Is.EqualTo(0x1400));
                Assert.That(Marshal.SizeOf<UserPresenceViewImpl>(), Is.EqualTo(0xE0));
                Assert.That(Marshal.SizeOf<ApplicationInfoV2>(), Is.EqualTo(0x18));
                Assert.That(Marshal.SizeOf<FriendNote>(), Is.EqualTo(0x58));
                Assert.That(Marshal.SizeOf<Relationship>(), Is.EqualTo(8));
                Assert.That(Marshal.SizeOf<FriendCode>(), Is.EqualTo(0x20));
                Assert.That(Marshal.SizeOf<FriendInvitationGameModeDescription>(), Is.EqualTo(0xC00));
            });
        }

        [Test]
        public void Offsets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.OffsetOf<ProfileImpl>(nameof(ProfileImpl.ThumbnailUrl)).ToInt64(), Is.EqualTo(0x30));
                Assert.That(Marshal.OffsetOf<ProfileImpl>(nameof(ProfileImpl.IsValid)).ToInt64(), Is.EqualTo(0xD0));
                Assert.That(Marshal.OffsetOf<ProfileExtraImpl>(nameof(ProfileExtraImpl.PlayLog)).ToInt64(), Is.EqualTo(0xD0));
                Assert.That(Marshal.OffsetOf<ProfileExtraImpl>(nameof(ProfileExtraImpl.IsValid)).ToInt64(), Is.EqualTo(0x3F0));
                Assert.That(Marshal.OffsetOf<ProfileExtraImplV2>(nameof(ProfileExtraImplV2.IsValid)).ToInt64(), Is.EqualTo(0x490));

                Assert.That(Marshal.OffsetOf<BlockedUserImpl>(nameof(BlockedUserImpl.Reason)).ToInt64(), Is.EqualTo(0xE0));
                Assert.That(Marshal.OffsetOf<BlockedUserImpl>(nameof(BlockedUserImpl.RouteName)).ToInt64(), Is.EqualTo(0xF8));
                Assert.That(Marshal.OffsetOf<BlockedUserImpl>(nameof(BlockedUserImpl.IsValid)).ToInt64(), Is.EqualTo(0x148));
                Assert.That(Marshal.OffsetOf<BlockedUserImplV2>(nameof(BlockedUserImplV2.AcdIndex)).ToInt64(), Is.EqualTo(0xF4));
                Assert.That(Marshal.OffsetOf<BlockedUserImplV2>(nameof(BlockedUserImplV2.IsValid)).ToInt64(), Is.EqualTo(0x150));

                Assert.That(Marshal.OffsetOf<UserSettingImpl>(nameof(UserSettingImpl.FriendCode)).ToInt64(), Is.EqualTo(0x20));
                Assert.That(Marshal.OffsetOf<UserSettingImpl>(nameof(UserSettingImpl.NetworkUserId)).ToInt64(), Is.EqualTo(0x48));
                Assert.That(Marshal.OffsetOf<UserSettingImpl>(nameof(UserSettingImpl.ThumbnailUrl)).ToInt64(), Is.EqualTo(0x78));
                Assert.That(Marshal.OffsetOf<UserSettingImpl>(nameof(UserSettingImpl.IsValid)).ToInt64(), Is.EqualTo(0x438));
                Assert.That(Marshal.OffsetOf<UserSettingImplV2>(nameof(UserSettingImplV2.IsValid)).ToInt64(), Is.EqualTo(0x4D8));

                Assert.That(Marshal.OffsetOf<FriendForViewerImpl>(nameof(FriendForViewerImpl.Presence)).ToInt64(), Is.EqualTo(0xE0));
                Assert.That(Marshal.OffsetOf<FriendForViewerImpl>(nameof(FriendForViewerImpl.IsValid)).ToInt64(), Is.EqualTo(0x1D0));

                Assert.That(Marshal.OffsetOf<FriendInvitationForViewerImpl>(nameof(FriendInvitationForViewerImpl.ApplicationData)).ToInt64(), Is.EqualTo(0xE0));
                Assert.That(Marshal.OffsetOf<FriendInvitationForViewerImpl>(nameof(FriendInvitationForViewerImpl.IsValid)).ToInt64(), Is.EqualTo(0x3A));
                Assert.That(Marshal.OffsetOf<FriendInvitationForViewerImplV2>(nameof(FriendInvitationForViewerImplV2.AcdIndex)).ToInt64(), Is.EqualTo(0x24));
                Assert.That(Marshal.OffsetOf<FriendInvitationForViewerImplV2>(nameof(FriendInvitationForViewerImplV2.IsValid)).ToInt64(), Is.EqualTo(0x42));

                Assert.That(Marshal.OffsetOf<FriendInvitationGroupImpl>(nameof(FriendInvitationGroupImpl.Messages)).ToInt64(), Is.EqualTo(0xA8));
                Assert.That(Marshal.OffsetOf<FriendInvitationGroupImpl>(nameof(FriendInvitationGroupImpl.IsValid)).ToInt64(), Is.EqualTo(0xCB9));
                Assert.That(Marshal.OffsetOf<FriendInvitationGroupImpl>(nameof(FriendInvitationGroupImpl.ApplicationData)).ToInt64(), Is.EqualTo(0xD60));
                Assert.That(Marshal.OffsetOf<FriendInvitationGroupImplV2>(nameof(FriendInvitationGroupImplV2.AcdIndex)).ToInt64(), Is.EqualTo(0xA4));
                Assert.That(Marshal.OffsetOf<FriendInvitationGroupImplV2>(nameof(FriendInvitationGroupImplV2.IsValid)).ToInt64(), Is.EqualTo(0xCC1));
            });
        }

        [Test]
        public void MapsAUser()
        {
            ProfileImpl impl = OpenPakFriends.ToProfileImpl(new BaasUser(0x2306080700000001, "Rosa", "https://example/1/r.jpg"));

            Assert.Multiple(() =>
            {
                Assert.That(impl.NetworkUserId.Id, Is.EqualTo(0x2306080700000001));
                Assert.That(impl.Nickname.ToString(), Is.EqualTo("Rosa"));
                Assert.That(Utf8(impl.ThumbnailUrl), Is.EqualTo("https://example/1/r.jpg"));
                Assert.That(impl.IsValid, Is.True);
            });
        }

        [Test]
        public void MapsABlock()
        {
            BaasBlock block = new(0x2618050200000002, "Nox", "https://example/1/n.jpg")
            {
                CreatedAt = 1788000123,
                Reason = 2,
                Route = new BaasRoute(0x0100a5a020d5e000, 3, 0x0100a5a020d5e000, null, "Lobby", "en-US", null, null),
            };

            BlockedUserImpl impl = OpenPakFriends.ToBlockedUserImpl(block, new Uid(1, 2));
            BlockedUserImplV2 implV2 = OpenPakFriends.ToBlockedUserImplV2(block, new Uid(1, 2));

            Assert.Multiple(() =>
            {
                Assert.That(impl.UserId, Is.EqualTo(new Uid(1, 2)));
                Assert.That(impl.NetworkUserId.Id, Is.EqualTo(0x2618050200000002));
                Assert.That(impl.Nickname.ToString(), Is.EqualTo("Nox"));
                Assert.That(impl.Reason, Is.EqualTo(2));
                Assert.That(impl.ApplicationId, Is.EqualTo(0x0100a5a020d5e000));
                Assert.That(Utf8(impl.RouteName), Is.EqualTo("Lobby"));
                Assert.That(Utf8(impl.RouteLanguage), Is.EqualTo("en-US"));
                Assert.That(impl.CreatedAt, Is.EqualTo(1788000123));
                Assert.That(impl.IsValid, Is.True);
                Assert.That(implV2.AcdIndex, Is.EqualTo(3));
                Assert.That(implV2.IsValid, Is.True);
            });
        }

        [Test]
        public void MapsTheOwnUser()
        {
            BaasUserSetting setting = new(0x2306080700000001, "Rosa", "https://example/1/r.jpg")
            {
                PresencePermission = 2,
                PlayLogPermission = 3,
                FriendRequestReception = true,
                FriendCode = "1234-5678-9012",
                FriendCodeRegenerableAt = 1790000000,
            };

            UserSettingImpl impl = OpenPakFriends.ToUserSettingImpl(setting, new Uid(3, 4));

            Assert.Multiple(() =>
            {
                Assert.That(impl.UserId, Is.EqualTo(new Uid(3, 4)));
                Assert.That(impl.PresencePermission, Is.EqualTo(2));
                Assert.That(impl.PlayLogPermission, Is.EqualTo(3));
                Assert.That(impl.FriendRequestReception, Is.True);
                Assert.That(Utf8(impl.FriendCode), Is.EqualTo("1234-5678-9012"));
                Assert.That(impl.FriendCodeRegenerableAt, Is.EqualTo(1790000000));
                Assert.That(impl.NetworkUserId.Id, Is.EqualTo(0x2306080700000001));
                Assert.That(impl.Nickname.ToString(), Is.EqualTo("Rosa"));
                Assert.That(impl.IsValid, Is.True);
            });
        }

        [Test]
        public void MapsAnInvitation()
        {
            BaasInvitation invitation = new(41, 7, 0x2306080700000001, 0x0100a5a020d5e000, 0x0100a5a020d5e000)
            {
                AcdIndex = 1,
                ApplicationData = "ECKAAL"u8.ToArray(),
                CreatedAt = 1788000500,
                Read = true,
                ApplicationIdMatch = true,
            };

            FriendInvitationForViewerImpl impl = OpenPakFriends.ToInvitationImpl(invitation);
            FriendInvitationForViewerImplV2 implV2 = OpenPakFriends.ToInvitationImplV2(invitation);

            Assert.Multiple(() =>
            {
                Assert.That(impl.InvitationId, Is.EqualTo(41));
                Assert.That(impl.GroupId, Is.EqualTo(7));
                Assert.That(impl.SenderId, Is.EqualTo(0x2306080700000001));
                Assert.That(impl.ApplicationDataSize, Is.EqualTo(6));
                Assert.That(Utf8(impl.ApplicationData), Is.EqualTo("ECKAAL"));
                Assert.That(impl.IsRead, Is.True);
                Assert.That(impl.IsValid, Is.True);
                Assert.That(implV2.AcdIndex, Is.EqualTo(1));
                Assert.That(implV2.ApplicationGroupId, Is.EqualTo(0x0100a5a020d5e000));
                Assert.That(implV2.IsValid, Is.True);
            });
        }

        [Test]
        public void MapsAnInvitationGroup()
        {
            BaasInvitationGroup group = new(7, 0x2306080700000001, [0x1111, 0x2222], 0x0100a5a020d5e000, 0x0100a5a020d5e000)
            {
                AcdIndex = 1,
                Messages = ["Join me", "", "いっしょに"],
                ApplicationData = [1, 2, 3],
                CreatedAt = 1788000600,
                ApplicationIdMatch = true,
            };

            FriendInvitationGroupImpl impl = OpenPakFriends.ToInvitationGroupImpl(group);

            Assert.Multiple(() =>
            {
                Assert.That(impl.GroupId, Is.EqualTo(7));
                Assert.That(impl.ReceiverCount, Is.EqualTo(2));
                Assert.That(impl.Receivers[0], Is.EqualTo(0x1111u));
                Assert.That(impl.Receivers[1], Is.EqualTo(0x2222u));
                Assert.That(impl.ApplicationDataSize, Is.EqualTo(3));
                Assert.That(impl.IsValid, Is.True);
            });

            // Slot 0 is en-US and slot 2 is ja, in the module's language order.
            byte[] slots = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in impl.Messages), 1)).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(Slot(slots, 0), Is.EqualTo("Join me"));
                Assert.That(Slot(slots, 1), Is.Empty);
                Assert.That(Slot(slots, 2), Is.EqualTo("いっしょに"));
            });
        }

        private static string Slot(ReadOnlySpan<byte> slots, int index)
        {
            ReadOnlySpan<byte> slot = slots.Slice(index * 0xC0, 0xC0);
            int end = slot.IndexOf((byte)0);

            return Encoding.UTF8.GetString(end < 0 ? slot : slot[..end]);
        }

        [Test]
        public void KeepsAWholeMultiByteTail()
        {
            // A name that fits keeps its last character; one that does not is cut on a
            // character boundary rather than mid-sequence.
            Assert.Multiple(() =>
            {
                Assert.That(OpenPakFriends.ToNickname("いっしょに").ToString(), Is.EqualTo("いっしょに"));
                Assert.That(OpenPakFriends.ToNickname(new string('あ', 12)).ToString(), Is.EqualTo(new string('あ', 10)));
                Assert.That(OpenPakFriends.ToNickname(new string('a', 40)).ToString(), Is.EqualTo(new string('a', 32)));
            });
        }

        [Test]
        public void InvitationMessagesAreTheFilledSlots()
        {
            FriendInvitationGameModeDescription description = default;

            Span<byte> slots = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref description, 1));

            Encoding.UTF8.GetBytes("Come in").CopyTo(slots[..0xC0]);
            Encoding.UTF8.GetBytes("Entrez").CopyTo(slots.Slice(3 * 0xC0, 0xC0));

            var messages = OpenPakFriends.InvitationMessages(description);

            Assert.That(messages, Is.EqualTo(new[] { ("en-US", "Come in"), ("fr", "Entrez") }));
        }
    }
}
