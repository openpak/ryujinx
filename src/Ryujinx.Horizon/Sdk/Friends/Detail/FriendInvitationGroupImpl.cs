using Ryujinx.Common.Memory;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// One invitation group in full, as 22001 returns it (invitations doc §2b): who it went to,
    /// the title it is for, the sixteen language message slots and the application data.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x1400, Pack = 0x1)]
    struct FriendInvitationGroupImpl
    {
        [FieldOffset(0x000)] public ulong GroupId;
        [FieldOffset(0x008)] public ulong SenderId;
        [FieldOffset(0x010)] public ReceiverHolder Receivers;
        [FieldOffset(0x090)] public int ReceiverCount;
        [FieldOffset(0x098)] public ulong ApplicationId;
        [FieldOffset(0x0A0)] public ulong ApplicationGroupId;
        [FieldOffset(0x0A8)] public MessageHolder Messages;
        [FieldOffset(0xCA8)] public uint ApplicationDataSize;
        [FieldOffset(0xCB0)] public long CreatedAt;
        [FieldOffset(0xCB8)] public bool ApplicationIdMatch;
        [FieldOffset(0xCB9)] public bool IsValid;
        [FieldOffset(0xD60)] public ApplicationDataHolder ApplicationData;

        /// <summary>At most sixteen receivers, as the module's parser accepts.</summary>
        [InlineArray(16)]
        public struct ReceiverHolder
        {
            public ulong Value;
        }

        /// <summary>
        /// Sixteen message slots of 0xC0 bytes, in the module's language order: en-US, en-GB, ja,
        /// fr, de, es-419, es, it, nl, fr-CA, pt, ru, zh-Hans, zh-Hant, ko, pt-BR.
        /// </summary>
        [InlineArray(16 * 0xC0)]
        public struct MessageHolder
        {
            public byte Value;
        }

        [InlineArray(0x400)]
        public struct ApplicationDataHolder
        {
            public byte Value;
        }
    }

    /// <summary>The same with the acd index at +0xA4, the tail after it shifted by 8 (22003).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x1400, Pack = 0x1)]
    struct FriendInvitationGroupImplV2
    {
        [FieldOffset(0x000)] public ulong GroupId;
        [FieldOffset(0x008)] public ulong SenderId;
        [FieldOffset(0x010)] public FriendInvitationGroupImpl.ReceiverHolder Receivers;
        [FieldOffset(0x090)] public int ReceiverCount;
        [FieldOffset(0x098)] public ulong ApplicationId;
        [FieldOffset(0x0A0)] public uint Unknown;
        [FieldOffset(0x0A4)] public byte AcdIndex;
        [FieldOffset(0x0A8)] public ulong ApplicationGroupId;
        [FieldOffset(0x0B0)] public FriendInvitationGroupImpl.MessageHolder Messages;
        [FieldOffset(0xCB0)] public uint ApplicationDataSize;
        [FieldOffset(0xCB8)] public long CreatedAt;
        [FieldOffset(0xCC0)] public bool ApplicationIdMatch;
        [FieldOffset(0xCC1)] public bool IsValid;
        [FieldOffset(0xD60)] public FriendInvitationGroupImpl.ApplicationDataHolder ApplicationData;
    }
}
