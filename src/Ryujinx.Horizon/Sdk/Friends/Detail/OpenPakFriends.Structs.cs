using Ryujinx.Common;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// The rest of the guest's friends structs (friends contract §B.3, invitations doc §2): the
    /// user, block, own-setting and invitation shapes, filled from the same caches the friend
    /// list is served from.
    ///
    /// Play-log blocks are left zero everywhere. The audit pins the block's stride (0x28, or
    /// 0x30 with acdIndex) but not the offsets of the fields inside one entry, and a guessed
    /// layout a game reads as play counts is worse than a block of zeros with the valid flag
    /// unset.
    /// </summary>
    static partial class OpenPakFriends
    {
        /// <summary>A fixed field of a struct as bytes, whatever its inline-array type.</summary>
        private static Span<byte> Bytes<T>(ref T field) where T : struct
            => MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref field), Unsafe.SizeOf<T>());

        /// <summary>A fixed NUL-terminated char field as the string it holds.</summary>
        public static string Text<T>(in T field) where T : struct
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in field), 1));

            int end = bytes.IndexOf((byte)0);

            return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
        }

        /// <summary>One flat user as 10500 returns them.</summary>
        public static ProfileImpl ToProfileImpl(BaasUser user)
        {
            ProfileImpl impl = new()
            {
                NetworkUserId = new NetworkServiceAccountId(user.Id),
                Nickname = ToNickname(user.Nickname),
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.ThumbnailUrl), user.ThumbnailUrl);

            return impl;
        }

        /// <summary>One flat user with a play-log block (20500 / 30500).</summary>
        public static ProfileExtraImpl ToProfileExtraImpl(BaasUser user)
        {
            ProfileExtraImpl impl = new()
            {
                NetworkUserId = new NetworkServiceAccountId(user.Id),
                Nickname = ToNickname(user.Nickname),
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.ThumbnailUrl), user.ThumbnailUrl);

            return impl;
        }

        /// <summary>As above, with the wider play-log entries (20502 / 30501).</summary>
        public static ProfileExtraImplV2 ToProfileExtraImplV2(BaasUser user)
        {
            ProfileExtraImplV2 impl = new()
            {
                NetworkUserId = new NetworkServiceAccountId(user.Id),
                Nickname = ToNickname(user.Nickname),
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.ThumbnailUrl), user.ThumbnailUrl);

            return impl;
        }

        /// <summary>One blocked user as 20400 returns them.</summary>
        public static BlockedUserImpl ToBlockedUserImpl(BaasBlock block, Uid userId)
        {
            BlockedUserImpl impl = new()
            {
                UserId = userId,
                NetworkUserId = new NetworkServiceAccountId(block.Id),
                Nickname = ToNickname(block.Nickname),
                Reason = (uint)block.Reason,
                ApplicationId = block.Route?.ApplicationId ?? 0,
                PresenceGroupId = block.Route?.PresenceGroupId ?? 0,
                CreatedAt = block.CreatedAt,
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.ThumbnailUrl), block.ThumbnailUrl);
            FixedUtf8(Bytes(ref impl.RouteName), block.Route?.Name);
            FixedUtf8(impl.RouteLanguage.AsSpan(), block.Route?.Language);

            return impl;
        }

        /// <summary>The same with the route acd index (20402).</summary>
        public static BlockedUserImplV2 ToBlockedUserImplV2(BaasBlock block, Uid userId)
        {
            BlockedUserImplV2 impl = new()
            {
                UserId = userId,
                NetworkUserId = new NetworkServiceAccountId(block.Id),
                Nickname = ToNickname(block.Nickname),
                Reason = (uint)block.Reason,
                ApplicationId = block.Route?.ApplicationId ?? 0,
                AcdIndex = block.Route?.AcdIndex ?? 0,
                PresenceGroupId = block.Route?.PresenceGroupId ?? 0,
                CreatedAt = block.CreatedAt,
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.ThumbnailUrl), block.ThumbnailUrl);
            FixedUtf8(Bytes(ref impl.RouteName), block.Route?.Name);
            FixedUtf8(impl.RouteLanguage.AsSpan(), block.Route?.Language);

            return impl;
        }

        /// <summary>The caller's own user as 20800 returns it.</summary>
        public static UserSettingImpl ToUserSettingImpl(BaasUserSetting setting, Uid userId)
        {
            UserSettingImpl impl = new()
            {
                UserId = userId,
                PresencePermission = (uint)setting.PresencePermission,
                PlayLogPermission = (uint)setting.PlayLogPermission,
                FriendRequestReception = setting.FriendRequestReception,
                FriendCodeRegenerableAt = setting.FriendCodeRegenerableAt,
                NetworkUserId = new NetworkServiceAccountId(setting.Id),
                Nickname = ToNickname(setting.Nickname),
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.FriendCode), setting.FriendCode);
            FixedUtf8(Bytes(ref impl.ThumbnailUrl), setting.ThumbnailUrl);

            return impl;
        }

        /// <summary>The same with the wider play-log entries (20802).</summary>
        public static UserSettingImplV2 ToUserSettingImplV2(BaasUserSetting setting, Uid userId)
        {
            UserSettingImplV2 impl = new()
            {
                UserId = userId,
                PresencePermission = (uint)setting.PresencePermission,
                PlayLogPermission = (uint)setting.PlayLogPermission,
                FriendRequestReception = setting.FriendRequestReception,
                FriendCodeRegenerableAt = setting.FriendCodeRegenerableAt,
                NetworkUserId = new NetworkServiceAccountId(setting.Id),
                Nickname = ToNickname(setting.Nickname),
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.FriendCode), setting.FriendCode);
            FixedUtf8(Bytes(ref impl.ThumbnailUrl), setting.ThumbnailUrl);

            return impl;
        }

        /// <summary>
        /// One friend as a viewer port reads them (20105 / 20106): the thumbnail URL is in the
        /// struct, and the presence blob is never filtered — a viewer is trusted with it.
        /// </summary>
        public static FriendForViewerImpl ToFriendForViewerImpl(BaasFriend friend, Uid userId)
        {
            FriendImpl basis = ToFriendImpl(friend, userId, viewer: true);

            FriendForViewerImpl impl = new()
            {
                UserId = userId,
                NetworkUserId = basis.NetworkUserId,
                Nickname = basis.Nickname,
                Presence = basis.Presence,
                IsFavourite = basis.IsFavourite,
                IsNew = basis.IsNew,
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.ThumbnailUrl), friend.ThumbnailUrl);

            return impl;
        }

        /// <summary>
        /// The caller's own presence slot (20600): what this console last published, which is
        /// PLAYING while a title has declared an online-play session, ONLINE while one runs
        /// without, and INACTIVE when none does.
        /// </summary>
        public static UserPresenceViewImpl OwnPresenceView()
        {
            (ulong applicationId, ulong presenceGroupId) = OpenPakPresence.Application();

            OpenPakPresence.State declared = OpenPakPresence.For(OpenPakConfig.ProfileId,
                TitleIDs.CurrentApplication.Value.OrDefault());

            UserPresenceViewImpl view = new()
            {
                ApplicationId = applicationId,
                PresenceGroupId = presenceGroupId,
                LastUpdateTimestamp = declared.UpdatedAt,
                State = applicationId == 0 ? 0u : declared.SessionOpen ? 2u : 1u,
            };

            if (applicationId != 0)
            {
                AppFieldToBlob(declared.AppField, Bytes(ref view.AppKeyValueStorage));
            }

            return view;
        }

        /// <summary>One friend's profile page (20102 / 20107).</summary>
        public static FriendDetailedInfoImpl ToDetailedInfoImpl(BaasFriend friend, Uid userId)
        {
            FriendDetailedInfoImpl impl = new()
            {
                UserId = userId,
                NetworkUserId = new NetworkServiceAccountId(friend.Id),
                Nickname = ToNickname(friend.Nickname),
                Channel = (uint)friend.Channel,
                IsValid = true,
            };

            FixedUtf8(Bytes(ref impl.ThumbnailUrl), friend.ThumbnailUrl);

            return impl;
        }

        /// <summary>The per-friend flags (20110).</summary>
        public static FriendSettingImpl ToFriendSettingImpl(BaasFriend friend, Uid userId)
            => new()
            {
                UserId = userId,
                NetworkUserId = new NetworkServiceAccountId(friend.Id),
                IsFavourite = friend.IsFavorite,
                IsNew = friend.IsNewly,
                IsOnlineNotification = friend.IsOnlineNotification,
            };

        /// <summary>The same plus the private note (20111).</summary>
        public static FriendSettingImplV2 ToFriendSettingImplV2(BaasFriend friend, Uid userId)
        {
            FriendSettingImplV2 impl = new()
            {
                UserId = userId,
                NetworkUserId = new NetworkServiceAccountId(friend.Id),
                IsFavourite = friend.IsFavorite,
                IsNew = friend.IsNewly,
                IsOnlineNotification = friend.IsOnlineNotification,
            };

            FixedUtf8(Bytes(ref impl.FriendNote), friend.Note);

            return impl;
        }

        /// <summary>One waiting invitation as 22000 returns it.</summary>
        public static FriendInvitationForViewerImpl ToInvitationImpl(BaasInvitation invitation)
        {
            FriendInvitationForViewerImpl impl = new()
            {
                InvitationId = invitation.Id,
                GroupId = invitation.GroupId,
                SenderId = invitation.SenderId,
                ApplicationId = invitation.ApplicationId,
                ApplicationGroupId = invitation.ApplicationGroupId,
                ApplicationDataSize = (uint)Math.Min(invitation.ApplicationData.Length, 0x400),
                CreatedAt = invitation.CreatedAt,
                IsRead = invitation.Read,
                ApplicationIdMatch = invitation.ApplicationIdMatch,
                IsValid = true,
            };

            invitation.ApplicationData.AsSpan(0, (int)impl.ApplicationDataSize)
                .CopyTo(Bytes(ref impl.ApplicationData));

            return impl;
        }

        /// <summary>The same with the acd index (22002).</summary>
        public static FriendInvitationForViewerImplV2 ToInvitationImplV2(BaasInvitation invitation)
        {
            FriendInvitationForViewerImplV2 impl = new()
            {
                InvitationId = invitation.Id,
                GroupId = invitation.GroupId,
                SenderId = invitation.SenderId,
                ApplicationId = invitation.ApplicationId,
                AcdIndex = invitation.AcdIndex,
                ApplicationGroupId = invitation.ApplicationGroupId,
                ApplicationDataSize = (uint)Math.Min(invitation.ApplicationData.Length, 0x400),
                CreatedAt = invitation.CreatedAt,
                IsRead = invitation.Read,
                ApplicationIdMatch = invitation.ApplicationIdMatch,
                IsValid = true,
            };

            invitation.ApplicationData.AsSpan(0, (int)impl.ApplicationDataSize)
                .CopyTo(Bytes(ref impl.ApplicationData));

            return impl;
        }

        /// <summary>One invitation group in full, as 22001 returns it.</summary>
        public static FriendInvitationGroupImpl ToInvitationGroupImpl(BaasInvitationGroup group)
        {
            FriendInvitationGroupImpl impl = new()
            {
                GroupId = group.Id,
                SenderId = group.SenderId,
                ApplicationId = group.ApplicationId,
                ApplicationGroupId = group.ApplicationGroupId,
                ApplicationDataSize = (uint)Math.Min(group.ApplicationData.Length, 0x400),
                CreatedAt = group.CreatedAt,
                ApplicationIdMatch = group.ApplicationIdMatch,
                IsValid = true,
            };

            impl.ReceiverCount = Receivers(group, ref impl.Receivers);

            Messages(group, Bytes(ref impl.Messages));

            group.ApplicationData.AsSpan(0, (int)impl.ApplicationDataSize)
                .CopyTo(Bytes(ref impl.ApplicationData));

            return impl;
        }

        /// <summary>The same with the acd index (22003).</summary>
        public static FriendInvitationGroupImplV2 ToInvitationGroupImplV2(BaasInvitationGroup group)
        {
            FriendInvitationGroupImplV2 impl = new()
            {
                GroupId = group.Id,
                SenderId = group.SenderId,
                ApplicationId = group.ApplicationId,
                AcdIndex = group.AcdIndex,
                ApplicationGroupId = group.ApplicationGroupId,
                ApplicationDataSize = (uint)Math.Min(group.ApplicationData.Length, 0x400),
                CreatedAt = group.CreatedAt,
                ApplicationIdMatch = group.ApplicationIdMatch,
                IsValid = true,
            };

            impl.ReceiverCount = Receivers(group, ref impl.Receivers);

            Messages(group, Bytes(ref impl.Messages));

            group.ApplicationData.AsSpan(0, (int)impl.ApplicationDataSize)
                .CopyTo(Bytes(ref impl.ApplicationData));

            return impl;
        }

        /// <summary>
        /// The message slots a game filled, as the send body carries them (invitations doc §3c):
        /// the module's language key and the text, and only for the slots that hold one.
        /// </summary>
        public static List<(string Language, string Text)> InvitationMessages(in FriendInvitationGameModeDescription description)
        {
            ReadOnlySpan<byte> slots = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in description), 1));

            List<(string, string)> messages = [];

            for (int index = 0; index < 16 && index < OpenPakBaas.MessageLanguages.Length; index++)
            {
                ReadOnlySpan<byte> slot = slots.Slice(index * 0xC0, 0xC0);
                int end = slot.IndexOf((byte)0);

                string text = Encoding.UTF8.GetString(end < 0 ? slot : slot[..end]);

                if (text.Length > 0)
                {
                    messages.Add((OpenPakBaas.MessageLanguages[index], text));
                }
            }

            return messages;
        }

        /// <summary>At most sixteen receivers, as the module's parser keeps.</summary>
        private static int Receivers(BaasInvitationGroup group, ref FriendInvitationGroupImpl.ReceiverHolder receivers)
        {
            int count = Math.Min(group.Receivers.Count, 16);

            for (int index = 0; index < count; index++)
            {
                receivers[index] = group.Receivers[index];
            }

            return count;
        }

        /// <summary>The sixteen 0xC0 message slots, in the module's language order.</summary>
        private static void Messages(BaasInvitationGroup group, Span<byte> slots)
        {
            for (int index = 0; index < 16 && index < group.Messages.Count; index++)
            {
                FixedUtf8(slots.Slice(index * 0xC0, 0xC0), group.Messages[index]);
            }
        }
    }
}
