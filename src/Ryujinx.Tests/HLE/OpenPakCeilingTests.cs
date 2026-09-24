using NUnit.Framework;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The signed redirect ceiling (docs/signed-ceiling.md): what verifies, what does not, that a
    /// replayed older ceiling is refused, and that the profile is filtered name by name rather
    /// than thrown away. Every key here is generated for the test; the pinned production key never
    /// signs anything in a test.
    /// </summary>
    public class OpenPakCeilingTests
    {
        private Ed25519PrivateKeyParameters _signer;
        private byte[] _publicKey;
        private string _store;

        [SetUp]
        public void SetUp()
        {
            _signer = new Ed25519PrivateKeyParameters(new SecureRandom());
            _publicKey = _signer.GeneratePublicKey().GetEncoded();

            _store = Path.Combine(Path.GetTempPath(), "openpak-ceiling-tests-" + Guid.NewGuid().ToString("N"));
            OpenPakCeilingService.StoreDirectory = _store;
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_store))
            {
                Directory.Delete(_store, recursive: true);
            }

            // Nothing cached any more: this drops the in-memory ceiling back to the fallback.
            OpenPakCeilingService.Update(null, "test teardown", []);
            OpenPakCeilingService.StoreDirectory = null;
        }

        private static byte[] Payload(long version, string[] switchFamilies, string[] wiiuFamilies = null, string type = "openpak-ceiling")
        {
            Dictionary<string, string[]> platforms = new() { ["switch"] = switchFamilies };

            if (wiiuFamilies != null)
            {
                platforms["wiiu"] = wiiuFamilies;
            }

            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["type"] = type,
                ["version"] = version,
                ["issued"] = "2026-09-24T10:30:00Z",
                ["platforms"] = platforms,
            });
        }

        private static byte[] Sign(Ed25519PrivateKeyParameters key, byte[] payload)
        {
            Ed25519Signer signer = new();
            signer.Init(true, key);
            signer.BlockUpdate(payload, 0, payload.Length);

            return signer.GenerateSignature();
        }

        private static string KeyId(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();

        private static byte[] Envelope(byte[] payload, byte[] signature, string keyId)
            => JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload = Convert.ToBase64String(payload),
                signatures = new[] { new { keyid = keyId, sig = Convert.ToBase64String(signature) } },
            });

        private byte[] Signed(byte[] payload) => Envelope(payload, Sign(_signer, payload), KeyId(_publicKey));

        private IReadOnlyList<byte[]> Pinned => [_publicKey];

        [Test]
        public void AGoodSignatureVerifies()
        {
            OpenPakCeiling ceiling = OpenPakCeilingService.Verify(
                Signed(Payload(3, [".nintendo.net", ".demonware.net"], [".nintendowifi.net"])), Pinned, out string reason);

            Assert.Multiple(() =>
            {
                Assert.That(ceiling, Is.Not.Null, reason);
                Assert.That(ceiling.Version, Is.EqualTo(3));
                Assert.That(ceiling.Families("switch"), Is.EqualTo(new[] { ".nintendo.net", ".demonware.net" }));
                Assert.That(ceiling.Families("3ds"), Is.Empty);
            });
        }

        [Test]
        public void ATamperedPayloadDoesNotVerify()
        {
            byte[] original = Payload(3, [".nintendo.net"]);
            byte[] widened = Payload(3, [".nintendo.net", ".example.com"]);

            // The signature is over the original bytes; the envelope carries different ones.
            byte[] envelope = Envelope(widened, Sign(_signer, original), KeyId(_publicKey));

            Assert.That(OpenPakCeilingService.Verify(envelope, Pinned, out _), Is.Null);
        }

        [Test]
        public void AnUnknownKeyIsRefused()
        {
            Ed25519PrivateKeyParameters stranger = new(new SecureRandom());
            byte[] strangerPublic = stranger.GeneratePublicKey().GetEncoded();
            byte[] payload = Payload(3, [".nintendo.net"]);

            Assert.Multiple(() =>
            {
                // Signed by a key nobody pinned, under its own key id.
                Assert.That(OpenPakCeilingService.Verify(
                    Envelope(payload, Sign(stranger, payload), KeyId(strangerPublic)), Pinned, out _), Is.Null);

                // Signed by that key while claiming the pinned key's id.
                Assert.That(OpenPakCeilingService.Verify(
                    Envelope(payload, Sign(stranger, payload), KeyId(_publicKey)), Pinned, out _), Is.Null);
            });
        }

        [Test]
        public void MalformedFilesAreRefused()
        {
            Assert.Multiple(() =>
            {
                Assert.That(OpenPakCeilingService.Verify(Encoding.UTF8.GetBytes("not json"), Pinned, out _), Is.Null);
                Assert.That(OpenPakCeilingService.Verify(Encoding.UTF8.GetBytes("{\"payload\":\"%%%\",\"signatures\":[]}"), Pinned, out _), Is.Null);
                Assert.That(OpenPakCeilingService.Verify(Signed(Payload(3, [".nintendo.net"], type: "something-else")), Pinned, out _), Is.Null);
                Assert.That(OpenPakCeilingService.Verify(Signed(Payload(0, [".nintendo.net"])), Pinned, out _), Is.Null);
                Assert.That(OpenPakCeilingService.Verify(Signed(Payload(3, [".Nintendo.net"])), Pinned, out _), Is.Null);
                Assert.That(OpenPakCeilingService.Verify(Signed(Payload(3, ["nintendo.net"])), Pinned, out _), Is.Null);

                byte[] notJson = Encoding.UTF8.GetBytes("signed, but not a payload");
                Assert.That(OpenPakCeilingService.Verify(Signed(notJson), Pinned, out _), Is.Null);
            });
        }

        [Test]
        public void AOneLabelFamilyRejectsTheWholeFile()
        {
            Assert.Multiple(() =>
            {
                Assert.That(OpenPakCeilingService.ValidFamily(".com"), Is.False);
                Assert.That(OpenPakCeilingService.ValidFamily(".ea.com"), Is.True);

                // One bad entry, even on another platform, and nothing in the file is used.
                Assert.That(OpenPakCeilingService.Verify(
                    Signed(Payload(3, [".nintendo.net"], [".nintendowifi.net", ".net"])), Pinned, out _), Is.Null);
            });
        }

        [Test]
        public void AnOlderVersionIsRefusedAfterANewerOne()
        {
            byte[] newer = Signed(Payload(5, [".nintendo.net", ".demonware.net"]));
            byte[] older = Signed(Payload(3, [".nintendo.net"]));

            Assert.That(OpenPakCeilingService.Update(newer, null, Pinned)?.Version, Is.EqualTo(5));

            // A replay keeps the cached newer ceiling, and the cache is the exact bytes received.
            Assert.Multiple(() =>
            {
                Assert.That(OpenPakCeilingService.Update(older, null, Pinned)?.Version, Is.EqualTo(5));
                Assert.That(OpenPakCeilingService.Current?.Version, Is.EqualTo(5));
                Assert.That(File.ReadAllBytes(Path.Combine(_store, "network-ceiling.json")), Is.EqualTo(newer));
            });

            // The highest version is remembered on its own: with the cache gone, the older file is
            // still refused and the compiled fallback applies instead.
            File.Delete(Path.Combine(_store, "network-ceiling.json"));

            Assert.That(OpenPakCeilingService.Update(older, null, Pinned), Is.Null);

            // The same version again, or a newer one, is fine.
            Assert.That(OpenPakCeilingService.Update(newer, null, Pinned)?.Version, Is.EqualTo(5));
        }

        [Test]
        public void AFailedFetchKeepsTheCachedCeiling()
        {
            OpenPakCeilingService.Update(Signed(Payload(4, [".nintendo.net"])), null, Pinned);

            Assert.That(OpenPakCeilingService.Update(null, "the fetch timed out", Pinned)?.Version, Is.EqualTo(4));
        }

        [Test]
        public void AFamilyCoversItsApexAndBelowOnly()
        {
            string[] families = [".ea.com"];

            Assert.Multiple(() =>
            {
                Assert.That(OpenPakCeilingService.Covers(families, "ea.com"), Is.True);
                Assert.That(OpenPakCeilingService.Covers(families, "x.ea.com"), Is.True);
                Assert.That(OpenPakCeilingService.Covers(families, ".x.ea.com"), Is.True);
                Assert.That(OpenPakCeilingService.Covers(families, "*.ea.com"), Is.True);
                Assert.That(OpenPakCeilingService.Covers(families, "evilea.com"), Is.False);
                Assert.That(OpenPakCeilingService.Covers(families, "ea.com.evil.net"), Is.False);
            });
        }

        private const string Profile = """
            {
              "version": 12,
              "server": { "address": "203.0.113.10" },
              "redirect": {
                "suffixes": [".nintendo.net", ".demonware.net"],
                "exact": ["x.nintendo.com", "matchmaker.among.us"],
                "never": ["ctest.cdn.nintendo.net"],
                "overrides": {
                  "nncs2-lp1.n.n.srv.nintendo.net": "203.0.113.11",
                  "stun.example.org": "203.0.113.12"
                }
              }
            }
            """;

        [Test]
        public void ANameOutsideTheCeilingIsDroppedAndTheRestKept()
        {
            OpenPakNetworkProfile profile = OpenPakNetworkProfileService.Parse(Profile, [".nintendo.net", ".nintendo.com"]);

            Assert.Multiple(() =>
            {
                Assert.That(profile, Is.Not.Null);
                Assert.That(profile.ServerAddress, Is.EqualTo("203.0.113.10"));
                Assert.That(profile.Suffixes, Is.EqualTo(new[] { ".nintendo.net" }));
                Assert.That(profile.Exact, Is.EqualTo(new[] { "x.nintendo.com" }));
                Assert.That(profile.Never, Is.EqualTo(new[] { "ctest.cdn.nintendo.net" }));
                Assert.That(profile.Overrides.Keys, Is.EqualTo(new[] { "nncs2-lp1.n.n.srv.nintendo.net" }));
            });
        }

        [Test]
        public void TheDigestIgnoresAnotherPlatformsList()
        {
            OpenPakCeiling before = OpenPakCeilingService.Verify(
                Signed(Payload(3, [".nintendo.net", ".nintendo.com"], [".nintendowifi.net"])), Pinned, out _);
            OpenPakCeiling wiiuChanged = OpenPakCeilingService.Verify(
                Signed(Payload(4, [".nintendo.net", ".nintendo.com"], [".nintendowifi.net", ".openpak.org"])), Pinned, out _);
            OpenPakCeiling switchChanged = OpenPakCeilingService.Verify(
                Signed(Payload(5, [".nintendo.net", ".nintendo.com", ".demonware.net"], [".nintendowifi.net"])), Pinned, out _);

            string Digest(OpenPakCeiling ceiling) => OpenPakNetworkProfileService.EffectiveDigest(
                OpenPakNetworkProfileService.Parse(Profile, ceiling.Families(OpenPakNetworkProfileService.Platform)));

            Assert.Multiple(() =>
            {
                Assert.That(Digest(wiiuChanged), Is.EqualTo(Digest(before)));

                // The Switch list widening lets .demonware.net through, which is a real change.
                Assert.That(Digest(switchChanged), Is.Not.EqualTo(Digest(before)));
                Assert.That(Digest(before), Has.Length.EqualTo(64));
            });
        }

        [Test]
        public void ThePinnedKeyMatchesTheContract()
        {
            Assert.That(KeyId(OpenPakCeilingService.PinnedKeys.Single()),
                Is.EqualTo("36e8bcdd93c2c1a1d7e5d87bbba05a7a4f97882131cf6878f1c053a25a377fe4"));
        }
    }
}
