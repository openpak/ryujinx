using Ryujinx.Common.Memory;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.Friends.Detail.Ipc;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// The OpenPak friend graph in the shape the guest's friends sysmodule expects.
    ///
    /// Everything here reads <see cref="OpenPakAccount"/>'s cache and never touches the network.
    /// These are IPC handlers running on the game's own thread: a title asking for its friend
    /// list expects an answer in microseconds, and one answered with an HTTP round trip stutters
    /// or gives up. The cache is kept warm on a timer instead.
    /// </summary>
    static class OpenPakFriends
    {
        /// <summary>Whether there is an account to project at all.</summary>
        public static bool Available => OpenPakAccount.Instance.SignedIn;

        /// <summary>
        /// Whether that profile is the one signed in. The account is the active profile's; any
        /// other profile a title asks about is offline and has no friends to be served.
        /// </summary>
        public static bool AvailableFor(Uid userId) => Available && userId.ToString() == OpenPakConfig.ProfileId;

        /// <summary>
        /// The friends of the signed-in account that pass <paramref name="filter"/>, oldest-first
        /// so that paging by <paramref name="offset"/> is stable between calls.
        /// </summary>
        public static List<OpenPakFriend> Filtered(SizedFriendFilter filter, int offset)
        {
            List<OpenPakFriend> friends = [];

            foreach (OpenPakFriend friend in OpenPakAccount.Instance.Friends)
            {
                if (!Matches(friend, filter))
                {
                    continue;
                }

                friends.Add(friend);
            }

            if (offset > 0)
            {
                friends.RemoveRange(0, Math.Min(offset, friends.Count));
            }

            return friends;
        }

        private static bool Matches(OpenPakFriend friend, SizedFriendFilter filter)
        {
            switch (filter.PresenceStatus)
            {
                case PresenceStatusFilter.Online when !friend.Online:
                case PresenceStatusFilter.OnlinePlay when !Playing(friend):
                case PresenceStatusFilter.OnlineOrOnlinePlay when !friend.Online:
                    return false;
            }

            // The core has no per-viewer favourite flag yet, so a favourites-only request can
            // only honestly answer "none". Returning the whole list instead would put people in
            // a list the person never chose to put them in.
            if (filter.IsFavoriteOnly)
            {
                return false;
            }

            // PresenceGroupId carries the title a same-app filter is asking about. Friends on
            // another title, or on none, are not in that group.
            if ((filter.IsSameAppPresenceOnly || filter.IsSameAppPlayedOnly) && filter.PresenceGroupId != 0)
            {
                return TitleId(friend) == filter.PresenceGroupId;
            }

            if (filter.IsArbitraryAppPlayedOnly)
            {
                return Playing(friend);
            }

            return true;
        }

        private static bool Playing(OpenPakFriend friend) => friend.Online && TitleId(friend) != 0;

        /// <summary>The title a friend is in, as the u64 the guest speaks, or 0.</summary>
        private static ulong TitleId(OpenPakFriend friend)
            => !string.IsNullOrEmpty(friend.TitleId) &&
                ulong.TryParse(friend.TitleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id)
                    ? id
                    : 0;

        /// <summary>
        /// One friend as the guest's struct.
        ///
        /// <c>IsValid</c> is what the sysmodule's parser counts, so a friend written without it
        /// is a friend the console will not show — which is precisely how an accepted request
        /// ends up as an empty list on screen.
        /// </summary>
        public static FriendImpl ToFriendImpl(OpenPakFriend friend, Uid userId) => new()
        {
            UserId = userId,
            NetworkUserId = new NetworkServiceAccountId(NetworkId(friend)),
            Nickname = ToNickname(friend.DisplayName),
            Presence = new UserPresenceImpl
            {
                UserId = userId,
                LastTimeOnlineTimestamp = friend.Since?.ToUnixTimeSecondsOrZero() ?? 0,
                Status = !friend.Online
                    ? PresenceStatus.Offline
                    : TitleId(friend) != 0
                        ? PresenceStatus.OnlinePlay
                        : PresenceStatus.Online,
                SamePresenceGroupApplication = false,
            },
            IsFavourite = false,
            IsNew = false,
            IsValid = true,
        };

        /// <summary>
        /// The id a title will use to refer to this person.
        ///
        /// The Switch adapter's pid when it gave one — that is the id every OpenPak title server
        /// resolves — and otherwise a stable hash of the account id, so a friend who has never
        /// touched a Switch still gets a consistent identity across sessions rather than a
        /// different one each launch.
        /// </summary>
        private static ulong NetworkId(OpenPakFriend friend)
        {
            if (friend.Pid != 0)
            {
                return friend.Pid;
            }

            ulong hash = 1469598103934665603;

            foreach (byte value in Encoding.UTF8.GetBytes(friend.AccountId ?? string.Empty))
            {
                hash = (hash ^ value) * 1099511628211;
            }

            // Never zero: a title reads that as "no account".
            return hash == 0 ? 1 : hash;
        }

        /// <summary>A display name into the fixed 33-byte field, truncated on a character boundary.</summary>
        public static Nickname ToNickname(string name)
        {
            Array33<byte> buffer = new();

            byte[] encoded = Encoding.UTF8.GetBytes(name ?? string.Empty);

            // 32 bytes plus the terminator: a name cut mid-sequence would decode to a
            // replacement character on the console rather than to a shorter name.
            int length = Math.Min(encoded.Length, 32);

            while (length > 0 && (encoded[length - 1] & 0xC0) == 0x80)
            {
                length--;
            }

            encoded.AsSpan(0, length).CopyTo(buffer.AsSpan());

            return new Nickname(buffer);
        }

        private static long ToUnixTimeSecondsOrZero(this DateTime value)
            => value == default ? 0 : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }
}
