using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
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
    public class OpenPakSession
    {
        public static OpenPakSession Instance { get; } = new();

        private const string DauthHost = "dauth-lp1.ndas.srv.nintendo.net";
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

        private HttpClient _http;
        private DeviceAccount _device;

        private string _idToken;
        private DateTime _idTokenExpiry;
        private ulong _networkServiceAccountId;
        private string _nickname;
        private string _friendCode;
        private string _avatarUrl;
        private byte[] _avatar;

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

        private static OpenPakServer Server => OpenPakServer.Current;

        /// <summary>
        /// Make sure a usable id_token is cached, fetching one if not. Never throws into the guest:
        /// a game that cannot reach OpenPak should behave like a console that cannot reach Nintendo,
        /// not like a crash.
        /// </summary>
        public async Task EnsureAsync(CancellationToken cancellationToken)
        {
            if (!Enabled || Fresh())
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

            _device ??= DeviceAccount.Load(Server.Key) ?? await CreateDeviceAccountAsync(applicationToken, cancellationToken);

            bool linking = accountIdToken != null;

            using JsonDocument login = await PostAsync(
                $"https://{BaasHost}/1.0.0/{(linking ? "federation" : "login")}",
                linking
                    ? Form(("id", _device.Id), ("password", _device.Password), ("idToken", accountIdToken))
                    : Form(("id", _device.Id), ("password", _device.Password)),
                applicationToken, cancellationToken);

            _idToken = login.RootElement.GetProperty("idToken").GetString();
            _idTokenExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(login.RootElement.GetProperty("expiresIn").GetInt32());
            _networkServiceAccountId = ParseUserId(login.RootElement.GetProperty("user").GetProperty("id").GetString());
            _nickname = NicknameOf(login.RootElement);
            _friendCode = FriendCodeOf(login.RootElement);
            _avatarUrl = login.RootElement.GetProperty("user").TryGetProperty("thumbnailUrl", out JsonElement thumbnail)
                ? thumbnail.GetString()
                : null;
            _avatar = null;

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

            device.Save(Server.Key);

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
                using JsonDocument state = await GetAsync(
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

        private async Task<JsonDocument> GetAsync(string url, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"GET {url} returned {(int)response.StatusCode}: {body}");
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

        /// <summary>The BAAS user id is 16 hex digits; the guest wants those 8 bytes as a u64.</summary>
        private static ulong ParseUserId(string id)
            => ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out ulong value) ? value : 0;

        private sealed class DeviceAccount
        {
            public string Id { get; init; }
            public string Password { get; init; }

            private static string PathFor(string serverKey)
                => Path.Combine(AppDataManager.BaseDirPath, "openpak", $"device-{serverKey}.json");

            /// <summary>
            /// Kept per server: pointing the emulator at a different OpenPak means a different device
            /// account, and reusing one across servers would send the wrong password to a stranger.
            /// </summary>
            public static DeviceAccount Load(string serverKey)
            {
                string path = PathFor(serverKey);

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

            public void Save(string serverKey)
            {
                string path = PathFor(serverKey);

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
