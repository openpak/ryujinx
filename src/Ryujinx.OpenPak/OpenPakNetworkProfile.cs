using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// Which names the emulated console redirects and where they go, as OpenPak publishes it.
    ///
    /// The reason this exists is drift: every client used to hardcode its own host list, the
    /// lists disagreed, and a title added on a new hostname needed a new build everywhere. The
    /// profile is fetched once per launch, best-effort — a fetch that fails or a server that is
    /// down costs one log line and last-known-good wins, because a game starts either way.
    /// </summary>
    public sealed class OpenPakNetworkProfile
    {
        public long Version { get; init; }

        /// <summary>What a redirected name resolves to. A literal IPv4; the hosts mechanism takes no other.</summary>
        public string ServerAddress { get; init; }

        /// <summary>Domain families, each starting with a dot.</summary>
        public IReadOnlyList<string> Suffixes { get; init; } = [];

        /// <summary>Whole hostnames, usually under a suffix family but narrower.</summary>
        public IReadOnlyList<string> Exact { get; init; } = [];

        /// <summary>
        /// Per-name addresses: names that must not resolve to <see cref="ServerAddress"/>. The
        /// NAT check is why this exists — it compares what two different addresses observe, so
        /// collapsing both probes onto one IP defeats the very question it asks.
        /// </summary>
        public IReadOnlyDictionary<string, string> Overrides { get; init; } =
            new Dictionary<string, string>();

        /// <summary>Names that must reach the real internet — a connection test pointed at OpenPak measures OpenPak.</summary>
        public IReadOnlyList<string> Never { get; init; } = [];

        /// <summary>Where the console-facing CA lives, when the profile carries one.</summary>
        public string CaUrl { get; init; }

        /// <summary>The digest that pins <see cref="CaUrl"/>, without which it is not trusted.</summary>
        public string CaSha256 { get; init; }
    }

    public static class OpenPakNetworkProfileService
    {
        public const string Platform = "switch";

        /// <summary>
        /// The first-boot fallback ceiling, used only while no signed ceiling has ever been
        /// verified and cached (<see cref="OpenPakCeilingService"/>). It is frozen: a new family is
        /// added to the signed ceiling on the server, never here again.
        /// </summary>
        public static readonly string[] AllowedFamilies =
        [
            ".nintendo.net",
            ".nintendo.com",
            ".nintendo.co.jp",
            ".nintendowifi.net",
            ".nintendo-europe.com",
            ".gamespy.com",
            ".openpak.org",
            // Third-party services OpenPak serves in place of their retail ones. A title that
            // matchmakes outside Nintendo's own names -- Among Us over its matchmaker, Outbound
            // over Photon -- can only be redirected if the family it lives in is one this build
            // is willing to be told about, and the server has been publishing exactly these in
            // the profile's exact list while every client binned the whole profile over them.
            ".among.us",
            ".photonengine.io",
            ".battle.net",
        ];

        /// <summary>
        /// What the console redirects when there is no profile at all: the families written as
        /// wildcards, to the console server's address.
        /// </summary>
        public static readonly string[] BuiltInFamilies =
        [
            ".nintendo.net",
            ".nintendo.com",
            ".nintendo.co.jp",
            ".nintendowifi.net",
            ".nintendo-europe.com",
        ];

        /// <summary>
        /// Names with an address of their own, compiled in as the same kind of fallback the
        /// wildcard list is: true until a network profile says otherwise. The NAT check is why
        /// this exists — it compares what two addresses observe of one console, so its second
        /// probe must not collapse onto the first address the way the wildcard would collapse it.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> BuiltInOverrides = new Dictionary<string, string>
        {
            ["nncs2-lp1.n.n.srv.nintendo.net"] = "145.241.228.207",
        };

        /// <summary>
        /// Raised when a re-check finds an effective redirect set that differs from the one the
        /// running console was started with, once per new set. Raised off the UI thread.
        /// </summary>
        public static event Action RedirectsChanged;

        private static readonly HttpClient _httpClient = OpenPakClientHeader.Apply(new HttpClient());
        private static readonly Lock _lock = new();
        private static readonly SemaphoreSlim _refreshGate = new(1, 1);

        private static OpenPakNetworkProfile _applied;

        private static string _inUseDigest;
        private static string _announcedDigest;
        private static int _watching;

        /// <summary>
        /// This platform's families: the verified signed ceiling when one has ever been accepted,
        /// the compiled-in fallback otherwise.
        /// </summary>
        public static IReadOnlyList<string> CeilingFamilies
            => OpenPakCeilingService.Current?.Families(Platform) ?? AllowedFamilies;

        private static string StorePath => Path.Combine(OpenPakConfig.DataDirectory, "network-profile.json");

        /// <summary>
        /// The profile currently in force, or null when there is none and the built-in list
        /// applies. Last-known-good: a fetch that failed never widens or empties the redirect.
        /// </summary>
        public static OpenPakNetworkProfile Applied
        {
            get
            {
                lock (_lock)
                {
                    return _applied;
                }
            }
        }

        /// <summary>
        /// One conditional request, best-effort. 200 applies a new profile, 304 keeps the stored
        /// one, and anything else — no network, a bad profile, a server that moved — keeps the
        /// stored one or falls back to the built-in list. Never blocks a launch, never retries.
        /// Returns the source the applied profile came from: fetched, cached, or built-in.
        ///
        /// The signed ceiling is refreshed first, because it decides which of the profile's names
        /// survive; then, when watching, the new effective set is compared with the one in use.
        /// </summary>
        public static async Task<string> RefreshAsync(CancellationToken cancellationToken)
        {
            await _refreshGate.WaitAsync(cancellationToken);

            try
            {
                await OpenPakCeilingService.RefreshAsync(cancellationToken);

                string source = await RefreshProfileAsync(cancellationToken);

                CheckForChange();

                return source;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        /// <summary>
        /// Start the background re-check: every 6 hours (±10 %) and after an OpenPak sign-in.
        /// Called once, after the boot refresh; the effective set at that point is the one in use
        /// unless a game already started on an older one.
        /// </summary>
        public static void StartWatching()
        {
            if (Interlocked.Exchange(ref _watching, 1) == 1)
            {
                return;
            }

            lock (_lock)
            {
                _inUseDigest ??= EffectiveDigest(_applied);
            }

            // A game started before the boot refresh landed runs on the older set.
            CheckForChange();

            OpenPakApi.Instance.SignedInChanged += () => _ = Task.Run(async () =>
            {
                if (OpenPakApi.Instance.SignedIn)
                {
                    await RecheckAsync();
                }
            });

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromHours(6 * (0.9 + 0.2 * Random.Shared.NextDouble())));

                    await RecheckAsync();
                }
            });
        }

        /// <summary>
        /// The console has just been given the current set (the hosts file was written as a game
        /// started), so that set is now the one in use.
        /// </summary>
        public static void MarkInUse()
        {
            lock (_lock)
            {
                _inUseDigest = EffectiveDigest(_applied);
            }
        }

        private static async Task RecheckAsync()
        {
            if (!OpenPakConfig.Enabled)
            {
                return;
            }

            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Network re-check failed: {exception.Message}");
            }
        }

        private static void CheckForChange()
        {
            if (Volatile.Read(ref _watching) == 0 || !OpenPakConfig.Enabled || !OpenPakConfig.RedirectGuestDns)
            {
                return;
            }

            string previous;
            string digest;

            lock (_lock)
            {
                digest = EffectiveDigest(_applied);

                if (_inUseDigest == null || digest == _inUseDigest || digest == _announcedDigest)
                {
                    return;
                }

                previous = _inUseDigest;
                _announcedDigest = digest;
            }

            Logger.Info?.Print(LogClass.Application,
                $"[OpenPak] The effective network redirects changed ({previous[..12]} -> {digest[..12]}); they apply when the game restarts");

            RedirectsChanged?.Invoke();
        }

        /// <summary>
        /// SHA-256 (lower-case hex) of this platform's effective redirect set after filtering: the
        /// sorted <c>family:</c>, <c>exact:</c>, <c>override:name=ip</c> and <c>address:ip</c>
        /// entries. Only this platform's profile goes in, so another platform's list never moves it.
        /// </summary>
        public static string EffectiveDigest(OpenPakNetworkProfile profile)
        {
            List<string> entries = [];

            if (profile != null)
            {
                entries.AddRange(profile.Suffixes.Select(suffix => "family:" + suffix));
                entries.AddRange(profile.Exact.Select(name => "exact:" + name));
                entries.AddRange(profile.Overrides.Select(entry => $"override:{entry.Key}={entry.Value}"));
                entries.Add("address:" + profile.ServerAddress);
            }
            else
            {
                // The built-in list's address is the console server, resolved when written.
                entries.AddRange(BuiltInFamilies.Select(family => "family:" + family));
                entries.AddRange(BuiltInOverrides.Select(entry => $"override:{entry.Key}={entry.Value}"));
                entries.Add("address:" + OpenPakConfig.ResolvedConsoleServer);
            }

            entries.Sort(StringComparer.Ordinal);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)))).ToLowerInvariant();
        }

        private static async Task<string> RefreshProfileAsync(CancellationToken cancellationToken)
        {
            (OpenPakNetworkProfile stored, string storedEtag) = LoadStored();

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                using HttpRequestMessage request = new(HttpMethod.Get,
                    $"{OpenPakConfig.WebsiteUrl}/api/v1/network/profile?platform={Platform}");

                if (storedEtag != null)
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", storedEtag);
                }

                using HttpResponseMessage response = await _httpClient.SendAsync(request, timeout.Token);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    Apply(stored);

                    LogSource("cached", stored);

                    return "cached";
                }

                if (!response.IsSuccessStatusCode)
                {
                    Apply(stored);

                    LogSource(stored != null ? "cached" : "built-in", stored,
                        $"the server answered {(int)response.StatusCode}");

                    return stored != null ? "cached" : "built-in";
                }

                string body = await response.Content.ReadAsStringAsync(timeout.Token);
                string etag = response.Headers.ETag?.Tag;

                OpenPakNetworkProfile profile = await ValidateAsync(body, cancellationToken);

                if (profile == null)
                {
                    // Rejected: a partial profile is worse than an old whole one.
                    Apply(stored);

                    LogSource(stored != null ? "cached" : "built-in", stored, "the new profile was rejected");

                    return stored != null ? "cached" : "built-in";
                }

                Store(body, etag);

                Apply(profile);

                LogSource("fetched", profile);

                return "fetched";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The two-second window closed: offline, or a server that stopped answering.
                Apply(stored);

                LogSource(stored != null ? "cached" : "built-in", stored, "the fetch timed out");

                return stored != null ? "cached" : "built-in";
            }
            catch (Exception exception)
            {
                Apply(stored);

                LogSource(stored != null ? "cached" : "built-in", stored, exception.Message);

                return stored != null ? "cached" : "built-in";
            }
        }

        /// <summary>Parse and check a profile against everything in the security model.</summary>
        private static async Task<OpenPakNetworkProfile> ValidateAsync(string body, CancellationToken cancellationToken)
        {
            OpenPakNetworkProfile profile = Parse(body, CeilingFamilies);

            if (profile == null || !await TrustCertificateAuthorityAsync(profile, cancellationToken))
            {
                return null;
            }

            return profile;
        }

        /// <summary>
        /// Parse a profile and filter it through <paramref name="ceiling"/>. A name outside the
        /// ceiling is dropped on its own and logged — never the whole profile, which is what once
        /// made one new family cost every redirect. A malformed profile is still rejected whole.
        /// </summary>
        public static OpenPakNetworkProfile Parse(string body, IReadOnlyList<string> ceiling)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;

                long version = Number(root, "version");

                JsonElement server = root.GetProperty("server");
                string address = String(server, "address");

                // The hosts mechanism writes IPv4 lines; and an address that would send the
                // console's traffic back into the machine or the link is not a redirect, it is
                // a trap.
                if (!IPAddress.TryParse(address, out IPAddress parsed) ||
                    parsed.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(parsed) ||
                    parsed.ToString().StartsWith("169.254."))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {version} rejected: '{address}' is not a usable IPv4 address");

                    return null;
                }

                JsonElement redirect = root.GetProperty("redirect");
                List<string> suffixes = Names(redirect, "suffixes", version, ceiling, allowBareHost: false);
                List<string> exact = Names(redirect, "exact", version, ceiling, allowBareHost: true);
                List<string> never = Names(redirect, "never", version, ceiling, allowBareHost: true);
                Dictionary<string, string> overrides = Addresses(redirect, version, ceiling);

                if (suffixes == null || exact == null || never == null || overrides == null)
                {
                    return null;
                }

                string caUrl = null;
                string caSha256 = null;

                if (root.TryGetProperty("ca", out JsonElement ca))
                {
                    caUrl = String(ca, "url");
                    caSha256 = String(ca, "sha256");

                    if (caUrl != null && (caUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || caSha256 == null))
                    {
                        Logger.Warning?.Print(LogClass.Application,
                            $"[OpenPak] Network profile {version} rejected: the CA must arrive over HTTPS and carry its digest");

                        return null;
                    }
                }

                OpenPakNetworkProfile profile = new()
                {
                    Version = version,
                    ServerAddress = address,
                    Suffixes = suffixes,
                    Exact = exact,
                    Never = never,
                    Overrides = overrides,
                    CaUrl = caUrl,
                    CaSha256 = caSha256,
                };

                return profile;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application,
                    $"[OpenPak] Network profile rejected: it did not parse as one ({exception.Message})");

                return null;
            }
        }

        /// <summary>
        /// The profile may move the console-facing CA, but only to the exact bytes it names: the
        /// digest is checked before anything is trusted or written. A mismatch rejects the whole
        /// profile — a trust anchor is the one thing that must never be updated on a maybe.
        /// </summary>
        private static async Task<bool> TrustCertificateAuthorityAsync(OpenPakNetworkProfile profile, CancellationToken cancellationToken)
        {
            if (profile.CaUrl == null)
            {
                return true;
            }

            string current = File.Exists(OpenPakConfig.CaPath)
                ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(OpenPakConfig.CaPath, cancellationToken)))
                : null;

            if (string.Equals(current, profile.CaSha256, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                byte[] downloaded = await _httpClient.GetByteArrayAsync(profile.CaUrl, timeout.Token);
                string digest = Convert.ToHexString(SHA256.HashData(downloaded));

                if (!string.Equals(digest, profile.CaSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {profile.Version} rejected: the CA it names hashes to {digest}, not {profile.CaSha256}");

                    return false;
                }

                Directory.CreateDirectory(OpenPakConfig.DataDirectory);
                await File.WriteAllBytesAsync(OpenPakConfig.CaPath, downloaded, cancellationToken);

                Logger.Info?.Print(LogClass.Application,
                    $"[OpenPak] Pinned the console-facing CA from the network profile, version {profile.Version}");

                return true;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application,
                    $"[OpenPak] Network profile {profile.Version} rejected: the CA could not be fetched and verified ({exception.Message})");

                return false;
            }
        }

        /// <summary>
        /// One list from the redirect object, filtered through the ceiling. A name outside it is
        /// dropped and logged on its own; the rest of the profile stands.
        /// </summary>
        private static List<string> Names(JsonElement redirect, string property, long version, IReadOnlyList<string> ceiling, bool allowBareHost)
        {
            List<string> names = [];

            if (!redirect.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (JsonElement name in list.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 1 } entry)
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {version} rejected: '{property}' holds a name that is not a name");

                    return null;
                }

                if (!allowBareHost && !entry.StartsWith('.'))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {version} rejected: '{entry}' in '{property}' is not a suffix");

                    return null;
                }

                if (!OpenPakCeilingService.Covers(ceiling, entry))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {version}: dropped '{entry}' from '{property}', it is outside the redirect ceiling");

                    continue;
                }

                names.Add(entry.ToLowerInvariant());
            }

            return names;
        }

        /// <summary>
        /// Names with an address of their own. The NAT check is the reason: it compares what two
        /// addresses observe of the same console, and one address cannot observe a difference
        /// against itself. A name outside the ceiling is dropped and logged on its own, the same
        /// as any other profile name; an address that fails the literal-public-IPv4 test the
        /// server's own passes still rejects the profile whole.
        /// </summary>
        private static Dictionary<string, string> Addresses(JsonElement redirect, long version, IReadOnlyList<string> ceiling)
        {
            Dictionary<string, string> overrides = [];

            if (!redirect.TryGetProperty("overrides", out JsonElement map) || map.ValueKind != JsonValueKind.Object)
            {
                return overrides;
            }

            foreach (JsonProperty entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String || entry.Name.Length < 2)
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {version} rejected: the override '{entry.Name}' is not a name with an address");

                    return null;
                }

                string address = entry.Value.GetString();

                if (!IPAddress.TryParse(address, out IPAddress parsed) ||
                    parsed.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(parsed) ||
                    parsed.ToString().StartsWith("169.254."))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {version} rejected: the override for '{entry.Name}' is not a usable IPv4 address");

                    return null;
                }

                if (!OpenPakCeilingService.Covers(ceiling, entry.Name))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[OpenPak] Network profile {version}: dropped the override for '{entry.Name}', it is outside the redirect ceiling");

                    continue;
                }

                overrides[entry.Name.ToLowerInvariant()] = parsed.ToString();
            }

            return overrides;
        }

        /// <summary>Make it live. Callers are the fetch, a stored profile re-read, and nobody else.</summary>
        private static void Apply(OpenPakNetworkProfile profile)
        {
            lock (_lock)
            {
                _applied = profile;
            }
        }

        private static void LogSource(string source, OpenPakNetworkProfile profile, string reason = null)
        {
            string description = profile != null
                ? $"network profile version {profile.Version}, {profile.Suffixes.Count + profile.Exact.Count} name(s) to {profile.ServerAddress}"
                : "no profile, the built-in list applies";

            Logger.Info?.Print(LogClass.Application,
                $"[OpenPak] Network profile: {source} — {description}" + (reason != null ? $" ({reason})" : string.Empty));
        }

        /// <summary>Last-known-good, re-validated on the way in: a store nobody can tamper with is not assumed.</summary>
        private static (OpenPakNetworkProfile Profile, string Etag) LoadStored()
        {
            try
            {
                if (!File.Exists(StorePath))
                {
                    return (null, null);
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(StorePath));
                JsonElement root = document.RootElement;

                string etag = String(root, "etag");

                if (!root.TryGetProperty("profile", out JsonElement raw))
                {
                    return (null, null);
                }

                // The stored profile goes through the same checks a fetched one would, so a
                // hand-edited or half-written store can only ever be as trusted as a fetch.
                OpenPakNetworkProfile profile = ValidateAsync(raw.GetRawText(), CancellationToken.None).GetAwaiter().GetResult();

                return (profile, etag);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application,
                    $"[OpenPak] Could not read the stored network profile: {exception.Message}");

                return (null, null);
            }
        }

        private static void Store(string rawProfile, string etag)
        {
            try
            {
                Directory.CreateDirectory(OpenPakConfig.DataDirectory);

                string body = Json(writer =>
                {
                    writer.WriteString("etag", etag);
                    writer.WritePropertyName("profile");
                    writer.WriteRawValue(rawProfile);
                });

                File.WriteAllText(StorePath, body);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application,
                    $"[OpenPak] Could not store the network profile: {exception.Message}");
            }
        }

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

        private static string String(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static long Number(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
                    ? number
                    : 0;
    }
}
