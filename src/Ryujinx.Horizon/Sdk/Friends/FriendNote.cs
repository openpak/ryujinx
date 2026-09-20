using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>
    /// The private note a person keeps on one friend (30130, friends contract §A.2): char[0x51],
    /// at most 20 characters, in the command's raw data rather than a buffer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x58, Pack = 0x1)]
    struct FriendNote
    {
        public NoteHolder Note;

        [InlineArray(0x51)]
        public struct NoteHolder
        {
            public byte Value;
        }
    }
}
