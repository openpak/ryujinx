using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// Sending online-play invitations, on the route a console's friends module takes for
    /// SendFriendInvitation (30901), and finding the people to send them to.
    ///
    /// The friend list the guest sees is keyed by pid, but the invitation surface speaks BAAS user
    /// ids. A friend code is the one thing both know, so each friend is looked up by it once — the
    /// same lookup a console makes when a code is typed in — and kept.
    ///
    /// Also here: publishing presence the moment a game changes it, as the module does.
    /// </summary>
    public partial class OpenPakSession
    {
        // Friend code → the BAAS user behind it, and their picture, both once found.
        private readonly Dictionary<string, (string Id, string ThumbnailUrl)> _friendUsers = new();
        private readonly Dictionary<string, byte[]> _friendAvatars = new();

        /// <summary>
        /// The BAAS user id an invitation to this friend is addressed to, or null when the friend
        /// has no friend code or the lookup finds nobody.
        /// </summary>
        public async Task<string> ReceiverIdAsync(OpenPakFriend friend, CancellationToken cancellationToken)
            => (await FriendUserAsync(friend?.FriendCode, cancellationToken))?.Id;

        /// <summary>
        /// The receiver for an id a game passed itself (StartSendingFriendInvitation). The friend
        /// list the guest reads is the server's, so an id in it is already the BAAS user id the
        /// invitation is addressed to. Anything else is still looked up through the website
        /// friend list by friend code, for a game that kept an id from before.
        /// </summary>
        public async Task<string> ReceiverIdAsync(ulong networkServiceAccountId, CancellationToken cancellationToken)
        {
            if (OpenPakBaas.Friend(networkServiceAccountId) != null)
            {
                return networkServiceAccountId.ToString("x16");
            }

            OpenPakFriend friend = OpenPakAccount.Instance.Friends.FirstOrDefault(known => known.Pid == networkServiceAccountId);

            return (friend != null ? await ReceiverIdAsync(friend, cancellationToken) : null)
                ?? networkServiceAccountId.ToString("x16");
        }

        /// <summary>A friend's picture from their BAAS user, fetched once over the pinned client. Null when there is none.</summary>
        public async Task<byte[]> FriendAvatarAsync(OpenPakFriend friend, CancellationToken cancellationToken)
        {
            string code = friend?.FriendCode;

            if (code == null)
            {
                return null;
            }

            lock (_friendAvatars)
            {
                if (_friendAvatars.TryGetValue(code, out byte[] cached))
                {
                    return cached;
                }
            }

            string url = (await FriendUserAsync(code, cancellationToken))?.ThumbnailUrl;

            if (url == null)
            {
                return null;
            }

            try
            {
                byte[] avatar = await _http.GetByteArrayAsync(url, cancellationToken);

                lock (_friendAvatars)
                {
                    _friendAvatars[code] = avatar;
                }

                return avatar;
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not fetch {friend.DisplayName}'s picture: {exception.Message}");

                return null;
            }
        }

        private async Task<(string Id, string ThumbnailUrl)?> FriendUserAsync(string friendCode, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(friendCode))
            {
                return null;
            }

            lock (_friendUsers)
            {
                if (_friendUsers.TryGetValue(friendCode, out (string, string) cached))
                {
                    return cached;
                }
            }

            await EnsureAsync(cancellationToken);

            if (_applicationToken == null)
            {
                return null;
            }

            // The console's filter, with the code as it types it: digits and dashes, no SW- prefix.
            string code = friendCode.StartsWith("SW-", StringComparison.OrdinalIgnoreCase) ? friendCode[3..] : friendCode;

            try
            {
                using JsonDocument found = await GetAsync(
                    $"https://{BaasHost}/1.0.0/users?filter.links.friendCode.id.$in={Uri.EscapeDataString(code)}", cancellationToken);

                // The flat user shape (contract A.7): items missing id, nickname or
                // thumbnailUrl are skipped, so a half-written user never poisons the cache.
                BaasUser user = OpenPakBaas.ParseUsers(found.RootElement).FirstOrDefault();

                if (user == null)
                {
                    return null;
                }

                (string, string) entry = (user.Id.ToString("x16"), user.ThumbnailUrl);

                lock (_friendUsers)
                {
                    _friendUsers[friendCode] = entry;
                }

                return entry;
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not look up friend code {friendCode}: {exception.Message}");
            }

            return null;
        }

        /// <summary>
        /// Publish the presence, if it is not the one already published. The module sends presence
        /// when the state or the app-field blob changes and at no other time — no heartbeat, no
        /// timer — so this is the only thing that ever puts one on the wire. <paramref name="force"/>
        /// is the console's republish on reconnect. Nothing is said before a sign-in.
        /// </summary>
        private async Task PublishPresenceAsync(bool force = false)
        {
            if (!Enabled || _userId == null || _device == null)
            {
                return;
            }

            string body = CurrentPresenceBody();

            await _presenceGate.WaitAsync();

            try
            {
                if (!force && body == _publishedPresence)
                {
                    return;
                }

                await PresenceAsync(body, CancellationToken.None);

                // Kept only once the server has it: a publish that failed is one to try again.
                _publishedPresence = body;
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Presence update failed: {exception.Message}");
            }
            finally
            {
                _presenceGate.Release();
            }
        }

        /// <summary>
        /// The friend list, on the module's own route (contract §A.1). A change raises the event
        /// the guest's notification queue carries, so a game or applet waiting on it refreshes.
        /// </summary>
        private async Task SyncFriendsAsync()
        {
            if (!Enabled || _userId == null)
            {
                return;
            }

            await OpenPakBaas.SyncFriendListAsync(force: false, CancellationToken.None);
        }

        /// <summary>
        /// The caches the guest's friends module reads and cannot fetch for itself, because a
        /// command answered with an HTTP round trip is a game left waiting: the block list
        /// (§A.8), the caller's own user (§A.6) and the invitation inbox with its groups (§A.5).
        /// </summary>
        private async Task SyncModuleCachesAsync()
        {
            if (!Enabled || _userId == null)
            {
                return;
            }

            await OpenPakBaas.SyncBlockListAsync(CancellationToken.None);
            await OpenPakBaas.SyncUserSettingAsync(CancellationToken.None);
            await OpenPakBaas.SyncInvitationsAsync(CancellationToken.None);
        }

        /// <summary>
        /// How <see cref="OpenPakBaas"/> reaches the server: this session's pinned client and the
        /// user-scoped bearer from the login, with the sign-in refreshed first when it has aged out.
        /// A request that cannot be made at all comes back as status 0, which the caller maps like
        /// any other unreachable server.
        /// </summary>
        private async Task<OpenPakBaas.Reply> SendBaasAsync(
            HttpMethod method, string url, string contentType, string body, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);

            if (!Enabled || _http == null || _applicationToken == null)
            {
                return new OpenPakBaas.Reply(0, null);
            }

            using HttpRequestMessage request = new(method, url);

            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
            }

            request.Headers.Add("Authorization", "Bearer " + _applicationToken);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

            return new OpenPakBaas.Reply((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }

        /// <summary>A string as a JSON string literal, UTF-8 left as it is, as the console writes it.</summary>
        private static string JsonString(string value)
            => "\"" + JsonEncodedText.Encode(value ?? string.Empty, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

        /// <summary>
        /// POST /v2/invitation_groups with the body the friends module builds (0x1e03d0), keys in its
        /// order: an empty application_data is left out, and only the message slots the game filled
        /// are sent. Any 2xx is success — the console never reads the reply.
        /// </summary>
        public async Task<bool> SendInvitationAsync(
            IReadOnlyList<string> receiverIds,
            ulong applicationId,
            ulong applicationGroupId,
            byte[] applicationData,
            IReadOnlyList<(string Language, string Text)> messages,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);

            if (!Enabled || _applicationToken == null)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, "[OpenPak] Invitation not sent: not signed in");

                return false;
            }

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
                writer.WriteNumber("acd_index", 0);
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
                writer.WriteBoolean("application_id_match", false);
            });

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, $"https://{FiveHost}/v2/invitation_groups")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

                request.Headers.Add("Authorization", "Bearer " + _applicationToken);

                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning?.Print(LogClass.ServiceAcc,
                        $"[OpenPak] Invitation returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");

                    return false;
                }

                Logger.Info?.Print(LogClass.ServiceAcc,
                    $"[OpenPak] Invited {string.Join(", ", receiverIds)} to {applicationId:x16}");

                return true;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Invitation not sent: {exception.Message}");

                return false;
            }
        }
    }
}
