using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
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
        [Test]
        public async Task SignsInAndGetsAnIdToken()
        {
            Assert.That(Environment.GetEnvironmentVariable("OPENPAK_SERVER"), Is.Not.Null.And.Not.Empty,
                "OPENPAK_SERVER is not set, so there is nothing to sign in to.");

            // A throwaway data directory: this writes a device account and a client certificate,
            // and reusing the real one would sign the tester's install in to the test server.
            string dataDirectory = Path.Combine(Path.GetTempPath(), "ryujinx-openpak-test-" + Guid.NewGuid().ToString("n"));

            // Initialize falls back to the user profile if the directory is not already there.
            Directory.CreateDirectory(dataDirectory);
            AppDataManager.Initialize(dataDirectory);

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
    }
}
