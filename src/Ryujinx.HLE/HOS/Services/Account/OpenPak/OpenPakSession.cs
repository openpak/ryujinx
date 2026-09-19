using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// The online chain a Switch walks at boot, walked here instead. The emulator HLEs the account
    /// sysmodule, so if this does not do it, nothing does — a game asking acc:u0 for an id_token
    /// gets whatever we hand it, and Nintendo's own servers are gone. Five requests, all to OpenPak:
    ///
    ///   POST dauth /v8/challenge            a challenge to mix into the device MAC
    ///   POST dauth /v8/device_auth_tokens   a device token
    ///   POST baas  /1.0.0/application/token an application token, the Bearer for the rest
    ///   POST baas  /1.0.0/users             a device account — once, then kept on disk
    ///   POST baas  /1.0.0/login             the id_token the game is actually asking for
    ///
    /// The hosts below are Nintendo's names on purpose: OpenPak answers to them and routes by SNI,
    /// so one address serves the whole chain and a title sees the names it was built to see.
    ///
    /// The device account identifies this emulator install, not a person. Its id_token carries an
    /// OpenPak identity only after the account has been linked, which is a separate flow.
    /// </summary>
    public partial class OpenPakSession
    {
        public static OpenPakSession Instance { get; } = new();

        private const string DauthHost = "dauth-lp1.ndas.srv.nintendo.net";

        // Where an invitation sent by a console lands. The website's /api/v1/me/invitations is the
        // core's own store and never sees one of these, so this is the only place to look.
        private const string FiveHost = "app.lp1.five.nintendo.net";

        private const string BaasHost = "e0d67c509fb203858ebcb2fe3f88c2aa.baas.nintendo.com";

        // Echoed back by the server, which knows the console from its client certificate rather
        // than from anything in the request body.
        private const string BaasClientId = "8f849b5d34778d8e";

        // The Nintendo Account surface: the sign-in page, the link states, and the token exchange.
        private const string NaHost = "accounts.nintendo.com";

        // Where the browser comes back to. Nothing listens on it — the emulator reads the code out
        // of the redirect itself rather than following it, because on a PC the same process that
        // asked for the link is the one approving it.
        private const string LinkRedirect = "openpak://linked";

        private readonly SemaphoreSlim _gate = new(1, 1);

        // Senders arrive as BAAS ids; the friend list is keyed by pid, so the name comes from the
        // same lookup the console uses, once per person.
        private readonly Dictionary<string, string> _senderNames = new();

        // Dismissed here as well as read on the server: read state is shared with every device on
        // the account, and hiding a row is this machine's business.
        private readonly HashSet<string> _dismissed = [];

        private HttpClient _http;
        private DeviceAccount _device;

        private string _idToken;
        private DateTime _idTokenExpiry;
        private ulong _networkServiceAccountId;
        private string _nickname;
        private string _friendCode;
        private string _userId;
        private string _avatarUrl;
        private byte[] _avatar;
        private Task _heartbeat;
        private IReadOnlyList<OpenPakInvitation> _invitations = [];

        /// <summary>The BAAS token presence speaks with: the login's own, user-scoped.</summary>
        private string _applicationToken;

        /// <summary>An OpenPak server is configured and reachable enough to have been set up.</summary>
        public bool Enabled => Server != null;

        /// <summary>The id_token OpenPak issued, or null when there is none to give.</summary>
        public string IdToken => _idToken;

        /// <summary>The BAAS user id as the u64 the guest calls a NetworkServiceAccountId, or 0.</summary>
        public ulong NetworkServiceAccountId => _networkServiceAccountId;

        /// <summary>host:port of the configured server, or null when there is none.</summary>
        public string ServerAddress => Server?.Address;

        /// <summary>The OpenPak account this install is linked to, or null while it is anonymous.</summary>
        public string Nickname => _nickname;

        /// <summary>The account's friend code, as other players would type it, or null.</summary>
        public string FriendCode => _friendCode;

        /// <summary>
        /// Whether the id_token carries an OpenPak identity. Signed in is not linked: an unlinked
        /// device account gets a perfectly valid token that no title server can attach to a person.
        /// </summary>
        public bool IsLinked => _nickname != null;

        /// <summary>What the native inbox held at the last poll, dismissals removed. Never null.</summary>
        public IReadOnlyList<OpenPakInvitation> Invitations => _invitations;

        /// <summary>Raised once for each invitation that was not there at the previous poll.</summary>
        public event Action<OpenPakInvitation> InvitationArrived;

        /// <summary>Raised on each linked sign-in with the profile id it belongs to and the account's nickname.</summary>
        public event Action<string, string> SignedInAs;

        private static OpenPakServer Server => OpenPakServer.Current;

        private OpenPakSession()
        {
            OpenPakConfig.ProfileChanged += () => _ = SwitchProfileAsync();
            OpenPakPresence.Changed += () => _ = PublishPresenceAsync();
        }

        /// <summary>
        /// Another local profile is another account, as another user is on a console: the one
        /// leaving goes offline now rather than at the end of its lease, nothing of it is kept, and
        /// the one arriving signs in with its own device account. Before anything signed in there
        /// is nothing to hand over, and the launch signs the first profile in by itself.
        /// </summary>
        private async Task SwitchProfileAsync()
        {
            if (_userId == null)
            {
                return;
            }

            await _gate.WaitAsync();

            try
            {
                await GoOfflineAsync();

                _device = null;
                _idToken = null;
                _idTokenExpiry = default;
                _networkServiceAccountId = 0;
                _nickname = null;
                _friendCode = null;
                _userId = null;
                _avatarUrl = null;
                _avatar = null;
                _applicationToken = null;
                _invitations = [];
                _senderNames.Clear();
                _dismissed.Clear();
            }
            finally
            {
                _gate.Release();
            }

            await EnsureAsync(CancellationToken.None);
        }

        /// <summary>
        /// Make sure a usable id_token is cached, fetching one if not. Never throws into the guest:
        /// a game that cannot reach OpenPak should behave like a console that cannot reach Nintendo,
        /// not like a crash.
        /// </summary>
        public async Task EnsureAsync(CancellationToken cancellationToken)
        {
            // No profile open yet means no device account to sign in with: the device account is
            // the profile's, and one made now would belong to nobody.
            if (!Enabled || OpenPakConfig.ProfileId.Length == 0 || Fresh())
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken);

            try
            {
                if (Fresh())
                {
                    return;
                }

                await LoginAsync(cancellationToken);

                // A signed-in person and an unlinked console is a state nobody asked for: the
                // website already holds the proof, so the link follows the login on its own.
                await LinkFromAccountLockedAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                // Leaving _idToken null is the whole error path: the caller falls back to the
                // offline token and the game sees a console that is simply not online.
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Sign-in failed: {exception.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Bind this install to the account that is signed in to the website, with the token the
        /// website mints for it. The e-mail and password are never asked for again: a person who
        /// signed in once has proved everything the console's link page would have asked.
        /// </summary>
        public async Task<bool> LinkFromAccountAsync(CancellationToken cancellationToken)
        {
            if (!Enabled)
            {
                return false;
            }

            await _gate.WaitAsync(cancellationToken);

            try
            {
                if (!Fresh())
                {
                    await LoginAsync(cancellationToken);
                }

                await LinkFromAccountLockedAsync(cancellationToken);

                return IsLinked;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Linking failed: {exception.Message}");

                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task LinkFromAccountLockedAsync(CancellationToken cancellationToken)
        {
            if (IsLinked || !OpenPakApi.Instance.SignedIn)
            {
                return;
            }

            string idToken = await OpenPakApi.Instance.SwitchLinkTokenAsync(BaasClientId, cancellationToken);

            if (idToken != null)
            {
                await LoginAsync(idToken, cancellationToken);
            }
        }

        // Five minutes of slack: a token that expires mid-session is worse than one fetched early.
        private bool Fresh() => _idToken != null && DateTime.UtcNow < _idTokenExpiry - TimeSpan.FromMinutes(5);

        private async Task LoginAsync(CancellationToken cancellationToken) => await LoginAsync(null, cancellationToken);

        /// <summary>
        /// The login chain. With a Nintendo Account id_token it goes to /federation instead, which
        /// is the same login plus the binding to an OpenPak account — after it, the id_token the
        /// guest receives carries the nnex claim a title server needs to know who is playing.
        /// </summary>
        private async Task LoginAsync(string accountIdToken, CancellationToken cancellationToken)
        {
            _http ??= Server.CreateClient();

            string challenge = (await PostAsync($"https://{DauthHost}/v8/challenge", null, null, cancellationToken))
                .RootElement.GetProperty("challenge").GetString();

            // Written by hand rather than serialized from a dictionary: the app publishes trimmed,
            // and a fixed shape does not need reflection to produce.
            string deviceTokenRequest = Json(writer =>
            {
                // The server verifies none of this — it has no Nintendo key to check the MAC with,
                // and says so in its own source. Sent because the shape is the console's.
                // ponytail: constants until a title turns up that reads them.
                writer.WriteString("system_version", "22.2.0-0.0");
                writer.WriteString("fw_revision", "0");
                writer.WriteNumber("key_generation", 11);
                writer.WriteString("challenge", challenge);
                writer.WriteString("mac", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                writer.WriteBoolean("ist", false);
                writer.WriteStartArray("token_requests");
                writer.WriteStartObject();
                writer.WriteString("client_id", BaasClientId);
                writer.WriteEndObject();
                writer.WriteEndArray();
            });

            using JsonDocument deviceTokens = await PostAsync($"https://{DauthHost}/v8/device_auth_tokens",
                new StringContent(deviceTokenRequest, Encoding.UTF8, "application/json"), null, cancellationToken);

            string deviceToken = deviceTokens.RootElement.GetProperty("results")[0]
                .GetProperty("device_auth_token").GetString();

            using JsonDocument application = await PostAsync($"https://{BaasHost}/1.0.0/application/token",
                Form(("assertion", deviceToken), ("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange")),
                null, cancellationToken);

            string applicationToken = application.RootElement.GetProperty("accessToken").GetString();

            // Presence and the other BAAS API calls after login speak with this token.
            _applicationToken = applicationToken;

            _device ??= DeviceAccount.Load(Server.Key, OpenPakConfig.ProfileId) ?? await CreateDeviceAccountAsync(applicationToken, cancellationToken);

            bool linking = accountIdToken != null;

            using JsonDocument login = await PostAsync(
                $"https://{BaasHost}/1.0.0/{(linking ? "federation" : "login")}",
                linking
                    ? Form(("id", _device.Id), ("password", _device.Password), ("idToken", accountIdToken))
                    : Form(("id", _device.Id), ("password", _device.Password)),
                applicationToken, cancellationToken);

            _idToken = login.RootElement.GetProperty("idToken").GetString();
            _idTokenExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(login.RootElement.GetProperty("expiresIn").GetInt32());
            _userId = login.RootElement.GetProperty("user").GetProperty("id").GetString();

            // Presence is user-scoped: the token it speaks with must carry the BAAS user as its
            // subject, which is the login's own accessToken and not the device-scoped one the
            // token exchange minted earlier — two tokens, same issuer, same typ, different
            // subject, and the user-scoped route refuses the device-shaped one with an opaque
            // 401.
            if (login.RootElement.TryGetProperty("accessToken", out JsonElement loginToken))
            {
                _applicationToken = loginToken.GetString();
            }

            _networkServiceAccountId = ParseUserId(_userId);
            _nickname = NicknameOf(login.RootElement);
            _friendCode = FriendCodeOf(login.RootElement);
            _avatarUrl = login.RootElement.GetProperty("user").TryGetProperty("thumbnailUrl", out JsonElement thumbnail)
                ? thumbnail.GetString()
                : null;
            _avatar = null;

            StartHeartbeat();

            if (_nickname != null)
            {
                SignedInAs?.Invoke(OpenPakConfig.ProfileId, _nickname);
            }

            Logger.Info?.Print(LogClass.ServiceAcc, _nickname != null
                ? $"[OpenPak] Signed in as {_nickname} on {Server.Address}"
                : $"[OpenPak] Signed in as device account {_device.Id} on {Server.Address} (not linked to an account)");
        }

        private async Task<DeviceAccount> CreateDeviceAccountAsync(string applicationToken, CancellationToken cancellationToken)
        {
            using JsonDocument created = await PostAsync($"https://{BaasHost}/1.0.0/users",
                new StringContent("{}", Encoding.UTF8, "application/json"), applicationToken, cancellationToken);

            JsonElement account = created.RootElement.GetProperty("deviceAccounts")[0];

            DeviceAccount device = new()
            {
                Id = account.GetProperty("id").GetString(),
                Password = account.GetProperty("password").GetString(),
            };

            device.Save(Server.Key, OpenPakConfig.ProfileId);

            Logger.Info?.Print(LogClass.ServiceAcc, $"[OpenPak] Registered device account {device.Id}");

            return device;
        }

        /// <summary>
        /// Bind this install to an OpenPak account.
        ///
        /// A console cannot do this itself: it has no keyboard worth the name, so it shows a QR and
        /// a code and lets a phone do the typing. This has a keyboard. The credentials go straight
        /// to OpenPak's authorize endpoint over the pinned connection, which answers with a redirect
        /// carrying an authorization code — the same code the browser flow would have delivered, and
        /// the same exchange after it. Nothing is stored: what is kept is the token that comes back.
        /// </summary>
        public async Task<bool> LinkAsync(string email, string password, CancellationToken cancellationToken)
        {
            if (!Enabled)
            {
                return false;
            }

            await _gate.WaitAsync(cancellationToken);

            try
            {
                _http ??= Server.CreateClient();

                string authorize = $"https://{NaHost}/connect/1.0.0/authorize" +
                    $"?response_type=code&client_id={BaasClientId}&redirect_uri={Uri.EscapeDataString(LinkRedirect)}";

                using HttpRequestMessage request = new(HttpMethod.Post, authorize)
                {
                    Content = Form(("email", email), ("password", password)),
                };

                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

                // Right credentials redirect. Wrong ones come back as the sign-in page again, with
                // the reason written on it — a 200, which is why this looks for the redirect rather
                // than for success.
                if (response.Headers.Location == null)
                {
                    Logger.Info?.Print(LogClass.ServiceAcc, "[OpenPak] Sign-in was refused.");

                    return false;
                }

                using JsonDocument token = await PostAsync($"https://{NaHost}/connect/1.0.0/api/token",
                    Form(("code", CodeFromRedirect(response.Headers.Location.ToString())),
                        ("client_id", BaasClientId), ("grant_type", "authorization_code")),
                    null, cancellationToken);

                // Federation is the login that also binds, so this replaces the cached token with
                // one that carries the account.
                await LoginAsync(token.RootElement.GetProperty("id_token").GetString(), cancellationToken);

                return IsLinked;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Linking failed: {exception.Message}");

                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The authorization code lives in the query of the redirect we are told to follow.</summary>
        private static string CodeFromRedirect(string redirect)
        {
            foreach (string part in redirect[(redirect.IndexOfAny(['?', '#']) + 1)..].Split('&'))
            {
                string[] pair = part.Split('=', 2);

                if (pair.Length == 2 && pair[0] == "code")
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            throw new InvalidOperationException($"the sign-in redirect carried no code: {redirect}");
        }

        /// <summary>
        /// The console's link screen, as the server draws it for every client. LinkUrl and Qr are
        /// null when the server has no address a phone could open, in which case NoPhoneReason says
        /// why — a square pointing at a name only this machine resolves would send the pairing id
        /// to whoever really owns that name.
        /// </summary>
        public sealed record LinkInvitation(string Code, string CodeDisplay, string LinkUrl, byte[] Qr, string NoPhoneReason);

        /// <summary>
        /// Start a link the way a console does, and get back what a console puts on screen: a code,
        /// and a QR pointing a phone at the page where it signs in. Rendered by the server so that
        /// every client — this emulator, the next one, a real console — shows the same screen and
        /// none of them needs a QR encoder of its own.
        /// </summary>
        public async Task<LinkInvitation> StartLinkAsync(CancellationToken cancellationToken)
        {
            if (!Enabled)
            {
                return null;
            }

            _http ??= Server.CreateClient();

            using JsonDocument start = await PostAsync(
                $"https://{NaHost}/connect/1.0.0/qr/new?response_type=code&client_id={BaasClientId}" +
                $"&redirect_uri={Uri.EscapeDataString(LinkRedirect)}", null, null, cancellationToken);

            JsonElement root = start.RootElement;
            const string DataUri = "data:image/png;base64,";

            string qr = root.TryGetProperty("qr", out JsonElement image) ? image.GetString() : null;

            return new LinkInvitation(
                root.GetProperty("code").GetString(),
                root.GetProperty("code_display").GetString(),
                root.TryGetProperty("link_url", out JsonElement link) ? link.GetString() : null,
                qr != null && qr.StartsWith(DataUri) ? Convert.FromBase64String(qr[DataUri.Length..]) : null,
                root.TryGetProperty("no_phone_reason", out JsonElement why) ? why.GetString() : null);
        }

        /// <summary>
        /// Follow the link through its two waits, in the order a console does them:
        ///
        ///   scan  ->  the phone signs in  ->  <paramref name="onSignedIn"/>, and only now is the
        ///   code worth showing  ->  the phone types it back  ->  this returns whose account it was
        ///
        /// The code is not shown before that first step on purpose. Revealing it only once someone
        /// has signed in is what makes a photograph of this screen worth nothing: the onlooker has
        /// a sign-in page, and the code still needs an account behind it.
        ///
        /// Null if the code was refused or ran out of time. Nothing is linked at this point either
        /// way — the person holding the emulator still has to say yes.
        /// </summary>
        public async Task<string> AwaitClaimAsync(string code, Action onSignedIn, CancellationToken cancellationToken)
        {
            bool announced = false;

            while (true)
            {
                using JsonDocument state = await GetClaimStateAsync(
                    $"https://{NaHost}/connect/1.0.0/qr/state?c={code}", cancellationToken);

                switch (state.RootElement.GetProperty("state").GetString())
                {
                    case "claimed":
                        return state.RootElement.GetProperty("account").GetString();

                    case "denied":
                    case "expired":
                        return null;

                    case "signed_in" when !announced:
                        announced = true;
                        onSignedIn();

                        goto default;

                    default:
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                        break;
                }
            }
        }

        /// <summary>Say yes to a claimed code, and finish the link it stands for.</summary>
        public async Task<bool> ApproveAsync(string code, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);

            try
            {
                using JsonDocument approved = await PostAsync(
                    $"https://{NaHost}/connect/1.0.0/qr/approve?c={code}", null, null, cancellationToken);

                using JsonDocument token = await PostAsync($"https://{NaHost}/connect/1.0.0/api/token",
                    Form(("code", CodeFromRedirect(approved.RootElement.GetProperty("redirect").GetString())),
                        ("client_id", BaasClientId), ("grant_type", "authorization_code")),
                    null, cancellationToken);

                await LoginAsync(token.RootElement.GetProperty("id_token").GetString(), cancellationToken);

                return IsLinked;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Approving the link failed: {exception.Message}");

                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<JsonDocument> GetClaimStateAsync(string url, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"GET {url} returned {(int)response.StatusCode}: {body}");
            }

            return JsonDocument.Parse(body);
        }

        /// <summary>
        /// Keep saying we are here, which is the only way anyone can tell that we are.
        ///
        /// Nothing reaches the server when a process is closed — an emulator that is gone sends
        /// nothing, including "I am gone" — so being online is a claim with a short life that has
        /// to be renewed. Stop renewing and the account drops offline on its own, which is what
        /// closing the window does. The console does this from its friends sysmodule; this is the
        /// same request on the same path.
        /// </summary>
        private void StartHeartbeat()
        {
            if (_heartbeat != null || _userId == null || _device == null)
            {
                return;
            }

            _heartbeat = Task.Run(async () =>
            {
                for (int tick = 0; ; tick++)
                {
                    // Three times inside the lease the server hands out, so one lost request is
                    // not a person blinking offline.
                    await Task.Delay(TimeSpan.FromSeconds(10));

                    // One beat for the life of the process: across a profile switch it speaks for
                    // whoever is signed in now, and in between for nobody.
                    if (_userId == null || _device == null)
                    {
                        continue;
                    }

                    // The inbox is store-and-forward and changes rarely; every third beat is
                    // often enough to hear about an invitation while the person still cares.
                    if (tick % 3 == 0)
                    {
                        await RefreshInvitationsAsync(CancellationToken.None);
                    }

                    try
                    {
                        await PresenceAsync("ONLINE", CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        // Presence is not worth a louder failure than this: the account simply
                        // goes quiet, which is exactly what it should look like.
                        Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Presence update failed: {exception.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Say we are going, so the account drops offline now rather than when the lease runs out.
        /// Best effort and briefly: a person closing a window should not wait on a network call,
        /// and if this never arrives the lease says the same thing half a minute later.
        /// </summary>
        public async Task GoOfflineAsync()
        {
            if (!Enabled || _userId == null)
            {
                return;
            }

            using CancellationTokenSource giveUp = new(TimeSpan.FromSeconds(2));

            try
            {
                await PresenceAsync("OFFLINE", giveUp.Token);
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not say goodbye: {exception.Message}");
            }
        }

        /// <summary>
        /// The presence request a console's friends sysmodule makes, on the same path.
        ///
        /// While a title runs the presence says what is being played, not merely that the
        /// console is on: the friends module reads the four app fields to draw "playing X", and
        /// acdIndex goes over the wire as a JSON number because the console sends it unquoted.
        ///
        /// In a title the state is the game's own declaration, as the module derives it: PLAYING
        /// only while an online-play session is declared open, ONLINE otherwise. appField is
        /// always sent, "{}" when the game set nothing, as a JSON object inside a JSON string.
        /// </summary>
        private async Task PresenceAsync(string state, CancellationToken cancellationToken)
        {
            string titleId = TitleIDs.CurrentApplication.Value.OrDefault();

            OpenPakPresence.State declared = OpenPakPresence.For(OpenPakConfig.ProfileId, titleId);

            string body = state == "OFFLINE" || titleId == null
                ? $$"""[{"op":"replace","path":"/presence/state","value":"{{state}}"}]"""
                : $$"""[{"op":"replace","path":"/presence/state","value":"{{(declared.SessionOpen ? "PLAYING" : "ONLINE")}}"},{"op":"replace","path":"/presence/extras/friends/appField","value":{{JsonString(declared.AppField)}}},{"op":"replace","path":"/presence/extras/friends/appInfo:appId","value":"{{titleId}}"},{"op":"replace","path":"/presence/extras/friends/appInfo:presenceGroupId","value":"{{titleId}}"},{"op":"replace","path":"/presence/extras/friends/appInfo:acdIndex","value":0}]""";

            using HttpRequestMessage request = new(HttpMethod.Patch,
                $"https://{BaasHost}/1.0.0/users/{_userId}/device_accounts/{_device.Id}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            // Unauthenticated presence is a 401 by design: the server has no idea whose device
            // is claiming to be online.
            if (_applicationToken != null)
            {
                request.Headers.Add("Authorization", "Bearer " + _applicationToken);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc,
                    $"[OpenPak] Presence {state} returned {(int)response.StatusCode}");
            }
        }

        /// <summary>
        /// What is waiting in the native invitation inbox, on the route a console's friends module
        /// asks for it.
        ///
        /// Polling is the whole delivery path here. A console is pushed its invitations over NPNS;
        /// this emulator holds no such connection, so the server queues the push, finds nobody, and
        /// drops it ten minutes later — while the invitation itself sits in the inbox for a day.
        /// Asking is therefore the difference between seeing one and never knowing it existed.
        ///
        /// Read state is not a filter: the same account signed in on a console marks these read
        /// from over there, and that is no reason for this machine to have missed it.
        /// </summary>
        public async Task RefreshInvitationsAsync(CancellationToken cancellationToken)
        {
            if (!Enabled || _userId == null || _applicationToken == null)
            {
                return;
            }

            List<OpenPakInvitation> waiting = [];
            List<OpenPakInvitation> arrived = [];

            try
            {
                using JsonDocument inbox = await GetAsync(
                    $"https://{FiveHost}/v2/users/{_userId}/invitations/inbox?invitation_types=friend&read=false",
                    cancellationToken);

                if (!inbox.RootElement.TryGetProperty("items", out JsonElement items))
                {
                    return;
                }

                foreach (JsonElement item in items.EnumerateArray())
                {
                    string sender = item.GetProperty("sender_id").GetString();

                    OpenPakInvitation invitation = InvitationOf(item,
                        await SenderNameAsync(sender, cancellationToken) ?? sender);

                    if (_dismissed.Contains(invitation.InvitationId))
                    {
                        continue;
                    }

                    waiting.Add(invitation);

                    if (!_invitations.Any(known => known.InvitationId == invitation.InvitationId))
                    {
                        arrived.Add(invitation);
                    }
                }
            }
            catch (Exception exception)
            {
                // An inbox that cannot be reached is the list staying as it was, not an error in
                // front of somebody who is playing something.
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Invitation inbox: {exception.Message}");

                return;
            }

            _invitations = waiting;

            // The guest's friends module asks for the count, and answers a title's "you have
            // been invited" badge with it; it marks read through the same door.
            OpenPakAccount.Instance.NativeInvitationsUnread = waiting.Count;
            OpenPakAccount.Instance.NativeInvitationsRead ??= ReadNativeAsync;

            foreach (OpenPakInvitation invitation in arrived)
            {
                InvitationArrived?.Invoke(invitation);
            }
        }

        /// <summary>The guest read some invitations (or all of them): the same dismissal a page does.</summary>
        private async Task ReadNativeAsync(IReadOnlyList<ulong> ids)
        {
            foreach (OpenPakInvitation invitation in _invitations.ToList())
            {
                if (ids.Count == 0 || ids.Contains(ulong.Parse(invitation.InvitationId)))
                {
                    await DismissInvitationAsync(invitation.InvitationId, CancellationToken.None);
                }
            }

            OpenPakAccount.Instance.NativeInvitationsUnread = _invitations.Count;
        }

        /// <summary>
        /// One inbox item as the pages show it.
        ///
        /// Ids are JSON numbers on the wire, because the friends module parses them with %llu and a
        /// string id would not survive that parser; everything above here wants the digits.
        /// The wire carries no expiry at all, so the server's own day is what an invitation is
        /// shown as having: it prunes on the same clock.
        /// </summary>
        public static OpenPakInvitation InvitationOf(JsonElement item, string senderName)
            => new(
                item.GetProperty("id").GetUInt64().ToString(),
                senderName,
                item.GetProperty("application_id").GetString(),
                "switch",
                DateTimeOffset.FromUnixTimeSeconds(
                    item.TryGetProperty("created_at", out JsonElement created) ? created.GetInt64() : 0)
                        .UtcDateTime + TimeSpan.FromHours(24))
            {
                ApplicationData = item.TryGetProperty("application_data", out JsonElement data) &&
                    data.ValueKind == JsonValueKind.String
                        ? data.GetString()
                        : null,
            };

        /// <summary>
        /// Take one off the list and tell the server it has been read, which is all the native
        /// surface has: there is no decline for an invitation, only read state and expiry.
        ///
        /// ponytail: the dismissed set is in memory, so a restart shows a dismissed invitation
        /// again until it expires. Persist it beside the device account if that ever grates.
        /// </summary>
        public async Task DismissInvitationAsync(string invitationId, CancellationToken cancellationToken)
        {
            _dismissed.Add(invitationId);
            _invitations = _invitations.Where(invitation => invitation.InvitationId != invitationId).ToList();

            if (!Enabled || _applicationToken == null)
            {
                return;
            }

            try
            {
                // The friends module's own body (0x1e24b0): a form, ids in decimal, joined by %2C.
                using HttpRequestMessage request = new(HttpMethod.Patch, $"https://{FiveHost}/v1/invitations")
                {
                    Content = new StringContent($"read=true&ids={invitationId}", Encoding.UTF8, "application/x-www-form-urlencoded"),
                };

                request.Headers.Add("Authorization", "Bearer " + _applicationToken);

                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Debug?.Print(LogClass.ServiceAcc,
                        $"[OpenPak] Marking invitation {invitationId} read returned {(int)response.StatusCode}");
                }
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not mark {invitationId} read: {exception.Message}");
            }
        }

        /// <summary>
        /// Who a BAAS id belongs to, by the lookup the friends module uses, kept once found. A
        /// name that cannot be looked up is null, and the id is shown instead of a blank.
        /// </summary>
        private async Task<string> SenderNameAsync(string senderId, CancellationToken cancellationToken)
        {
            if (senderId == null)
            {
                return null;
            }

            if (_senderNames.TryGetValue(senderId, out string cached))
            {
                return cached;
            }

            try
            {
                using JsonDocument found = await GetAsync(
                    $"https://{BaasHost}/1.0.0/users?filter.id.$in={Uri.EscapeDataString(senderId)}", cancellationToken);

                foreach (JsonElement user in found.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (user.TryGetProperty("nickname", out JsonElement nickname) &&
                        !string.IsNullOrEmpty(nickname.GetString()))
                    {
                        return _senderNames[senderId] = nickname.GetString();
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not name {senderId}: {exception.Message}");
            }

            return null;
        }

        private async Task<JsonDocument> GetAsync(string url, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);

            request.Headers.Add("Authorization", "Bearer " + _applicationToken);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{request.RequestUri.AbsolutePath} returned {(int)response.StatusCode}: {body}");
            }

            return JsonDocument.Parse(body);
        }

        private async Task<JsonDocument> PostAsync(string url, HttpContent content, string bearer, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = content };

            if (bearer != null)
            {
                request.Headers.Add("Authorization", "Bearer " + bearer);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // The body carries the reason; a bare status code has cost days on this chain before.
                throw new HttpRequestException($"{request.RequestUri.AbsolutePath} returned {(int)response.StatusCode}: {body}");
            }

            return JsonDocument.Parse(body);
        }

        private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
        {
            Dictionary<string, string> form = new();

            foreach ((string key, string value) in fields)
            {
                form[key] = value;
            }

            return new FormUrlEncodedContent(form);
        }

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

        /// <summary>
        /// The account name, or null when the device account is still anonymous. An unlinked user
        /// object carries an empty nickname, which is not the same thing as being linked to someone
        /// with no name.
        /// </summary>
        private static string NicknameOf(JsonElement login)
        {
            if (!login.TryGetProperty("user", out JsonElement user) ||
                !user.TryGetProperty("nickname", out JsonElement nickname))
            {
                return null;
            }

            string value = nickname.GetString();

            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>The friend code the account was issued, or null before it has one.</summary>
        private static string FriendCodeOf(JsonElement login)
            => login.GetProperty("user").TryGetProperty("links", out JsonElement links)
                && links.TryGetProperty("friendCode", out JsonElement code)
                && code.TryGetProperty("id", out JsonElement id)
                    ? id.GetString()
                    : null;

        /// <summary>
        /// The account's picture, fetched once and kept. It comes from the same server as
        /// everything else here, over the same pinned connection: an avatar url is still a url
        /// this emulator was told to fetch by whoever answers as OpenPak.
        /// </summary>
        public async Task<byte[]> AvatarAsync(CancellationToken cancellationToken)
        {
            if (_avatar != null || _avatarUrl == null || !Enabled)
            {
                return _avatar;
            }

            try
            {
                _http ??= Server.CreateClient();

                return _avatar = await _http.GetByteArrayAsync(_avatarUrl, cancellationToken);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not fetch the avatar: {exception.Message}");

                return null;
            }
        }

        /// <summary>
        /// A local profile was deleted: its bearer is revoked and its device account forgotten, so
        /// no sign-in outlives the profile that could show it.
        /// </summary>
        public static async Task ForgetProfileAsync(string profileId)
        {
            try
            {
                DeviceAccount.Delete(profileId);

                await OpenPakApi.Instance.ForgetProfileAsync(profileId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Could not forget profile {profileId}: {exception.Message}");
            }
        }

        /// <summary>The BAAS user id is 16 hex digits; the guest wants those 8 bytes as a u64.</summary>
        private static ulong ParseUserId(string id)
            => ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out ulong value) ? value : 0;

        private sealed class DeviceAccount
        {
            public string Id { get; init; }
            public string Password { get; init; }

            private static string PathFor(string serverKey, string profileId = null)
                => Path.Combine(AppDataManager.BaseDirPath, "openpak",
                    profileId == null ? $"device-{serverKey}.json" : $"device-{serverKey}-{profileId}.json");

            /// <summary>
            /// Kept per server: pointing the emulator at a different OpenPak means a different device
            /// account, and reusing one across servers would send the wrong password to a stranger.
            /// And per profile, as a console keeps one per user: each profile is its own BAAS user.
            /// </summary>
            public static DeviceAccount Load(string serverKey, string profileId)
            {
                string path = PathFor(serverKey, profileId);

                // From before profiles, one per server: it goes to the profile open at the first
                // launch since, which is the one whose account it was linked to.
                if (!File.Exists(path) && File.Exists(PathFor(serverKey)))
                {
                    File.Move(PathFor(serverKey), path);
                }

                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

                    return new DeviceAccount
                    {
                        Id = document.RootElement.GetProperty("id").GetString(),
                        Password = document.RootElement.GetProperty("password").GetString(),
                    };
                }
                catch (Exception exception)
                {
                    Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Ignoring unreadable {path}: {exception.Message}");

                    return null;
                }
            }

            /// <summary>Forget a deleted profile's device account on every server it had one on.</summary>
            public static void Delete(string profileId)
            {
                string directory = Path.Combine(AppDataManager.BaseDirPath, "openpak");

                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(directory, $"device-*-{profileId}.json"))
                {
                    File.Delete(file);
                }
            }

            public void Save(string serverKey, string profileId)
            {
                string path = PathFor(serverKey, profileId);

                Directory.CreateDirectory(Path.GetDirectoryName(path));

                File.WriteAllText(path, Json(writer =>
                {
                    writer.WriteString("id", Id);
                    writer.WriteString("password", Password);
                }));
            }
        }
    }
}
