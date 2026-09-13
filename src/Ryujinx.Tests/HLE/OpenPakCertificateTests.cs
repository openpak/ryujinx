using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Ssl;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ryujinx.Tests.HLE
{
    public class OpenPakCertificateTests
    {
        [Test]
        public void DisabledDoesNotReadConfiguredCertificate()
        {
            Assert.That(OpenPakCertificate.Load(false, null), Is.Null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OnlyACaCanReplaceTheSystemRoot(bool isCa)
        {
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new("CN=Configured CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, certificate.ExportCertificatePem());
                Assert.That(OpenPakCertificate.Load(true, path), isCa ? Is.EqualTo(certificate.RawData) : Is.Null);
                File.WriteAllText(path, "invalid certificate");
                Assert.That(OpenPakCertificate.Load(true, path), Is.Null);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
