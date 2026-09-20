using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>An NNID Mii name: char[0x20], at most 10 characters (friends contract §B.3).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x20, Pack = 0x1)]
    struct MiiName
    {
        public NameHolder Name;

        [InlineArray(0x20)]
        public struct NameHolder
        {
            public byte Value;
        }
    }
}
