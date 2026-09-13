using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.HLE.HOS.Services.Sockets.Nsd;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Net;

namespace Ryujinx.HLE.HOS.Services.Sockets.Sfdnsres.Proxy
{
    class DnsMitmResolver
    {
        private const string HostsFilePath = "/atmosphere/hosts/default.txt";

        private static DnsMitmResolver _instance;
        public static DnsMitmResolver Instance => _instance ??= new DnsMitmResolver();

        private readonly Dictionary<string, IPAddress> _mitmHostEntries = new();

        /// <summary>Names the hosts file marks direct: they always reach the real internet.</summary>
        private readonly List<string> _mitmDirectNames = [];

        public void ReloadEntries(ServiceCtx context)
        {
            string sdPath = FileSystem.VirtualFileSystem.GetSdCardPath();

            // Before the file is read, not after: the OpenPak redirect is expressed as entries in
            // this very file, so it has to be in there by the time this parses it.
            OpenPakHosts.Apply(sdPath);

            string filePath = FileSystem.VirtualFileSystem.GetFullPath(sdPath, HostsFilePath);

            LoadEntriesFromFile(filePath);
        }

        internal void LoadEntriesFromFile(string filePath)
        {
            _mitmHostEntries.Clear();
            _mitmDirectNames.Clear();

            if (File.Exists(filePath))
            {
                using FileStream fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(fileStream);

                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();

                    if (line == null)
                    {
                        break;
                    }

                    // Ignore comments and empty lines
                    if (line.StartsWith('#') || line.Trim().Length == 0)
                    {
                        continue;
                    }

                    string[] entry = line.Split([' ', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                    // Hosts file example entry:
                    // 127.0.0.1  localhost loopback

                    // OpenPak extension: a line beginning with "direct" marks names that must
                    // never be redirected, however broadly the entries above them match. It is
                    // how the network profile honours redirect.never through a hosts file.
                    if (entry[0].Equals("direct", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 1; i < entry.Length; i++)
                        {
                            entry[i] = entry[i].Replace("%", IManager.NsdSettings.Environment);

                            _mitmDirectNames.Add(entry[i]);
                        }

                        continue;
                    }

                    // 0. Check the size of the array
                    if (entry.Length < 2)
                    {
                        Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Invalid entry in hosts file: {line}");

                        continue;
                    }

                    // 1. Parse the address
                    if (!IPAddress.TryParse(entry[0], out IPAddress address))
                    {
                        Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Failed to parse IP address in hosts file: {entry[0]}");

                        continue;
                    }

                    // 2. Check for AMS hosts file extension: "%"
                    for (int i = 1; i < entry.Length; i++)
                    {
                        entry[i] = entry[i].Replace("%", IManager.NsdSettings.Environment);
                    }

                    // 3. Add hostname to entry dictionary (updating duplicate entries)
                    foreach (string hostname in entry[1..])
                    {
                        _mitmHostEntries[hostname] = address;
                    }
                }
            }
        }

        public IPHostEntry ResolveAddress(string host)
        {
            // Numeric endpoints already identify the destination. GetHostEntry would
            // perform a reverse DNS lookup and fail when no PTR record exists.
            if (IPAddress.TryParse(host, out IPAddress address))
            {
                return new IPHostEntry
                {
                    AddressList = [address],
                    HostName = host,
                    Aliases = [],
                };
            }

            return TryResolveRedirect(host, out IPHostEntry entry) ? entry : Dns.GetHostEntry(host);
        }

        // A configured IP redirect may bypass the public Nintendo DNS block.
        // A "direct" exception is not a redirect and must retain that block.
        internal bool TryResolveRedirect(string host, out IPHostEntry entry)
        {
            entry = null;
            foreach (string direct in _mitmDirectNames)
            {
                // Check for AMS hosts file extension: "*"
                if (FileSystemName.MatchesSimpleExpression(direct, host))
                {
                    Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Not redirecting '{host}': the hosts file marks it direct");

                    return false;
                }
            }

            foreach (KeyValuePair<string, IPAddress> hostEntry in _mitmHostEntries)
            {
                // Check for AMS hosts file extension: "*"
                // NOTE: MatchesSimpleExpression also allows "?" as a wildcard
                if (FileSystemName.MatchesSimpleExpression(hostEntry.Key, host))
                {
                    Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Redirecting '{host}' to: {hostEntry.Value}");

                    entry = new IPHostEntry
                    {
                        AddressList = [hostEntry.Value],
                        HostName = hostEntry.Key,
                        Aliases = [],
                    };

                    return true;
                }
            }

            return false;
        }
    }
}
