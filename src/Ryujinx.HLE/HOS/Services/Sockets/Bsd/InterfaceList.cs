using System;
using System.Buffers.Binary;
using System.Net;

namespace Ryujinx.HLE.HOS.Services.Sockets.Bsd
{
    /// <summary>
    /// The answer to sysctl {CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLISTL, 0}: FreeBSD's getifaddrs().
    /// NPLN's WebRTC asks for it to gather connection candidates; refused, a Dinkum host offers a
    /// joiner no address at all and the join times out with 2321-5248 (2026-09-18).
    ///
    /// One interface, the address games are already told through nifm. Every message carries its
    /// own length and the offset of what follows (ifm_len, ifm_data_off), and the parser walks by
    /// those, so a guest whose if_data differs in size still finds the addresses. Every sockaddr is
    /// a multiple of 8 bytes, which reads the same under 4- or 8-byte SA_SIZE rounding.
    /// </summary>
    public static class InterfaceList
    {
        public const int CtlNet = 4;
        public const int PfRoute = 17;
        public const int NetRtIfListL = 5;

        private const byte RtmVersion = 5;
        private const byte RtmIfInfo = 0x0e;
        private const byte RtmNewAddr = 0x0c;
        private const int RtaNetmask = 0x04;
        private const int RtaIfp = 0x10;
        private const int RtaIfa = 0x20;
        private const int RtaBrd = 0x80;
        private const int IffUpBroadcastRunningMulticast = 0x1 | 0x2 | 0x40 | 0x8000;

        private const int HeaderSize = 24; // up to and including the padding before if_data
        private const int IfDataSize = 152; // FreeBSD 11+ struct if_data
        private const int SockaddrDlSize = 24;
        private const int SockaddrInSize = 16;
        private const ushort InterfaceIndex = 1;

        public static bool Matches(ReadOnlySpan<int> mib) =>
            mib.Length >= 5 && mib[0] == CtlNet && mib[1] == PfRoute && mib[4] == NetRtIfListL;

        public static byte[] Build(IPAddress address, IPAddress netmask)
        {
            byte[] ip = address.GetAddressBytes();
            byte[] mask = netmask.GetAddressBytes();
            byte[] broadcast = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                broadcast[i] = (byte)(ip[i] | ~mask[i]);
            }

            int infoLength = HeaderSize + IfDataSize + SockaddrDlSize;
            int addrLength = HeaderSize + IfDataSize + 3 * SockaddrInSize;
            byte[] buffer = new byte[infoLength + addrLength];

            // RTM_IFINFO (struct if_msghdrl) + the link-level sockaddr_dl naming the interface
            Span<byte> info = buffer.AsSpan(0, infoLength);
            WriteHeader(info, RtmIfInfo, RtaIfp, IffUpBroadcastRunningMulticast);
            WriteIfData(info[HeaderSize..]);
            Span<byte> dl = info[(HeaderSize + IfDataSize)..];
            dl[0] = SockaddrDlSize; // sdl_len, padded: see the class comment
            dl[1] = 18; // AF_LINK
            BinaryPrimitives.WriteUInt16LittleEndian(dl[2..], InterfaceIndex);
            dl[4] = 6; // IFT_ETHER
            dl[5] = 4; // sdl_nlen: "eth0"
            dl[6] = 6; // sdl_alen
            "eth0"u8.CopyTo(dl[8..]);
            byte[] mac = { 0x02, 0x00, ip[0], ip[1], ip[2], ip[3] }; // locally administered
            mac.CopyTo(dl[12..]);

            // RTM_NEWADDR (struct ifa_msghdrl) + netmask, address, broadcast in RTA_* bit order
            Span<byte> addr = buffer.AsSpan(infoLength, addrLength);
            WriteHeader(addr, RtmNewAddr, RtaNetmask | RtaIfa | RtaBrd, 0);
            WriteIfData(addr[HeaderSize..]);
            Span<byte> sa = addr[(HeaderSize + IfDataSize)..];
            WriteSockaddrIn(sa, mask);
            WriteSockaddrIn(sa[SockaddrInSize..], ip);
            WriteSockaddrIn(sa[(2 * SockaddrInSize)..], broadcast);

            return buffer;
        }

        // if_msghdrl and ifa_msghdrl share this prefix; ifam_metric (offset 20) stays 0.
        private static void WriteHeader(Span<byte> m, byte type, int addrs, int flags)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(m, (ushort)m.Length); // msglen
            m[2] = RtmVersion;
            m[3] = type;
            BinaryPrimitives.WriteInt32LittleEndian(m[4..], addrs);
            BinaryPrimitives.WriteInt32LittleEndian(m[8..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(m[12..], InterfaceIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(m[16..], HeaderSize + IfDataSize); // len: sockaddrs follow
            BinaryPrimitives.WriteUInt16LittleEndian(m[18..], HeaderSize); // data_off
        }

        private static void WriteIfData(Span<byte> d)
        {
            d[0] = 6; // IFT_ETHER
            d[2] = 6; // addrlen
            d[3] = 14; // hdrlen
            d[4] = 2; // LINK_STATE_UP
            BinaryPrimitives.WriteUInt16LittleEndian(d[6..], IfDataSize); // datalen
            BinaryPrimitives.WriteUInt32LittleEndian(d[8..], 1500); // mtu
        }

        private static void WriteSockaddrIn(Span<byte> s, byte[] address)
        {
            s[0] = SockaddrInSize;
            s[1] = 2; // AF_INET
            address.CopyTo(s[4..]); // network order already; port stays 0
        }
    }
}
