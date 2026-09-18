using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The whole sign-in chain against a real OpenPak server. Explicit: it needs one running, so it
    /// is run by hand rather than in CI —
    ///
    ///   OPENPAK_SERVER=127.0.0.1:21000 OPENPAK_CA=/path/to/ca.pem \
    ///     dotnet test src/Ryujinx.Tests/Ryujinx.Tests.csproj --filter FullyQualifiedName~OpenPakSessionTests
    ///
    /// — and it is the only thing that tells you the emulator, not curl, can complete it.
    /// </summary>
    [Explicit("Needs a running OpenPak server; see the class comment.")]
    public class OpenPakSessionTests
    {
        private static string DataDirectory()
        {
            // A throwaway data directory: these write a device account and a client certificate,
            // and reusing the real one would sign the tester's install in to the test server.
            string directory = Path.Combine(Path.GetTempPath(), "ryujinx-openpak-test-" + Guid.NewGuid().ToString("n"));

            // Initialize falls back to the user profile if the directory is not already there.
            Directory.CreateDirectory(directory);
            AppDataManager.Initialize(directory);

            // The session signs in as the open profile, and does nothing before one is open.
            OpenPakConfig.SetProfile("00000000000000010000000000000000", "Test");

            return directory;
        }

        [Test]
        public async Task SignsInAndGetsAnIdToken()
        {
            Assert.That(Environment.GetEnvironmentVariable("OPENPAK_SERVER"), Is.Not.Null.And.Not.Empty,
                "OPENPAK_SERVER is not set, so there is nothing to sign in to.");

            string dataDirectory = DataDirectory();

            try
            {
                await OpenPakSession.Instance.EnsureAsync(CancellationToken.None);

                Assert.Multiple(() =>
                {
                    Assert.That(OpenPakSession.Instance.IdToken, Is.Not.Null, "no id_token came back");
                    Assert.That(OpenPakSession.Instance.IdToken.Split('.'), Has.Length.EqualTo(3), "id_token is not a JWT");
                    Assert.That(OpenPakSession.Instance.NetworkServiceAccountId, Is.Not.Zero, "no BAAS user id came back");
                });

                Console.WriteLine($"NetworkServiceAccountId: {OpenPakSession.Instance.NetworkServiceAccountId:x16}");
            }
            finally
            {
                Directory.Delete(dataDirectory, true);
            }
        }
        /// <summary>
        /// The whole link, by the path the emulator's own form takes: credentials in, an account on
        /// the id_token that comes back out. Needs an account on the server —
        ///
        ///   OPENPAK_SERVER=… OPENPAK_CA=… OPENPAK_TEST_EMAIL=… OPENPAK_TEST_PASSWORD=… dotnet test …
        /// </summary>
        [Test]
        public async Task LinksToAnAccount()
        {
            string email = Environment.GetEnvironmentVariable("OPENPAK_TEST_EMAIL");
            string password = Environment.GetEnvironmentVariable("OPENPAK_TEST_PASSWORD");

            Assert.That(email, Is.Not.Null.And.Not.Empty, "OPENPAK_TEST_EMAIL is not set.");
            Assert.That(password, Is.Not.Null.And.Not.Empty, "OPENPAK_TEST_PASSWORD is not set.");

            string dataDirectory = DataDirectory();

            try
            {
                await OpenPakSession.Instance.EnsureAsync(CancellationToken.None);

                Assert.That(OpenPakSession.Instance.IdToken, Is.Not.Null, "could not even sign in");
                Assert.That(OpenPakSession.Instance.IsLinked, Is.False, "a fresh device account is not linked to anyone");

                bool linked = await OpenPakSession.Instance.LinkAsync(email, password, CancellationToken.None);

                Assert.Multiple(() =>
                {
                    Assert.That(linked, Is.True, "the link did not complete");
                    Assert.That(OpenPakSession.Instance.IsLinked, Is.True, "linked, but with no account name");
                });

                Console.WriteLine($"linked as {OpenPakSession.Instance.Nickname}");
            }
            finally
            {
                Directory.Delete(dataDirectory, true);
            }
        }

        /// <summary>Wrong credentials must not link anything, and must not throw at the caller.</summary>
        [Test]
        public async Task RefusesTheWrongPassword()
        {
            string dataDirectory = DataDirectory();

            try
            {
                await OpenPakSession.Instance.EnsureAsync(CancellationToken.None);

                // The session is one object for the whole process, as it is in the emulator, so a
                // test that ran before this one may already have linked it. What must hold either
                // way is that a refused sign-in changes nothing.
                string before = OpenPakSession.Instance.Nickname;

                bool linked = await OpenPakSession.Instance.LinkAsync(
                    "nobody@example.invalid", "not-the-password", CancellationToken.None);

                Assert.That(linked, Is.False, "a wrong password linked something");
                Assert.That(OpenPakSession.Instance.Nickname, Is.EqualTo(before), "a refused sign-in changed the account");
            }
            finally
            {
                Directory.Delete(dataDirectory, true);
            }
        }

    }
}
