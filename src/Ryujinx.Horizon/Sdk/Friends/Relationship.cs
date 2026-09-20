using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>
    /// What the caller is to another user (20501, friends contract §B.3). +1 is always 0.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x8, Pack = 0x1)]
    struct Relationship
    {
        [FieldOffset(0)] public bool IsFriend;
        [FieldOffset(2)] public bool IsBlocking;
        [FieldOffset(3)] public bool IsRequestSent;
    }
}
