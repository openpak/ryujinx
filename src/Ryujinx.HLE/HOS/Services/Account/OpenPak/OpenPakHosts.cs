using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Ryujinx.OpenPak;
using OpenPakConfig = Ryujinx.OpenPak.OpenPakConfig;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// Pointing the emulated console's DNS at OpenPak, by writing the file it already reads.
    ///
    /// Upstream's <c>DnsMitmResolver</c> resolves an Atmosphère hosts file from the virtual SD
    /// card at <c>/atmosphere/hosts/default.txt</c> and understands the AMS <c>*</c> wildcard, so
    /// the redirect needs no emulator code at all — only for that file to say the right thing.
    /// Writing it here is the difference between "supported" and "supported if you hand-edit a
    /// file inside a disk image".
    ///
    /// The block this owns is fenced with markers and rewritten in place. Whatever else is in
    /// that file is somebody's, and a redirect toggle has no business deleting it.
    /// </summary>
    public static class OpenPakHosts
    {
        private const string RelativePath = "atmosphere/hosts/default.txt";

        private const string Begin = "# >>> OpenPak (managed by Ryujinx) >>>";
        private const string End = "# <<< OpenPak (managed by Ryujinx) <<<";

        /// <summary>
        /// Put the OpenPak block in the console's hosts file, or take it out again.
        ///
        /// Called before a game starts, so a toggle in the settings window is in effect the next
        /// time something is launched rather than only after the SD card is edited by hand.
        /// </summary>
        public static void Apply(string sdCardPath)
        {
            try
            {
                string path = Path.Combine(sdCardPath, RelativePath);

                bool wanted = OpenPakConfig.Enabled && OpenPakConfig.RedirectGuestDns;

                // The profile decides which names and which address; the built-in list is what
                // ships when there is none. The profile's address is a literal already, so a
                // launch never waits on a DNS round trip to know where the console points.
                OpenPakNetworkProfile profile = OpenPakNetworkProfileService.Applied;

                string address = null;
                List<string> names = [];
                List<string> direct = [];

                // The first line a name matches wins in the resolver, so the block is written
                // most-specific first: per-name addresses, then exact names, then the wildcard
                // families that catch whatever is left.
                List<(string Address, string Name)> entries = [];

                if (wanted)
                {
                    if (profile != null)
                    {
                        address = profile.ServerAddress;

                        foreach ((string name, string nameAddress) in profile.Overrides)
                        {
                            entries.Add((nameAddress, name));
                        }

                        foreach (string name in profile.Exact)
                        {
                            entries.Add((address, name));
                        }

                        foreach (string suffix in profile.Suffixes)
                        {
                            entries.Add((address, suffix.StartsWith('*') ? suffix : "*" + suffix));
                        }

                        // The hosts mechanism only redirects; a name that must reach the real
                        // internet is written as a direct line the resolver honours first.
                        foreach (string never in profile.Never)
                        {
                            direct.Add(never.StartsWith('.') ? "*" + never : never);
                        }
                    }
                    else
                    {
                        address = Address();

                        // The built-in names live beside the profile they stand in for, so the
                        // change notice can digest them the same way.
                        foreach ((string name, string nameAddress) in OpenPakNetworkProfileService.BuiltInOverrides)
                        {
                            entries.Add((nameAddress, name));
                        }

                        foreach (string family in OpenPakNetworkProfileService.BuiltInFamilies)
                        {
                            entries.Add((address, "*" + family));
                        }
                    }
                }

                if (wanted && address == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceBsd,
                        $"[OpenPak] Cannot redirect the console's DNS: {OpenPakConfig.ResolvedConsoleServer} does not resolve. " +
                        "Leaving the hosts file alone.");

                    return;
                }

                List<string> lines = File.Exists(path)
                    ? [.. File.ReadAllLines(path)]
                    : [];

                lines = Without(lines);

                if (wanted)
                {
                    lines.Add(Begin);
                    lines.Add("# Written from Settings -> OpenPak. Edits inside this block are lost.");

                    foreach ((string entryAddress, string entryName) in entries)
                    {
                        lines.Add($"{entryAddress} {entryName}");
                    }

                    foreach (string name in direct)
                    {
                        lines.Add($"direct {name}");
                    }

                    lines.Add(End);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));

                File.WriteAllLines(path, lines);

                // What the console now resolves is the set in use: a later change to it is what
                // the "restart the game" notice is about.
                OpenPakNetworkProfileService.MarkInUse();

                Logger.Info?.Print(LogClass.ServiceBsd, wanted
                    ? $"[OpenPak] The emulated console resolves {entries.Count} name(s) to {address} " +
                        (profile != null ? $"(network profile version {profile.Version})" : "(built-in list)")
                    : "[OpenPak] The emulated console's DNS redirect is off");
            }
            catch (Exception exception)
            {
                // A hosts file that could not be written is a title that stays offline, which is
                // worth a line in the log and nothing more dramatic than that.
                Logger.Warning?.Print(LogClass.ServiceBsd, $"[OpenPak] Could not write the hosts file: {exception.Message}");
            }
        }

        /// <summary>Everything except a block this wrote, including a half-written one.</summary>
        private static List<string> Without(List<string> lines)
        {
            List<string> kept = [];
            bool inside = false;

            foreach (string line in lines)
            {
                if (line.StartsWith(Begin, StringComparison.Ordinal))
                {
                    inside = true;

                    continue;
                }

                if (inside)
                {
                    inside = !line.StartsWith(End, StringComparison.Ordinal);

                    continue;
                }

                kept.Add(line);
            }

            // Trailing blank lines accumulate otherwise, one per toggle.
            while (kept.Count > 0 && kept[^1].Trim().Length == 0)
            {
                kept.RemoveAt(kept.Count - 1);
            }

            return kept;
        }

        /// <summary>
        /// The console server as an IPv4 literal, because the hosts file takes an address and not
        /// a name. The port is dropped: a hosts file cannot express one, and OpenPak's edge is on
        /// 443 for everything a console asks for.
        /// </summary>
        private static string Address()
        {
            string host = OpenPakConfig.ResolvedConsoleServer;

            if (host.Length == 0)
            {
                return null;
            }

            int colon = host.LastIndexOf(':');

            if (colon > 0 && int.TryParse(host[(colon + 1)..], out _))
            {
                host = host[..colon];
            }

            if (IPAddress.TryParse(host, out IPAddress parsed))
            {
                return parsed.AddressFamily == AddressFamily.InterNetwork ? parsed.ToString() : null;
            }

            try
            {
                return Dns.GetHostAddresses(host)
                    .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)?
                    .ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
