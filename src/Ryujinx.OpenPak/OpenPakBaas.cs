using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The friends sysmodule's own REST surface (friends contract 22.5.0 §A), spoken to nx-baas and
    /// to the Five invitation host, plus what it last answered.
    ///
    /// The session that holds the pinned client and the user-scoped bearer (a project up) plugs
    /// itself in through <see cref="Attach"/>; the guest's friends module (a project down) calls
    /// these and reads the caches. Every id here is the BAAS user id the server hands out — the id
    /// a friend request, an invitation and a relationship are all addressed by — and never anything
    /// derived from it.
    ///
    /// Results are friends-module descriptions (2121-xxxx), 0 for success, mapped from the status
    /// and error body exactly as §1.2 maps them.
    /// </summary>
    public static partial class OpenPakBaas
    {
        public const string BaasHost = "e0d67c509fb203858ebcb2fe3f88c2aa.baas.nintendo.com";
        public const string FiveHost = "app.lp1.five.nintendo.net";

        private const string JsonPatch = "application/json-patch+json";
        private const string JsonType = "application/json";

        /// <summary>The list sync's cooldown (§2.2, `cooldown_time_for_sync_friend_list`).</summary>
        private static readonly TimeSpan _listCooldown = TimeSpan.FromSeconds(30);

        /// <summary>What one request produced: any 2xx is success, whatever the body (§1.2).</summary>
        public sealed record Reply(int Status, string Body)
        {
            public bool Success => Status is >= 200 and < 300;
        }

        /// <summary>How a request reaches the server, with this user's bearer on it.</summary>
        public delegate Task<Reply> SendDelegate(HttpMethod method, string url, string contentType, string body, CancellationToken cancellationToken);

        private static readonly Lock _lock = new();

        private static SendDelegate _send;
        private static string _userId;

        private static IReadOnlyList<BaasFriend> _friends = [];
        private static string _friendsSignature;
        private static bool _friendsSynced;
        private static DateTime _friendsSyncedAt;

        private static IReadOnlyList<BaasBlock> _blocks = [];
        private static bool _blocksSynced;

        private static BaasUserSetting _setting;

        /// <summary>The friend list changed: a friend, a flag or a presence is not what it was.</summary>
        public static event Action FriendListChanged;

        /// <summary>Whether there is a signed-in BAAS user to speak for.</summary>
        public static bool Ready => _send != null && _userId != null;

        /// <summary>The caller's own BAAS user id, or null.</summary>
        public static string UserId => _userId;

        /// <summary>Whether a friend list has ever been fetched for this user.</summary>
        public static bool FriendListAvailable => _friendsSynced;

        /// <summary>Whether a block list has ever been fetched for this user.</summary>
        public static bool BlockListAvailable => _blocksSynced;

        /// <summary>The friend list as of the last sync. Never null.</summary>
        public static IReadOnlyList<BaasFriend> Friends
        {
            get
            {
                lock (_lock)
                {
                    return _friends;
                }
            }
        }

        /// <summary>The block list as of the last sync. Never null.</summary>
        public static IReadOnlyList<BaasBlock> Blocks
        {
            get
            {
                lock (_lock)
                {
                    return _blocks;
                }
            }
        }

        /// <summary>The caller's own user as of the last sync, or null.</summary>
        public static BaasUserSetting UserSetting
        {
            get
            {
                lock (_lock)
                {
                    return _setting;
                }
            }
        }

        /// <summary>One friend out of the cache, or null.</summary>
        public static BaasFriend Friend(ulong id)
        {
            foreach (BaasFriend friend in Friends)
            {
                if (friend.Id == id)
                {
                    return friend;
                }
            }

            return null;
        }

        /// <summary>A signed-in session offers itself as the way out. Caches from before are dropped.</summary>
        public static void Attach(string userId, SendDelegate send)
        {
            lock (_lock)
            {
                if (_userId != userId)
                {
                    Forget();
                }

                _userId = userId;
                _send = send;
            }
        }

        /// <summary>Signed out, or another profile: nothing of the last user may be read as this one's.</summary>
        public static void Detach()
        {
            lock (_lock)
            {
                _send = null;
                _userId = null;

                Forget();
            }
        }

        private static void Forget()
        {
            _friends = [];
            _friendsSignature = null;
            _friendsSynced = false;
            _friendsSyncedAt = default;
            _blocks = [];
            _blocksSynced = false;
            _setting = null;

            ForgetCaches();
        }

        // ---- results (§1.2) ----

        /// <summary>Success.</summary>
        public const int Ok = 0;

        /// <summary>2121-0002, a bad argument.</summary>
        public const int InvalidArgument = 2;

        /// <summary>2121-0012, a body that does not fit the module's buffer.</summary>
        public const int TooLarge = 12;

        /// <summary>2121-2411, "that friend is not in the cache".</summary>
        public const int FriendNotFound = 2411;

        /// <summary>2121-3999, a status the module has no mapping for, and no reachable server.</summary>
        public const int Unknown = 3999;

        private static readonly Dictionary<(int Status, string Code), int> _baasErrors = new()
        {
            [(400, "invalid_params")] = 2001,
            [(400, "invalid_request")] = 2002,
            [(400, "invalid_operation")] = 2003,
            [(400, "sender_friend_capacity_is_full")] = 2004,
            [(400, "receiver_friend_capacity_is_full")] = 2005,
            [(401, "invalid_token")] = 2011,
            [(403, "insufficient_scope")] = 2021,
            [(403, "forbidden")] = 2022,
            [(403, "operation_is_not_permitted")] = 2023,
            [(404, "resource_is_not_found")] = 2031,
            [(405, "method_not_allowed")] = 2041,
            [(406, "not_acceptable_language")] = 2051,
            [(409, "resource_already_exists")] = 2061,
            [(409, "resource_duplicated")] = 2062,
            [(400, "receiver_must_be_different_from_sender")] = 5,
            [(400, "target_user_must_be_different_from_user")] = 5,
            [(400, "could_not_retrieve_sender")] = 2212,
            [(400, "could_not_retrieve_receiver")] = 2222,
            [(400, "could_not_retrieve_target_user")] = 2223,
            [(404, "deleted_user")] = 2224,
            [(400, "sender_and_receiver_are_already_friends")] = 2501,
            [(409, "sender_blocks_receiver")] = 2506,
            [(400, "could_not_send_request")] = 2507,
            [(400, "sender_is_in_cool_time")] = 2508,
            [(400, "friend_request_in_cool_time")] = 2509,
            [(409, "user_block_capacity_is_full")] = 2702,
            [(400, "invalid_friend_code_format")] = 2801,
            [(412, "precondition_failed")] = 2201,
            [(422, "friend_code_unregenerable_state")] = 2301,
            [(500, "internal_server_error")] = 2101,
            [(503, "under_maintenance")] = 2111,
        };

        /// <summary>The status alone, when no error body was understood (§1.2).</summary>
        private static int FromStatus(int status) => status switch
        {
            >= 200 and < 300 => Ok,
            302 => 3302,
            >= 300 and < 400 => 3399,
            400 => 3400,
            401 or 402 => 3499,
            403 => 3403,
            404 => 3404,
            >= 400 and < 500 => 3499,
            500 => 3500,
            501 or 502 => 3599,
            503 => 3503,
            504 => 3504,
            >= 500 and < 600 => 3599,
            _ => Unknown,
        };

        /// <summary>A BAAS error body: $.status and $.errorCode together pick the result.</summary>
        public static int BaasError(Reply reply)
        {
            if (reply.Success)
            {
                return Ok;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(reply.Body ?? string.Empty);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Number &&
                    status.TryGetInt32(out int code) &&
                    root.TryGetProperty("errorCode", out JsonElement error) && error.ValueKind == JsonValueKind.String &&
                    error.GetString() is { Length: <= 0x3F } text)
                {
                    if (_baasErrors.TryGetValue((code, text), out int description))
                    {
                        return description;
                    }

                    return FromStatus(code);
                }
            }
            catch (JsonException)
            {
                // No body the parser understood; the status alone decides.
            }

            return FromStatus(reply.Status);
        }

        /// <summary>A Five error body: $.error.code is four characters, and picks 2121-29xx.</summary>
        public static int FiveError(Reply reply)
        {
            if (reply.Success)
            {
                return Ok;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(reply.Body ?? string.Empty);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("error", out JsonElement error) &&
                    error.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String &&
                    code.GetString() is { Length: 4 } text &&
                    error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                {
                    return text switch
                    {
                        "0001" => 2901,
                        "0002" => 2902,
                        "0003" => 2903,
                        "0004" => 2904,
                        "0005" => 2905,
                        "0006" => 2906,
                        "0007" => 2907,
                        "0008" => 2908,
                        "0009" => 2909,
                        "0010" => 2910,
                        _ => 2999,
                    };
                }
            }
            catch (JsonException)
            {
                // As above: fall back to the status.
            }

            return FromStatus(reply.Status);
        }

        /// <summary>Remap one description to another, as the send and answer paths do (§A.4).</summary>
        private static int Remap(int description, params (int From, int To)[] remaps)
        {
            foreach ((int from, int to) in remaps)
            {
                if (description == from)
                {
                    return to;
                }
            }

            return description;
        }

        // ---- transport ----

        private static async Task<Reply> SendAsync(HttpMethod method, string url, string contentType, string body, CancellationToken cancellationToken)
        {
            SendDelegate send = _send;

            if (send == null || _userId == null)
            {
                return new Reply(0, null);
            }

            try
            {
                return await send(method, url, contentType, body, cancellationToken);
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceFriend, $"[OpenPak] {method} {url}: {exception.Message}");

                return new Reply(0, null);
            }
        }

        private static async Task<(int Error, JsonElement Root, JsonDocument Document)> GetJsonAsync(
            string url, bool five, CancellationToken cancellationToken)
        {
            Reply reply = await SendAsync(HttpMethod.Get, url, null, null, cancellationToken);

            if (!reply.Success)
            {
                return (five ? FiveError(reply) : BaasError(reply), default, null);
            }

            try
            {
                JsonDocument document = JsonDocument.Parse(reply.Body ?? string.Empty);

                return (Ok, document.RootElement, document);
            }
            catch (JsonException)
            {
                return (Unknown, default, null);
            }
        }

        private static string Baas(string path) => $"https://{BaasHost}{path}";

        private static string Five(string path) => $"https://{FiveHost}{path}";

        /// <summary>One object, written straight out: no reflection, so trimming cannot break it.</summary>
        internal static string Json(Action<Utf8JsonWriter> body)
        {
            using MemoryStream stream = new();

            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>A JSON-Patch body, one op per request as the module sends it (§A.2).</summary>
        private static string Patch(string op, string path, Action<Utf8JsonWriter> value)
        {
            using MemoryStream stream = new();

            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("op", op);
                writer.WriteString("path", path);
                writer.WritePropertyName("value");
                value(writer);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        // ---- the friend list (§A.1, §A.2, §A.3) ----

        /// <summary>
        /// GET /2.0.0/users/&lt;me&gt;/friends?count=300. The server's list is authoritative: it
        /// replaces the cache whole, and anything that changed raises
        /// <see cref="FriendListChanged"/>, which is what the guest's notification queue carries.
        ///
        /// <paramref name="force"/> clears the 30 s cooldown, as an explicit sync and every
        /// successful write do.
        /// </summary>
        public static async Task<int> SyncFriendListAsync(bool force, CancellationToken cancellationToken)
        {
            if (!Ready)
            {
                return Unknown;
            }

            lock (_lock)
            {
                if (!force && _friendsSynced && DateTime.UtcNow - _friendsSyncedAt < _listCooldown)
                {
                    return Ok;
                }
            }

            (int error, JsonElement root, JsonDocument document) =
                await GetJsonAsync(Baas($"/2.0.0/users/{_userId}/friends?count=300"), false, cancellationToken);

            using (document)
            {
                if (error != Ok)
                {
                    return error;
                }

                List<BaasFriend> friends = ParseFriendList(root);
                string signature = Signature(root);
                bool changed;

                lock (_lock)
                {
                    changed = _friendsSignature != signature;
                    _friends = friends;
                    _friendsSignature = signature;
                    _friendsSynced = true;
                    _friendsSyncedAt = DateTime.UtcNow;
                }

                if (changed)
                {
                    FriendListChanged?.Invoke();
                }

                return Ok;
            }
        }

        /// <summary>
        /// What the module compares field by field: the items as they arrived. Anything different
        /// is a changed list, and a list that did not change says nothing to anyone.
        /// </summary>
        private static string Signature(JsonElement root)
        {
            JsonElement items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out JsonElement value)
                ? value
                : default;

            return items.ValueKind == JsonValueKind.Array ? items.GetRawText() : string.Empty;
        }

        /// <summary>Put one friend back into the cache, from a reply that carried the whole relationship.</summary>
        private static void Replace(BaasFriend friend)
        {
            lock (_lock)
            {
                List<BaasFriend> friends = [.. _friends];
                int index = friends.FindIndex(known => known.Id == friend.Id);

                if (index < 0)
                {
                    return;
                }

                friends[index] = friend;
                _friends = friends;
            }
        }

        /// <summary>Change one friend in the cache, for the local half of a queued change (§A.2).</summary>
        public static void Update(ulong id, Func<BaasFriend, BaasFriend> change)
        {
            lock (_lock)
            {
                List<BaasFriend> friends = [.. _friends];
                int index = friends.FindIndex(known => known.Id == id);

                if (index < 0)
                {
                    return;
                }

                friends[index] = change(friends[index]);
                _friends = friends;
            }
        }

        private static void Remove(ulong id)
        {
            lock (_lock)
            {
                List<BaasFriend> friends = [.. _friends];

                if (friends.RemoveAll(known => known.Id == id) > 0)
                {
                    _friends = friends;
                }
            }
        }

        /// <summary>
        /// PATCH /2.0.0/users/&lt;me&gt;/friends/&lt;id&gt;, one op. The reply must be the whole
        /// relationship object: the module replaces its cached entry with it, and a reply that is
        /// not one fails with 2121-2411.
        /// </summary>
        public static async Task<int> PatchFriendAsync(ulong friendId, string op, string path, Action<Utf8JsonWriter> value, CancellationToken cancellationToken)
        {
            Reply reply = await SendAsync(HttpMethod.Patch, Baas($"/2.0.0/users/{_userId}/friends/{friendId:x16}"),
                JsonPatch, Patch(op, path, value), cancellationToken);

            if (!reply.Success)
            {
                return BaasError(reply);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(reply.Body ?? string.Empty);

                if (ParseFriend(document.RootElement) is not { } friend)
                {
                    return FriendNotFound;
                }

                Replace(friend);
            }
            catch (JsonException)
            {
                return FriendNotFound;
            }

            // The entry the guest reads is not the one it read a moment ago, which is exactly
            // what the notification queue's list-update event says.
            FriendListChanged?.Invoke();

            return Ok;
        }

        /// <summary>
        /// DELETE /2.0.0/users/&lt;me&gt;/friends/&lt;id&gt; (§A.3). On success the list is synced
        /// inline — the friendship is gone because the next list no longer carries it. A friend the
        /// server does not know is dropped locally and reported as 2121-2411.
        /// </summary>
        public static async Task<int> DeleteFriendAsync(ulong friendId, CancellationToken cancellationToken)
        {
            Reply reply = await SendAsync(HttpMethod.Delete, Baas($"/2.0.0/users/{_userId}/friends/{friendId:x16}"),
                null, null, cancellationToken);

            if (reply.Success)
            {
                await SyncFriendListAsync(true, cancellationToken);

                return Ok;
            }

            int error = BaasError(reply);

            if (error == 2031 || error is >= 2220 and <= 2229)
            {
                Remove(friendId);

                FriendListChanged?.Invoke();

                return FriendNotFound;
            }

            return error;
        }

        // ---- users, relationships and blocks (§A.7, §A.8) ----

        /// <summary>GET /1.0.0/users?filter.id.$in=… — at most 100 de-duplicated ids, comma-joined.</summary>
        public static async Task<(int Error, List<BaasUser> Users)> UsersAsync(IEnumerable<ulong> ids, CancellationToken cancellationToken)
        {
            List<string> unique = [];

            foreach (ulong id in ids)
            {
                string text = id.ToString("x16");

                if (unique.Count == 100)
                {
                    break;
                }

                if (!unique.Contains(text))
                {
                    unique.Add(text);
                }
            }

            if (unique.Count == 0)
            {
                return (Ok, []);
            }

            (int error, JsonElement root, JsonDocument document) = await GetJsonAsync(
                Baas($"/1.0.0/users?filter.id.$in={string.Join(",", unique)}"), false, cancellationToken);

            using (document)
            {
                return error != Ok ? (error, []) : (Ok, ParseUsers(root));
            }
        }

        /// <summary>
        /// GET /1.0.0/users?filter.links.friendCode.id.$in=&lt;code&gt;. The code goes over the wire
        /// exactly as it was typed. An empty result is 2121-2221.
        /// </summary>
        public static async Task<(int Error, BaasUser User)> UserByFriendCodeAsync(string friendCode, CancellationToken cancellationToken)
        {
            (int error, JsonElement root, JsonDocument document) = await GetJsonAsync(
                Baas($"/1.0.0/users?filter.links.friendCode.id.$in={Uri.EscapeDataString(friendCode ?? string.Empty)}"),
                false, cancellationToken);

            using (document)
            {
                if (error != Ok)
                {
                    return (error, null);
                }

                List<BaasUser> users = ParseUsers(root);

                return users.Count == 0 ? (2221, null) : (Ok, users[0]);
            }
        }

        /// <summary>GET /2.0.0/users/&lt;me&gt;/relationships/&lt;id&gt;; a 404 is 2121-2221.</summary>
        public static async Task<(int Error, BaasRelationship Relationship)> RelationshipAsync(ulong userId, CancellationToken cancellationToken)
        {
            (int error, JsonElement root, JsonDocument document) = await GetJsonAsync(
                Baas($"/2.0.0/users/{_userId}/relationships/{userId:x16}"), false, cancellationToken);

            using (document)
            {
                if (error == 2031 || error == 3404)
                {
                    return (2221, null);
                }

                return error != Ok ? (error, null) : (Ok, ParseRelationship(root));
            }
        }

        /// <summary>
        /// GET /1.0.0/users/&lt;me&gt;/blocks?count=100 (§A.8). One item without targetUserId or a
        /// nickname fails the whole parse, as it does on the console.
        /// </summary>
        public static async Task<int> SyncBlockListAsync(CancellationToken cancellationToken)
        {
            if (!Ready)
            {
                return Unknown;
            }

            (int error, JsonElement root, JsonDocument document) = await GetJsonAsync(
                Baas($"/1.0.0/users/{_userId}/blocks?count=100"), false, cancellationToken);

            using (document)
            {
                if (error != Ok)
                {
                    return error;
                }

                if (ParseBlocks(root) is not { } blocks)
                {
                    return Unknown;
                }

                lock (_lock)
                {
                    _blocks = blocks;
                    _blocksSynced = true;
                }

                return Ok;
            }
        }

        // ---- the caller's own user (§A.6) ----

        /// <summary>
        /// GET /1.0.0/users/&lt;me&gt;, and the friend code it issues when there is none yet. The
        /// module's steps 3 and 4 (an initial playLog and the local play-log queue) are not walked:
        /// which group the module would pick is not established, and nothing here keeps a queue.
        /// </summary>
        public static async Task<int> SyncUserSettingAsync(CancellationToken cancellationToken)
        {
            if (!Ready)
            {
                return Unknown;
            }

            (int error, JsonElement root, JsonDocument document) = await GetJsonAsync(
                Baas($"/1.0.0/users/{_userId}"), false, cancellationToken);

            using (document)
            {
                if (error != Ok)
                {
                    return error;
                }

                if (ParseUserSetting(root) is not { } setting)
                {
                    return Unknown;
                }

                lock (_lock)
                {
                    _setting = setting;
                }

                if (string.IsNullOrEmpty(setting.FriendCode))
                {
                    return await IssueFriendCodeAsync(cancellationToken);
                }

                return Ok;
            }
        }

        /// <summary>
        /// PATCH /1.0.0/users/&lt;me&gt;. The reply is the whole user object and replaces what is
        /// held here.
        /// </summary>
        public static async Task<int> PatchUserAsync(string body, CancellationToken cancellationToken)
        {
            Reply reply = await SendAsync(HttpMethod.Patch, Baas($"/1.0.0/users/{_userId}"), JsonPatch, body, cancellationToken);

            if (!reply.Success)
            {
                return BaasError(reply);
            }

            StoreUser(reply.Body);

            return Ok;
        }

        private static void StoreUser(string body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body ?? string.Empty);

                if (ParseUserSetting(document.RootElement) is { } setting)
                {
                    lock (_lock)
                    {
                        _setting = setting;
                    }
                }
            }
            catch (JsonException)
            {
                // A reply that is not a user object leaves the last one standing.
            }
        }

        /// <summary>
        /// POST /1.0.0/users/&lt;me&gt;/generate_code with the literal body `type=NX` (§A.6). The
        /// reply is the whole user, carrying the new code.
        /// </summary>
        public static async Task<int> IssueFriendCodeAsync(CancellationToken cancellationToken)
        {
            Reply reply = await SendAsync(HttpMethod.Post, Baas($"/1.0.0/users/{_userId}/generate_code"),
                "application/x-www-form-urlencoded", "type=NX", cancellationToken);

            if (!reply.Success)
            {
                return BaasError(reply);
            }

            StoreUser(reply.Body);

            return Ok;
        }

        /// <summary>
        /// The four `add` ops a play-log permission change is made of, always in the same order: the
        /// chosen group keeps the log and the other three are emptied (§A.6).
        /// </summary>
        public static string PlayLogPermissionBody(int permission, string playLog)
        {
            (int Group, string Path)[] groups =
            [
                (1, "/extras/self/playLog"),
                (2, "/extras/favoriteFriends/playLog"),
                (3, "/extras/friends/playLog"),
                (5, "/extras/everyone/playLog"),
            ];

            using MemoryStream stream = new();

            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartArray();

                foreach ((int group, string path) in groups)
                {
                    writer.WriteStartObject();
                    writer.WriteString("op", "add");
                    writer.WriteString("path", path);
                    writer.WriteString("value", group == permission ? playLog : string.Empty);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>One `replace` op on the caller's own user.</summary>
        public static string UserPatchBody(string path, Action<Utf8JsonWriter> value) => Patch("replace", path, value);

        // ---- invitations (Five, invitations doc §1c–§3e) ----

        /// <summary>GET /v2/users/&lt;me&gt;/invitations/inbox?invitation_types=friend: read and unread.</summary>
        public static async Task<(int Error, List<BaasInvitation> Invitations)> InvitationsAsync(CancellationToken cancellationToken)
        {
            (int error, JsonElement root, JsonDocument document) = await GetJsonAsync(
                Five($"/v2/users/{_userId}/invitations/inbox?invitation_types=friend"), true, cancellationToken);

            using (document)
            {
                if (error != Ok)
                {
                    return (error, []);
                }

                return ParseInvitations(root) is { } invitations ? (Ok, invitations) : (Unknown, []);
            }
        }

        /// <summary>GET /v1/invitation_groups/&lt;id&gt;: one group, with its receivers and messages.</summary>
        public static async Task<(int Error, BaasInvitationGroup Group)> InvitationGroupAsync(ulong groupId, CancellationToken cancellationToken)
        {
            (int error, JsonElement root, JsonDocument document) = await GetJsonAsync(
                Five($"/v1/invitation_groups/{groupId}"), true, cancellationToken);

            using (document)
            {
                if (error != Ok)
                {
                    return (error, null);
                }

                return ParseInvitationGroup(root) is { } group ? (Ok, group) : (Unknown, null);
            }
        }

        /// <summary>
        /// POST /v2/invitation_groups with the body the module builds (invitations doc §3c), keys in
        /// its order: an empty application_data is left out, and only the message slots the game
        /// filled are sent. Any 2xx is success — the reply is never read.
        /// </summary>
        public static async Task<int> SendInvitationAsync(
            IReadOnlyList<string> receiverIds,
            ulong applicationId,
            byte acdIndex,
            ulong applicationGroupId,
            byte[] applicationData,
            IReadOnlyList<(string Language, string Text)> messages,
            bool applicationIdMatch,
            CancellationToken cancellationToken)
        {
            string body = Json(writer =>
            {
                writer.WriteStartArray("receiver_ids");

                foreach (string id in receiverIds)
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
                writer.WriteString("invitation_type", "friend");
                writer.WriteString("application_id", applicationId.ToString("x16"));
                writer.WriteNumber("acd_index", acdIndex);
                writer.WriteString("application_group_id", applicationGroupId.ToString("x16"));

                if (applicationData is { Length: > 0 })
                {
                    writer.WriteString("application_data", Convert.ToBase64String(applicationData));
                }

                writer.WriteStartObject("messages");

                foreach ((string language, string text) in messages)
                {
                    writer.WriteString(language, text);
                }

                writer.WriteEndObject();
                writer.WriteBoolean("application_id_match", applicationIdMatch);
            });

            // The module's body buffer is 0x2400 bytes and a longer one never leaves the console.
            if (Encoding.UTF8.GetByteCount(body) > 0x2400)
            {
                return TooLarge;
            }

            Reply reply = await SendAsync(HttpMethod.Post, Five("/v2/invitation_groups"), JsonType, body, cancellationToken);

            return reply.Success ? Ok : FiveError(reply);
        }

        /// <summary>
        /// PATCH /v1/invitations, the form body the module sends: decimal ids joined by %2C, at most
        /// 100 of them. An empty list is the mark-all form instead.
        /// </summary>
        public static async Task<int> ReadInvitationsAsync(IReadOnlyList<ulong> invitationIds, CancellationToken cancellationToken)
        {
            if (invitationIds.Count == 0)
            {
                Reply all = await SendAsync(HttpMethod.Patch, Five($"/v1/users/{_userId}/invitations/mark_as_read"),
                    null, string.Empty, cancellationToken);

                return all.Success ? Ok : FiveError(all);
            }

            List<string> ids = [];

            foreach (ulong id in invitationIds)
            {
                if (ids.Count == 100)
                {
                    break;
                }

                ids.Add(id.ToString());
            }

            Reply reply = await SendAsync(HttpMethod.Patch, Five("/v1/invitations"), "application/x-www-form-urlencoded",
                "read=true&ids=" + string.Join("%2C", ids), cancellationToken);

            return reply.Success ? Ok : FiveError(reply);
        }
    }
}
