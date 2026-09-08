using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
        /// The whole link: the emulator starts one, something signs in and types the code back, the
        /// emulator approves, and the id_token it then holds belongs to an account. The part a
        /// person does in a browser is done here with the same requests the page makes, so this
        /// needs an account on the server —
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

                Exception browserFailure = null;

                bool linked = await OpenPakSession.Instance.LinkAsync(
                    // Off this thread, and not waited on: a real browser is a separate program, and
                    // the request it makes lands on a loopback port nothing accepts until this
                    // callback returns. Blocking here waits for a reply that cannot come yet.
                    url => Task.Run(() =>
                    {
                        try
                        {
                            SignInAsABrowserWould(url, email, password);
                        }
                        catch (Exception exception)
                        {
                            browserFailure = exception;
                        }
                    }), CancellationToken.None);

                Assert.That(browserFailure, Is.Null, $"the browser half failed: {browserFailure}");

                Assert.Multiple(() =>
                {
                    Assert.That(linked, Is.True, "the link did not complete");
                    Assert.That(OpenPakSession.Instance.IsLinked, Is.True, "linked, but with no account name");
                    Assert.That(OpenPakSession.Instance.IdToken, Does.Not.Contain(" "));
                });

                Console.WriteLine($"linked as {OpenPakSession.Instance.Nickname}");
            }
            finally
            {
                Directory.Delete(dataDirectory, true);
            }
        }

        /// <summary>
        /// What the browser does: post the credentials to OpenPak's sign-in page, then follow the
        /// redirect home. Two clients, because they end up on different machines — the sign-in page
        /// is the OpenPak server, pinned to its CA, and the redirect is a loopback port on this one.
        /// </summary>
        private static void SignInAsABrowserWould(string authorizeUrl, string email, string password)
        {
            using HttpClient toOpenPak = OpenPakServer.Current.CreateClient();

            HttpResponseMessage signIn = toOpenPak.PostAsync(authorizeUrl, new FormUrlEncodedContent(
                new Dictionary<string, string> { ["email"] = email, ["password"] = password })).Result;

            if (signIn.Headers.Location == null)
            {
                throw new HttpRequestException(
                    $"signing in returned {(int)signIn.StatusCode} and no redirect — wrong credentials?");
            }

            using HttpClient toLoopback = new();

            HttpResponseMessage back = toLoopback.GetAsync(signIn.Headers.Location).Result;

            if (!back.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"the callback answered {(int)back.StatusCode}");
            }
        }

    }
}
