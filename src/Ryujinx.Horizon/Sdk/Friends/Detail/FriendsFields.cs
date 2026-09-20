using System.Runtime.CompilerServices;

namespace Ryujinx.Horizon.Sdk.Friends.Detail
{
    /// <summary>
    /// The fixed char and record fields the friends structs are built from (friends contract
    /// §B.3). They are their own types so that one offset table can be written per struct and
    /// the sizes checked by the tests.
    /// </summary>
    static class FriendsFields
    {
        /// <summary>One play-log entry, 0x28 (V1) or 0x30 (V2). Twenty of them make a block.</summary>
        public const int PlayLogEntries = 20;
    }

    /// <summary>A thumbnail URL field: char[0xA0], NUL-terminated.</summary>
    [InlineArray(0xA0)]
    struct ThumbnailUrlField
    {
        public byte Value;
    }

    /// <summary>An in-app screen name or route name field: char[0x40].</summary>
    [InlineArray(0x40)]
    struct RouteNameField
    {
        public byte Value;
    }

    /// <summary>A private friend note: char[0x51], at most 20 characters (30130).</summary>
    [InlineArray(0x51)]
    struct FriendNoteField
    {
        public byte Value;
    }

    /// <summary>
    /// A play-log block: 20 entries of 0x28 (V1). The fields inside one entry — appId,
    /// presenceGroupId and the four counters — have no established offsets in the audit, so
    /// the block is carried as bytes and left zero rather than filled with a guess.
    /// </summary>
    [InlineArray(FriendsFields.PlayLogEntries * 0x28)]
    struct PlayLogBlock
    {
        public byte Value;
    }

    /// <summary>A play-log block with acdIndex: 20 entries of 0x30 (V2). Left zero, as above.</summary>
    [InlineArray(FriendsFields.PlayLogEntries * 0x30)]
    struct PlayLogBlockV2
    {
        public byte Value;
    }
}
