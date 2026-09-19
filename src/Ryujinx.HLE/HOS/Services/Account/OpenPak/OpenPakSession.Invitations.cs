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
        /// The receiver for an id a game passed itself (StartSendingFriendInvitation). Games read
        /// their friends' ids from friend:u, which serves pids; one that matches a friend is sent
        /// to that friend's BAAS user, and anything else is taken to be a BAAS id already.
        /// </summary>
        public async Task<string> ReceiverIdAsync(ulong networkServiceAccountId, CancellationToken cancellationToken)
        {
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

                foreach (JsonElement user in found.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (!user.TryGetProperty("id", out JsonElement id) || string.IsNullOrEmpty(id.GetString()))
                    {
                        continue;
                    }

                    (string, string) entry = (id.GetString(),
                        user.TryGetProperty("thumbnailUrl", out JsonElement thumbnail) ? thumbnail.GetString() : null);

                    lock (_friendUsers)
                    {
                        _friendUsers[friendCode] = entry;
                    }

                    return entry;
                }
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not look up friend code {friendCode}: {exception.Message}");
            }

            return null;
        }

        /// <summary>
        /// The game changed its presence declaration: publish it now, as the module does on each
        /// change, rather than at the next beat. Nothing is said before a sign-in.
        /// </summary>
        private async Task PublishPresenceAsync()
        {
            if (!Enabled || _userId == null || _device == null)
            {
                return;
            }

            try
            {
                await PresenceAsync("ONLINE", CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Presence update failed: {exception.Message}");
            }
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
