using System.Collections.Generic;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// One `playLog` entry (friends contract §A.1): a title the person played, as the server counts it.
    /// </summary>
    public sealed record BaasPlayLog(
        ulong ApplicationId,
        byte AcdIndex,
        ulong PresenceGroupId,
        long TotalPlayCount,
        long TotalPlayTime,
        long FirstPlayedAt,
        long LastPlayedAt);

    /// <summary>
    /// Where a friendship, request or block came from: the `route:*` keys of `extras.self`,
    /// `extras.sender` or `extras.senderAndReceiver`.
    /// </summary>
    public sealed record BaasRoute(
        ulong ApplicationId,
        byte AcdIndex,
        ulong PresenceGroupId,
        string CatalogId,
        string Name,
        string Language,
        string MiiName,
        string MiiImageUrlParam);

    /// <summary>
    /// One item of GET /2.0.0/users/&lt;me&gt;/friends (§A.1), or the reply to a PATCH of one (§A.2).
    /// <see cref="Id"/> is the friend's BAAS user id, the id every other call names them by.
    /// </summary>
    public sealed record BaasFriend(ulong Id, string Nickname, string ThumbnailUrl)
    {
        /// <summary>ONLINE 1, PLAYING 2, anything else 0.</summary>
        public int State { get; init; }

        /// <summary>presence.updatedAt, or presence.logoutAt when the state is 0 and it is present.</summary>
        public long UpdatedAt { get; init; }

        public ulong ApplicationId { get; init; }
        public ulong PresenceGroupId { get; init; }
        public byte AcdIndex { get; init; }

        /// <summary>The published appField, a JSON object as a string, or null.</summary>
        public string AppField { get; init; }

        public bool IsFavorite { get; init; }

        /// <summary>extras.self.isConfirmed absent or false.</summary>
        public bool IsNewly { get; init; }

        public bool IsOnlineNotification { get; init; }
        public long CreatedAt { get; init; }
        public string Note { get; init; }

        /// <summary>The channels table, 1-based; 0 when absent or unknown.</summary>
        public int Channel { get; init; }

        public BaasRoute Route { get; init; }
        public IReadOnlyList<BaasPlayLog> PlayLog { get; init; } = [];
    }

    /// <summary>
    /// One friend request (§A.4.2), seen from one side: <see cref="OtherId"/> is the sender for the
    /// inbox and the receiver for the outbox.
    /// </summary>
    public sealed record BaasRequest(ulong Id, int Channel, int State, ulong OtherId, string Nickname, string ThumbnailUrl)
    {
        public long CreatedAt { get; init; }
        public bool Read { get; init; }
        public BaasRoute Route { get; init; }
    }

    /// <summary>A flat user from GET /1.0.0/users?filter… (§A.7).</summary>
    public sealed record BaasUser(ulong Id, string Nickname, string ThumbnailUrl)
    {
        public IReadOnlyList<BaasPlayLog> PlayLog { get; init; } = [];
    }

    /// <summary>The caller's own user, GET /1.0.0/users/&lt;me&gt; (§A.6).</summary>
    public sealed record BaasUserSetting(ulong Id, string Nickname, string ThumbnailUrl)
    {
        /// <summary>SELF 0, FAVORITE_FRIENDS 1, FRIENDS 2; anything else 0.</summary>
        public int PresencePermission { get; init; }

        /// <summary>The group that carries playLog: self 1, favoriteFriends 2, friends 3, everyone 5; 0 unset.</summary>
        public int PlayLogPermission { get; init; }

        public bool FriendRequestReception { get; init; }
        public string FriendCode { get; init; }
        public long FriendCodeRegenerableAt { get; init; }

        /// <summary>The playLog string as the server holds it, "[…]", or null.</summary>
        public string PlayLogText { get; init; }

        public IReadOnlyList<BaasPlayLog> PlayLog { get; init; } = [];
    }

    /// <summary>One item of GET /1.0.0/users/&lt;me&gt;/blocks (§A.8).</summary>
    public sealed record BaasBlock(ulong Id, string Nickname, string ThumbnailUrl)
    {
        public long CreatedAt { get; init; }

        /// <summary>BAD_FRIEND_REQUEST 1, BAD_FRIEND 2, IN_APP 3, IN_CAMPUS 4; 0 otherwise.</summary>
        public int Reason { get; init; }

        public BaasRoute Route { get; init; }
    }

    /// <summary>
    /// A request the guest asked to send (§A.4.1): who it goes to, on which channel, and —
    /// for the application variants — the title and the two in-app screen names. The plain
    /// variant leaves the application half null.
    ///
    /// <see cref="TargetId"/> is the network-service account id as the guest gave it: the
    /// session resolves it to the BAAS user the server names, as it does for invitations.
    /// </summary>
    public sealed record BaasFriendRequestSend(ulong TargetId, string Channel)
    {
        public ulong ApplicationId { get; init; }
        public byte AcdIndex { get; init; }
        public ulong PresenceGroupId { get; init; }
        public string TargetName { get; init; }
        public string TargetLanguage { get; init; }
        public string OwnName { get; init; }
        public string OwnLanguage { get; init; }

        /// <summary>The external catalog id of a catalog route (30215), 32 hex digits, or null.</summary>
        public string CatalogId { get; init; }

        /// <summary>The sender's own Mii name and image parameter, for an NNID route (30217).</summary>
        public string MiiName { get; init; }

        public string MiiImageUrlParam { get; init; }
    }

    /// <summary>
    /// The relationship to one user, GET /2.0.0/users/&lt;me&gt;/relationships/&lt;id&gt; (§A.7).
    /// </summary>
    public sealed record BaasRelationship(bool IsFriend, bool IsBlocking, bool IsRequestSent);

    /// <summary>One item of the Five invitation inbox (invitations doc §2a).</summary>
    public sealed record BaasInvitation(ulong Id, ulong GroupId, ulong SenderId, ulong ApplicationId, ulong ApplicationGroupId)
    {
        public byte AcdIndex { get; init; }
        public byte[] ApplicationData { get; init; } = [];
        public long CreatedAt { get; init; }
        public bool Read { get; init; }
        public bool ApplicationIdMatch { get; init; }
    }

    /// <summary>GET /v1/invitation_groups/&lt;id&gt; (invitations doc §2b).</summary>
    public sealed record BaasInvitationGroup(ulong Id, ulong SenderId, IReadOnlyList<ulong> Receivers, ulong ApplicationId, ulong ApplicationGroupId)
    {
        public byte AcdIndex { get; init; }

        /// <summary>16 slots in the module's language order; empty where the sender filled nothing.</summary>
        public IReadOnlyList<string> Messages { get; init; } = [];

        public byte[] ApplicationData { get; init; } = [];
        public long CreatedAt { get; init; }
        public bool ApplicationIdMatch { get; init; }
    }
}
