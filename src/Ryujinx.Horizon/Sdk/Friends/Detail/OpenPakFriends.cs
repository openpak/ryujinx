using Ryujinx.Common;
using Ryujinx.Common.Memory;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.Friends.Detail.Ipc;
using Ryujinx.OpenPak;
using System;
using System.Buffers.Binary;
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
    static partial class OpenPakFriends
    {
        /// <summary>
        /// Whether this profile has a signed-in BAAS user whose friends can be served. The friend
        /// graph the guest sees is the server's (contract §A.1) and nothing else: every id in it is
        /// the BAAS user id the same server answers requests, invitations and relationships by.
        /// </summary>
        public static bool AvailableFor(Uid userId) => OpenPakBaas.Ready && userId.ToString() == OpenPakConfig.ProfileId;

        /// <summary>Whether a friend list has been fetched for this profile at all (10120).</summary>
        public static bool ListAvailableFor(Uid userId) => AvailableFor(userId) && OpenPakBaas.FriendListAvailable;

        /// <summary>
        /// The friends that pass <paramref name="filter"/>, in the server's own order so that
        /// paging by <paramref name="offset"/> is stable between calls.
        /// </summary>
        public static List<BaasFriend> Filtered(SizedFriendFilter filter, int offset)
        {
            ulong ownGroup = OwnPresenceGroupId();
            List<BaasFriend> friends = [];

            foreach (BaasFriend friend in OpenPakBaas.Friends)
            {
                if (Matches(friend, filter, ownGroup))
                {
                    friends.Add(friend);
                }
            }

            if (offset > 0)
            {
                friends.RemoveRange(0, Math.Min(offset, friends.Count));
            }

            return friends;
        }

        /// <summary>The FriendFilter, as the module applies it (presence doc §3.2).</summary>
        private static bool Matches(BaasFriend friend, SizedFriendFilter filter, ulong ownGroup)
        {
            // Online is 1 alone, OnlinePlay is 2 alone: a friend in a title without a declared
            // session is only ever 1, and a game asking for 2 must not be told otherwise.
            switch (filter.PresenceStatus)
            {
                case PresenceStatusFilter.Online when friend.State != 1:
                case PresenceStatusFilter.OnlinePlay when friend.State != 2:
                case PresenceStatusFilter.OnlineOrOnlinePlay when friend.State == 0:
                    return false;
            }

            if (filter.IsFavoriteOnly && !friend.IsFavorite)
            {
                return false;
            }

            // The same-app filters compare against the caller's own presence group, not against
            // anything in the filter; only the arbitrary-app one names a group of its own.
            bool inOwnGroup = friend.ApplicationId != 0 && friend.PresenceGroupId == ownGroup;

            if (filter.IsSameAppPresenceOnly && !inOwnGroup)
            {
                return false;
            }

            if (filter.IsSameAppPlayedOnly && !inOwnGroup && !Played(friend, ownGroup))
            {
                return false;
            }

            if (filter.IsArbitraryAppPlayedOnly &&
                !(friend.ApplicationId != 0 && friend.PresenceGroupId == filter.PresenceGroupId) &&
                !Played(friend, filter.PresenceGroupId))
            {
                return false;
            }

            return true;
        }

        /// <summary>Whether that presence group appears in the friend's play log (§A.1).</summary>
        private static bool Played(BaasFriend friend, ulong presenceGroupId)
        {
            if (presenceGroupId == 0)
            {
                return false;
            }

            foreach (BaasPlayLog entry in friend.PlayLog)
            {
                if (entry.PresenceGroupId == presenceGroupId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// One friend as the guest's struct (contract §B.3, presence doc §3.2).
        ///
        /// <c>IsValid</c> is what the sysmodule's parser counts, so a friend written without it is
        /// a friend the console will not show. The Uid at +0 is the caller's own, not the friend's.
        ///
        /// A friend whose status is 0 carries only the time — and the title, when that time is
        /// known — and never the blob. The blob is zeroed for a caller outside the friend's
        /// presence group unless the port carries the viewer bit, which friend:u and friend:s do
        /// not.
        /// </summary>
        public static FriendImpl ToFriendImpl(BaasFriend friend, Uid userId, bool viewer)
        {
            ulong ownGroup = OwnPresenceGroupId();
            bool playing = friend.State != 0;
            bool known = playing || friend.UpdatedAt > 0;

            ulong applicationId = known ? friend.ApplicationId : 0;
            ulong presenceGroupId = known ? friend.PresenceGroupId : 0;
            bool sameGroup = applicationId != 0 && presenceGroupId == ownGroup;

            FriendImpl impl = new()
            {
                UserId = userId,
                NetworkUserId = new NetworkServiceAccountId(friend.Id),
                Nickname = ToNickname(friend.Nickname),
                Presence = new FriendPresenceImpl
                {
                    ApplicationId = applicationId,
                    PresenceGroupId = presenceGroupId,
                    LastUpdateTimestamp = friend.UpdatedAt,
                    Status = (PresenceStatus)Math.Clamp(friend.State, 0, 2),
                    SamePresenceGroupApplication = sameGroup,
                },
                IsFavourite = friend.IsFavorite,
                IsNew = friend.IsNewly,
                IsValid = true,
            };

            if (playing && (sameGroup || viewer))
            {
                AppFieldToBlob(friend.AppField, impl.Presence.AppKeyValueStorage);
            }

            return impl;
        }

        /// <summary>
        /// The caller's own presence group: the running title's NACP group, which is what the
        /// module compares a friend's against. 0 when nothing runs.
        /// </summary>
        public static ulong OwnPresenceGroupId() => OpenPakPresence.Application().PresenceGroupId;

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
        /// An in-app screen name as a request route carries it: the UTF-8 name and the
        /// ASCII language tag.
        /// </summary>
        public static (string Name, string Language) ScreenName(InAppScreenName screenName)
        {
            string language = Encoding.ASCII.GetString(screenName.LanguageCode.Value.AsSpan());
            int end = language.IndexOf('\0');

            return (screenName.ToString(), end < 0 ? language : language[..end]);
        }

        /// <summary>
        /// One friend request as the guest's V1 struct (20201).
        ///
        /// <paramref name="listType"/> is the box it came from: 1 sent, 2 received. The
        /// request id and the other party's id are the BAAS ids the server speaks, so a
        /// cancel/accept/reject names the same request the box listed.
        /// </summary>
        public static FriendRequestImpl ToRequestImpl(BaasRequest request, Uid userId, int listType)
        {
            FriendRequestImpl impl = new()
            {
                UserId = userId,
                RequestId = request.Id,
                OtherUserId = request.OtherId,
                Nickname = ToNickname(request.Nickname),
                ListType = (uint)listType,
                Channel = (uint)request.Channel,
                State = (uint)request.State,
                RouteApplicationId = request.Route?.ApplicationId ?? 0,
                RoutePresenceGroupId = request.Route?.PresenceGroupId ?? 0,
                CreatedAt = request.CreatedAt,
                IsRead = request.Read,
                IsValid = true,
            };

            FixedUtf8(impl.ThumbnailUrl, request.ThumbnailUrl);
            FixedUtf8(impl.RouteName.AsSpan(), request.Route?.Name);
            FixedUtf8(impl.RouteLanguage.AsSpan(), request.Route?.Language);
            WriteRoute(request.Route, impl.Union.CatalogId.AsSpan(),
                impl.Union.MiiName.AsSpan(), impl.Union.MiiImageUrlParam.AsSpan());

            return impl;
        }

        /// <summary>One friend request as the guest's V2 struct (20202): the route carries acdIndex.</summary>
        public static FriendRequestImplV2 ToRequestImplV2(BaasRequest request, Uid userId, int listType)
        {
            FriendRequestImplV2 impl = new()
            {
                UserId = userId,
                RequestId = request.Id,
                OtherUserId = request.OtherId,
                Nickname = ToNickname(request.Nickname),
                ListType = (uint)listType,
                Channel = (uint)request.Channel,
                State = (uint)request.State,
                RouteApplicationId = request.Route?.ApplicationId ?? 0,
                RouteAcdIndex = request.Route?.AcdIndex ?? 0,
                RoutePresenceGroupId = request.Route?.PresenceGroupId ?? 0,
                CreatedAt = request.CreatedAt,
                IsRead = request.Read,
                IsValid = true,
            };

            FixedUtf8(impl.ThumbnailUrl, request.ThumbnailUrl);
            FixedUtf8(impl.RouteName.AsSpan(), request.Route?.Name);
            FixedUtf8(impl.RouteLanguage.AsSpan(), request.Route?.Language);
            WriteRoute(request.Route, impl.Union.CatalogId.AsSpan(),
                impl.Union.MiiName.AsSpan(), impl.Union.MiiImageUrlParam.AsSpan());

            return impl;
        }

        /// <summary>
        /// The route union: the external catalog id when the request carries one, else the
        /// NNID Mii data when it carries that, else zeros. The two variants never mix on
        /// the wire — each send variant writes exactly one.
        /// </summary>
        private static void WriteRoute(BaasRoute route, Span<byte> catalog, Span<byte> miiName, Span<byte> miiParam)
        {
            if (route == null)
            {
                return;
            }

            if (OpenPakBaas.TryCatalogId(route.CatalogId, out ulong hi, out ulong lo))
            {
                BinaryPrimitives.WriteUInt64LittleEndian(catalog, hi);
                BinaryPrimitives.WriteUInt64LittleEndian(catalog[8..], lo);

                return;
            }

            FixedUtf8(miiName, route.MiiName);
            FixedUtf8(miiParam, route.MiiImageUrlParam);
        }

        /// <summary>
        /// A string into a fixed char field, truncated on a character boundary with room
        /// left for the terminator — as the module keeps at most N-1 bytes of an N-byte field.
        /// </summary>
        private static void FixedUtf8(Span<byte> destination, string value)
        {
            destination.Clear();

            if (string.IsNullOrEmpty(value) || destination.Length < 2)
            {
                return;
            }

            byte[] encoded = Encoding.UTF8.GetBytes(value);

            encoded.AsSpan(0, Fits(encoded, destination.Length - 1)).CopyTo(destination);
        }

        /// <summary>
        /// How many of <paramref name="encoded"/>'s bytes fit in <paramref name="limit"/> without
        /// cutting a character in half. A cut lands mid-sequence when the first byte left out is a
        /// continuation byte, and only then is anything backed off: a name that fits whole keeps
        /// its last character, multi-byte or not.
        /// </summary>
        private static int Fits(ReadOnlySpan<byte> encoded, int limit)
        {
            int length = Math.Min(encoded.Length, limit);

            while (length > 0 && length < encoded.Length && (encoded[length] & 0xC0) == 0x80)
            {
                length--;
            }

            return length;
        }

        /// <summary>A display name into the fixed 33-byte field, truncated on a character boundary.</summary>
        public static Nickname ToNickname(string name)
        {
            Array33<byte> buffer = new();

            byte[] encoded = Encoding.UTF8.GetBytes(name ?? string.Empty);

            // 32 bytes plus the terminator: a name cut mid-sequence would decode to a
            // replacement character on the console rather than to a shorter name.
            encoded.AsSpan(0, Fits(encoded, 32)).CopyTo(buffer.AsSpan());

            return new Nickname(buffer);
        }

    }
}
