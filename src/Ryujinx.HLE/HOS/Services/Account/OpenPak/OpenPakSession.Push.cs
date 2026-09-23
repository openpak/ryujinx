using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// The push connection a console holds (Penne/NPNS, nx-baas docs/penne-protocol.md): register a
    /// Penne id, get a login ticket, and keep a POST to the frontline open, reading the
    /// length-prefixed frames the server streams down it. A friend request, an accepted one, a
    /// removal, an invitation or a friend's presence arrives as a message on it, and the cache it
    /// concerns is re-read at once instead of at the next poll.
    ///
    /// Downlink only, which is how the console's frontline POST is too: its body is empty, and what
    /// a console says upward (the record sync, DAPresence) needs the chunked uplink this does not
    /// open. Presence keeps going out on the REST route the heartbeat already uses, which the server
    /// treats the same. The poll stays as the fallback for whatever a dropped connection missed.
    /// </summary>
    public partial class OpenPakSession
    {
        private const string PenneGodHost = "god.penne.srv.nintendo.net";
        private const string PenneValHost = "val.penne.srv.nintendo.net";

        private Task _push;
        private CancellationTokenSource _pushConnection;
        private PenneRegistration _penne;
        private string _penneTicket;
        private string _penneFrontline;
        private DateTime _penneTicketExpiry;
        private readonly PushCoalescer _pushWork = new();

        /// <summary>Whether a frontline connection is open right now.</summary>
        public bool PushConnected { get; private set; }

        /// <summary>One loop for the life of the process, speaking for whoever is signed in.</summary>
        private void StartPush()
        {
            if (_push != null || Environment.GetEnvironmentVariable("OPENPAK_NO_PUSH") == "1")
            {
                return;
            }

            _push = Task.Run(async () =>
            {
                TimeSpan backoff = TimeSpan.FromSeconds(5);

                while (true)
                {
                    string userId = _userId;

                    if (!Enabled || userId == null || _applicationToken == null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));

                        continue;
                    }

                    using CancellationTokenSource connection = new();

                    _pushConnection = connection;

                    DateTime opened = DateTime.UtcNow;

                    try
                    {
                        await HoldFrontlineAsync(userId, connection.Token);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Push connection: {exception.Message}");
                    }
                    finally
                    {
                        PushConnected = false;
                        _pushConnection = null;
                    }

                    // A connection that lived is one that ended normally (a proxy's idle cut, a
                    // server restart): come back quickly. One that failed at once backs off.
                    backoff = DateTime.UtcNow - opened > TimeSpan.FromMinutes(1)
                        ? TimeSpan.FromSeconds(2)
                        : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));

                    await Task.Delay(backoff);
                }
            });
        }

        /// <summary>The account is changing: the connection speaking for the old one ends now.</summary>
        private void DropPush()
        {
            _pushConnection?.Cancel();
            _penne = null;
            _penneTicket = null;
            _penneFrontline = null;
        }

        private async Task HoldFrontlineAsync(string userId, CancellationToken cancellationToken)
        {
            _penne ??= PenneRegistration.Load(Server.Key, OpenPakConfig.ProfileId) ?? await RegisterPenneAsync(cancellationToken);

            if (_penneTicket == null || DateTime.UtcNow > _penneTicketExpiry - TimeSpan.FromHours(1))
            {
                await LoginTicketAsync(cancellationToken);
            }

            using HttpRequestMessage request = new(HttpMethod.Post, $"https://{_penneFrontline}/")
            {
                // Empty, as the console's is: a finite body is the downlink-only shape.
                Content = new ByteArrayContent([]),
            };

            request.Headers.Add("Authorization", "Bearer " + _penneTicket);
            request.Headers.Add("X-Protocol-Version", "4");
            request.Headers.Add("X-Power-State", "FullAwake");

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Expired or revoked: a fresh ticket next time, and a fresh registration if that
                // is refused too.
                _penneTicket = null;

                throw new HttpRequestException("the frontline refused the login ticket");
            }

            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested && userId == _userId)
            {
                byte[] frame = await PenneFrames.ReadFrameAsync(stream, cancellationToken);

                if (frame == null)
                {
                    return;
                }

                switch (PenneFrames.EnvelopeType(frame))
                {
                    case PenneFrames.TypeHandoverResult:
                        PushConnected = true;

                        Logger.Info?.Print(LogClass.ServiceAcc, "[OpenPak] Push connection open");

                        // Whatever happened while no connection was up is only in the lists.
                        _pushWork.Run("catch-up", CatchUpAsync);

                        break;

                    case PenneFrames.TypeReset:
                        return;

                    case PenneFrames.TypePutRecord when PenneFrames.TryReadMessage(frame, out string name, out string body):
                        OnPushMessage(name, body);

                        break;
                }
            }
        }

        /// <summary>The event kind a message names: the body's "type", else the message name.</summary>
        public static string PushKind(string name, string body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body ?? string.Empty);

                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("type", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    return type.GetString();
                }
            }
            catch (JsonException)
            {
                // The name still says what it is.
            }

            return name;
        }

        /// <summary>
        /// A delivery: re-read what it concerns, which is what the console's friends module does
        /// on each of these (the list sync, the inbox, the invitation box). Bursts coalesce.
        /// </summary>
        private void OnPushMessage(string name, string body)
        {
            string kind = PushKind(name, body);

            Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Pushed: {kind}");

            switch (kind)
            {
                case "presence_updated":
                    _pushWork.Run("friends", () => OpenPakBaas.SyncFriendListAsync(true, CancellationToken.None));

                    break;

                case "friend_request_received":
                    _pushWork.Run("requests", () => RefreshFriendRequestsAsync(CancellationToken.None));

                    break;

                case "friend_request_authorized":
                case "friend_deleted":
                    _pushWork.Run("friends", () => OpenPakBaas.SyncFriendListAsync(true, CancellationToken.None));
                    _pushWork.Run("requests", () => RefreshFriendRequestsAsync(CancellationToken.None));

                    break;

                case "friend_invitation_received":
                    _pushWork.Run("invitations", async () =>
                    {
                        await RefreshInvitationsAsync(CancellationToken.None);
                        await OpenPakBaas.SyncInvitationsAsync(CancellationToken.None);
                    });

                    break;
            }
        }

        private async Task CatchUpAsync()
        {
            await OpenPakBaas.SyncFriendListAsync(true, CancellationToken.None);
            await RefreshFriendRequestsAsync(CancellationToken.None);
            await RefreshInvitationsAsync(CancellationToken.None);
        }

        /// <summary>POST god /v1/penne_ids: the registration this profile keeps, as a console keeps its own.</summary>
        private async Task<PenneRegistration> RegisterPenneAsync(CancellationToken cancellationToken)
        {
            using JsonDocument reply = await PostAsync($"https://{PenneGodHost}/v1/penne_ids",
                new StringContent("{}", Encoding.UTF8, "application/json"), _applicationToken, cancellationToken);

            PenneRegistration registration = new()
            {
                Id = reply.RootElement.GetProperty("id").GetString(),
                Password = reply.RootElement.GetProperty("password").GetString(),
            };

            registration.Save(Server.Key, OpenPakConfig.ProfileId);

            return registration;
        }

        /// <summary>POST val /v1/login_tickets with the registration: the ticket and the frontline to use it on.</summary>
        private async Task LoginTicketAsync(CancellationToken cancellationToken)
        {
            string body = Json(writer =>
            {
                writer.WriteString("id", _penne.Id);
                writer.WriteString("password", _penne.Password);
            });

            JsonDocument reply;

            try
            {
                reply = await PostAsync($"https://{PenneValHost}/v1/login_tickets",
                    new StringContent(body, Encoding.UTF8, "application/json"), _applicationToken, cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.Message.Contains(" returned 401") || exception.Message.Contains(" returned 403"))
            {
                // A registration the server no longer honours is one to replace, not to retry.
                PenneRegistration.Delete(Server.Key, OpenPakConfig.ProfileId);
                _penne = null;

                throw;
            }

            using (reply)
            {
                JsonElement root = reply.RootElement;

                _penneTicket = root.GetProperty("ticket").GetString();
                _penneFrontline = root.GetProperty("frontline_fqdn").GetString();
                _penneTicketExpiry = root.TryGetProperty("expires_at", out JsonElement expires) && expires.TryGetInt64(out long at)
                    ? DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime
                    : DateTime.UtcNow + TimeSpan.FromDays(1);
            }
        }

        /// <summary>
        /// Runs background refreshes one per key: a burst of deliveries for the same list costs
        /// one fetch in flight and one after it, never one each.
        /// </summary>
        private sealed class PushCoalescer
        {
            private readonly Lock _lock = new();
            private readonly System.Collections.Generic.Dictionary<string, bool> _pending = new();

            public void Run(string key, Func<Task> work)
            {
                lock (_lock)
                {
                    if (_pending.TryGetValue(key, out bool _))
                    {
                        _pending[key] = true;

                        return;
                    }

                    _pending[key] = false;
                }

                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            await work();
                        }
                        catch (Exception exception)
                        {
                            Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Push refresh {key}: {exception.Message}");
                        }

                        lock (_lock)
                        {
                            if (!_pending[key])
                            {
                                _pending.Remove(key);

                                return;
                            }

                            _pending[key] = false;
                        }
                    }
                });
            }
        }

        private sealed class PenneRegistration
        {
            public string Id { get; init; }
            public string Password { get; init; }

            private static string PathFor(string serverKey, string profileId)
                => Path.Combine(AppDataManager.BaseDirPath, "openpak", $"penne-{serverKey}-{profileId}.json");

            public static PenneRegistration Load(string serverKey, string profileId)
            {
                string path = PathFor(serverKey, profileId);

                try
                {
                    if (!File.Exists(path))
                    {
                        return null;
                    }

                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

                    return new PenneRegistration
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

            public static void Delete(string serverKey, string profileId)
            {
                try
                {
                    if (serverKey != null)
                    {
                        File.Delete(PathFor(serverKey, profileId));

                        return;
                    }

                    string directory = Path.Combine(AppDataManager.BaseDirPath, "openpak");

                    if (Directory.Exists(directory))
                    {
                        foreach (string file in Directory.EnumerateFiles(directory, $"penne-*-{profileId}.json"))
                        {
                            File.Delete(file);
                        }
                    }
                }
                catch (IOException)
                {
                    // Replaced on the next registration either way.
                }
            }

            /// <summary>A deleted profile's registration, on every server it had one on.</summary>
            public static void DeleteEverywhere(string profileId) => Delete(null, profileId);

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
