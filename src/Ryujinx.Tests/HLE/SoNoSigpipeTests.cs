using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System;
using System.Net.Sockets;

namespace Ryujinx.Tests.HLE
{
    // SO_NOSIGPIPE has no Linux equivalent and the option table maps it to DontLinger, which .NET
    // on Linux rejects. Answering EOPNOTSUPP reads to a guest as an unusable socket: Risk of Rain
    // Returns sets it on its NPLN socket and gives up before resolving its tenant.
    public class SoNoSigpipeTests
    {
        [Test]
        public void SetSockOpt_SO_NOSIGPIPE_succeeds()
        {
            using ManagedSocket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, null);

            Span<byte> one = stackalloc byte[4];
            one[0] = 1;

            Assert.That(
                socket.SetSocketOption(BsdSocketOption.SoNoSigpipe, SocketOptionLevel.Socket, one),
                Is.EqualTo(LinuxError.SUCCESS));
        }
    }
}
