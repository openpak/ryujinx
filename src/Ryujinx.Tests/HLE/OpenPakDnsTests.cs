using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Sockets.Sfdnsres.Proxy;
using System;
using System.IO;
using System.Net;

namespace Ryujinx.Tests.HLE
{
    [NonParallelizable]
    public class OpenPakDnsTests
    {
        private const string NatHost = "nncs1-lp1.n.n.srv.nintendo.net";

        [TestCase("192.0.2.123")]
        [TestCase("2001:db8::123")]
        [TestCase("145.241.199.19")]
        public void NumericEndpointsDoNotRequireReverseDns(string host)
        {
            IPHostEntry entry = new DnsMitmResolver().ResolveAddress(host);

            Assert.That(entry.AddressList, Is.EqualTo(new[] { IPAddress.Parse(host) }));
            Assert.That(entry.HostName, Is.EqualTo(host));
            Assert.That(entry.Aliases, Is.Empty);
        }

        [TestCase("145.241.199.19 *.nintendo.net", true)]
        [TestCase("145.241.199.19 example.test", false)]
        [TestCase("145.241.199.19 *.nintendo.net\ndirect nncs1-lp1.n.n.srv.nintendo.net", false)]
        public void OnlyAnActualRedirectCanBypassTheNintendoBlock(string hosts, bool expected)
        {
            string sdPath = Path.Combine(Path.GetTempPath(), "openpak-dns-" + Guid.NewGuid().ToString("N"));
            try
            {
                string directory = Path.Combine(sdPath, "atmosphere", "hosts");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "default.txt"), hosts);
                DnsMitmResolver resolver = new();
                resolver.LoadEntriesFromFile(Path.Combine(directory, "default.txt"));

                Assert.That(DnsBlacklist.IsHostBlocked(NatHost), Is.True);
                bool redirected = resolver.TryResolveRedirect(NatHost, out IPHostEntry entry);
                Assert.That(redirected, Is.EqualTo(expected));
                if (expected)
                {
                    Assert.That(entry.AddressList, Is.EqualTo(new[] { IPAddress.Parse("145.241.199.19") }));
                }
                else
                {
                    Assert.That(entry, Is.Null);
                }
            }
            finally
            {
                Directory.Delete(sdPath, recursive: true);
            }
        }
    }
}
