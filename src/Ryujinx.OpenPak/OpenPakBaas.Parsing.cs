using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The friends module's parsers, as far as they decide what a reply means: which fields are
    /// required, what types they must have, and what a missing one does (friends contract §1.3).
    /// Unknown keys are ignored everywhere; a field of the wrong type counts as absent.
    /// </summary>
    public static partial class OpenPakBaas
    {
        /// <summary>The channels table (§A.1), 1-based.</summary>
        public static readonly string[] Channels =
        [
            "NX_FACED", "FRIEND_CODE", "IN_APP", "NINTENDO_ACCOUNT", "3DS",
            "NNID", "FACEBOOK", "TWITTER", "WECHAT", "CAMPUS",
        ];

        /// <summary>The invitation message slots, in the module's order (invitations doc §2b).</summary>
        public static readonly string[] MessageLanguages =
        [
            "en-US", "en-GB", "ja", "fr", "de", "es-419", "es", "it",
            "nl", "fr-CA", "pt", "ru", "zh-Hans", "zh-Hant", "ko", "pt-BR",
        ];

        private static readonly string[] _requestStates = ["PENDING", "CANCELED", "AUTHORIZED", "REJECTED", "EXPIRED"];

        /// <summary>A channel string as its 1-based table index; 0 when unknown.</summary>
        public static int ChannelOf(string channel) => channel == null ? 0 : Array.IndexOf(Channels, channel) + 1;

        /// <summary>
        /// A 64-bit id as the module reads one (strtoull base 16): leading spaces, an optional '+'
        /// and "0x", at most 16 digits; a '-' rejects it. Only a JSON string qualifies.
        /// </summary>
        public static bool TryHex(JsonElement element, out ulong value)
        {
            value = 0;

            return element.ValueKind == JsonValueKind.String && TryHexText(element.GetString().AsSpan(), out value);
        }

        /// <summary>
        /// A 64-bit id as the module reads one (strtoull base 16): leading spaces, an optional '+'
        /// and "0x", at most 16 digits; a '-' rejects it.
        /// </summary>
        public static bool TryHexText(ReadOnlySpan<char> text, out ulong value)
        {
            value = 0;
            text = text.TrimStart(' ');

            if (text.StartsWith("+"))
            {
                text = text[1..];
            }

            if (text.StartsWith("0x") || text.StartsWith("0X"))
            {
                text = text[2..];
            }

            int digits = 0;

            while (digits < text.Length && char.IsAsciiHexDigit(text[digits]))
            {
                digits++;
            }

            return digits is > 0 and <= 16 &&
                ulong.TryParse(text[..digits], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        private static bool Hex(JsonElement parent, string name, out ulong value)
        {
            value = 0;

            return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) && TryHex(element, out value);
        }

        private static string Text(JsonElement parent, string name)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) &&
                element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;

        private static long? Integer(JsonElement parent, string name)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) &&
                element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value)
                    ? value
                    : null;

        private static bool? Flag(JsonElement parent, string name)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) &&
                element.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? element.GetBoolean()
                    : null;

        private static JsonElement Child(JsonElement parent, string name)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) ? element : default;

        private static JsonElement Path(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                element = Child(element, name);
            }

            return element;
        }

        private static IEnumerable<JsonElement> Items(JsonElement root)
        {
            JsonElement items = Child(root, "items");

            return items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
        }

        private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

        /// <summary>
        /// A friend-list item or a single-friend PATCH reply (§A.1, §A.2). Null when friendId,
        /// friend.nickname or friend.thumbnailUrl is missing: the module skips that item.
        /// </summary>
        public static BaasFriend ParseFriend(JsonElement item)
        {
            JsonElement friend = Child(item, "friend");

            string nickname = Text(friend, "nickname");
            string thumbnail = Text(friend, "thumbnailUrl");

            if (!Hex(item, "friendId", out ulong id) || nickname == null || thumbnail == null)
            {
                return null;
            }

            JsonElement presence = Child(friend, "presence");
            JsonElement appInfo = Path(presence, "extras", "friends");
            JsonElement self = Path(item, "extras", "self");

            int state = Text(presence, "state") switch
            {
                "ONLINE" => 1,
                "PLAYING" => 2,
                _ => 0,
            };

            long updatedAt = Integer(presence, "updatedAt") ?? 0;

            if (state == 0 && Integer(presence, "logoutAt") is { } logoutAt)
            {
                updatedAt = logoutAt;
            }

            Hex(appInfo, "appInfo:appId", out ulong applicationId);
            Hex(appInfo, "appInfo:presenceGroupId", out ulong groupId);

            string appField = Text(appInfo, "appField");

            if (appField != null && !(appField.StartsWith('{') && appField.EndsWith('}')))
            {
                appField = null;
            }

            int channel = 0;
            JsonElement channels = Child(item, "channels");

            if (channels.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in channels.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        channel = ChannelOf(value.GetString());

                        break;
                    }
                }
            }

            return new BaasFriend(id, nickname, thumbnail)
            {
                State = state,
                UpdatedAt = updatedAt,
                ApplicationId = applicationId,
                PresenceGroupId = groupId,
                AcdIndex = (byte)(Integer(appInfo, "appInfo:acdIndex") ?? 0),
                AppField = appField,
                IsFavorite = Flag(item, "isFavorite") ?? false,
                IsNewly = Flag(self, "isConfirmed") != true,
                IsOnlineNotification = Flag(self, "isOnlineNotification") ?? false,
                CreatedAt = Integer(item, "createdAt") ?? 0,
                Note = Text(item, "friendNote"),
                Channel = channel,
                Route = ParseRoute(self, self),
                PlayLog = PlayLogOf(Child(friend, "extras")),
            };
        }

        /// <summary>The whole friend list, at most 300 items, incomplete ones skipped.</summary>
        public static List<BaasFriend> ParseFriendList(JsonElement root)
        {
            List<BaasFriend> friends = [];

            foreach (JsonElement item in Items(root))
            {
                if (friends.Count == 300)
                {
                    break;
                }

                if (ParseFriend(item) is { } friend)
                {
                    friends.Add(friend);
                }
            }

            return friends;
        }

        /// <summary>
        /// The route keys. <paramref name="shared"/> carries appInfo and the catalog id,
        /// <paramref name="named"/> the in-app name and the NNID Mii data (§A.4.2: the inbox reads
        /// both from senderAndReceiver, the outbox the names from sender).
        /// </summary>
        private static BaasRoute ParseRoute(JsonElement shared, JsonElement named)
        {
            Hex(shared, "route:appInfo:appId", out ulong applicationId);
            Hex(shared, "route:appInfo:presenceGroupId", out ulong groupId);

            BaasRoute route = new(
                applicationId,
                (byte)(Integer(shared, "route:appInfo:acdIndex") ?? 0),
                groupId,
                Text(shared, "route:candidate:catalogId"),
                Text(named, "route:name"),
                Text(named, "route:name:language"),
                Text(named, "route:nnid:miiName"),
                Text(named, "route:nnid:miiImageUrlParam"));

            return route == new BaasRoute(0, 0, 0, null, null, null, null, null) ? null : route;
        }

        /// <summary>
        /// The first `extras.&lt;group&gt;.playLog` that is a "[…]" string, and which group held it.
        /// </summary>
        private static (string Text, string Group) PlayLogTextOf(JsonElement extras)
        {
            if (extras.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            foreach (JsonProperty group in extras.EnumerateObject())
            {
                string text = Text(group.Value, "playLog");

                if (text != null && text.StartsWith('[') && text.EndsWith(']'))
                {
                    return (text, group.Name);
                }
            }

            return (null, null);
        }

        private static IReadOnlyList<BaasPlayLog> PlayLogOf(JsonElement extras) => ParsePlayLog(PlayLogTextOf(extras).Text);

        /// <summary>
        /// A playLog string: a JSON array of at most 20 entries. Each needs appId, presenceGroupId
        /// and the four counters; acdIndex is optional. A repeated appId merges, the larger
        /// counters winning.
        /// </summary>
        public static IReadOnlyList<BaasPlayLog> ParsePlayLog(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith('[') || !text.EndsWith(']'))
            {
                return [];
            }

            List<BaasPlayLog> entries = [];

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);

                foreach (JsonElement entry in document.RootElement.EnumerateArray())
                {
                    if (entries.Count == 20)
                    {
                        break;
                    }

                    if (!Hex(entry, "appInfo:appId", out ulong applicationId) ||
                        !Hex(entry, "appInfo:presenceGroupId", out ulong groupId) ||
                        Integer(entry, "totalPlayCount") is not { } count ||
                        Integer(entry, "totalPlayTime") is not { } time ||
                        Integer(entry, "firstPlayedAt") is not { } first ||
                        Integer(entry, "lastPlayedAt") is not { } last)
                    {
                        continue;
                    }

                    BaasPlayLog log = new(applicationId, (byte)(Integer(entry, "appInfo:acdIndex") ?? 0), groupId, count, time, first, last);

                    int existing = entries.FindIndex(known => known.ApplicationId == applicationId);

                    if (existing < 0)
                    {
                        entries.Add(log);

                        continue;
                    }

                    BaasPlayLog known = entries[existing];

                    entries[existing] = known with
                    {
                        TotalPlayCount = Math.Max(known.TotalPlayCount, count),
                        TotalPlayTime = Math.Max(known.TotalPlayTime, time),
                        FirstPlayedAt = Math.Max(known.FirstPlayedAt, first),
                        LastPlayedAt = Math.Max(known.LastPlayedAt, last),
                    };
                }
            }
            catch (JsonException)
            {
                return [];
            }

            return entries;
        }

        /// <summary>
        /// One friend request (§A.4.1, §A.4.2, §A.4.4). <paramref name="inbox"/> is receiver mode:
        /// the other party is the sender. Null when a required field is missing or the nickname is
        /// 0x21 bytes or longer.
        /// </summary>
        public static BaasRequest ParseRequest(JsonElement item, bool inbox)
        {
            string side = inbox ? "sender" : "receiver";
            JsonElement other = Child(item, side);

            string channel = null;
            JsonElement channels = Child(item, "channels");

            if (channels.ValueKind == JsonValueKind.Array && channels.GetArrayLength() > 0 &&
                channels[0].ValueKind == JsonValueKind.String)
            {
                channel = channels[0].GetString();
            }

            string state = Text(item, "state");
            string nickname = Text(other, "nickname");
            string thumbnail = Text(other, "thumbnailUrl");

            if (!Hex(item, "id", out ulong id) || channel == null || state == null ||
                !Hex(item, side + "Id", out ulong otherId) ||
                nickname == null || Utf8Length(nickname) >= 0x21 ||
                thumbnail == null || Utf8Length(thumbnail) >= 0xA0)
            {
                return null;
            }

            JsonElement extras = Child(item, "extras");
            JsonElement shared = Child(extras, "senderAndReceiver");

            return new BaasRequest(
                id,
                ChannelOf(channel),
                Array.FindIndex(_requestStates, known => known.Equals(state, StringComparison.OrdinalIgnoreCase)) + 1,
                otherId,
                nickname,
                thumbnail)
            {
                CreatedAt = Math.Max(Integer(item, "createdAt") ?? 0, 0),
                Read = inbox && Flag(Child(extras, "receiver"), "read") == true,
                Route = ParseRoute(shared, inbox ? shared : Child(extras, "sender")),
            };
        }

        /// <summary>A request box page. Items missing a required field are left out.</summary>
        public static List<BaasRequest> ParseRequestList(JsonElement root, bool inbox)
        {
            List<BaasRequest> requests = [];

            foreach (JsonElement item in Items(root))
            {
                if (ParseRequest(item, inbox) is { } request)
                {
                    requests.Add(request);
                }
            }

            return requests;
        }

        /// <summary>The badge (§A.4.3): unread is the items without extras.receiver.read true.</summary>
        public static (int Unread, int Read) ParseRequestCount(JsonElement root)
        {
            int total = 0;
            int read = 0;

            foreach (JsonElement item in Items(root))
            {
                total++;

                if (Flag(Path(item, "extras", "receiver"), "read") == true)
                {
                    read++;
                }
            }

            return (total - read, read);
        }

        /// <summary>A flat user (§A.7); null without id, nickname and thumbnailUrl.</summary>
        public static BaasUser ParseUser(JsonElement item)
        {
            string nickname = Text(item, "nickname");
            string thumbnail = Text(item, "thumbnailUrl");

            if (!Hex(item, "id", out ulong id) || nickname == null || thumbnail == null)
            {
                return null;
            }

            return new BaasUser(id, nickname, thumbnail) { PlayLog = PlayLogOf(Child(item, "extras")) };
        }

        public static List<BaasUser> ParseUsers(JsonElement root)
        {
            List<BaasUser> users = [];

            foreach (JsonElement item in Items(root))
            {
                if (ParseUser(item) is { } user)
                {
                    users.Add(user);
                }
            }

            return users;
        }

        /// <summary>The caller's own user (§A.6); null without id, nickname and thumbnailUrl.</summary>
        public static BaasUserSetting ParseUserSetting(JsonElement root)
        {
            string nickname = Text(root, "nickname");
            string thumbnail = Text(root, "thumbnailUrl");

            if (!Hex(root, "id", out ulong id) || nickname == null || thumbnail == null)
            {
                return null;
            }

            JsonElement permissions = Child(root, "permissions");
            JsonElement friendCode = Path(root, "links", "friendCode");

            (string playLog, string group) = PlayLogTextOf(Child(root, "extras"));

            return new BaasUserSetting(id, nickname, thumbnail)
            {
                PresencePermission = Text(permissions, "presence") switch
                {
                    "FAVORITE_FRIENDS" => 1,
                    "FRIENDS" => 2,
                    _ => 0,
                },
                FriendRequestReception = Flag(permissions, "friendRequestReception") ?? false,
                FriendCode = Text(friendCode, "id"),
                FriendCodeRegenerableAt = Integer(friendCode, "regenerableAt") ?? 0,
                PlayLogPermission = group switch
                {
                    "self" => 1,
                    "favoriteFriends" => 2,
                    "friends" => 3,
                    "everyone" => 5,
                    _ => 0,
                },
                PlayLogText = playLog,
                PlayLog = ParsePlayLog(playLog),
            };
        }

        /// <summary>
        /// The block list (§A.8). Null — the whole parse failed — when an item lacks targetUserId or
        /// targetUser.nickname, or there are more than 100 items.
        /// </summary>
        public static List<BaasBlock> ParseBlocks(JsonElement root)
        {
            List<BaasBlock> blocks = [];

            foreach (JsonElement item in Items(root))
            {
                JsonElement target = Child(item, "targetUser");
                string nickname = Text(target, "nickname");

                if (blocks.Count == 100 || !Hex(item, "targetUserId", out ulong id) || nickname == null)
                {
                    return null;
                }

                JsonElement self = Path(item, "extras", "self");

                blocks.Add(new BaasBlock(id, nickname, Text(target, "thumbnailUrl") ?? string.Empty)
                {
                    CreatedAt = Integer(item, "createdAt") ?? 0,
                    Reason = Text(self, "reason") switch
                    {
                        "BAD_FRIEND_REQUEST" => 1,
                        "BAD_FRIEND" => 2,
                        "IN_APP" => 3,
                        "IN_CAMPUS" => 4,
                        _ => 0,
                    },
                    Route = ParseRoute(self, self),
                });
            }

            return blocks;
        }

        private static ulong? Unsigned(JsonElement parent, string name)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement element) &&
                element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out ulong value)
                    ? value
                    : null;

        /// <summary>
        /// application_data: base64, or null for none. The key itself is required; false here when
        /// it is absent or neither form.
        /// </summary>
        private static bool ApplicationData(JsonElement parent, out byte[] data)
        {
            data = [];

            if (!parent.TryGetProperty("application_data", out JsonElement element))
            {
                return false;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            try
            {
                data = Convert.FromBase64String(element.GetString());
            }
            catch (FormatException)
            {
                return false;
            }

            if (data.Length > 0x400)
            {
                data = data[..0x400];
            }

            return true;
        }

        /// <summary>
        /// The invitation inbox (invitations doc §2a). Null when any item is incomplete: an
        /// incomplete item fails the whole parse.
        /// </summary>
        public static List<BaasInvitation> ParseInvitations(JsonElement root)
        {
            List<BaasInvitation> invitations = [];

            foreach (JsonElement item in Items(root))
            {
                if (Unsigned(item, "id") is not { } id ||
                    Unsigned(item, "invitation_group_id") is not { } groupId ||
                    !Hex(item, "sender_id", out ulong senderId) ||
                    !Hex(item, "application_id", out ulong applicationId) ||
                    !Hex(item, "application_group_id", out ulong applicationGroupId) ||
                    !ApplicationData(item, out byte[] data) ||
                    Flag(item, "read") is not { } read ||
                    Integer(item, "created_at") is not { } createdAt ||
                    Integer(item, "updated_at") is null)
                {
                    return null;
                }

                invitations.Add(new BaasInvitation(id, groupId, senderId, applicationId, applicationGroupId)
                {
                    AcdIndex = (byte)Math.Clamp(Integer(item, "acd_index") ?? 0, 0, 255),
                    ApplicationData = data,
                    CreatedAt = createdAt,
                    Read = read,
                    ApplicationIdMatch = Flag(item, "application_id_match") ?? false,
                });
            }

            return invitations;
        }

        /// <summary>
        /// One invitation group (invitations doc §2b); null when anything required is missing, or
        /// invitations is empty or holds more than 16.
        /// </summary>
        public static BaasInvitationGroup ParseInvitationGroup(JsonElement root)
        {
            JsonElement messages = Child(root, "messages");
            JsonElement invitations = Child(root, "invitations");

            if (Unsigned(root, "id") is not { } id ||
                !Hex(root, "sender_id", out ulong senderId) ||
                !Hex(root, "application_id", out ulong applicationId) ||
                !Hex(root, "application_group_id", out ulong applicationGroupId) ||
                !ApplicationData(root, out byte[] data) ||
                Integer(root, "created_at") is not { } createdAt ||
                Integer(root, "updated_at") is null ||
                messages.ValueKind != JsonValueKind.Object ||
                invitations.ValueKind != JsonValueKind.Array ||
                invitations.GetArrayLength() is < 1 or > 16)
            {
                return null;
            }

            List<ulong> receivers = [];

            foreach (JsonElement invitation in invitations.EnumerateArray())
            {
                if (!Hex(invitation, "receiver_id", out ulong receiver))
                {
                    return null;
                }

                receivers.Add(receiver);
            }

            return new BaasInvitationGroup(id, senderId, receivers, applicationId, applicationGroupId)
            {
                AcdIndex = (byte)Math.Clamp(Integer(root, "acd_index") ?? 0, 0, 255),
                Messages = MessageLanguages.Select(language =>
                    Text(messages, language) is { } text && Utf8Length(text) < 0xC0 ? text : string.Empty).ToList(),
                ApplicationData = data,
                CreatedAt = createdAt,
                ApplicationIdMatch = Flag(root, "application_id_match") ?? false,
            };
        }

        /// <summary>GET relationships (§A.7): nothing is required.</summary>
        public static BaasRelationship ParseRelationship(JsonElement root)
        {
            JsonElement sent = Child(root, "sentFriendRequestIds");

            return new BaasRelationship(
                Flag(root, "isFriend") ?? false,
                Flag(root, "isBlocking") ?? false,
                sent.ValueKind == JsonValueKind.Array && sent.GetArrayLength() > 0);
        }
    }
}
