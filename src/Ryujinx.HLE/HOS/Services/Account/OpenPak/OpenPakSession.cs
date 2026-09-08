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
    class OpenPakSession
    {
        public static OpenPakSession Instance { get; } = new();

        private const string DauthHost = "dauth-lp1.ndas.srv.nintendo.net";
        private const string BaasHost = "e0d67c509fb203858ebcb2fe3f88c2aa.baas.nintendo.com";

        // Echoed back by the server, which knows the console from its client certificate rather
        // than from anything in the request body.
        private const string BaasClientId = "8f849b5d34778d8e";

        private readonly SemaphoreSlim _gate = new(1, 1);

        private HttpClient _http;
        private DeviceAccount _device;

        private string _idToken;
        private DateTime _idTokenExpiry;
        private ulong _networkServiceAccountId;

        /// <summary>An OpenPak server is configured and reachable enough to have been set up.</summary>
        public bool Enabled => Server != null;

        /// <summary>The id_token OpenPak issued, or null when there is none to give.</summary>
        public string IdToken => _idToken;

        /// <summary>The BAAS user id as the u64 the guest calls a NetworkServiceAccountId, or 0.</summary>
        public ulong NetworkServiceAccountId => _networkServiceAccountId;

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

        private async Task LoginAsync(CancellationToken cancellationToken)
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

            using JsonDocument login = await PostAsync($"https://{BaasHost}/1.0.0/login",
                Form(("id", _device.Id), ("password", _device.Password)), applicationToken, cancellationToken);

            _idToken = login.RootElement.GetProperty("idToken").GetString();
            _idTokenExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(login.RootElement.GetProperty("expiresIn").GetInt32());
            _networkServiceAccountId = ParseUserId(login.RootElement.GetProperty("user").GetProperty("id").GetString());

            Logger.Info?.Print(LogClass.ServiceAcc,
                $"[OpenPak] Signed in as device account {_device.Id} on {Server.Address}");
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
