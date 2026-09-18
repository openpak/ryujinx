using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd;
using System;
using System.Buffers.Binary;
using System.Net;

namespace Ryujinx.Tests.HLE
{
    public class InterfaceListTests
    {
        // Walks the list the way FreeBSD's getifaddrs() does: each message by its msglen, its
        // sockaddrs from its len field, in RTA_* bit order, each rounded to SA_SIZE.
        [Test]
        public void GetifaddrsFindsTheAddress()
        {
            byte[] list = InterfaceList.Build(IPAddress.Parse("10.87.0.2"), IPAddress.Parse("255.255.255.0"));

            int at = 0;
            string name = null;
            IPAddress address = null, netmask = null, broadcast = null;
            while (at < list.Length)
            {
                ReadOnlySpan<byte> m = list.AsSpan(at);
                int msglen = BinaryPrimitives.ReadUInt16LittleEndian(m);
                Assert.That(m[2], Is.EqualTo(5), "RTM_VERSION");
                int addrs = BinaryPrimitives.ReadInt32LittleEndian(m[4..]);
                int len = BinaryPrimitives.ReadUInt16LittleEndian(m[16..]);
                int dataOff = BinaryPrimitives.ReadUInt16LittleEndian(m[18..]);
                Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(m[(dataOff + 6)..]), Is.EqualTo(len - dataOff), "if_data datalen");

                int sa = len;
                for (int bit = 1; bit <= 0x80; bit <<= 1)
                {
                    if ((addrs & bit) == 0)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> s = m[sa..];
                    Assert.That(s[0] % 8, Is.EqualTo(0), "sa_len survives 4- or 8-byte rounding");
                    if (m[3] == 0x0e && bit == 0x10)
                    {
                        name = System.Text.Encoding.ASCII.GetString(s.Slice(8, s[5]));
                    }
                    else if (m[3] == 0x0c)
                    {
                        IPAddress ip = new(s.Slice(4, 4).ToArray());
                        if (bit == 0x04) netmask = ip;
                        if (bit == 0x20) address = ip;
                        if (bit == 0x80) broadcast = ip;
                    }

                    sa += s[0];
                }

                Assert.That(sa, Is.EqualTo(msglen), "sockaddrs end the message");
                at += msglen;
            }

            Assert.That(at, Is.EqualTo(list.Length));
            Assert.That(name, Is.EqualTo("eth0"));
            Assert.That(address, Is.EqualTo(IPAddress.Parse("10.87.0.2")));
            Assert.That(netmask, Is.EqualTo(IPAddress.Parse("255.255.255.0")));
            Assert.That(broadcast, Is.EqualTo(IPAddress.Parse("10.87.0.255")));
            Assert.That(InterfaceList.Matches(new[] { 4, 17, 0, 0, 5, 0 }), Is.True);
            Assert.That(InterfaceList.Matches(new[] { 4, 17, 0, 0, 1, 0 }), Is.False);
        }
    }
}
