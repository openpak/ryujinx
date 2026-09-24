using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The verified redirect ceiling: per platform, the domain families a client may be told to
    /// redirect. See docs/signed-ceiling.md in the OpenPak workspace — this follows that contract.
    /// </summary>
    public sealed class OpenPakCeiling
    {
        public long Version { get; init; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> Platforms { get; init; } =
            new Dictionary<string, IReadOnlyList<string>>();

        /// <summary>This platform's families; none when the ceiling does not name the platform.</summary>
        public IReadOnlyList<string> Families(string platform)
            => Platforms.TryGetValue(platform, out IReadOnlyList<string> families) ? families : [];
    }

    /// <summary>
    /// Fetches, verifies and caches the signed ceiling.
    ///
    /// The box that serves the ceiling only serves it: the bytes are signed with an Ed25519 key
    /// that never touches the server, and this build pins the public half. A compromised server
    /// can therefore withhold a newer ceiling or replay an older one — the persisted highest
    /// version stops the replay — but it cannot widen what the console redirects.
    /// </summary>
    public static class OpenPakCeilingService
    {
        /// <summary>
        /// Fixed origin, ordinary public TLS. Never the configurable website address, never a URL
        /// out of the profile, never through the guest's DNS or the OpenPak CA.
        /// </summary>
        public const string Url = "https://openpak.org/api/v1/network/ceiling";

        public const string PayloadType = "openpak-ceiling";

        /// <summary>
        /// Raw Ed25519 public keys this build trusts, as a list so a successor can be added before
        /// the current key is retired. Key ids are the SHA-256 of the raw key.
        /// </summary>
        public static readonly IReadOnlyList<byte[]> PinnedKeys =
        [
            // keyid 36e8bcdd93c2c1a1d7e5d87bbba05a7a4f97882131cf6878f1c053a25a377fe4
            Convert.FromHexString("0fd2a2660868e53d20fc811e3f15710cb1ad157ba7da8019f7a59e8ad604d87c"),
        ];

        // A plain handler: system trust store, no custom certificate callback, no OpenPak CA.
        private static readonly HttpClient _httpClient = OpenPakClientHeader.Apply(new HttpClient(new SocketsHttpHandler()));

        private static readonly Lock _lock = new();

        private static OpenPakCeiling _current;
        private static string _lastLoggedFailure;
        private static string _storeDirectory;

        /// <summary>Where the envelope and the highest accepted version live. Tests point it elsewhere.</summary>
        public static string StoreDirectory
        {
            get => _storeDirectory ?? OpenPakConfig.DataDirectory;
            set => _storeDirectory = value;
        }

        private static string CachePath => Path.Combine(StoreDirectory, "network-ceiling.json");

        private static string HighestPath => Path.Combine(StoreDirectory, "network-ceiling-version");

        /// <summary>The verified ceiling in force, or null when none was ever accepted and the compiled fallback applies.</summary>
        public static OpenPakCeiling Current
        {
            get
            {
                lock (_lock)
                {
                    return _current;
                }
            }
        }

        /// <summary>
        /// Boot and re-check: the cached ceiling re-verified, then one fetch (2 s, no retries).
        /// Never throws; whatever fails keeps the cached verified ceiling or the compiled fallback.
        /// </summary>
        public static async Task<OpenPakCeiling> RefreshAsync(CancellationToken cancellationToken)
        {
            byte[] fetched = null;
            string fetchFailure = null;

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                using HttpResponseMessage response = await _httpClient.GetAsync(Url, timeout.Token);

                if (response.IsSuccessStatusCode)
                {
                    fetched = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                }
                else
                {
                    fetchFailure = $"the server answered {(int)response.StatusCode}";
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                fetchFailure = "the fetch timed out";
            }
            catch (Exception exception)
            {
                fetchFailure = exception.Message;
            }

            return Update(fetched, fetchFailure, PinnedKeys);
        }

        /// <summary>
        /// The decision without the network: load and re-verify the cache, then consider a freshly
        /// fetched envelope (null when the fetch failed for <paramref name="fetchFailure"/>).
        /// </summary>
        public static OpenPakCeiling Update(byte[] fetched, string fetchFailure, IReadOnlyList<byte[]> keys)
        {
            lock (_lock)
            {
                long highest = ReadHighest();

                OpenPakCeiling cached = LoadCached(keys, highest, out string cacheFailure);

                highest = Math.Max(highest, cached?.Version ?? 0);

                OpenPakCeiling result = cached;
                string source = "cached";
                string failure = fetchFailure;

                if (fetched != null)
                {
                    OpenPakCeiling candidate = Verify(fetched, keys, out string reason);

                    if (candidate == null)
                    {
                        failure = reason;
                    }
                    else if (candidate.Version < highest)
                    {
                        failure = $"version {candidate.Version} is older than version {highest} already accepted";
                    }
                    else
                    {
                        try
                        {
                            // Envelope first, then the version: a crash between the two leaves a
                            // cache at least as new as the number, never the other way round.
                            WriteAtomic(CachePath, fetched);
                            WriteAtomic(HighestPath, System.Text.Encoding.ASCII.GetBytes(candidate.Version.ToString()));
                        }
                        catch (Exception exception)
                        {
                            Logger.Warning?.Print(LogClass.Application,
                                $"[OpenPak] Could not store the redirect ceiling: {exception.Message}");
                        }

                        result = candidate;
                        source = "fetched";
                        failure = null;
                        cacheFailure = null;
                    }
                }

                if (failure != null || cacheFailure != null)
                {
                    string message = failure != null && cacheFailure != null
                        ? $"{failure}; the cached one was unusable too ({cacheFailure})"
                        : failure ?? $"the cached one was unusable ({cacheFailure})";

                    // Once per reason: a server that stays down is one line, not one every re-check.
                    if (message != _lastLoggedFailure)
                    {
                        _lastLoggedFailure = message;

                        Logger.Warning?.Print(LogClass.Application,
                            $"[OpenPak] Redirect ceiling not updated: {message}. Using " +
                            (result != null ? $"cached version {result.Version}" : "the compiled-in fallback"));
                    }
                }
                else
                {
                    _lastLoggedFailure = null;
                }

                if (result != null && (_current == null || _current.Version != result.Version))
                {
                    Logger.Info?.Print(LogClass.Application,
                        $"[OpenPak] Redirect ceiling: verified version {result.Version} ({source})");
                }

                _current = result;

                return result;
            }
        }

        /// <summary>
        /// Signature over the decoded payload bytes, then the payload rules. Null with a reason when
        /// anything is wrong; one malformed family rejects the whole file.
        /// </summary>
        public static OpenPakCeiling Verify(byte[] envelope, IReadOnlyList<byte[]> keys, out string reason)
        {
            byte[] payload;
            bool knownKey = false;
            bool valid = false;

            try
            {
                using JsonDocument document = JsonDocument.Parse(envelope);
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("payload", out JsonElement payloadElement) ||
                    payloadElement.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("signatures", out JsonElement signatures) ||
                    signatures.ValueKind != JsonValueKind.Array)
                {
                    reason = "the envelope is malformed";

                    return null;
                }

                payload = Convert.FromBase64String(payloadElement.GetString());

                foreach (JsonElement signature in signatures.EnumerateArray())
                {
                    if (signature.ValueKind != JsonValueKind.Object ||
                        !signature.TryGetProperty("keyid", out JsonElement keyId) || keyId.ValueKind != JsonValueKind.String ||
                        !signature.TryGetProperty("sig", out JsonElement sig) || sig.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    byte[] key = KeyFor(keyId.GetString(), keys);

                    if (key == null)
                    {
                        continue;
                    }

                    knownKey = true;

                    if (VerifySignature(key, payload, sig.GetString()))
                    {
                        valid = true;

                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                reason = "the envelope is malformed";

                return null;
            }

            if (!knownKey)
            {
                reason = "no signature is by a pinned key";

                return null;
            }

            if (!valid)
            {
                reason = "the signature does not verify";

                return null;
            }

            return ParsePayload(payload, out reason);
        }

        /// <summary>
        /// A family is a dot, then at least two lower-case DNS labels: <c>.ea.com</c> yes, <c>.com</c> no.
        /// </summary>
        public static bool ValidFamily(string family)
        {
            if (family is not { Length: > 1 } || family[0] != '.')
            {
                return false;
            }

            string[] labels = family[1..].Split('.');

            if (labels.Length < 2)
            {
                return false;
            }

            foreach (string label in labels)
            {
                if (label.Length is 0 or > 63)
                {
                    return false;
                }

                foreach (char c in label)
                {
                    if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Whether a profile name lies inside the families. A family covers its apex and everything
        /// below it; a profile suffix (<c>.x.ea.com</c>, <c>*.x.ea.com</c>) is covered the same way.
        /// </summary>
        public static bool Covers(IReadOnlyList<string> families, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string normalised = name.ToLowerInvariant().TrimStart('*');

            if (!normalised.StartsWith('.'))
            {
                normalised = "." + normalised;
            }

            foreach (string family in families)
            {
                if (normalised.EndsWith(family, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static OpenPakCeiling ParsePayload(byte[] payload, out string reason)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String ||
                    type.GetString() != PayloadType)
                {
                    reason = $"the payload is not an {PayloadType}";

                    return null;
                }

                if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                    versionElement.ValueKind != JsonValueKind.Number ||
                    !versionElement.TryGetInt64(out long version) || version <= 0)
                {
                    reason = "the payload's version is not a positive integer";

                    return null;
                }

                if (!root.TryGetProperty("platforms", out JsonElement platforms) || platforms.ValueKind != JsonValueKind.Object)
                {
                    reason = "the payload has no platforms";

                    return null;
                }

                Dictionary<string, IReadOnlyList<string>> parsed = [];

                foreach (JsonProperty platform in platforms.EnumerateObject())
                {
                    if (platform.Value.ValueKind != JsonValueKind.Array)
                    {
                        reason = $"platform '{platform.Name}' is not a list";

                        return null;
                    }

                    List<string> families = [];

                    foreach (JsonElement family in platform.Value.EnumerateArray())
                    {
                        string value = family.ValueKind == JsonValueKind.String ? family.GetString() : null;

                        if (!ValidFamily(value))
                        {
                            reason = $"platform '{platform.Name}' holds a malformed family '{value}'";

                            return null;
                        }

                        families.Add(value);
                    }

                    parsed[platform.Name] = families;
                }

                reason = null;

                return new OpenPakCeiling { Version = version, Platforms = parsed };
            }
            catch (JsonException)
            {
                reason = "the payload is not JSON";

                return null;
            }
        }

        private static byte[] KeyFor(string keyId, IReadOnlyList<byte[]> keys)
        {
            foreach (byte[] key in keys)
            {
                if (string.Equals(Convert.ToHexString(SHA256.HashData(key)), keyId, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }

            return null;
        }

        private static bool VerifySignature(byte[] key, byte[] payload, string signature)
        {
            try
            {
                byte[] sig = Convert.FromBase64String(signature);

                // Raw 32-byte keys, 64-byte signatures; anything else is not Ed25519.
                if (key.Length != 32 || sig.Length != 64)
                {
                    return false;
                }

                Ed25519Signer verifier = new();
                verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
                verifier.BlockUpdate(payload, 0, payload.Length);

                return verifier.VerifySignature(sig);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static OpenPakCeiling LoadCached(IReadOnlyList<byte[]> keys, long highest, out string failure)
        {
            failure = null;

            try
            {
                if (!File.Exists(CachePath))
                {
                    return null;
                }

                // Re-verified every load: a file on disk is only as trusted as its signature.
                OpenPakCeiling cached = Verify(File.ReadAllBytes(CachePath), keys, out string reason);

                if (cached == null)
                {
                    failure = reason;

                    return null;
                }

                if (cached.Version < highest)
                {
                    failure = $"version {cached.Version} is older than version {highest} already accepted";

                    return null;
                }

                return cached;
            }
            catch (Exception exception)
            {
                failure = exception.Message;

                return null;
            }
        }

        private static long ReadHighest()
        {
            try
            {
                return File.Exists(HighestPath) && long.TryParse(File.ReadAllText(HighestPath).Trim(), out long highest)
                    ? highest
                    : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>Temp file beside the target, owner-only where there are permissions, then a rename over it.</summary>
        private static void WriteAtomic(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");

            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                using (FileStream stream = new(temporary, options))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception)
                {
                    // Nothing more to do about a temp file that cannot be removed either.
                }

                throw;
            }
        }
    }
}
