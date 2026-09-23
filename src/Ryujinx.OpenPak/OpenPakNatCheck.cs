using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The NAT type test a Switch runs from System Settings -> Internet -> Test Connection
    /// (qlaunch's nn::netdiag; no system service answers it, so a game that wants a NAT type runs
    /// the same exchange itself through its sockets). It speaks the nncs protocol to the two
    /// servers the console's DNS redirect points nncs1 and nncs2 at — OpenPak's nn-nncs, primary
    /// on the main server and secondary on its own address — from this machine's own UDP stack,
    /// which is the stack the emulated console's sockets use, so the answer is the one a game on
    /// this machine would get.
    ///
    /// A probe is 16 bytes, four big-endian u32s (type, 0, 0, 0); the reply echoes the type with
    /// the port and address the server saw. Types 1 are answered from where they were sent, type
    /// 2 from the other server's address, type 3 from the same address on another port.
    /// </summary>
    public static class OpenPakNatCheck
    {
        public const string PrimaryHost = "nncs1-lp1.n.n.srv.nintendo.net";
        public const string SecondaryHost = "nncs2-lp1.n.n.srv.nintendo.net";
        public const int PrimaryPort = 10025;
        public const int SecondaryPort = 10125;

        public enum Mapping { Unknown, None, EndpointIndependent, AddressDependent, AddressAndPortDependent }

        public enum Filtering { Unknown, EndpointIndependent, AddressDependent, AddressAndPortDependent }

        /// <summary>What the test found, with the letter System Settings would show.</summary>
        public sealed record Result(Mapping Mapping, Filtering Filtering, IPAddress External)
        {
            /// <summary>
            /// The console's letter. The exact table is Nintendo's and not published; this is the
            /// usual reading of it: A for no NAT or a cone that lets anyone the console has spoken
            /// to answer, B for a port-restricted cone, C for a mapping that changes per
            /// destination but filters loosely, D for symmetric and strict, F for no answer at all.
            /// </summary>
            public char Type => (Mapping, Filtering) switch
            {
                (Mapping.Unknown, _) => 'F',
                (Mapping.None, _) => 'A',
                (Mapping.EndpointIndependent, Filtering.EndpointIndependent or Filtering.AddressDependent) => 'A',
                (Mapping.EndpointIndependent, _) => 'B',
                (_, Filtering.EndpointIndependent or Filtering.AddressDependent) => 'C',
                _ => 'D',
            };
        }

        /// <summary>
        /// The two servers, as the redirect sends the console to them: an address of its own for
        /// each name when the network profile names one, the profile's server otherwise. Null
        /// when there is no profile, or both names land on one address (a NAT test needs two).
        /// </summary>
        public static (IPAddress Primary, IPAddress Secondary)? Targets(OpenPakNetworkProfile profile)
        {
            if (profile == null || !IPAddress.TryParse(profile.ServerAddress, out IPAddress server))
            {
                return null;
            }

            IPAddress primary = profile.Overrides.TryGetValue(PrimaryHost, out string first) && IPAddress.TryParse(first, out IPAddress one)
                ? one
                : server;

            IPAddress secondary = profile.Overrides.TryGetValue(SecondaryHost, out string second) && IPAddress.TryParse(second, out IPAddress two)
                ? two
                : server;

            return primary.Equals(secondary) ? null : (primary, secondary);
        }

        /// <summary>The probe for one message type.</summary>
        public static byte[] Probe(uint type)
        {
            byte[] probe = new byte[16];

            BinaryPrimitives.WriteUInt32BigEndian(probe, type);

            return probe;
        }

        /// <summary>
        /// Run the test. The mapping half sends type 1 to three endpoints from one socket and
        /// compares the ports they saw; the filtering half uses a fresh socket that has spoken only
        /// to the primary, and asks for replies from places it has not (type 2: the other server,
        /// type 3: another port on the same one).
        /// </summary>
        public static async Task<Result> RunAsync(IPAddress primary, IPAddress secondary, CancellationToken cancellationToken, int primaryPort = PrimaryPort, int secondaryPort = SecondaryPort)
        {
            using Socket mapping = Bind();

            IPEndPoint[] targets =
            [
                new(primary, primaryPort),
                new(primary, secondaryPort),
                new(secondary, primaryPort),
            ];

            Dictionary<IPEndPoint, (int Port, IPAddress Address)> seen = await ExchangeAsync(mapping, 1,
                targets, targets, cancellationToken);

            if (!seen.TryGetValue(targets[0], out (int Port, IPAddress Address) first))
            {
                return new Result(Mapping.Unknown, Filtering.Unknown, null);
            }

            Mapping mapped;

            if (IsLocal(first.Address))
            {
                mapped = Mapping.None;
            }
            else if (!seen.TryGetValue(targets[2], out (int Port, IPAddress Address) other))
            {
                // One server answering and the other not is a server problem, not a NAT type.
                mapped = seen.TryGetValue(targets[1], out var samePort) && samePort.Port != first.Port
                    ? Mapping.AddressAndPortDependent
                    : Mapping.EndpointIndependent;
            }
            else if (other.Port == first.Port)
            {
                mapped = Mapping.EndpointIndependent;
            }
            else
            {
                mapped = seen.TryGetValue(targets[1], out var second) && second.Port == first.Port
                    ? Mapping.AddressDependent
                    : Mapping.AddressAndPortDependent;
            }

            using Socket filtering = Bind();

            Filtering filtered;

            if ((await ExchangeAsync(filtering, 2, [targets[0]], null, cancellationToken)).Count > 0)
            {
                filtered = Filtering.EndpointIndependent;
            }
            else if ((await ExchangeAsync(filtering, 3, [targets[0]], null, cancellationToken)).Count > 0)
            {
                filtered = Filtering.AddressDependent;
            }
            else
            {
                filtered = Filtering.AddressAndPortDependent;
            }

            return new Result(mapped, filtered, first.Address);
        }

        /// <summary>
        /// The round trip to the primary NAT check server, in milliseconds: one type-1 probe and
        /// its answer, the best of three tries. Null when it never answered. This is the server a
        /// game's matchmaking talks to first, so it is the latency worth showing as "Ping".
        /// </summary>
        public static async Task<long?> PingAsync(IPAddress primary, CancellationToken cancellationToken, int port = PrimaryPort)
        {
            using Socket socket = Bind();

            IPEndPoint target = new(primary, port);
            byte[] buffer = new byte[64];
            long? best = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                window.CancelAfter(1000);

                long started = System.Diagnostics.Stopwatch.GetTimestamp();

                try
                {
                    await socket.SendToAsync(Probe(1), SocketFlags.None, target, cancellationToken);

                    while (true)
                    {
                        SocketReceiveFromResult received;

                        try
                        {
                            received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                                new IPEndPoint(IPAddress.Any, 0), window.Token);
                        }
                        catch (SocketException)
                        {
                            break;
                        }

                        if (received.ReceivedBytes >= 16 && BinaryPrimitives.ReadUInt32BigEndian(buffer) == 1 &&
                            target.Equals(received.RemoteEndPoint))
                        {
                            long elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                            best = best == null ? elapsed : Math.Min(best.Value, elapsed);

                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // No answer inside this try's second; try again.
                }
            }

            return best;
        }

        private static Socket Bind()
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            return socket;
        }

        /// <summary>
        /// Send one probe type to each target (three tries, 400 ms apart) and collect replies of
        /// that type. With <paramref name="from"/> set, only replies from those endpoints count
        /// and are keyed by them; with it null, a reply from anywhere counts, which is the point
        /// of the filtering probes.
        /// </summary>
        private static async Task<Dictionary<IPEndPoint, (int, IPAddress)>> ExchangeAsync(
            Socket socket, uint type, IPEndPoint[] targets, IPEndPoint[] from, CancellationToken cancellationToken)
        {
            Dictionary<IPEndPoint, (int, IPAddress)> seen = [];
            byte[] buffer = new byte[64];

            for (int attempt = 0; attempt < 3; attempt++)
            {
                foreach (IPEndPoint target in targets)
                {
                    if (from == null || !seen.ContainsKey(target))
                    {
                        await socket.SendToAsync(Probe(type), SocketFlags.None, target, cancellationToken);
                    }
                }

                using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                window.CancelAfter(400);

                try
                {
                    while (true)
                    {
                        SocketReceiveFromResult received;

                        try
                        {
                            received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                                new IPEndPoint(IPAddress.Any, 0), window.Token);
                        }
                        catch (SocketException)
                        {
                            // Windows reports an earlier probe's ICMP unreachable here; the
                            // other probes may still be answered.
                            continue;
                        }

                        if (received.ReceivedBytes < 16 || BinaryPrimitives.ReadUInt32BigEndian(buffer) != type)
                        {
                            continue;
                        }

                        IPEndPoint sender = (IPEndPoint)received.RemoteEndPoint;
                        IPEndPoint key = from?.FirstOrDefault(endpoint => endpoint.Equals(sender)) ?? (from == null ? sender : null);

                        if (key == null)
                        {
                            continue;
                        }

                        seen[key] = ((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4)),
                            new IPAddress(buffer.AsSpan(8, 4)));

                        if (from == null || seen.Count == from.Length)
                        {
                            return seen;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // This round's window closed; send again.
                }
            }

            return seen;
        }

        private static bool IsLocal(IPAddress address)
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Any(unicast => unicast.Address.Equals(address));
            }
            catch (NetworkInformationException)
            {
                return false;
            }
        }
    }
}
