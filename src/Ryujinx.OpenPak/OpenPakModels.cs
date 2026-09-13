using System;
using System.Collections.Generic;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// What the OpenPak surfaces answer with, as the emulator needs it.
    ///
    /// These are records rather than deserialised classes on purpose: the app publishes trimmed,
    /// so nothing here may depend on reflection. <see cref="OpenPakApi"/> reads every response
    /// with JsonDocument and fills these by hand.
    /// </summary>
    public sealed record OpenPakProfile(
        string AccountId,
        string DisplayName,
        string Country,
        string Birthday,
        string AvatarUrl,
        string FriendCode,
        IReadOnlyList<string> LinkedPlatforms);

    /// <summary>One person on the account's friend list, as the core knows them.</summary>
    public sealed record OpenPakFriend(
        string AccountId,
        string DisplayName,
        bool Online,
        string TitleId,
        string Namespace,
        DateTime? Since)
    {
        /// <summary>The Switch pid, when the Switch adapter has told us one. 0 otherwise.</summary>
        public ulong Pid { get; init; }

        /// <summary>Their friend code, when the adapter gave one.</summary>
        public string FriendCode { get; init; }
    }

    /// <summary>A friend request in either direction; the account id is who it concerns.</summary>
    public sealed record OpenPakRequest(string AccountId, string DisplayName, bool Incoming)
    {
        public ulong Pid { get; init; }
        public string FriendCode { get; init; }
    }

    /// <summary>
    /// The Switch identity the adapter mints for this account: what the games see, and the token
    /// a title server resolves back to a person.
    /// </summary>
    public sealed record OpenPakSwitchIdentity(
        ulong Pid,
        string Username,
        string FriendCode,
        string BaasUserId,
        string Token,
        IReadOnlyList<OpenPakFriend> Friends,
        IReadOnlyList<OpenPakRequest> Requests);

    /// <summary>An invitation waiting for this account.</summary>
    public sealed record OpenPakInvitation(
        string InvitationId,
        string From,
        string TitleId,
        string Namespace,
        DateTime ExpiresAt);

    /// <summary>One stored version of one title's save.</summary>
    public sealed record OpenPakSaveVersion(
        long Id,
        int Number,
        bool Conflict,
        long Size,
        string Sha256,
        string Device,
        DateTime CreatedAt);

    /// <summary>Every version the cloud holds for one title on one platform.</summary>
    public sealed record OpenPakSave(
        string Platform,
        string TitleId,
        IReadOnlyList<OpenPakSaveVersion> Versions)
    {
        public OpenPakSaveVersion Newest => Versions.Count > 0 ? Versions[0] : null;
    }

    /// <summary>How much of the allowance the account has used.</summary>
    public sealed record OpenPakSaveUsage(long AllowanceUsed, long Allowance, long Total);

    /// <summary>What came back from a save download: the bytes, and what the server called them.</summary>
    public sealed record OpenPakSaveDownload(byte[] Data, string Version, string Sha256, bool Conflict);

    /// <summary>One published mod for a title.</summary>
    public sealed record OpenPakMod(
        string Id,
        string Slug,
        string TitleId,
        string Name,
        string Version,
        string Author,
        string Licence,
        string SourceUrl,
        string Layout,
        string Summary,
        string Sha256,
        long Size,
        string PackageUrl);

    /// <summary>How many people are on one title, or on one console's network.</summary>
    public sealed record OpenPakPopulation(string Key, string Namespace, int Players);

    /// <summary>The public status page's numbers.</summary>
    public sealed record OpenPakStatus(
        int PlayersOnline,
        IReadOnlyList<OpenPakPopulation> Titles,
        IReadOnlyList<OpenPakPopulation> Networks);

    /// <summary>One file in a title's current news dataset.</summary>
    public sealed record OpenPakNewsFile(string Path, long Size, string Sha256, string Url);

    /// <summary>The news dataset a title would receive over BCAT.</summary>
    public sealed record OpenPakNewsManifest(
        string TitleId,
        DateTime ValidFrom,
        DateTime? ValidUntil,
        IReadOnlyList<OpenPakNewsFile> Files);
}
