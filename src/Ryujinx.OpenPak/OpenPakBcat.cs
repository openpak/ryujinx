using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// A title's BCAT data, as the news service publishes it for emulators
    /// (<c>GET /api/emulator/v1/bcat/titles/&lt;tid&gt;</c>: a manifest of paths, sizes, sha256
    /// and file URLs), shaped into the delivery cache a game reads through bcat:u
    /// (IDeliveryCacheStorageService / Directory / File).
    ///
    /// The delivery cache is two levels deep — directories of files, each name at most 31
    /// characters — with a <c>directories.meta</c> at the root and a <c>files.meta</c> in each
    /// directory. The metas are a u32 1 and then fixed entries (the layout LibHac's bcat server
    /// reads): a directory is name[0x20], digest[0x10], reserved[0x10]; a file is name[0x20],
    /// id u64, size u64, digest[0x10], reserved[0x40]. The digest is the file's MD5.
    ///
    /// The last dataset fetched is kept on disk, so a title started offline still gets it while
    /// it is inside its validity window.
    /// </summary>
    public static class OpenPakBcat
    {
        public const int MaxNameLength = 31;
        public const int MaxEntries = 100;

        /// <summary>One file of the delivery cache.</summary>
        public sealed record File(string Directory, string Name, byte[] Data);

        public enum Outcome
        {
            /// <summary>The service holds a dataset for this title and it is in its window.</summary>
            Dataset,

            /// <summary>The service answered that there is nothing to deliver.</summary>
            None,

            /// <summary>The service could not be asked; the cached copy, if any, stands.</summary>
            Unreachable,
        }

        private static readonly HttpClient _http = OpenPakClientHeader.Apply(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

        private static string CacheDirectory(string titleId) => Path.Combine(OpenPakConfig.DataDirectory, "bcat", titleId);

        /// <summary>
        /// What this title should find in its delivery cache: the service's dataset, or the cached
        /// one when the service cannot be reached. A dataset is taken whole or not at all — one
        /// file failing its sha256 leaves the last good copy in place.
        /// </summary>
        public static async Task<(Outcome Outcome, IReadOnlyList<File> Files)> FetchAsync(string titleId, CancellationToken cancellationToken)
        {
            titleId = titleId.ToLowerInvariant();

            try
            {
                using HttpResponseMessage response = await _http.GetAsync(
                    $"{OpenPakConfig.WebsiteUrl}/api/emulator/v1/bcat/titles/{titleId}", cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Forget(titleId);

                    return (Outcome.None, []);
                }

                response.EnsureSuccessStatusCode();

                string manifest = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!InWindow(manifest, DateTime.UtcNow))
                {
                    Forget(titleId);

                    return (Outcome.None, []);
                }

                List<File> files = [];

                foreach ((string path, string sha256, string url) in Entries(manifest))
                {
                    if (!Split(path, out string directory, out string name))
                    {
                        Logger.Warning?.Print(LogClass.ServiceBcat,
                            $"[OpenPak] {titleId}: {path} is not <directory>/<file> within 31 characters each; not delivered");

                        continue;
                    }

                    byte[] data = await _http.GetByteArrayAsync(
                        url.StartsWith("http", StringComparison.Ordinal) ? url : OpenPakConfig.WebsiteUrl + url, cancellationToken);

                    if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"{path} does not match its sha256");
                    }

                    files.Add(new File(directory, name, data));
                }

                Store(titleId, manifest, files);

                return (Outcome.Dataset, files);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException)
            {
                Logger.Info?.Print(LogClass.ServiceBcat, $"[OpenPak] BCAT data for {titleId} not fetched: {exception.Message}");

                IReadOnlyList<File> cached = Cached(titleId);

                return cached == null ? (Outcome.Unreachable, []) : (Outcome.Dataset, cached);
            }
        }

        /// <summary>Whether a manifest's valid_from/valid_until window contains <paramref name="now"/>.</summary>
        public static bool InWindow(string manifest, DateTime now)
        {
            using JsonDocument document = JsonDocument.Parse(manifest);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("valid_from", out JsonElement from) && from.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(from.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime start) &&
                now < start)
            {
                return false;
            }

            return !(root.TryGetProperty("valid_until", out JsonElement until) && until.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(until.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime end) &&
                now >= end);
        }

        /// <summary>The manifest's files: path, sha256 and URL.</summary>
        public static List<(string Path, string Sha256, string Url)> Entries(string manifest)
        {
            using JsonDocument document = JsonDocument.Parse(manifest);
            List<(string, string, string)> entries = [];

            if (document.RootElement.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement file in files.EnumerateArray())
                {
                    entries.Add((file.GetProperty("path").GetString(), file.GetProperty("sha256").GetString(),
                        file.GetProperty("url").GetString()));
                }
            }

            return entries;
        }

        /// <summary>A manifest path as the cache's directory and file name, when it is one.</summary>
        public static bool Split(string path, out string directory, out string name)
        {
            directory = null;
            name = null;

            string[] parts = path?.Split('/') ?? [];

            if (parts.Length != 2 || !ValidName(parts[0]) || !ValidName(parts[1]))
            {
                return false;
            }

            directory = parts[0];
            name = parts[1];

            return true;
        }

        private static bool ValidName(string name)
        {
            if (name.Length is 0 or > MaxNameLength || name is "." or "..")
            {
                return false;
            }

            foreach (char character in name)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The directories the files fall into, in the order they first appear, at most 100 of each.</summary>
        public static List<(string Directory, List<File> Files)> Group(IReadOnlyList<File> files)
        {
            List<(string, List<File>)> groups = [];

            foreach (File file in files)
            {
                List<File> group = groups.Find(entry => string.Equals(entry.Item1, file.Directory, StringComparison.OrdinalIgnoreCase)).Item2;

                if (group == null)
                {
                    if (groups.Count == MaxEntries)
                    {
                        continue;
                    }

                    group = [];
                    groups.Add((file.Directory, group));
                }

                if (group.Count < MaxEntries && !group.Exists(known => string.Equals(known.Name, file.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    group.Add(file);
                }
            }

            return groups;
        }

        /// <summary>A directory's files.meta.</summary>
        public static byte[] FilesMeta(IReadOnlyList<File> files)
        {
            byte[] meta = new byte[4 + (0x80 * files.Count)];

            BinaryPrimitives.WriteInt32LittleEndian(meta, 1);

            for (int index = 0; index < files.Count; index++)
            {
                Span<byte> entry = meta.AsSpan(4 + (0x80 * index), 0x80);

                Encoding.ASCII.GetBytes(files[index].Name, entry[..0x20]);
                BinaryPrimitives.WriteInt64LittleEndian(entry[0x20..], index + 1);
                BinaryPrimitives.WriteInt64LittleEndian(entry[0x28..], files[index].Data.Length);
                MD5.HashData(files[index].Data).CopyTo(entry[0x30..]);
            }

            return meta;
        }

        /// <summary>The root directories.meta: each directory with the MD5 of its files' digests.</summary>
        public static byte[] DirectoriesMeta(IReadOnlyList<(string Directory, List<File> Files)> groups)
        {
            byte[] meta = new byte[4 + (0x40 * groups.Count)];

            BinaryPrimitives.WriteInt32LittleEndian(meta, 1);

            for (int index = 0; index < groups.Count; index++)
            {
                Span<byte> entry = meta.AsSpan(4 + (0x40 * index), 0x40);

                Encoding.ASCII.GetBytes(groups[index].Directory, entry[..0x20]);

                using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

                foreach (File file in groups[index].Files)
                {
                    digest.AppendData(MD5.HashData(file.Data));
                }

                digest.GetHashAndReset().CopyTo(entry[0x20..]);
            }

            return meta;
        }

        // ---- the local copy ----

        private static void Store(string titleId, string manifest, IReadOnlyList<File> files)
        {
            string directory = CacheDirectory(titleId);

            Forget(titleId);
            Directory.CreateDirectory(directory);

            foreach (File file in files)
            {
                Directory.CreateDirectory(Path.Combine(directory, "files", file.Directory));
                System.IO.File.WriteAllBytes(Path.Combine(directory, "files", file.Directory, file.Name), file.Data);
            }

            System.IO.File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest);
        }

        private static IReadOnlyList<File> Cached(string titleId)
        {
            string directory = CacheDirectory(titleId);
            string manifestPath = Path.Combine(directory, "manifest.json");

            try
            {
                if (!System.IO.File.Exists(manifestPath))
                {
                    return null;
                }

                string manifest = System.IO.File.ReadAllText(manifestPath);

                if (!InWindow(manifest, DateTime.UtcNow))
                {
                    return null;
                }

                List<File> files = [];

                foreach ((string path, _, _) in Entries(manifest))
                {
                    if (Split(path, out string dir, out string name))
                    {
                        files.Add(new File(dir, name, System.IO.File.ReadAllBytes(Path.Combine(directory, "files", dir, name))));
                    }
                }

                return files;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void Forget(string titleId)
        {
            string directory = CacheDirectory(titleId);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
