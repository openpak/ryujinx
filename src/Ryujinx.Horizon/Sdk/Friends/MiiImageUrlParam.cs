using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>An NNID Mii image parameter: char[0x10] (friends contract §B.3).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x10, Pack = 0x1)]
    struct MiiImageUrlParam
    {
        public ParamHolder Param;

        [InlineArray(0x10)]
        public struct ParamHolder
        {
            public byte Value;
        }
    }
}
