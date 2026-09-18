using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// Which local profile is linked to which OpenPak account, per site.
    ///
    /// The bearers themselves are in the OS password store, one per profile, and reading one is a
    /// process spawn on Linux and macOS — too slow to paint a row of profile tiles with, and a
    /// store cannot be asked "who else holds this account". This file answers both, and holds
    /// nothing that signs anybody in: an account id and two names.
    /// </summary>
    public static class OpenPakLinks
    {
        public sealed record Link(string AccountId, string DisplayName, string ProfileName);

        private static readonly Lock _lock = new();

        private static Dictionary<string, Link> _links;

        /// <summary>Raised after any link was added, changed or removed.</summary>
        public static event Action Changed;

        private static string PathOf => Path.Combine(OpenPakConfig.DataDirectory, "profiles.json");

        private static string KeyOf(string profileId) => $"{OpenPakApi.SiteKey}/{profileId}";

        /// <summary>The account this profile is linked to on the current site, or null when it is offline.</summary>
        public static Link Get(string profileId)
        {
            lock (_lock)
            {
                return Links.GetValueOrDefault(KeyOf(profileId));
            }
        }

        /// <summary>The local name of the other profile already linked to this account, or null.</summary>
        public static string HolderOf(string accountId, string exceptProfileId)
        {
            string prefix = OpenPakApi.SiteKey + "/";
            string except = KeyOf(exceptProfileId);

            lock (_lock)
            {
                return Links.FirstOrDefault(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                    pair.Key != except && pair.Value.AccountId == accountId).Value?.ProfileName;
            }
        }

        public static void Set(string profileId, string accountId, string displayName, string profileName)
        {
            Link link = new(accountId, displayName, profileName);

            lock (_lock)
            {
                if (Links.GetValueOrDefault(KeyOf(profileId)) == link)
                {
                    return;
                }

                Links[KeyOf(profileId)] = link;

                Save();
            }

            Changed?.Invoke();
        }

        public static void Remove(string profileId)
        {
            lock (_lock)
            {
                if (!Links.Remove(KeyOf(profileId)))
                {
                    return;
                }

                Save();
            }

            Changed?.Invoke();
        }

        private static Dictionary<string, Link> Links => _links ??= Load();

        private static Dictionary<string, Link> Load()
        {
            Dictionary<string, Link> links = [];

            if (!File.Exists(PathOf))
            {
                return links;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PathOf));

                foreach (JsonProperty entry in document.RootElement.EnumerateObject())
                {
                    links[entry.Name] = new Link(
                        entry.Value.GetProperty("account").GetString(),
                        entry.Value.GetProperty("name").GetString(),
                        entry.Value.GetProperty("profile").GetString());
                }
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Ignoring unreadable {PathOf}: {exception.Message}");
            }

            return links;
        }

        private static void Save()
        {
            using MemoryStream stream = new();

            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                foreach ((string key, Link link) in _links)
                {
                    writer.WriteStartObject(key);
                    writer.WriteString("account", link.AccountId);
                    writer.WriteString("name", link.DisplayName);
                    writer.WriteString("profile", link.ProfileName);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            Directory.CreateDirectory(OpenPakConfig.DataDirectory);

            File.WriteAllText(PathOf, Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}
