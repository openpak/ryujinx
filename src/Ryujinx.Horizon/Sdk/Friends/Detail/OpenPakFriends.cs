using Ryujinx.Common;
using Ryujinx.Common.Memory;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.Friends.Detail.Ipc;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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
            // By the friends module's status, as the guest will read it back: Online is 1 alone,
            // OnlinePlay is 2 alone, and a friend in a title without a session is only 1.
            switch (filter.PresenceStatus)
            {
                case PresenceStatusFilter.Online when friend.Status != 1:
                case PresenceStatusFilter.OnlinePlay when friend.Status != 2:
                case PresenceStatusFilter.OnlineOrOnlinePlay when friend.Status == 0:
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
                return friend.Online && TitleId(friend) != 0;
            }

            return true;
        }

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
        ///
        /// The Uid at +0 is the caller's own, not the friend's. The presence starts with the
        /// friend's title; its app-field pairs are only handed over when that title's group is
        /// the caller's own, as the module zeroes them for a game that is not.
        /// </summary>
        public static FriendImpl ToFriendImpl(OpenPakFriend friend, Uid userId)
        {
            ulong applicationId = TitleId(friend);

            // The presence group goes over the wire as the title id (OpenPakSession publishes it
            // so), which makes the caller's group its own title.
            ulong ownGroup = OwnTitleId();

            FriendImpl impl = new()
            {
                UserId = userId,
                NetworkUserId = new NetworkServiceAccountId(NetworkId(friend)),
                Nickname = ToNickname(friend.DisplayName),
                Presence = new FriendPresenceImpl
                {
                    ApplicationId = applicationId,
                    PresenceGroupId = applicationId,
                    LastUpdateTimestamp = friend.Since?.ToUnixTimeSecondsOrZero() ?? 0,
                    Status = (PresenceStatus)Math.Clamp(friend.Status, 0, 2),
                    SamePresenceGroupApplication = applicationId != 0 && applicationId == ownGroup,
                },
                IsFavourite = false,
                IsNew = false,
                IsValid = true,
            };

            if (impl.Presence.SamePresenceGroupApplication && friend.Status != 0)
            {
                AppFieldToBlob(friend.AppField, impl.Presence.AppKeyValueStorage);
            }

            return impl;
        }

        /// <summary>The running title as a u64, or 0 when nothing runs.</summary>
        public static ulong OwnTitleId()
            => TitleIDs.CurrentApplication.Value.OrDefault() is { } titleId &&
                ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id)
                    ? id
                    : 0;

        /// <summary>
        /// A UpdateUserPresence for the signed-in profile, handed to the session that publishes it.
        /// Other profiles have no account to publish for, and are not kept.
        /// </summary>
        public static void Declare(Uid userId, byte declaration, string appField)
        {
            if (!AvailableFor(userId))
            {
                return;
            }

            OpenPakPresence.Update(OpenPakConfig.ProfileId, TitleIDs.CurrentApplication.Value.OrDefault(), declaration, appField);
        }

        /// <summary>
        /// The 0xC0 key\0value\0 blob as the JSON object the module publishes for appField:
        /// "{}" when empty, and "{}" when it does not validate — the module sends "" there, but an
        /// empty appField makes a friend's presence_updated parse fail, and "{}" never does.
        /// </summary>
        public static string BlobToAppField(ReadOnlySpan<byte> blob)
        {
            List<(string, string)> pairs = [];

            try
            {
                while (!blob.IsEmpty && blob[0] != 0)
                {
                    int keyEnd = blob.IndexOf((byte)0);

                    if (keyEnd < 0)
                    {
                        return "{}";
                    }

                    string key = Encoding.ASCII.GetString(blob[..keyEnd]);

                    blob = blob[(keyEnd + 1)..];

                    int valueEnd = blob.IndexOf((byte)0);

                    if (valueEnd < 0)
                    {
                        return "{}";
                    }

                    string value = new UTF8Encoding(false, true).GetString(blob[..valueEnd]);

                    blob = blob[(valueEnd + 1)..];

                    if (!ValidKey(key) || pairs.Exists(pair => pair.Item1 == key))
                    {
                        return "{}";
                    }

                    pairs.Add((key, value));
                }
            }
            catch (DecoderFallbackException)
            {
                return "{}";
            }

            using MemoryStream stream = new();

            // Raw UTF-8 rather than \u escapes, as the console writes it.
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();

                foreach ((string key, string value) in pairs)
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// A published appField back into the blob. It must be a JSON object; anything else is
        /// skipped, as the module's list parser skips it. Pairs that do not fit are left out.
        /// </summary>
        private static void AppFieldToBlob(string appField, Span<byte> blob)
        {
            if (string.IsNullOrEmpty(appField) || !appField.StartsWith('{') || !appField.EndsWith('}'))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(appField);

                int offset = 0;

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String || !ValidKey(property.Name))
                    {
                        continue;
                    }

                    byte[] key = Encoding.UTF8.GetBytes(property.Name);
                    byte[] value = Encoding.UTF8.GetBytes(property.Value.GetString());

                    if (offset + key.Length + value.Length + 2 > blob.Length)
                    {
                        break;
                    }

                    key.CopyTo(blob[offset..]);
                    offset += key.Length + 1;
                    value.CopyTo(blob[offset..]);
                    offset += value.Length + 1;
                }
            }
            catch (JsonException)
            {
                blob.Clear();
            }
        }

        private static bool ValidKey(string key)
        {
            if (key.Length == 0)
            {
                return false;
            }

            foreach (char character in key)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

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
