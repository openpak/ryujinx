using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.OpenPak;
using System;
using System.IO;

namespace Ryujinx.Tests.HLE
{
    /// <summary>One OpenPak account, one local profile: the check the sign-in refuses on.</summary>
    public class OpenPakLinksTests
    {
        private const string Alice = "0000000000000001000000000000000a";
        private const string Bob = "0000000000000001000000000000000b";

        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ryujinx-openpak-links-" + Guid.NewGuid().ToString("n"));

            Directory.CreateDirectory(_directory);
            AppDataManager.Initialize(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            OpenPakLinks.Remove(Alice);
            OpenPakLinks.Remove(Bob);

            Directory.Delete(_directory, true);
        }

        [Test]
        public void AnAccountHeldByAnotherProfileNamesThatProfile()
        {
            OpenPakLinks.Set(Alice, "account-1", "alice", "Alice's profile");

            Assert.Multiple(() =>
            {
                Assert.That(OpenPakLinks.HolderOf("account-1", Bob), Is.EqualTo("Alice's profile"));
                Assert.That(OpenPakLinks.HolderOf("account-1", Alice), Is.Null, "a profile re-signing in to its own account is not a conflict");
                Assert.That(OpenPakLinks.HolderOf("account-2", Bob), Is.Null);
                Assert.That(OpenPakLinks.Get(Alice)?.DisplayName, Is.EqualTo("alice"));
                Assert.That(OpenPakLinks.Get(Bob), Is.Null, "a profile never signed in is offline");
            });
        }

        [Test]
        public void SigningOutFreesTheAccount()
        {
            OpenPakLinks.Set(Alice, "account-1", "alice", "Alice's profile");
            OpenPakLinks.Remove(Alice);

            Assert.That(OpenPakLinks.HolderOf("account-1", Bob), Is.Null);
        }

        [Test]
        public void TheLinkIsWrittenToDisk()
        {
            OpenPakLinks.Set(Alice, "account-1", "alice", "Alice's profile");

            string written = File.ReadAllText(Path.Combine(OpenPakConfig.DataDirectory, "profiles.json"));

            Assert.That(written, Does.Contain(Alice).And.Contain("account-1"));
        }
    }
}
