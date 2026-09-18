using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// openpak.org, as this emulator speaks to it: `/api/v1`, the mods catalogue the website
    /// proxies, and the news service's emulator surface.
    ///
    /// This is ordinary public TLS to an ordinary hostname — nothing here is redirected, nothing
    /// here is pinned, and the CA fetched through it is the one the *guest's* traffic will later
    /// be checked against. Keeping those two apart is the whole point of having two clients: the
    /// guest's connections go to <c>OpenPakServer</c> under Nintendo's names, and a person's
    /// sign-in goes here under OpenPak's own.
    ///
    /// Every response is read with JsonDocument. The app publishes trimmed, so a deserialiser
    /// that needs reflection would work in a debug build and fail in a release one.
    /// </summary>
    public sealed class OpenPakApi
    {
        public static OpenPakApi Instance { get; } = new();

        private readonly SemaphoreSlim _gate = new(1, 1);

        private HttpClient _http;
        private string _baseUrl;
        private string _token;
        private bool _tokenLoaded;

        private OpenPakApi()
        {
            // A different site is a different account, so the client and the token it carries are
            // dropped whenever the address changes.
            OpenPakConfig.Changed += () =>
            {
                if (_baseUrl != OpenPakConfig.WebsiteUrl)
                {
                    Reset();
                }
            };

            // Another profile is another account: its own bearer, and everything that was showing
            // the last one's friends has to hear about it.
            OpenPakConfig.ProfileChanged += () =>
            {
                _tokenLoaded = false;
                _token = null;

                SignedInChanged?.Invoke();
            };
        }

        /// <summary>Raised when the signed-in account changes, so open dialogs can catch up.</summary>
        public event Action SignedInChanged;

        /// <summary>Whether a bearer is held. Not a promise that the server still honours it.</summary>
        public bool SignedIn => Token != null;

        /// <summary>The site this is talking to.</summary>
        public string BaseUrl => OpenPakConfig.WebsiteUrl;

        private string Token
        {
            get
            {
                if (!_tokenLoaded)
                {
                    _tokenLoaded = true;
                    _token = SecretStore.Available && OpenPakConfig.ProfileId.Length > 0 ? LoadToken() : null;
                }

                return _token;
            }
        }

        /// <summary>
        /// The active profile's bearer. An install from before profiles had one bearer per site;
        /// the first profile to ask for it — the one open at the first launch since — takes it.
        /// </summary>
        private static string LoadToken()
        {
            string token = SecretStore.Load(StoreKey);

            if (token != null)
            {
                return token;
            }

            token = SecretStore.Load(SiteKey);

            if (token != null && SecretStore.Store(StoreKey, token))
            {
                SecretStore.Erase(SiteKey);

                Logger.Info?.Print(LogClass.Application, $"[OpenPak] The saved sign-in now belongs to profile {OpenPakConfig.ProfileName}");
            }

            return token;
        }

        /// <summary>The site, as a key: pointing at another OpenPak is another account.</summary>
        internal static string SiteKey => OpenPakConfig.WebsiteUrl.Replace("https://", string.Empty)
            .Replace("http://", string.Empty).Replace('/', '_').Replace(':', '-');

        /// <summary>Tokens are kept per site and per profile: each profile is its own account.</summary>
        private static string StoreKey => KeyFor(OpenPakConfig.ProfileId);

        private static string KeyFor(string profileId) => $"{SiteKey}/{profileId}";

        private void Reset()
        {
            _http?.Dispose();
            _http = null;
            _tokenLoaded = false;
            _token = null;
            _baseUrl = OpenPakConfig.WebsiteUrl;
        }

        private HttpClient Client
        {
            get
            {
                if (_http == null)
                {
                    _baseUrl = OpenPakConfig.WebsiteUrl;
                    _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ryujinx-OpenPak/1.0");
                }

                return _http;
            }
        }

        // ---- sign-in ----

        /// <summary>
        /// Sign in with the account's own credentials and keep the bearer in the OS password
        /// store. The password is used once, here, and never written anywhere.
        /// </summary>
        /// <returns>null when it worked; otherwise why it did not, for showing to a person.</returns>
        public async Task<string> SignInAsync(string email, string password, string deviceName, CancellationToken cancellationToken)
        {
            if (!SecretStore.Available)
            {
                return SecretStore.UnavailableReason;
            }

            string body = Json(writer =>
            {
                writer.WriteString("email", email);
                writer.WriteString("password", password);
                writer.WriteString("device_name", deviceName);
            });

            try
            {
                using HttpResponseMessage response = await Client.PostAsync($"{BaseUrl}/api/v1/token",
                    new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);

                string text = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return "Wrong email or password.";
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return "Too many attempts. Wait a minute and try again.";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"OpenPak answered {(int)response.StatusCode}: {Trim(text)}";
                }

                using JsonDocument document = JsonDocument.Parse(text);

                string token = document.RootElement.GetProperty("token").GetString();

                // One account, one profile: two profiles on one account would share a cloud-save
                // slot and overwrite each other's progress. The token just minted is revoked
                // rather than kept, so the refusal leaves nothing behind.
                OpenPakProfile me = await MeAsync(token, cancellationToken);

                if (me?.AccountId != null && OpenPakLinks.HolderOf(me.AccountId, OpenPakConfig.ProfileId) is { } holder)
                {
                    await RevokeAsync(token, cancellationToken);

                    return $"This OpenPak account is already linked to the profile \"{holder}\". Sign in there, or sign that profile out first.";
                }

                if (!SecretStore.Store(StoreKey, token))
                {
                    return "Signed in, but the token could not be saved to the password store, so it was discarded.";
                }

                _token = token;
                _tokenLoaded = true;

                if (me != null)
                {
                    OpenPakLinks.Set(OpenPakConfig.ProfileId, me.AccountId, me.DisplayName, OpenPakConfig.ProfileName);
                }

                SignedInChanged?.Invoke();

                return null;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Sign-in failed: {exception.Message}");

                return $"Could not reach {BaseUrl}: {exception.Message}";
            }
        }

        /// <summary>
        /// Revoke the bearer on the server and forget it here. The local half happens even when
        /// the server cannot be reached: a person clicking sign out is done either way.
        /// </summary>
        public async Task SignOutAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RevokeAsync(Token, cancellationToken);
            }
            finally
            {
                SecretStore.Erase(StoreKey);
                OpenPakLinks.Remove(OpenPakConfig.ProfileId);

                _token = null;
                _tokenLoaded = true;

                SignedInChanged?.Invoke();
            }
        }

        /// <summary>
        /// Sign a profile that is not the active one out, for good: the profile is being deleted,
        /// and a bearer outliving its profile would be an account nobody can see to sign out.
        /// </summary>
        public async Task ForgetProfileAsync(string profileId, CancellationToken cancellationToken)
        {
            if (SecretStore.Available && SecretStore.Load(KeyFor(profileId)) is { } token)
            {
                await RevokeAsync(token, cancellationToken);

                SecretStore.Erase(KeyFor(profileId));
            }

            OpenPakLinks.Remove(profileId);
        }

        private async Task RevokeAsync(string token, CancellationToken cancellationToken)
        {
            if (token == null)
            {
                return;
            }

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Delete, $"{BaseUrl}/api/v1/token");

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] Sign-out did not reach the server: {exception.Message}");
            }
        }

        // ---- the account ----

        public async Task<OpenPakProfile> MeAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/me", cancellationToken);

            return document == null ? null : ReadProfile(document);
        }

        /// <summary>/me with a token that is not kept yet, to learn whose it is before keeping it.</summary>
        private async Task<OpenPakProfile> MeAsync(string token, CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, $"{BaseUrl}/api/v1/me");

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

                return ReadProfile(document);
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] Could not read the new sign-in's account: {exception.Message}");

                return null;
            }
        }

        private static OpenPakProfile ReadProfile(JsonDocument document)
        {
            JsonElement root = document.RootElement;
            List<string> platforms = [];

            if (root.TryGetProperty("linked_platforms", out JsonElement linked) && linked.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement platform in linked.EnumerateArray())
                {
                    platforms.Add(String(platform, "namespace"));
                }
            }

            return new OpenPakProfile(
                String(root, "account_id"),
                String(root, "display_name"),
                String(root, "country"),
                String(root, "birthday"),
                String(root, "avatar_url"),
                String(root, "friend_code"),
                platforms);
        }

        /// <summary>The built-in avatar, or any other image the site pointed us at.</summary>
        public async Task<byte[]> ImageAsync(string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            try
            {
                return await Client.GetByteArrayAsync(url.StartsWith("http") ? url : BaseUrl + url, cancellationToken);
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] Could not fetch {url}: {exception.Message}");

                return null;
            }
        }

        // ---- friends ----

        public async Task<IReadOnlyList<OpenPakFriend>> FriendsAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/me/friends", cancellationToken);

            return document == null ? [] : ReadFriends(document.RootElement, "friends");
        }

        public async Task<IReadOnlyList<OpenPakRequest>> RequestsAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/me/friends/requests", cancellationToken);

            if (document == null)
            {
                return [];
            }

            List<OpenPakRequest> requests = [];

            foreach ((string property, bool incoming) in new[] { ("incoming", true), ("outgoing", false) })
            {
                foreach (OpenPakFriend friend in ReadFriends(document.RootElement, property))
                {
                    requests.Add(new OpenPakRequest(friend.AccountId, friend.DisplayName, incoming)
                    {
                        Pid = friend.Pid,
                        FriendCode = friend.FriendCode,
                    });
                }
            }

            return requests;
        }

        /// <summary>Add by friend code. Returns null on success, or the server's reason.</summary>
        public Task<string> SendFriendRequestAsync(string friendCode, CancellationToken cancellationToken)
            => PostAsync("/api/v1/me/friends/requests",
                Json(writer => writer.WriteString("friend_code", friendCode)), cancellationToken);

        public Task<string> AcceptFriendAsync(string accountId, CancellationToken cancellationToken)
            => PostAsync("/api/v1/me/friends/requests/accept", AccountBody(accountId), cancellationToken);

        /// <summary>Removes a friendship, and is also how a pending request is declined.</summary>
        public Task<string> RemoveFriendAsync(string accountId, CancellationToken cancellationToken)
            => PostAsync("/api/v1/me/friends/remove", AccountBody(accountId), cancellationToken);

        public Task<string> BlockAsync(string accountId, CancellationToken cancellationToken)
            => PostAsync("/api/v1/me/friends/block", AccountBody(accountId), cancellationToken);

        public Task<string> UnblockAsync(string accountId, CancellationToken cancellationToken)
            => PostAsync("/api/v1/me/friends/unblock", AccountBody(accountId), cancellationToken);

        // ---- the Switch identity ----

        /// <summary>
        /// The adapter's view of this account as a Switch: the pid and friend code games see, and
        /// the friend graph with the presence the title servers reported. Minted on first ask,
        /// exactly as linking a console mints it.
        /// </summary>
        public async Task<OpenPakSwitchIdentity> SwitchIdentityAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/me/switch", cancellationToken);

            if (document == null)
            {
                return null;
            }

            JsonElement root = document.RootElement;

            return new OpenPakSwitchIdentity(
                Number(root, "pid"),
                String(root, "username"),
                String(root, "friend_code"),
                String(root, "baas_user_id"),
                String(root, "token"),
                ReadSwitchFriends(root, "friends"),
                [.. ReadSwitchFriends(root, "requests").ConvertAll(f =>
                    new OpenPakRequest(f.AccountId, f.DisplayName, true) { Pid = f.Pid, FriendCode = f.FriendCode })]);
        }

        /// <summary>
        /// The Nintendo-Account id_token the emulated console's device account federates with.
        /// Signing in here proved the account; this turns that proof into the console link without
        /// anybody typing the password a second time. Null when the site cannot mint one.
        /// </summary>
        public async Task<string> SwitchLinkTokenAsync(string clientId, CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage request = Authorised(HttpMethod.Post, $"{BaseUrl}/api/v1/me/switch/link");

                request.Content = new StringContent(Json(writer => writer.WriteString("client_id", clientId)),
                    Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

                return String(document.RootElement, "id_token");
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Link token: {exception.Message}");

                return null;
            }
        }

        /// <summary>request / accept / decline, in the adapter's own terms (pid or friend code).</summary>
        public Task<string> SwitchFriendAsync(string action, ulong pid, string friendCode, CancellationToken cancellationToken)
            => PostAsync("/api/v1/me/switch/friend", Json(writer =>
            {
                writer.WriteString("action", action);
                writer.WriteNumber("pid", pid);
                writer.WriteString("friend_code", friendCode ?? string.Empty);
            }), cancellationToken);

        // ---- invitations ----

        public async Task<IReadOnlyList<OpenPakInvitation>> InvitationsAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/me/invitations", cancellationToken);

            if (document == null || !document.RootElement.TryGetProperty("invitations", out JsonElement list))
            {
                return [];
            }

            List<OpenPakInvitation> invitations = [];

            foreach (JsonElement item in list.EnumerateArray())
            {
                invitations.Add(new OpenPakInvitation(
                    String(item, "invitation_id"),
                    String(item, "from"),
                    String(item, "title_id"),
                    String(item, "namespace"),
                    Time(item, "expires_at") ?? DateTime.UtcNow));
            }

            return invitations;
        }

        public Task<string> DeclineInvitationAsync(string invitationId, CancellationToken cancellationToken)
            => PostAsync("/api/v1/me/invitations/decline",
                Json(writer => writer.WriteString("invitation_id", invitationId)), cancellationToken);

        // ---- cloud saves ----

        public async Task<(IReadOnlyList<OpenPakSave> Saves, OpenPakSaveUsage Usage)> SavesAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/me/saves", cancellationToken);

            if (document == null)
            {
                return ([], new OpenPakSaveUsage(0, 0, 0));
            }

            JsonElement root = document.RootElement;
            List<OpenPakSave> saves = [];

            if (root.TryGetProperty("saves", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement save in list.EnumerateArray())
                {
                    List<OpenPakSaveVersion> versions = [];

                    if (save.TryGetProperty("versions", out JsonElement versionList) && versionList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement version in versionList.EnumerateArray())
                        {
                            versions.Add(new OpenPakSaveVersion(
                                (long)Number(version, "id"),
                                (int)Number(version, "number"),
                                Boolean(version, "conflict"),
                                (long)Number(version, "size"),
                                String(version, "sha256"),
                                String(version, "device"),
                                Time(version, "created_at") ?? DateTime.MinValue));
                        }
                    }

                    // Newest first, so a dialog can show "the one you would get" without sorting.
                    versions.Sort((left, right) => right.Number.CompareTo(left.Number));

                    saves.Add(new OpenPakSave(String(save, "platform"), String(save, "title_id"), versions));
                }
            }

            OpenPakSaveUsage usage = new(0, 0, 0);

            if (root.TryGetProperty("usage", out JsonElement used))
            {
                usage = new OpenPakSaveUsage((long)Number(used, "allowance_used"),
                    (long)Number(used, "allowance"), (long)Number(used, "total"));
            }

            return (saves, usage);
        }

        /// <summary>The newest save for a title, or null when the cloud has none.</summary>
        public async Task<OpenPakSaveDownload> DownloadSaveAsync(string platform, string titleId, CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage request = Authorised(HttpMethod.Get,
                    $"{BaseUrl}/api/v1/me/saves/{platform}/{titleId}");

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
                {
                    return null;
                }

                return new OpenPakSaveDownload(
                    await response.Content.ReadAsByteArrayAsync(cancellationToken),
                    Header(response, "X-Save-Version"),
                    Header(response, "X-Save-Sha256"),
                    Header(response, "X-Save-Conflict") is "true" or "1");
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Save download failed: {exception.Message}");

                return null;
            }
        }

        /// <summary>Push a save up. Failure is null on success, or why not; Version is what the cloud now calls it.</summary>
        public async Task<(string Failure, string Version)> UploadSaveAsync(string platform, string titleId, byte[] data,
            string baseVersion, string device, CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage request = Authorised(HttpMethod.Put,
                    $"{BaseUrl}/api/v1/me/saves/{platform}/{titleId}");

                request.Content = new ByteArrayContent(data);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentLength = data.Length;

                if (!string.IsNullOrEmpty(baseVersion))
                {
                    request.Headers.Add("X-Save-Base", baseVersion);
                }

                request.Headers.Add("X-Save-Device", device);

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                string text = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument created = JsonDocument.Parse(text);

                    return (null, ((long)Number(created.RootElement, "number")).ToString());
                }

                return (response.StatusCode == HttpStatusCode.InsufficientStorage
                    ? "The OpenPak allowance is full. Connect your own storage at openpak.org/account/saves."
                    : $"OpenPak answered {(int)response.StatusCode}: {Trim(text)}", null);
            }
            catch (Exception exception)
            {
                return (exception.Message, null);
            }
        }

        // ---- mods ----

        public async Task<IReadOnlyList<OpenPakMod>> ModsAsync(string titleId, CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync($"/api/v1/titles/{titleId}/mods", cancellationToken);

            return document == null ? [] : ReadMods(document.RootElement, "mods");
        }

        public async Task<IReadOnlyList<OpenPakMod>> FavouriteModsAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/me/favourites", cancellationToken);

            return document == null ? [] : ReadMods(document.RootElement, "favourites");
        }

        public async Task<bool> FavouriteModAsync(string id, bool favourite, CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage request = Authorised(favourite ? HttpMethod.Put : HttpMethod.Delete,
                    $"{BaseUrl}/api/v1/me/favourites/{id}");

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The mod's zip, verified against the sha256 the catalogue published.</summary>
        public async Task<byte[]> ModPackageAsync(OpenPakMod mod, CancellationToken cancellationToken)
        {
            try
            {
                string url = mod.PackageUrl.StartsWith("http") ? mod.PackageUrl : BaseUrl + mod.PackageUrl;

                byte[] package = await Client.GetByteArrayAsync(url, cancellationToken);

                string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(package));

                if (!string.IsNullOrEmpty(mod.Sha256) && !actual.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] {mod.Name} did not match its published hash ({actual} != {mod.Sha256}); refusing it.");

                    return null;
                }

                return package;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not download {mod.Name}: {exception.Message}");

                return null;
            }
        }

        // ---- news ----

        /// <summary>Title ids the news service currently has a dataset for.</summary>
        public async Task<IReadOnlyList<string>> NewsTitlesAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/emulator/v1/bcat/titles", cancellationToken);

            if (document == null || !document.RootElement.TryGetProperty("titles", out JsonElement list))
            {
                return [];
            }

            List<string> titles = [];

            foreach (JsonElement title in list.EnumerateArray())
            {
                titles.Add(title.GetString());
            }

            return titles;
        }

        /// <summary>What a title would receive over BCAT right now.</summary>
        public async Task<OpenPakNewsManifest> NewsManifestAsync(string titleId, CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync($"/api/emulator/v1/bcat/titles/{titleId.ToLowerInvariant()}", cancellationToken);

            if (document == null)
            {
                return null;
            }

            JsonElement root = document.RootElement;
            List<OpenPakNewsFile> files = [];

            if (root.TryGetProperty("files", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement file in list.EnumerateArray())
                {
                    files.Add(new OpenPakNewsFile(String(file, "path"), (long)Number(file, "size"),
                        String(file, "sha256"), String(file, "url")));
                }
            }

            return new OpenPakNewsManifest(String(root, "title_id"),
                Time(root, "valid_from") ?? DateTime.MinValue, Time(root, "valid_until"), files);
        }

        public async Task<byte[]> NewsFileAsync(OpenPakNewsFile file, CancellationToken cancellationToken)
        {
            try
            {
                return await Client.GetByteArrayAsync(
                    file.Url.StartsWith("http") ? file.Url : BaseUrl + file.Url, cancellationToken);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- status and the CA ----

        /// <summary>Who is online. Public: this answers even when the sign-in is the broken part.</summary>
        public async Task<OpenPakStatus> StatusAsync(CancellationToken cancellationToken)
        {
            using JsonDocument document = await GetAsync("/api/v1/status", cancellationToken);

            if (document == null)
            {
                return null;
            }

            JsonElement root = document.RootElement;

            return new OpenPakStatus((int)Number(root, "players_online"),
                ReadPopulations(root, "titles", "title_id"), ReadPopulations(root, "networks", "namespace"));
        }

        /// <summary>
        /// The CA every console-facing certificate chains to, fetched over public TLS from the
        /// site. This is the one bootstrap that has to come from somewhere already trusted:
        /// afterwards the guest's redirected connections are checked against it and nothing else.
        /// </summary>
        public async Task<byte[]> CertificateAuthorityAsync(CancellationToken cancellationToken)
        {
            try
            {
                using HttpResponseMessage response = await Client.GetAsync($"{BaseUrl}/ca.pem", cancellationToken);

                return response.IsSuccessStatusCode
                    ? await response.Content.ReadAsByteArrayAsync(cancellationToken)
                    : null;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not fetch the CA: {exception.Message}");

                return null;
            }
        }

        // ---- plumbing ----

        private HttpRequestMessage Authorised(HttpMethod method, string url)
        {
            HttpRequestMessage request = new(method, url);

            if (Token != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            }

            return request;
        }

        private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                using HttpRequestMessage request = Authorised(HttpMethod.Get, BaseUrl + path);

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The bearer outlived its core session, or was revoked elsewhere. Dropping it
                    // here is what turns "everything silently returns nothing" into "signed out".
                    await ForgetAsync();

                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Debug?.Print(LogClass.Application,
                        $"[OpenPak] GET {path} returned {(int)response.StatusCode}: {Trim(body)}");

                    return null;
                }

                return JsonDocument.Parse(body);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] GET {path} failed: {exception.Message}");

                return null;
            }
        }

        private async Task<string> PostAsync(string path, string body, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);

            try
            {
                using HttpRequestMessage request = Authorised(HttpMethod.Post, BaseUrl + path);

                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return null;
                }

                string text = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await ForgetAsync();

                    return "That sign-in is no longer valid. Sign in again.";
                }

                // The body carries the reason the core gave; a bare status code says nothing
                // useful about a conflicting relationship or an unknown friend code.
                return Reason(text) ?? $"OpenPak answered {(int)response.StatusCode}.";
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Drop a bearer the server no longer honours, without calling back out to it.</summary>
        private Task ForgetAsync()
        {
            SecretStore.Erase(StoreKey);

            _token = null;
            _tokenLoaded = true;

            SignedInChanged?.Invoke();

            return Task.CompletedTask;
        }

        private static List<OpenPakFriend> ReadFriends(JsonElement root, string property)
        {
            List<OpenPakFriend> friends = [];

            if (!root.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return friends;
            }

            foreach (JsonElement friend in list.EnumerateArray())
            {
                friends.Add(new OpenPakFriend(
                    String(friend, "account_id"),
                    String(friend, "display_name"),
                    Boolean(friend, "online"),
                    String(friend, "title_id"),
                    String(friend, "namespace"),
                    Time(friend, "since")));
            }

            return friends;
        }

        /// <summary>
        /// The adapter's own friend shape, which is not the core's: it speaks in pids and carries
        /// a presence object rather than a flat `online`.
        /// </summary>
        private static List<OpenPakFriend> ReadSwitchFriends(JsonElement root, string property)
        {
            List<OpenPakFriend> friends = [];

            if (!root.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return friends;
            }

            foreach (JsonElement friend in list.EnumerateArray())
            {
                bool online = false;
                string titleId = string.Empty;

                if (friend.TryGetProperty("presence", out JsonElement presence))
                {
                    online = PresenceOnline(presence);
                    titleId = PresenceTitleId(presence);
                }

                friends.Add(new OpenPakFriend(
                    String(friend, "account_id"),
                    String(friend, "name"),
                    online,
                    titleId,
                    "switch",
                    null)
                {
                    Pid = Number(friend, "pid"),
                    FriendCode = String(friend, "friend_code"),
                });
            }

            return friends;
        }

        /// <summary>
        /// The adapter reports the state as a number (0 offline, 1 online, 2 playing) and the
        /// core as a word; either can arrive, and neither may, so every shape reads as offline
        /// rather than throwing — a presence that cannot be read must not take the friend list
        /// down with it.
        /// </summary>
        private static bool PresenceOnline(JsonElement presence)
        {
            if (!presence.TryGetProperty("status", out JsonElement status))
            {
                return false;
            }

            if (status.ValueKind == JsonValueKind.Number)
            {
                return status.TryGetInt32(out int code) && code > 0;
            }

            string state = status.ValueKind == JsonValueKind.String ? status.GetString() : null;

            return state != null &&
                (state.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("PLAYING", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The title a playing friend is in, as the sixteen-digit id games use.</summary>
        private static string PresenceTitleId(JsonElement presence)
        {
            if (presence.TryGetProperty("app_id", out JsonElement appId))
            {
                ulong id = appId.ValueKind == JsonValueKind.String && ulong.TryParse(appId.GetString(), out ulong parsed)
                    ? parsed
                    : appId.ValueKind == JsonValueKind.Number && appId.TryGetUInt64(out ulong number) ? number : 0;

                if (id != 0)
                {
                    return id.ToString("X16");
                }
            }

            return string.Empty;
        }

        private static List<OpenPakMod> ReadMods(JsonElement root, string property)
        {
            List<OpenPakMod> mods = [];

            if (!root.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return mods;
            }

            foreach (JsonElement mod in list.EnumerateArray())
            {
                mods.Add(new OpenPakMod(
                    String(mod, "id"),
                    String(mod, "slug"),
                    String(mod, "title_id"),
                    String(mod, "name"),
                    String(mod, "version"),
                    String(mod, "author"),
                    String(mod, "licence"),
                    String(mod, "source_url"),
                    String(mod, "layout"),
                    String(mod, "summary"),
                    String(mod, "sha256"),
                    (long)Number(mod, "size"),
                    String(mod, "package_url")));
            }

            return mods;
        }

        private static List<OpenPakPopulation> ReadPopulations(JsonElement root, string property, string key)
        {
            List<OpenPakPopulation> populations = [];

            if (!root.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return populations;
            }

            foreach (JsonElement item in list.EnumerateArray())
            {
                populations.Add(new OpenPakPopulation(String(item, key), String(item, "namespace"),
                    (int)Number(item, "players")));
            }

            return populations;
        }

        private static string AccountBody(string accountId)
            => Json(writer => writer.WriteString("account_id", accountId));

        /// <summary>The `error` field, when the server sent one; null when it did not.</summary>
        private static string Reason(string body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);

                return document.RootElement.TryGetProperty("error", out JsonElement error)
                    ? error.GetString()
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Header(HttpResponseMessage response, string name)
            => response.Headers.TryGetValues(name, out IEnumerable<string> values) ||
                response.Content.Headers.TryGetValues(name, out values)
                    ? string.Join(string.Empty, values)
                    : null;

        private static string String(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static ulong Number(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number)
                    ? number
                    : 0;

        private static bool Boolean(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static DateTime? Time(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
                        ? parsed
                        : null;

        /// <summary>One object, written straight out: no reflection, so trimming cannot break it.</summary>
        private static string Json(Action<Utf8JsonWriter> body)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string Trim(string body) => body.Length > 200 ? body[..200] : body;
    }
}
