using Ryujinx.Common.Memory;
using Ryujinx.Horizon.Sdk.Account;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// The caller's own presence, as nn::friends::UserPresence builds it and UpdateUserPresence
    /// (10610) receives it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 0xE0)]
    struct UserPresenceImpl
    {
        public Uid UserId;
        public ulong Unused;

        /// <summary>0 none, 1 DeclareOpenOnlinePlaySession, 2 DeclareCloseOnlinePlaySession.</summary>
        public byte OnlinePlayDeclaration;
        public Array7<byte> Padding;

        /// <summary>key\0value\0 pairs, zero-padded; sys_description is SetDescription's.</summary>
        public AppKeyValueStorageHolder AppKeyValueStorage;

        [InlineArray(0xC0)]
        public struct AppKeyValueStorageHolder
        {
            public byte Value;
        }

        public readonly override string ToString()
        {
            return $"{{ UserId: {UserId}, OnlinePlayDeclaration: {OnlinePlayDeclaration} }}";
        }
    }
}
