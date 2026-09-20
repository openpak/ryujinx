using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>
    /// The sixteen per-language invitation messages a game passes (invitations doc §2c):
    /// char[16][0xC0], in the module's language order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 0xC00, Pack = 0x1)]
    struct FriendInvitationGameModeDescription
    {
        public MessageHolder Messages;

        [InlineArray(16 * 0xC0)]
        public struct MessageHolder
        {
            public byte Value;
        }
    }
}
