using Ryujinx.Common.Logging;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.OsTypes;
using Ryujinx.Horizon.Sdk.Settings;
using Ryujinx.Horizon.Sdk.Sf;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Horizon.Sdk.Friends.Detail.Ipc
{
    partial class FriendService : IFriendService, IDisposable
    {
        private readonly IEmulatorAccountManager _accountManager;
        private readonly FriendsServicePermissionLevel _permissionLevel;
        private SystemEventType _completionEvent;

        public FriendService(IEmulatorAccountManager accountManager, FriendsServicePermissionLevel permissionLevel)
        {
            _accountManager = accountManager;
            _permissionLevel = permissionLevel;

            Os.CreateSystemEvent(out _completionEvent, EventClearMode.ManualClear, interProcess: true).AbortOnFailure();
            Os.SignalSystemEvent(ref _completionEvent); // TODO: Figure out where we are supposed to signal this.
        }

        /// <summary>
        /// Whether this port carries the viewer bit (friend:v, friend:m, friend:a). A viewer reads
        /// a friend's presence blob whatever group it belongs to; friend:u and friend:s get the
        /// privacy filter (contract §B.3).
        /// </summary>
        private bool Viewer => _permissionLevel.HasFlag(FriendsServicePermissionLevel.ViewerMask);

        /// <summary>The viewer bit, or 2121-0090 (contract §B.1). 20xxx and 22xxx need it.</summary>
        private Result RequireViewer()
            => Viewer ? Result.Success : FriendResult.PermissionDenied;

        /// <summary>The manager bit (friend:m, friend:a), or 2121-0090. Every 30xxx needs it.</summary>
        private Result RequireManager()
            => _permissionLevel.HasFlag(FriendsServicePermissionLevel.ManagerMask) ? Result.Success : FriendResult.PermissionDenied;

        /// <summary>The system bit (friend:s, friend:a), or 2121-0090. 40100/40400/49900 need it.</summary>
        private Result RequireSystem()
            => _permissionLevel.HasFlag(FriendsServicePermissionLevel.SystemMask) ? Result.Success : FriendResult.PermissionDenied;

        [CmifCommand(0)]
        public Result GetCompletionEvent([CopyHandle] out int completionEventHandle)
        {
            completionEventHandle = Os.GetReadableHandleOfSystemEvent(ref _completionEvent);

            return Result.Success;
        }

        [CmifCommand(1)]
        public Result Cancel()
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend);

            return Result.Success;
        }

        [CmifCommand(10100)]
        public Result GetFriendListIds(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer)] Span<NetworkServiceAccountId> friendIds,
            Uid userId,
            int offset,
            SizedFriendFilter filter,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            count = 0;

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, offset, filter, pidPlaceholder, pid });

                return Result.Success;
            }

            foreach (BaasFriend friend in OpenPakFriends.Filtered(filter, offset))
            {
                if (count == friendIds.Length)
                {
                    break;
                }

                friendIds[count++] = new NetworkServiceAccountId(friend.Id);
            }

            return Result.Success;
        }

        [CmifCommand(10101)]
        public Result GetFriendList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendImpl> friendList,
            Uid userId,
            int offset,
            SizedFriendFilter filter,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            count = 0;

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, offset, filter, pidPlaceholder, pid });

                return Result.Success;
            }

            foreach (BaasFriend friend in OpenPakFriends.Filtered(filter, offset))
            {
                if (count == friendList.Length)
                {
                    break;
                }

                friendList[count++] = OpenPakFriends.ToFriendImpl(friend, userId, Viewer);
            }

            Logger.Info?.Print(LogClass.ServiceFriend, $"[OpenPak] Served {count} friends to the guest");

            return Result.Success;
        }

        [CmifCommand(10102)]
        public Result UpdateFriendInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendImpl> info,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend,
                    new { userId, friendIdList = string.Join(", ", friendIds.ToArray()), pidPlaceholder, pid });

                return Result.Success;
            }

            // The guest asks about specific ids and expects them back in the order it asked, so
            // this answers per slot rather than filling the buffer with whoever matched.
            List<BaasFriend> friends = OpenPakFriends.Filtered(default, 0);

            for (int index = 0; index < friendIds.Length && index < info.Length; index++)
            {
                info[index] = default;

                foreach (BaasFriend friend in friends)
                {
                    if (friend.Id == friendIds[index].Id)
                    {
                        info[index] = OpenPakFriends.ToFriendImpl(friend, userId, Viewer);

                        break;
                    }
                }
            }

            return Result.Success;
        }

        [CmifCommand(10110)]
        public Result GetFriendProfileImage(
            out int size,
            Uid userId,
            NetworkServiceAccountId friendId,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> profileImage)
        {
            size = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(10120)]
        public Result CheckFriendListAvailability(out bool listAvailable, Uid userId)
        {
            listAvailable = OpenPakFriends.ListAvailableFor(userId);

            return Result.Success;
        }

        [CmifCommand(10121)]
        public Result EnsureFriendListAvailable(Uid userId)
        {
            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            // The console syncs inline when it has no cache. Here the sync is started and the
            // guest answered at once: blocking a game's thread on an HTTP round trip is the one
            // thing this service may never do.
            if (OpenPakFriends.AvailableFor(userId) && !OpenPakBaas.FriendListAvailable)
            {
                _ = OpenPakBaas.SyncFriendListAsync(true, CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(10200)]
        public Result SendFriendRequestForApplication(
            Uid userId,
            NetworkServiceAccountId friendId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg2,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg3,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, arg3, pidPlaceholder, pid });

                return Result.Success;
            }

            // The application info comes from the caller's process, and the channel is
            // always IN_APP: this is the in-game "send friend request" button.
            ulong titleId = OpenPakFriends.OwnTitleId();
            (string targetName, string targetLanguage) = OpenPakFriends.ScreenName(arg2);
            (string ownName, string ownLanguage) = OpenPakFriends.ScreenName(arg3);

            // Fire and forget: the POST is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.SendFriendRequest?.Invoke(new BaasFriendRequestSend(friendId.Id, "IN_APP")
            {
                ApplicationId = titleId,
                PresenceGroupId = titleId,
                TargetName = targetName,
                TargetLanguage = targetLanguage,
                OwnName = ownName,
                OwnLanguage = ownLanguage,
            });

            return Result.Success;
        }

        [CmifCommand(10211)]
        public Result AddFacedFriendRequestForApplication(
            Uid userId,
            FacedFriendRequestRegistrationKey key,
            Nickname nickname,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> arg3,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg5,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, key, nickname, arg4, arg5, pidPlaceholder, pid });

            return Result.Success;
        }

        [CmifCommand(10400)]
        public Result GetBlockedUserListIds(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer)] Span<NetworkServiceAccountId> blockedIds,
            Uid userId,
            int offset)
        {
            count = 0;

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            IReadOnlyList<BaasBlock> blocks = OpenPakBaas.Blocks;

            for (int index = Math.Max(offset, 0); index < blocks.Count && count < blockedIds.Length; index++)
            {
                blockedIds[count++] = new NetworkServiceAccountId(blocks[index].Id);
            }

            return Result.Success;
        }

        [CmifCommand(10420)]
        public Result CheckBlockedUserListAvailability(out bool listAvailable, Uid userId)
        {
            listAvailable = OpenPakFriends.AvailableFor(userId) && OpenPakBaas.BlockListAvailable;

            return Result.Success;
        }

        [CmifCommand(10421)]
        public Result EnsureBlockedUserListAvailable(Uid userId)
        {
            if (OpenPakFriends.AvailableFor(userId) && !OpenPakBaas.BlockListAvailable)
            {
                _ = OpenPakBaas.SyncBlockListAsync(CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(10500)]
        public Result GetProfileList(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<ProfileImpl> profileList,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds)
        {
            // Answered per slot, in the order asked, as the module matches its results by id.
            // A user nobody has looked up yet is left invalid and fetched for the next call.
            for (int index = 0; index < friendIds.Length && index < profileList.Length; index++)
            {
                profileList[index] = User(friendIds[index].Id) is { } user
                    ? OpenPakFriends.ToProfileImpl(user)
                    : default;
            }

            Warm(friendIds);

            return Result.Success;
        }

        /// <summary>
        /// One user for the profile commands: a friend out of the list cache, else whatever the
        /// user cache has. Both carry the same three fields the flat user shape requires.
        /// </summary>
        private static BaasUser User(ulong id)
        {
            if (OpenPakBaas.Friend(id) is { } friend)
            {
                return new BaasUser(friend.Id, friend.Nickname, friend.ThumbnailUrl) { PlayLog = friend.PlayLog };
            }

            return OpenPakBaas.User(id);
        }

        /// <summary>Ask for the ids nothing has cached yet, off this thread (§A.7).</summary>
        private static void Warm(ReadOnlySpan<NetworkServiceAccountId> friendIds)
        {
            List<ulong> ids = [];

            foreach (NetworkServiceAccountId friendId in friendIds)
            {
                if (User(friendId.Id) == null)
                {
                    ids.Add(friendId.Id);
                }
            }

            if (ids.Count > 0)
            {
                OpenPakBaas.WarmUsers(ids);
            }
        }

        [CmifCommand(10600)]
        public Result DeclareOpenOnlinePlaySession(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            _accountManager.OpenUserOnlinePlay(userId);

            OpenPakFriends.Declare(userId, 1, null);

            return Result.Success;
        }

        [CmifCommand(10601)]
        public Result DeclareCloseOnlinePlaySession(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            _accountManager.CloseUserOnlinePlay(userId);

            OpenPakFriends.Declare(userId, 2, null);

            return Result.Success;
        }

        [CmifCommand(10610)]
        public Result UpdateUserPresence(
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0xE0)] in UserPresenceImpl userPresence,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            // Commit: the declaration byte and the whole blob, published as PLAYING/ONLINE and an
            // appField object by the session, and only when one of them changed.
            string appField = OpenPakFriends.BlobToAppField(userPresence.AppKeyValueStorage);

            Logger.Debug?.Print(LogClass.ServiceFriend,
                $"UpdateUserPresence: declaration {userPresence.OnlinePlayDeclaration}, appField {appField}");

            OpenPakFriends.Declare(userId, userPresence.OnlinePlayDeclaration, appField);

            return Result.Success;
        }

        [CmifCommand(10700)]
        public Result GetPlayHistoryRegistrationKey(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x40)] out PlayHistoryRegistrationKey registrationKey,
            Uid userId,
            bool arg2)
        {
            if (userId.IsNull)
            {
                registrationKey = default;

                return FriendResult.InvalidArgument;
            }

            // NOTE: Calls nn::friends::detail::service::core::PlayHistoryManager::GetInstance and stores the instance.

            // NOTE: Calls nn::friends::detail::service::core::UuidManager::GetInstance and stores the instance.
            //       Then calls nn::friends::detail::service::core::AccountStorageManager::GetInstance and stores the instance.
            //       Then it checks if an Uuid is already stored for the UserId, if not it generates a random Uuid,
            //       and stores it in the savedata 8000000000000080 in the friends:/uid.bin file.

            /*

            NOTE: The service uses the KeyIndex to get a random key from a keys buffer (since the key index is stored in the returned buffer).
                  We currently don't support play history and online services so we can use a blank key for now.
                  Code for reference:

            byte[] hmacKey = new byte[0x20];

            HMACSHA256 hmacSha256 = new HMACSHA256(hmacKey);
            byte[]     hmacHash   = hmacSha256.ComputeHash(playHistoryRegistrationKeyBuffer);

            */

            Uid randomGuid = new();

            Guid.NewGuid().TryWriteBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref randomGuid, 1)));

            registrationKey = new()
            {
                Type = 0x101,
                KeyIndex = (byte)(Random.Shared.Next() & 7),
                UserIdBool = 0, // TODO: Find it.
                UnknownBool = (byte)(arg2 ? 1 : 0), // TODO: Find it.
                Reserved = new(),
                Uuid = randomGuid,
                HmacHash = new(),
            };

            return Result.Success;
        }

        [CmifCommand(10701)]
        public Result GetPlayHistoryRegistrationKeyWithNetworkServiceAccountId(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x40)] out PlayHistoryRegistrationKey registrationKey,
            NetworkServiceAccountId friendId,
            bool arg2)
        {
            registrationKey = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { friendId, arg2 });

            return Result.Success;
        }

        [CmifCommand(10702)]
        public Result AddPlayHistory(
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x40)] in PlayHistoryRegistrationKey registrationKey,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg2,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg3,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, registrationKey, arg2, arg3, pidPlaceholder, pid });

            return Result.Success;
        }

        [CmifCommand(11000)]
        public Result GetProfileImageUrl(out Url imageUrl, Url url, int arg2)
        {
            imageUrl = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { url, arg2 });

            return Result.Success;
        }

        [CmifCommand(20100)]
        public Result GetFriendCount(out int count, Uid userId, SizedFriendFilter filter, ulong pidPlaceholder, [ClientProcessId] ulong pid)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            count = OpenPakFriends.AvailableFor(userId) ? OpenPakFriends.Filtered(filter, 0).Count : 0;

            return Result.Success;
        }

        [CmifCommand(20101)]
        public Result GetNewlyFriendCount(out int count, Uid userId)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            // "Newly" is extras.self.isConfirmed absent or false (§A.1): a friendship the person
            // has not yet looked at, which is what the badge on the friend list counts.
            foreach (BaasFriend friend in OpenPakBaas.Friends)
            {
                if (friend.IsNewly)
                {
                    count++;
                }
            }

            return Result.Success;
        }

        [CmifCommand(20102)]
        public Result GetFriendDetailedInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x800)] out FriendDetailedInfoImpl detailedInfo,
            Uid userId,
            NetworkServiceAccountId friendId)
        {
            detailedInfo = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId) || OpenPakBaas.Friend(friendId.Id) is not { } friend)
            {
                return FriendResult.From(OpenPakBaas.FriendNotFound);
            }

            detailedInfo = OpenPakFriends.ToDetailedInfoImpl(friend, userId);

            return Result.Success;
        }

        [CmifCommand(20107)]
        public Result GetFriendDetailedInfoV2(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x800)] out FriendDetailedInfoImpl detailedInfo,
            Uid userId,
            NetworkServiceAccountId friendId)
            => GetFriendDetailedInfo(out detailedInfo, userId, friendId);

        [CmifCommand(20103)]
        public Result SyncFriendList(Uid userId)
        {
            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            // The console clears the cooldown and syncs inline; here the sync is started and the
            // guest answered at once. The list on screen refreshes through the notification event.
            if (OpenPakFriends.AvailableFor(userId))
            {
                _ = OpenPakBaas.SyncFriendListAsync(true, CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(20104)]
        public Result RequestSyncFriendList(Uid userId) => SyncFriendList(userId);

        [CmifCommand(20105)]
        public Result GetFriendListForViewer(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendForViewerImpl> friendList,
            Uid userId,
            int offset,
            SizedFriendFilter filter)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            foreach (BaasFriend friend in OpenPakFriends.Filtered(filter, offset))
            {
                if (count == friendList.Length)
                {
                    break;
                }

                friendList[count++] = OpenPakFriends.ToFriendForViewerImpl(friend, userId);
            }

            return Result.Success;
        }

        [CmifCommand(20106)]
        public Result UpdateFriendInfoForViewer(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendForViewerImpl> info,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds)
        {
            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            for (int index = 0; index < friendIds.Length && index < info.Length; index++)
            {
                info[index] = OpenPakFriends.AvailableFor(userId) && OpenPakBaas.Friend(friendIds[index].Id) is { } friend
                    ? OpenPakFriends.ToFriendForViewerImpl(friend, userId)
                    : default;
            }

            return Result.Success;
        }

        // 20108 / 20109 are the 0x220 viewer shape. Its first part is the same, but the audit
        // does not pin where the valid flag sits in it, and a friend written without one is a
        // friend the caller drops — so the commands exist (a missing id is a CMIF error the
        // caller cannot tell from a real failure) and answer an empty list.
        [CmifCommand(20108)]
        public Result GetFriendListForViewerV2(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> friendList,
            Uid userId,
            int offset,
            SizedFriendFilter filter)
        {
            count = 0;

            return RequireViewer();
        }

        [CmifCommand(20109)]
        public Result UpdateFriendInfoForViewerV2(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> info,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds)
            => RequireViewer();

        [CmifCommand(20110)]
        public Result LoadFriendSetting(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x40)] out FriendSettingImpl friendSetting,
            Uid userId,
            NetworkServiceAccountId friendId)
        {
            friendSetting = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId) || OpenPakBaas.Friend(friendId.Id) is not { } friend)
            {
                return FriendResult.From(OpenPakBaas.FriendNotFound);
            }

            friendSetting = OpenPakFriends.ToFriendSettingImpl(friend, userId);

            return Result.Success;
        }

        [CmifCommand(20111)]
        public Result LoadFriendSettingV2(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x80)] out FriendSettingImplV2 friendSetting,
            Uid userId,
            NetworkServiceAccountId friendId)
        {
            friendSetting = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId) || OpenPakBaas.Friend(friendId.Id) is not { } friend)
            {
                return FriendResult.From(OpenPakBaas.FriendNotFound);
            }

            friendSetting = OpenPakFriends.ToFriendSettingImplV2(friend, userId);

            return Result.Success;
        }

        [CmifCommand(20200)]
        public Result GetReceivedFriendRequestCount(out int count, out int count2, Uid userId)
        {
            count = 0;
            count2 = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

                return Result.Success;
            }

            // The badge (contract §A.4.3): unread is the inbox items without
            // extras.receiver.read true, read the ones with it.
            foreach (BaasRequest request in OpenPakAccount.Instance.InboxRequests)
            {
                if (request.Read)
                {
                    count2++;
                }
                else
                {
                    count++;
                }
            }

            return Result.Success;
        }

        [CmifCommand(20201)]
        public Result GetFriendRequestList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendRequestImpl> requestList,
            Uid userId,
            int offset,
            int listType)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, offset, listType });

                return Result.Success;
            }

            // Type 1 is the outbox (sent), 2 the inbox (received). Type 0 is in-person
            // requests, which live in faced.v2.bin and never touch REST.
            IReadOnlyList<BaasRequest> box = listType switch
            {
                1 => OpenPakAccount.Instance.OutboxRequests,
                2 => OpenPakAccount.Instance.InboxRequests,
                _ => [],
            };

            for (int index = offset; index < box.Count && count < requestList.Length; index++)
            {
                if (index < 0)
                {
                    continue;
                }

                requestList[count++] = OpenPakFriends.ToRequestImpl(box[index], userId, listType);
            }

            return Result.Success;
        }

        [CmifCommand(20202)]
        public Result GetFriendRequestListV2(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendRequestImplV2> requestList,
            Uid userId,
            int offset,
            int listType)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, offset, listType });

                return Result.Success;
            }

            IReadOnlyList<BaasRequest> box = listType switch
            {
                1 => OpenPakAccount.Instance.OutboxRequests,
                2 => OpenPakAccount.Instance.InboxRequests,
                _ => [],
            };

            for (int index = offset; index < box.Count && count < requestList.Length; index++)
            {
                if (index < 0)
                {
                    continue;
                }

                requestList[count++] = OpenPakFriends.ToRequestImplV2(box[index], userId, listType);
            }

            return Result.Success;
        }

        // 20203 counts the `friend_request_received` pushes this console has seen. Nothing here
        // holds a push connection — the boxes are polled — so the counter is always 0, which is
        // what a console that has been pushed nothing reports.
        [CmifCommand(20203)]
        public Result GetReceivedFriendRequestPushCount(out int count, Uid userId)
        {
            count = 0;

            return RequireViewer();
        }

        [CmifCommand(20300)]
        public Result GetFriendCandidateList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendCandidateImpl> candidateList,
            Uid userId,
            int arg3)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20301)]
        public Result GetNintendoNetworkIdInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x38)] out NintendoNetworkIdUserInfo networkIdInfo,
            out int arg1,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<NintendoNetworkIdFriendImpl> friendInfo,
            Uid userId,
            int arg4)
        {
            networkIdInfo = default;
            arg1 = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg4 });

            return Result.Success;
        }

        [CmifCommand(20302)]
        public Result GetSnsAccountLinkage(out SnsAccountLinkage accountLinkage, Uid userId)
        {
            accountLinkage = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20303)]
        public Result GetSnsAccountProfile(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x380)] out SnsAccountProfile accountProfile,
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg3)
        {
            accountProfile = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20304)]
        public Result GetSnsAccountFriendList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<SnsAccountFriendImpl> friendList,
            Uid userId,
            int arg3)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20400)]
        public Result GetBlockedUserList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<BlockedUserImpl> blockedUsers,
            Uid userId,
            int offset)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            IReadOnlyList<BaasBlock> blocks = OpenPakBaas.Blocks;

            for (int index = Math.Max(offset, 0); index < blocks.Count && count < blockedUsers.Length; index++)
            {
                blockedUsers[count++] = OpenPakFriends.ToBlockedUserImpl(blocks[index], userId);
            }

            return Result.Success;
        }

        [CmifCommand(20402)]
        public Result GetBlockedUserListV2(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<BlockedUserImplV2> blockedUsers,
            Uid userId,
            int offset)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            IReadOnlyList<BaasBlock> blocks = OpenPakBaas.Blocks;

            for (int index = Math.Max(offset, 0); index < blocks.Count && count < blockedUsers.Length; index++)
            {
                blockedUsers[count++] = OpenPakFriends.ToBlockedUserImplV2(blocks[index], userId);
            }

            return Result.Success;
        }

        [CmifCommand(20401)]
        public Result SyncBlockedUserList(Uid userId)
        {
            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                _ = OpenPakBaas.SyncBlockListAsync(CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(20500)]
        public Result GetProfileExtraList(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<ProfileExtraImpl> extraList,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds)
        {
            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            for (int index = 0; index < friendIds.Length && index < extraList.Length; index++)
            {
                extraList[index] = User(friendIds[index].Id) is { } user
                    ? OpenPakFriends.ToProfileExtraImpl(user)
                    : default;
            }

            Warm(friendIds);

            return Result.Success;
        }

        [CmifCommand(20502)]
        public Result GetProfileExtraListV2(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<ProfileExtraImplV2> extraList,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds)
        {
            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            for (int index = 0; index < friendIds.Length && index < extraList.Length; index++)
            {
                extraList[index] = User(friendIds[index].Id) is { } user
                    ? OpenPakFriends.ToProfileExtraImplV2(user)
                    : default;
            }

            Warm(friendIds);

            return Result.Success;
        }

        [CmifCommand(20501)]
        public Result GetRelationship(out Relationship relationship, Uid userId, NetworkServiceAccountId friendId)
        {
            relationship = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            // Answered from the caches the server already filled rather than with a round trip:
            // the friend list, the block list, and the sent-request box (§A.7).
            relationship.IsFriend = OpenPakBaas.Friend(friendId.Id) != null;

            foreach (BaasBlock block in OpenPakBaas.Blocks)
            {
                if (block.Id == friendId.Id)
                {
                    relationship.IsBlocking = true;

                    break;
                }
            }

            foreach (BaasRequest request in OpenPakAccount.Instance.OutboxRequests)
            {
                if (request.OtherId == friendId.Id)
                {
                    relationship.IsRequestSent = true;

                    break;
                }
            }

            return Result.Success;
        }

        [CmifCommand(20600)]
        public Result GetUserPresenceView([Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0xE0)] out UserPresenceViewImpl userPresenceView, Uid userId)
        {
            userPresenceView = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            userPresenceView = OpenPakFriends.OwnPresenceView();

            return Result.Success;
        }

        [CmifCommand(20700)]
        public Result GetPlayHistoryList(out int count, [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<PlayHistoryImpl> playHistoryList, Uid userId, int arg3)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20701)]
        public Result GetPlayHistoryStatistics(out PlayHistoryStatistics statistics, Uid userId)
        {
            statistics = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20800)]
        public Result LoadUserSetting([Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x800)] out UserSettingImpl userSetting, Uid userId)
        {
            userSetting = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId) || OpenPakBaas.UserSetting is not { } setting)
            {
                return Result.Success;
            }

            userSetting = OpenPakFriends.ToUserSettingImpl(setting, userId);

            return Result.Success;
        }

        [CmifCommand(20802)]
        public Result LoadUserSettingV2([Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x800)] out UserSettingImplV2 userSetting, Uid userId)
        {
            userSetting = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId) || OpenPakBaas.UserSetting is not { } setting)
            {
                return Result.Success;
            }

            userSetting = OpenPakFriends.ToUserSettingImplV2(setting, userId);

            return Result.Success;
        }

        [CmifCommand(20801)]
        public Result SyncUserSetting(Uid userId)
        {
            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                _ = OpenPakBaas.SyncUserSettingAsync(CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(20900)]
        public Result RequestListSummaryOverlayNotification()
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend);

            return Result.Success;
        }

        [CmifCommand(21000)]
        public Result GetExternalApplicationCatalog(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x4B8)] out ExternalApplicationCatalog catalog,
            ExternalApplicationCatalogId catalogId,
            LanguageCode language)
        {
            catalog = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { catalogId, language });

            return Result.Success;
        }

        [CmifCommand(22000)]
        public Result GetReceivedFriendInvitationList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendInvitationForViewerImpl> invitationList,
            Uid userId)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            // Read and unread both: the inbox request the module makes carries no read filter,
            // and each item says which it is.
            foreach (BaasInvitation invitation in OpenPakBaas.Invitations)
            {
                if (count == invitationList.Length)
                {
                    break;
                }

                invitationList[count++] = OpenPakFriends.ToInvitationImpl(invitation);
            }

            return Result.Success;
        }

        [CmifCommand(22002)]
        public Result GetReceivedFriendInvitationListV2(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendInvitationForViewerImplV2> invitationList,
            Uid userId)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            foreach (BaasInvitation invitation in OpenPakBaas.Invitations)
            {
                if (count == invitationList.Length)
                {
                    break;
                }

                invitationList[count++] = OpenPakFriends.ToInvitationImplV2(invitation);
            }

            return Result.Success;
        }

        [CmifCommand(22001)]
        public Result GetReceivedFriendInvitationDetailedInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0x1400)] out FriendInvitationGroupImpl invitationGroup,
            Uid userId,
            FriendInvitationGroupId groupId)
        {
            invitationGroup = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId) || OpenPakBaas.InvitationGroup(groupId.Id) is not { } group)
            {
                return FriendResult.From(OpenPakBaas.InvalidArgument);
            }

            invitationGroup = OpenPakFriends.ToInvitationGroupImpl(group);

            return Result.Success;
        }

        [CmifCommand(22003)]
        public Result GetReceivedFriendInvitationDetailedInfoV2(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0x1400)] out FriendInvitationGroupImplV2 invitationGroup,
            Uid userId,
            FriendInvitationGroupId groupId)
        {
            invitationGroup = default;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId) || OpenPakBaas.InvitationGroup(groupId.Id) is not { } group)
            {
                return FriendResult.From(OpenPakBaas.InvalidArgument);
            }

            invitationGroup = OpenPakFriends.ToInvitationGroupImplV2(group);

            return Result.Success;
        }

        [CmifCommand(22010)]
        public Result GetReceivedFriendInvitationCountCache(out int count, Uid userId)
        {
            count = 0;

            if (RequireViewer() is { IsSuccess: false } denied)
            {
                return denied;
            }

            count = OpenPakFriends.AvailableFor(userId) ? OpenPakAccount.Instance.NativeInvitationsUnread : 0;

            return Result.Success;
        }

        [CmifCommand(30100)]
        public Result DropFriendNewlyFlags(Uid userId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            foreach (BaasFriend friend in OpenPakBaas.Friends)
            {
                if (friend.IsNewly)
                {
                    DropNewly(friend.Id);
                }
            }

            return Result.Success;
        }

        [CmifCommand(30101)]
        public Result DeleteFriend(Uid userId, NetworkServiceAccountId friendId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            // Fire and forget: the DELETE is a network call and this is the game's thread. The
            // friendship disappears from the cache because the sync that follows it no longer
            // carries it, which is how the console loses it too (§A.3).
            _ = OpenPakBaas.DeleteFriendAsync(friendId.Id, CancellationToken.None);

            return Result.Success;
        }

        [CmifCommand(30110)]
        public Result DropFriendNewlyFlag(Uid userId, NetworkServiceAccountId friendId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                DropNewly(friendId.Id);
            }

            return Result.Success;
        }

        /// <summary>
        /// One friend is no longer "newly" (§A.2): the flag is cleared locally at once — the
        /// module does that before it sends anything — and the PATCH replaces the cached entry
        /// with the relationship the server answers with.
        /// </summary>
        private static void DropNewly(ulong friendId)
        {
            OpenPakBaas.Update(friendId, friend => friend with { IsNewly = false });

            _ = OpenPakBaas.PatchFriendAsync(friendId, "add", "/extras/self/isConfirmed",
                writer => writer.WriteBooleanValue(true), CancellationToken.None);
        }

        [CmifCommand(30120)]
        public Result ChangeFriendFavoriteFlag(Uid userId, NetworkServiceAccountId friendId, bool favoriteFlag)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            OpenPakBaas.Update(friendId.Id, friend => friend with { IsFavorite = favoriteFlag });

            _ = OpenPakBaas.PatchFriendAsync(friendId.Id, "replace", "/isFavorite",
                writer => writer.WriteBooleanValue(favoriteFlag), CancellationToken.None);

            return Result.Success;
        }

        [CmifCommand(30121)]
        public Result ChangeFriendOnlineNotificationFlag(Uid userId, NetworkServiceAccountId friendId, bool onlineNotificationFlag)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            OpenPakBaas.Update(friendId.Id, friend => friend with { IsOnlineNotification = onlineNotificationFlag });

            _ = OpenPakBaas.PatchFriendAsync(friendId.Id, "add", "/extras/self/isOnlineNotification",
                writer => writer.WriteBooleanValue(onlineNotificationFlag), CancellationToken.None);

            return Result.Success;
        }

        [CmifCommand(30130)]
        public Result ChangeFriendNote(Uid userId, NetworkServiceAccountId friendId, FriendNote note)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            string text = OpenPakFriends.Text(note.Note);

            OpenPakBaas.Update(friendId.Id, friend => friend with { Note = text });

            _ = OpenPakBaas.PatchFriendAsync(friendId.Id, "replace", "/friendNote",
                writer => writer.WriteStringValue(text), CancellationToken.None);

            return Result.Success;
        }

        // Nothing here keeps a queue of pending friend changes: every 301xx write goes out the
        // moment it is made. So a flush has nothing to flush, and says so by succeeding. 30131 is
        // the same command without the port check.
        [CmifCommand(30190)]
        public Result SendPendingFriendChange(Uid userId, NetworkServiceAccountId friendId) => RequireManager();

        [CmifCommand(30131)]
        public Result SendPendingFriendChangeUnchecked(Uid userId, NetworkServiceAccountId friendId) => Result.Success;

        [CmifCommand(30200)]
        public Result SendFriendRequest(Uid userId, NetworkServiceAccountId friendId, int channel)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, channel });

                return Result.Success;
            }

            // Fire and forget: the POST is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.SendFriendRequest?.Invoke(
                new BaasFriendRequestSend(friendId.Id, RequestChannel(channel)));

            return Result.Success;
        }

        [CmifCommand(30201)]
        public Result SendFriendRequestWithApplicationInfo(
            Uid userId,
            NetworkServiceAccountId friendId,
            int channel,
            ApplicationInfo applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg5)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, channel, applicationInfo, arg4, arg5 });

                return Result.Success;
            }

            // Screen name #1 is the target's name as the sender saw it, #2 the sender's
            // own in-app name — the buffers arrive in that order.
            (string targetName, string targetLanguage) = OpenPakFriends.ScreenName(arg4);
            (string ownName, string ownLanguage) = OpenPakFriends.ScreenName(arg5);

            // Fire and forget: the POST is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.SendFriendRequest?.Invoke(
                new BaasFriendRequestSend(friendId.Id, RequestChannel(channel))
                {
                    ApplicationId = applicationInfo.ApplicationId.Id,
                    PresenceGroupId = applicationInfo.PresenceGroupId,
                    TargetName = targetName,
                    TargetLanguage = targetLanguage,
                    OwnName = ownName,
                    OwnLanguage = ownLanguage,
                });

            return Result.Success;
        }

        /// <summary>
        /// The IPC channel number as the channel string the POST carries. The table is the
        /// module's 1-based one (contract §A.1); anything else goes out as FRIEND_CODE
        /// rather than failing a request the person asked to send.
        /// </summary>
        private static string RequestChannel(int channel)
        {
            if (channel is >= 1 and <= 10)
            {
                return OpenPakBaas.Channels[channel - 1];
            }

            Logger.Warning?.Print(LogClass.ServiceFriend,
                $"[OpenPak] Unknown friend-request channel {channel}; sending as FRIEND_CODE");

            return "FRIEND_CODE";
        }

        [CmifCommand(30202)]
        public Result CancelFriendRequest(Uid userId, RequestId requestId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

                return Result.Success;
            }

            // Fire and forget: the PATCH is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.AnswerFriendRequest?.Invoke(requestId.Id, "CANCELED");

            return Result.Success;
        }

        [CmifCommand(30203)]
        public Result AcceptFriendRequest(Uid userId, RequestId requestId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

                return Result.Success;
            }

            // Fire and forget: the PATCH is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.AnswerFriendRequest?.Invoke(requestId.Id, "AUTHORIZED");

            return Result.Success;
        }

        [CmifCommand(30204)]
        public Result RejectFriendRequest(Uid userId, RequestId requestId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

                return Result.Success;
            }

            // Fire and forget: the PATCH is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.AnswerFriendRequest?.Invoke(requestId.Id, "REJECTED");

            return Result.Success;
        }

        [CmifCommand(30205)]
        public Result ReadFriendRequest(Uid userId, RequestId requestId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

                return Result.Success;
            }

            // Fire and forget: the PATCH is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.ReadFriendRequest?.Invoke(requestId.Id);

            return Result.Success;
        }

        [CmifCommand(30210)]
        public Result GetFacedFriendRequestRegistrationKey(out FacedFriendRequestRegistrationKey registrationKey, Uid userId)
        {
            registrationKey = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30211)]
        public Result AddFacedFriendRequest(
            Uid userId,
            FacedFriendRequestRegistrationKey registrationKey,
            Nickname nickname,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> arg3)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, registrationKey, nickname });

            return Result.Success;
        }

        [CmifCommand(30212)]
        public Result CancelFacedFriendRequest(Uid userId, NetworkServiceAccountId friendId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30213)]
        public Result GetFacedFriendRequestProfileImage(
            out int size,
            Uid userId,
            NetworkServiceAccountId friendId,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> profileImage)
        {
            size = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30214)]
        public Result GetFacedFriendRequestProfileImageFromPath(
            out int size,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<byte> path,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> profileImage)
        {
            size = 0;

            string pathString = Encoding.UTF8.GetString(path);

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { pathString });

            return Result.Success;
        }

        [CmifCommand(30215)]
        public Result SendFriendRequestWithExternalApplicationCatalogId(
            Uid userId,
            NetworkServiceAccountId friendId,
            int channel,
            ExternalApplicationCatalogId catalogId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg5)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, channel, catalogId, arg4, arg5 });

                return Result.Success;
            }

            (string targetName, string targetLanguage) = OpenPakFriends.ScreenName(arg4);
            (string ownName, string ownLanguage) = OpenPakFriends.ScreenName(arg5);

            // Fire and forget: the POST is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.SendFriendRequest?.Invoke(
                new BaasFriendRequestSend(friendId.Id, RequestChannel(channel))
                {
                    CatalogId = $"{catalogId.High:x16}{catalogId.Low:x16}",
                    TargetName = targetName,
                    TargetLanguage = targetLanguage,
                    OwnName = ownName,
                    OwnLanguage = ownLanguage,
                });

            return Result.Success;
        }

        [CmifCommand(30218)]
        public Result SendFriendRequestWithApplicationInfoV2(
            Uid userId,
            NetworkServiceAccountId friendId,
            int channel,
            ApplicationInfoV2 applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg5)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, channel, arg4, arg5 });

                return Result.Success;
            }

            (string targetName, string targetLanguage) = OpenPakFriends.ScreenName(arg4);
            (string ownName, string ownLanguage) = OpenPakFriends.ScreenName(arg5);

            // The V2 send is the app route with the acd index the caller named (§A.4.1).
            _ = OpenPakAccount.Instance.SendFriendRequest?.Invoke(
                new BaasFriendRequestSend(friendId.Id, RequestChannel(channel))
                {
                    ApplicationId = applicationInfo.ApplicationId.Id,
                    AcdIndex = applicationInfo.AcdIndex,
                    PresenceGroupId = applicationInfo.PresenceGroupId,
                    TargetName = targetName,
                    TargetLanguage = targetLanguage,
                    OwnName = ownName,
                    OwnLanguage = ownLanguage,
                });

            return Result.Success;
        }

        [CmifCommand(30216)]
        public Result ResendFacedFriendRequest(Uid userId, NetworkServiceAccountId friendId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30217)]
        public Result SendFriendRequestWithNintendoNetworkIdInfo(
            Uid userId,
            NetworkServiceAccountId friendId,
            int channel,
            MiiName arg3,
            MiiImageUrlParam arg4,
            MiiName arg5,
            MiiImageUrlParam arg6)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, channel });

                return Result.Success;
            }

            // The NNID route carries the sender's own Mii name and image parameter — block A of
            // the pair — in both extras halves (§A.4.1).
            _ = OpenPakAccount.Instance.SendFriendRequest?.Invoke(
                new BaasFriendRequestSend(friendId.Id, RequestChannel(channel))
                {
                    MiiName = OpenPakFriends.Text(arg3),
                    MiiImageUrlParam = OpenPakFriends.Text(arg4),
                });

            return Result.Success;
        }

        [CmifCommand(30300)]
        public Result GetSnsAccountLinkPageUrl([Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0x1000)] out WebPageUrl url, Uid userId, int arg2)
        {
            url = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg2 });

            return Result.Success;
        }

        [CmifCommand(30301)]
        public Result UnlinkSnsAccount(Uid userId, int arg1)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg1 });

            return Result.Success;
        }

        [CmifCommand(30400)]
        public Result BlockUser(Uid userId, NetworkServiceAccountId friendId, int arg2)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2 });

                return Result.Success;
            }

            // BlockUser names the reason itself: 1 a bad friend request, 2 a bad friend (§B.4).
            if (arg2 is not (1 or 2))
            {
                return FriendResult.InvalidArgument;
            }

            return Block(friendId, arg2, null);
        }

        [CmifCommand(30401)]
        public Result BlockUserWithApplicationInfo(
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg2,
            ApplicationInfo applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, applicationInfo, arg4 });

                return Result.Success;
            }

            (string name, string language) = OpenPakFriends.ScreenName(arg4);

            // A block from inside a title is IN_APP (3) whatever the caller passed, and carries
            // the title and the in-app name the blocked person went by there (§A.8).
            return Block(friendId, 3, new BaasRoute(applicationInfo.ApplicationId.Id, 0,
                applicationInfo.PresenceGroupId, null, name, language, null, null));
        }

        [CmifCommand(30403)]
        public Result BlockUserWithApplicationInfoV2(
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg2,
            ApplicationInfoV2 applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, arg4 });

                return Result.Success;
            }

            (string name, string language) = OpenPakFriends.ScreenName(arg4);

            return Block(friendId, 3, new BaasRoute(applicationInfo.ApplicationId.Id, applicationInfo.AcdIndex,
                applicationInfo.PresenceGroupId, null, name, language, null, null));
        }

        /// <summary>
        /// Fire and forget, as the other writes are: the POST is a network call and this is the
        /// game's thread. The block is in the cache before this returns (a title reading the list
        /// or the relationship straight after sees it), and the sync that follows the write
        /// replaces it with the server's list, the friend list with it.
        /// </summary>
        private static Result Block(NetworkServiceAccountId friendId, int reason, BaasRoute route)
        {
            if (friendId.Id == 0)
            {
                return FriendResult.InvalidArgument;
            }

            _ = LogFailure(OpenPakBaas.BlockUserAsync(friendId.Id, reason, route, CancellationToken.None),
                $"Blocking {friendId.Id:x16}");

            return Result.Success;
        }

        [CmifCommand(30402)]
        public Result UnblockUser(Uid userId, NetworkServiceAccountId friendId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

                return Result.Success;
            }

            _ = LogFailure(OpenPakBaas.UnblockUserAsync(friendId.Id, CancellationToken.None),
                $"Unblocking {friendId.Id:x16}");

            return Result.Success;
        }

        /// <summary>A background write's result, said out loud when it is not success.</summary>
        private static async Task LogFailure(Task<int> write, string what)
        {
            int result = await write;

            if (result != OpenPakBaas.Ok)
            {
                Logger.Warning?.Print(LogClass.ServiceFriend, $"[OpenPak] {what} failed: 2121-{result:D4}");
            }
        }

        [CmifCommand(30500)]
        public Result GetProfileExtraFromFriendCode(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x400)] out ProfileExtraImpl profileExtra,
            Uid userId,
            FriendCode friendCode)
        {
            profileExtra = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendCode });

            return Result.Success;
        }

        [CmifCommand(30700)]
        public Result DeletePlayHistory(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30810)]
        public Result ChangePresencePermission(Uid userId, int permission)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            string value = permission switch
            {
                0 => "SELF",
                1 => "FAVORITE_FRIENDS",
                2 => "FRIENDS",
                _ => null,
            };

            if (value == null)
            {
                return FriendResult.InvalidArgument;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                _ = OpenPakBaas.PatchUserAsync(
                    OpenPakBaas.UserPatchBody("/permissions/presence", writer => writer.WriteStringValue(value)),
                    CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(30811)]
        public Result ChangeFriendRequestReception(Uid userId, bool reception)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                _ = OpenPakBaas.PatchUserAsync(
                    OpenPakBaas.UserPatchBody("/permissions/friendRequestReception", writer => writer.WriteBooleanValue(reception)),
                    CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(30812)]
        public Result ChangePlayLogPermission(Uid userId, int permission)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (permission is not (1 or 2 or 3 or 5))
            {
                return FriendResult.InvalidArgument;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                // The chosen group keeps the log the server already holds; the other three are
                // emptied, which is what makes the group the permission (§A.6).
                _ = OpenPakBaas.PatchUserAsync(
                    OpenPakBaas.PlayLogPermissionBody(permission, OpenPakBaas.UserSetting?.PlayLogText ?? "[]"),
                    CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(30820)]
        public Result IssueFriendCode(Uid userId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                _ = OpenPakBaas.IssueFriendCodeAsync(CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(30830)]
        public Result ClearPlayLog(Uid userId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            if (OpenPakFriends.AvailableFor(userId))
            {
                // The same four ops with an empty log in the group that carries the permission.
                _ = OpenPakBaas.PatchUserAsync(
                    OpenPakBaas.PlayLogPermissionBody(OpenPakBaas.UserSetting?.PlayLogPermission ?? 0, "[]"),
                    CancellationToken.None);
            }

            return Result.Success;
        }

        [CmifCommand(30900)]
        public Result SendFriendInvitation(
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias, 0xC00)] in FriendInvitationGameModeDescription description,
            ApplicationInfo applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> applicationData,
            bool applicationIdMatch)
            => SendInvitation(userId, friendIds, description, applicationInfo.ApplicationId.Id, 0,
                applicationInfo.PresenceGroupId, applicationData, applicationIdMatch);

        [CmifCommand(30901)]
        public Result SendFriendInvitationV2(
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias, 0xC00)] in FriendInvitationGameModeDescription description,
            ApplicationInfoV2 applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> applicationData,
            bool applicationIdMatch)
            => SendInvitation(userId, friendIds, description, applicationInfo.ApplicationId.Id, applicationInfo.AcdIndex,
                applicationInfo.PresenceGroupId, applicationData, applicationIdMatch);

        /// <summary>
        /// POST /v2/invitation_groups (invitations doc §3c). The ids are the friends' own BAAS
        /// user ids, which is what the guest's friend list serves, so they go out as they arrive.
        /// </summary>
        private Result SendInvitation(
            Uid userId,
            ReadOnlySpan<NetworkServiceAccountId> friendIds,
            in FriendInvitationGameModeDescription description,
            ulong applicationId,
            byte acdIndex,
            ulong presenceGroupId,
            ReadOnlySpan<byte> applicationData,
            bool applicationIdMatch)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            // The module refuses anything outside 1..16 receivers or 0x400 bytes of data.
            if (friendIds.Length is < 1 or > 16 || applicationData.Length > 0x400)
            {
                return FriendResult.InvalidArgument;
            }

            if (!OpenPakFriends.AvailableFor(userId))
            {
                return Result.Success;
            }

            List<string> receivers = [];

            foreach (NetworkServiceAccountId friendId in friendIds)
            {
                receivers.Add(friendId.Id.ToString("x16"));
            }

            List<(string Language, string Text)> messages = OpenPakFriends.InvitationMessages(description);
            byte[] data = applicationData.ToArray();

            // Fire and forget: the POST is a network call and this is the game's thread.
            _ = OpenPakBaas.SendInvitationAsync(receivers, applicationId, acdIndex, presenceGroupId,
                data, messages, applicationIdMatch, CancellationToken.None);

            return Result.Success;
        }

        [CmifCommand(30910)]
        public Result ReadFriendInvitation(Uid userId, [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<FriendInvitationId> invitationIds)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            List<ulong> ids = [];

            foreach (FriendInvitationId id in invitationIds)
            {
                ids.Add(id.Id);
            }

            // Fire and forget: the mark-read is a network call and this is the game's thread.
            _ = OpenPakAccount.Instance.NativeInvitationsRead?.Invoke(ids);

            return Result.Success;
        }

        [CmifCommand(30911)]
        public Result ReadAllFriendInvitations(Uid userId)
        {
            if (RequireManager() is { IsSuccess: false } denied)
            {
                return denied;
            }

            _ = OpenPakAccount.Instance.NativeInvitationsRead?.Invoke([]);

            return Result.Success;
        }

        [CmifCommand(40100)]
        public Result DeleteFriendListCache(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return RequireSystem();
        }

        [CmifCommand(40400)]
        public Result DeleteBlockedUserListCache(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return RequireSystem();
        }

        [CmifCommand(49900)]
        public Result DeleteNetworkServiceAccountCache(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return RequireSystem();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Os.DestroySystemEvent(ref _completionEvent);
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
