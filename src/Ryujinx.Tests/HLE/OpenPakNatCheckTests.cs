using NUnit.Framework;
using Ryujinx.OpenPak;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The NAT type test against a pair of in-process nncs responders on two loopback addresses,
    /// answering the way nn-nncs does: type 1 from where it was asked, type 2 from the other
    /// server, type 3 from another port on the same one.
    /// </summary>
    [NonParallelizable]
    public class OpenPakNatCheckTests
    {
        private sealed class Responder : IDisposable
        {
            private readonly List<Socket> _sockets = [];
            private readonly CancellationTokenSource _stop = new();

            public int PortA { get; }
            public int PortB { get; }

            public Responder(IPAddress primary, IPAddress secondary, bool answerType2, bool answerType3)
            {
                for (int tries = 0; ; tries++)
                {
                    try
                    {
                        Socket a1 = Udp(primary, 0);
                        PortA = ((IPEndPoint)a1.LocalEndPoint).Port;
                        Socket b1 = Udp(primary, 0);
                        PortB = ((IPEndPoint)b1.LocalEndPoint).Port;
                        Socket a2 = Udp(secondary, PortA);
                        Socket b2 = Udp(secondary, PortB);
                        Socket alt1 = Udp(primary, 0);
                        Socket alt2 = Udp(secondary, 0);

                        _sockets.AddRange([a1, b1, a2, b2, alt1, alt2]);

                        foreach (Socket socket in new[] { a1, b1 })
                        {
                            _ = Serve(socket, answerType2 ? alt2 : null, answerType3 ? alt1 : null);
                        }

                        foreach (Socket socket in new[] { a2, b2 })
                        {
                            _ = Serve(socket, answerType2 ? alt1 : null, answerType3 ? alt2 : null);
                        }

                        return;
                    }
                    catch (SocketException) when (tries < 20)
                    {
                        _sockets.ForEach(socket => socket.Dispose());
                        _sockets.Clear();
                    }
                }
            }

            private static Socket Udp(IPAddress address, int port)
            {
                Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                socket.Bind(new IPEndPoint(address, port));

                return socket;
            }

            private async Task Serve(Socket socket, Socket otherServer, Socket otherPort)
            {
                byte[] buffer = new byte[16];

                try
                {
                    while (true)
                    {
                        SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                            new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                        IPEndPoint sender = (IPEndPoint)received.RemoteEndPoint;
                        uint type = BinaryPrimitives.ReadUInt32BigEndian(buffer);

                        byte[] reply = new byte[16];
                        BinaryPrimitives.WriteUInt32BigEndian(reply, type);
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), (uint)sender.Port);
                        sender.Address.GetAddressBytes().CopyTo(reply, 8);

                        Socket from = type switch
                        {
                            2 => otherServer,
                            3 => otherPort,
                            _ => socket,
                        };

                        if (from != null)
                        {
                            await from.SendToAsync(reply, SocketFlags.None, sender);
                        }
                    }
                }
                catch (Exception)
                {
                    // Stopped.
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                _sockets.ForEach(socket => socket.Dispose());
            }
        }

        private static readonly IPAddress _primary = IPAddress.Parse("127.0.0.1");
        private static readonly IPAddress _secondary = IPAddress.Parse("127.0.0.2");

        [Test]
        public async Task AnOpenLoopbackIsTypeA()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("127.0.0.2 is only a loopback address out of the box on Linux");
            }

            using Responder responder = new(_primary, _secondary, true, true);

            OpenPakNatCheck.Result result = await OpenPakNatCheck.RunAsync(_primary, _secondary, CancellationToken.None,
                responder.PortA, responder.PortB);

            Assert.That(result.Mapping, Is.EqualTo(OpenPakNatCheck.Mapping.None));
            Assert.That(result.Filtering, Is.EqualTo(OpenPakNatCheck.Filtering.EndpointIndependent));
            Assert.That(result.External, Is.EqualTo(_primary));
            Assert.That(result.Type, Is.EqualTo('A'));
        }

        [Test]
        public async Task FilteringIsReadFromWhichRepliesArrive()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("127.0.0.2 is only a loopback address out of the box on Linux");
            }

            using (Responder onlyPort = new(_primary, _secondary, false, true))
            {
                Assert.That((await OpenPakNatCheck.RunAsync(_primary, _secondary, CancellationToken.None, onlyPort.PortA, onlyPort.PortB)).Filtering,
                    Is.EqualTo(OpenPakNatCheck.Filtering.AddressDependent));
            }

            using Responder neither = new(_primary, _secondary, false, false);

            Assert.That((await OpenPakNatCheck.RunAsync(_primary, _secondary, CancellationToken.None, neither.PortA, neither.PortB)).Filtering,
                Is.EqualTo(OpenPakNatCheck.Filtering.AddressAndPortDependent));
        }

        [Test]
        public async Task NoAnswerIsTypeF()
        {
            // Nothing listens on these; the test gives up after its three rounds.
            OpenPakNatCheck.Result result = await OpenPakNatCheck.RunAsync(_primary, _secondary, CancellationToken.None, 9, 9);

            Assert.That(result.Type, Is.EqualTo('F'));
        }

        [Test]
        public async Task PingTimesARoundTripToThePrimary()
        {
            if (!OperatingSystem.IsLinux())
            {
                Assert.Ignore("127.0.0.2 is only a loopback address out of the box on Linux");
            }

            using Responder responder = new(_primary, _secondary, true, true);

            long? ping = await OpenPakNatCheck.PingAsync(_primary, CancellationToken.None, responder.PortA);

            Assert.That(ping, Is.Not.Null);
            Assert.That(ping, Is.GreaterThanOrEqualTo(0).And.LessThan(1000));
        }

        [Test]
        public async Task PingWithNoAnswerIsNull()
        {
            // Nothing listens here; three one-second tries, then no figure rather than a made-up one.
            Assert.That(await OpenPakNatCheck.PingAsync(_primary, CancellationToken.None, 9), Is.Null);
        }

        [TestCase(OpenPakNatCheck.Mapping.EndpointIndependent, OpenPakNatCheck.Filtering.AddressAndPortDependent, 'B')]
        [TestCase(OpenPakNatCheck.Mapping.AddressDependent, OpenPakNatCheck.Filtering.AddressDependent, 'C')]
        [TestCase(OpenPakNatCheck.Mapping.AddressAndPortDependent, OpenPakNatCheck.Filtering.AddressAndPortDependent, 'D')]
        [TestCase(OpenPakNatCheck.Mapping.EndpointIndependent, OpenPakNatCheck.Filtering.EndpointIndependent, 'A')]
        public void TheLetter(OpenPakNatCheck.Mapping mapping, OpenPakNatCheck.Filtering filtering, char type)
        {
            Assert.That(new OpenPakNatCheck.Result(mapping, filtering, null).Type, Is.EqualTo(type));
        }

        [Test]
        public void TheTargetsFollowTheProfile()
        {
            OpenPakNetworkProfile profile = new()
            {
                ServerAddress = "145.241.199.19",
                Overrides = new Dictionary<string, string> { [OpenPakNatCheck.SecondaryHost] = "145.241.228.207" },
            };

            Assert.That(OpenPakNatCheck.Targets(profile), Is.EqualTo((IPAddress.Parse("145.241.199.19"), IPAddress.Parse("145.241.228.207"))));
            Assert.That(OpenPakNatCheck.Targets(new OpenPakNetworkProfile { ServerAddress = "145.241.199.19" }), Is.Null);
        }
    }
}
