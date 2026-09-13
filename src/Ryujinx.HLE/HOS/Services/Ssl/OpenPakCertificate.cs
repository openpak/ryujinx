using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ryujinx.HLE.HOS.Services.Ssl
{
    internal static class OpenPakCertificate
    {
        // Stardew requests this system root itself. Supplying the configured CA
        // preserves the game's certificate verification and avoids executable patches.
        internal static byte[] Load(bool enabled, string path)
        {
            if (!enabled)
            {
                return null;
            }

            try
            {
                using X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(path);
                X509BasicConstraintsExtension constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
                if (constraints?.CertificateAuthority != true)
                {
                    throw new CryptographicException("The configured certificate is not a CA.");
                }

                Logger.Info?.Print(LogClass.ServiceSsl, "[OpenPak] Supplying the configured CA through system certificate 1033.");
                return certificate.RawData;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                Logger.Warning?.Print(LogClass.ServiceSsl, "[OpenPak] Cannot load the configured CA; retaining the system certificate.");
                return null;
            }
        }
    }
}
