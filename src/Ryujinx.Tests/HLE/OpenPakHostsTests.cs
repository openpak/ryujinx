using NUnit.Framework;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.Linq;
using OpenPakHosts = Ryujinx.HLE.HOS.Services.Account.OpenPak.OpenPakHosts;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The redirect toggle writes the console's own hosts file, which is a file the person using
    /// the emulator may well have put their own entries in. These tests are about not eating
    /// them: the OpenPak block is fenced, rewritten in place, and removable without trace.
    /// </summary>
    public class OpenPakHostsTests
    {
        private string _sdCard;
        private string _hostsFile;

        [SetUp]
        public void SetUp()
        {
            _sdCard = Path.Combine(Path.GetTempPath(), "openpak-hosts-tests-" + Guid.NewGuid().ToString("N"));
            _hostsFile = Path.Combine(_sdCard, "atmosphere", "hosts", "default.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(_hostsFile));

            OpenPakConfig.Enabled = true;
            OpenPakConfig.RedirectGuestDns = true;
            OpenPakConfig.ConsoleServer = "10.20.30.40:443";
        }

        [TearDown]
        public void TearDown()
        {
            OpenPakConfig.Enabled = false;
            OpenPakConfig.ConsoleServer = string.Empty;
            OpenPakConfig.WebsiteUrl = OpenPakConfig.DefaultWebsiteUrl;

            if (Directory.Exists(_sdCard))
            {
                Directory.Delete(_sdCard, recursive: true);
            }
        }

        private string[] Lines() => File.ReadAllLines(_hostsFile);

        [Test]
        public void WritesTheRedirectWithThePortDropped()
        {
            OpenPakHosts.Apply(_sdCard);

            // A hosts file takes an address, not an address and a port; keeping the port would
            // make every line unparseable to the resolver that reads this.
            Assert.That(Lines(), Does.Contain("10.20.30.40 *.nintendo.net"));
        }

        [Test]
        public void LeavesSomebodyElsesEntriesAlone()
        {
            File.WriteAllLines(_hostsFile, ["# mine", "127.0.0.1 example.test"]);

            OpenPakHosts.Apply(_sdCard);

            Assert.Multiple(() =>
            {
                Assert.That(Lines(), Does.Contain("# mine"));
                Assert.That(Lines(), Does.Contain("127.0.0.1 example.test"));
                Assert.That(Lines(), Does.Contain("10.20.30.40 *.nintendo.net"));
            });
        }

        [Test]
        public void RewritesItsOwnBlockRatherThanAppendingAnother()
        {
            OpenPakHosts.Apply(_sdCard);

            OpenPakConfig.ConsoleServer = "10.99.99.99";

            OpenPakHosts.Apply(_sdCard);

            Assert.Multiple(() =>
            {
                Assert.That(Lines().Count(line => line.Contains("*.nintendo.net")), Is.EqualTo(1));
                Assert.That(Lines(), Does.Contain("10.99.99.99 *.nintendo.net"));
            });
        }

        [Test]
        public void TurningItOffRemovesTheBlockAndNothingElse()
        {
            File.WriteAllLines(_hostsFile, ["127.0.0.1 example.test"]);

            OpenPakHosts.Apply(_sdCard);

            OpenPakConfig.RedirectGuestDns = false;

            OpenPakHosts.Apply(_sdCard);

            Assert.That(Lines(), Is.EqualTo(new[] { "127.0.0.1 example.test" }));
        }

        [Test]
        public void AnEmptyConsoleServerFallsBackToTheWebsiteHost()
        {
            OpenPakConfig.ConsoleServer = string.Empty;
            OpenPakConfig.WebsiteUrl = "https://openpak.example/";

            // A normal deployment serves both from one machine, so an empty box means "the same
            // place", not "nowhere" — and the scheme and trailing slash are not part of a host.
            Assert.That(OpenPakConfig.ResolvedConsoleServer, Is.EqualTo("openpak.example"));
        }

        [Test]
        public void AnUnresolvableServerLeavesTheFileUntouched()
        {
            File.WriteAllLines(_hostsFile, ["127.0.0.1 example.test"]);

            OpenPakConfig.ConsoleServer = "openpak.invalid";

            OpenPakHosts.Apply(_sdCard);

            // Better a title that stays offline than a hosts file quietly emptied of the
            // redirect it had a moment ago because a name did not resolve once.
            Assert.That(Lines(), Is.EqualTo(new[] { "127.0.0.1 example.test" }));
        }
    }
}
