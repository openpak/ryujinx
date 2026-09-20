using Ryujinx.Horizon.Sdk.Ncm;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>
    /// The title an invitation or a friend request came from, with the acd index (30218 / 30901,
    /// invitations doc §2d). The u32 at +8 is never serialised or parsed.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x18, Pack = 0x8)]
    struct ApplicationInfoV2
    {
        [FieldOffset(0x00)] public ApplicationId ApplicationId;
        [FieldOffset(0x08)] public uint Unknown;
        [FieldOffset(0x0C)] public byte AcdIndex;
        [FieldOffset(0x10)] public ulong PresenceGroupId;
    }
}
