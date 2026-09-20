using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>One invitation group's id (invitations doc §2b).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 0x8, Pack = 0x8)]
    struct FriendInvitationGroupId
    {
        public ulong Id;

        public override readonly string ToString() => Id.ToString();
    }
}
