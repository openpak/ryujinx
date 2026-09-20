using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>
    /// A friend code as the module carries one: char[0x20], NUL-terminated, exactly the
    /// characters the server issued (friends contract §A.6 — no prefix, no normalisation).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x20, Pack = 0x1)]
    struct FriendCode
    {
        public CodeHolder Code;

        [InlineArray(0x20)]
        public struct CodeHolder
        {
            public byte Value;
        }
    }
}
