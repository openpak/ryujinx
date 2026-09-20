using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>
    /// A 128-bit external application catalog id (friends contract §B.3), written on the wire as
    /// <c>%016llx%016llx</c>: the high half first.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x10, Pack = 0x8)]
    struct ExternalApplicationCatalogId
    {
        public ulong High;
        public ulong Low;

        public override readonly string ToString() => $"{High:x16}{Low:x16}";
    }
}
