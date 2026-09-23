using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OpenPakConfig = Ryujinx.OpenPak.OpenPakConfig;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// Where the emulator's OpenPak requests go, and what it agrees to trust at the other end.
    ///
    /// Every name in this chain belongs to Nintendo — dauth, BAAS, later NPLN — so no public CA
    /// will ever issue for them and no OpenPak server can present a certificate the host machine
    /// already trusts. The answer is a CA the user installs deliberately, pinned here and used for
    /// these connections only: not a blanket "accept anything", which would hand the session to
    /// whoever holds the redirected name.
    ///
    /// There is no restriction on which server may be configured. OpenPak is meant to be
    /// self-hosted, and the emulator never sends an OpenPak password anywhere: it holds only
    /// tokens that the configured server itself issued, so pointing it at another server risks
    /// nothing that server did not already give you.
    /// </summary>
    sealed class OpenPakServer
    {
        private static OpenPakServer _current;
        private static bool _resolved;

        static OpenPakServer()
        {
            // The settings window can point this somewhere else while the emulator is running, so
            // the answer below is cached until something actually changes rather than forever.
            OpenPakConfig.Changed += Forget;
        }

        /// <summary>Drop the cached answer, so the next ask re-reads the configuration.</summary>
        public static void Forget()
        {
            _current = null;
            _resolved = false;
        }

        private readonly X509Certificate2 _ca;
        private X509Certificate2 _clientCertificate;

        /// <summary>host:port as configured, for logs.</summary>
        public string Address { get; }

        /// <summary>The configured server, or null when OpenPak is not set up.</summary>
        public static OpenPakServer Current
        {
            get
            {
                if (!_resolved)
                {
                    _current = Resolve();
                    _resolved = true;
                }

                return _current;
            }
        }

        private readonly string _host;
        private readonly int _port;

        internal OpenPakServer(string host, int port, X509Certificate2 ca)
        {
            _host = host;
            _port = port;
            _ca = ca;

            Address = $"{host}:{port}";
        }

        /// <summary>Filename-safe form of the address; keys the per-server device account.</summary>
        public string Key => Address.Replace(':', '-');

        private static OpenPakServer Resolve()
        {
            if (!OpenPakConfig.Enabled)
            {
                return null;
            }

            string raw = OpenPakConfig.ResolvedConsoleServer;

            if (raw.Length == 0)
            {
                return null;
            }

            string host = raw;
            int port = 443;

            int colon = raw.LastIndexOf(':');

            if (colon > 0 && int.TryParse(raw[(colon + 1)..], out int parsed))
            {
                host = raw[..colon];
                port = parsed;
            }

            string caPath = OpenPakConfig.CaPath;

            if (!File.Exists(caPath))
            {
                Logger.Warning?.Print(LogClass.ServiceAcc,
                    $"[OpenPak] {raw} is configured but no CA certificate is at {caPath}. " +
                    "Online services stay offline: nothing can verify a server serving Nintendo's names. " +
                    "Settings \u2192 OpenPak \u2192 Fetch fixes this.");

                return null;
            }

            try
            {
                OpenPakServer server = new(host, port, X509CertificateLoader.LoadCertificateFromFile(caPath));

                Logger.Info?.Print(LogClass.ServiceAcc, $"[OpenPak] Using {server.Address}, trusting {caPath}");

                return server;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Cannot read the CA at {caPath}: {exception.Message}");

                return null;
            }
        }

        /// <summary>
        /// An HTTP client that resolves every host to this server. The url keeps the Nintendo name
        /// so the Host header and SNI carry it — that name is how OpenPak decides which service
        /// answers — while the socket goes where the user pointed it.
        /// </summary>
        public HttpClient CreateClient()
        {
            SocketsHttpHandler handler = new()
            {
                // Every host on this client is pinned to the OpenPak server, so a redirect must not
                // be followed blindly — the sign-in redirect points at a loopback port on this
                // machine, which is emphatically not there. Nothing in the token chain redirects.
                AllowAutoRedirect = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                    try
                    {
                        await socket.ConnectAsync(_host, _port, cancellationToken);

                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();

                        throw;
                    }
                },
            };

            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => Validate(certificate, errors);
            handler.SslOptions.ClientCertificates = [ClientCertificate()];

            return Ryujinx.OpenPak.OpenPakClientHeader.Apply(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) });
        }

        /// <summary>
        /// Accept a certificate this server's CA issued, and nothing else it could not have.
        /// A name mismatch or a missing certificate is never excused: the redirect means the name
        /// is the only thing identifying who answered.
        /// </summary>
        public bool Validate(X509Certificate certificate, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0 || certificate == null)
            {
                return false;
            }

            using X509Chain chain = new();

            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            return chain.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
        }

        /// <summary>
        /// A self-signed certificate, kept per server, presented on the device-auth hosts. A real
        /// console proves itself with the certificate in its NAND and that is how the server tells
        /// consoles apart; an emulator has none, and without this every install would arrive as the
        /// same anonymous device. It asserts nothing about hardware — it is a stable identifier.
        /// </summary>
        private X509Certificate2 ClientCertificate()
        {
            if (_clientCertificate != null)
            {
                return _clientCertificate;
            }

            string path = Path.Combine(AppDataManager.BaseDirPath, "openpak", $"device-{Key}.pfx");

            try
            {
                if (File.Exists(path))
                {
                    return _clientCertificate = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), null,
                        X509KeyStorageFlags.Exportable);
                }
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Replacing unreadable {path}: {exception.Message}");
            }

            using RSA key = RSA.Create(2048);

            CertificateRequest request = new("CN=OpenPak Ryujinx device", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10));

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));

            return _clientCertificate = certificate;
        }
    }
}
