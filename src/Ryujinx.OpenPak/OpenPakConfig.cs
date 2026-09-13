using Ryujinx.Common.Configuration;
using System;
using System.IO;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// Where OpenPak lives and whether the emulator is talking to it.
    ///
    /// Two addresses, because they are two different servers and conflating them has cost time
    /// before:
    ///
    ///   <see cref="ConsoleServer"/>  the console-facing edge. Everything a *game* asks for —
    ///                                dauth, BAAS, NPLN, NEX — arrives there under Nintendo's own
    ///                                hostnames and is routed by SNI, so it is a bare host:port
    ///                                and its certificate can only ever chain to the OpenPak CA.
    ///   <see cref="WebsiteUrl"/>     openpak.org. Ordinary public TLS, an ordinary URL, and the
    ///                                surface a *person* signs in to: /api/v1.
    ///
    /// This lives here rather than in the Avalonia configuration because the guest services in
    /// Ryujinx.HLE read it too, and they cannot see the UI project. The UI pushes its settings in
    /// whenever they change; the environment still wins, so the shared launchers keep working
    /// without a GUI in the loop.
    /// </summary>
    public static class OpenPakConfig
    {
        private const string ServerVariable = "OPENPAK_SERVER";
        private const string CaVariable = "OPENPAK_CA";
        private const string WebsiteVariable = "OPENPAK_WEBSITE";

        public const string DefaultWebsiteUrl = "https://openpak.org";

        private static string _consoleServer = string.Empty;
        private static string _websiteUrl = DefaultWebsiteUrl;
        private static string _caPath = string.Empty;
        private static bool _enabled;
        private static bool _redirectGuestDns = true;

        /// <summary>Raised whenever anything here changes, so cached clients can be dropped.</summary>
        public static event Action Changed;

        /// <summary>
        /// Whether this install talks to OpenPak at all. Off is upstream Ryujinx behaviour:
        /// offline, with the made-up id_token it has always produced.
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        /// <summary>host[:port] of the console-facing edge; empty when none is configured.</summary>
        public static string ConsoleServer
        {
            get => Environment.GetEnvironmentVariable(ServerVariable)?.Trim() is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : _consoleServer;
            set => Set(ref _consoleServer, (value ?? string.Empty).Trim());
        }

        /// <summary>The site the account lives on, without a trailing slash.</summary>
        public static string WebsiteUrl
        {
            get => Environment.GetEnvironmentVariable(WebsiteVariable)?.Trim() is { Length: > 0 } fromEnvironment
                ? fromEnvironment.TrimEnd('/')
                : _websiteUrl;
            set
            {
                string trimmed = (value ?? string.Empty).Trim().TrimEnd('/');

                Set(ref _websiteUrl, trimmed.Length == 0 ? DefaultWebsiteUrl : trimmed);
            }
        }

        /// <summary>
        /// The CA the console-facing edge chains to. Fetched from the website by the settings
        /// page; kept in the data directory so the guest's TLS can pin it.
        /// </summary>
        public static string CaPath
        {
            get
            {
                if (Environment.GetEnvironmentVariable(CaVariable) is { Length: > 0 } fromEnvironment)
                {
                    return fromEnvironment;
                }

                return _caPath.Length > 0 ? _caPath : DefaultCaPath;
            }
            set => Set(ref _caPath, (value ?? string.Empty).Trim());
        }

        /// <summary>
        /// The console-facing address actually used, which is <see cref="ConsoleServer"/> when one
        /// was given and the website's own host otherwise.
        ///
        /// A normal OpenPak deployment serves both from one machine — the console edge is Traefik
        /// on 443 at the same address — so making somebody type the host twice to get online would
        /// be asking for a value already known. Set it explicitly to split them.
        /// </summary>
        public static string ResolvedConsoleServer
        {
            get
            {
                if (ConsoleServer.Length > 0)
                {
                    return ConsoleServer;
                }

                string website = WebsiteUrl;

                int scheme = website.IndexOf("://", StringComparison.Ordinal);

                if (scheme >= 0)
                {
                    website = website[(scheme + 3)..];
                }

                int slash = website.IndexOf('/');

                return slash >= 0 ? website[..slash] : website;
            }
        }

        /// <summary>Where a fetched CA is written, and where one is looked for by default.</summary>
        public static string DefaultCaPath => Path.Combine(DataDirectory, "ca.pem");

        /// <summary>Everything OpenPak keeps on disk for this install.</summary>
        public static string DataDirectory => Path.Combine(AppDataManager.BaseDirPath, "openpak");

        /// <summary>
        /// Whether to point the emulated console's DNS at OpenPak. Upstream already resolves an
        /// Atmosphère hosts file from the virtual SD card; this writes the one entry that needs to
        /// be in it, so nobody has to hand-edit a file on a disk image to get online.
        /// </summary>
        public static bool RedirectGuestDns
        {
            get => _redirectGuestDns;
            set => Set(ref _redirectGuestDns, value);
        }

        /// <summary>True when there is enough here to try: a server, a CA on disk, and the toggle on.</summary>
        public static bool Configured => Enabled && ResolvedConsoleServer.Length > 0 && File.Exists(CaPath);

        /// <summary>
        /// Say that something outside these properties changed — the certificate file appearing,
        /// mostly. Whoever caches a decision made from this has to make it again.
        /// </summary>
        public static void NotifyChanged() => Changed?.Invoke();

        private static void Set<T>(ref T field, T value)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;

            Changed?.Invoke();
        }
    }
}
