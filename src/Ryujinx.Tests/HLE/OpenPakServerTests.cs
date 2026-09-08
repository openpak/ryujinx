using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// OpenPak serves Nintendo's hostnames from its own CA, so the emulator has to accept a
    /// certificate the host machine does not trust. These tests are about what it must still
    /// refuse: a guard that accepts everything is worse than no guard, because it is believed.
    /// </summary>
    public class OpenPakServerTests
    {
        private const string Host = "dauth-lp1.ndas.srv.nintendo.net";

        private static X509Certificate2 CreateCa()
        {
            using RSA key = RSA.Create(2048);

            CertificateRequest request = new("CN=OpenPak Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            // Wider than the leaf below on purpose: a leaf whose validity leaves its issuer's
            // window is refused outright, and with both windows computed from UtcNow that happens
            // whenever the clock ticks between the two calls. This test failed exactly once that way.
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        }

        private static X509Certificate2 CreateLeaf(X509Certificate2 ca)
        {
            using RSA key = RSA.Create(2048);

            CertificateRequest request = new($"CN={Host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            return request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), new byte[] { 1, 2, 3, 4 });
        }

        [Test]
        public void AcceptsACertificateTheConfiguredCaIssued()
        {
            using X509Certificate2 ca = CreateCa();
            using X509Certificate2 leaf = CreateLeaf(ca);

            OpenPakServer server = new(Host, 443, ca);

            Assert.That(server.Validate(leaf, SslPolicyErrors.RemoteCertificateChainErrors), Is.True);
        }

        [Test]
        public void RefusesACertificateFromSomeOtherCa()
        {
            using X509Certificate2 ca = CreateCa();
            using X509Certificate2 stranger = CreateCa();
            using X509Certificate2 leaf = CreateLeaf(stranger);

            OpenPakServer server = new(Host, 443, ca);

            Assert.That(server.Validate(leaf, SslPolicyErrors.RemoteCertificateChainErrors), Is.False);
        }

        [Test]
        public void RefusesTheWrongHostnameEvenFromItsOwnCa()
        {
            // The redirect points a Nintendo name at whoever the user configured, so the name is
            // the only thing left identifying who answered. Our own CA does not excuse it.
            using X509Certificate2 ca = CreateCa();
            using X509Certificate2 leaf = CreateLeaf(ca);

            OpenPakServer server = new(Host, 443, ca);

            Assert.That(server.Validate(leaf, SslPolicyErrors.RemoteCertificateNameMismatch), Is.False);
            Assert.That(server.Validate(leaf,
                SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors), Is.False);
        }

        [Test]
        public void RefusesNoCertificateAtAll()
        {
            using X509Certificate2 ca = CreateCa();

            OpenPakServer server = new(Host, 443, ca);

            Assert.That(server.Validate(null, SslPolicyErrors.RemoteCertificateNotAvailable), Is.False);
        }
    }
}
