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

        /// <summary>
        /// The presence state as the friends module reads it: 0 offline, 1 online (in a title or
        /// not), 2 in a declared online-play session. Only 2 passes an OnlinePlay filter.
        /// </summary>
        public int Status { get; init; }

        /// <summary>The appField their console last published, a JSON object as a string, or null.</summary>
        public string AppField { get; init; }

        /// <summary>When the friendship began, as the adapter dates it. Null off a route that carries none.</summary>
        public DateTime? FriendsSince { get; init; }
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
        DateTime ExpiresAt)
    {
        /// <summary>
        /// What the sender's game attached, base64 as the native inbox carries it, or null. The
        /// receiving game is handed the decoded bytes, never this string.
        /// </summary>
        public string ApplicationData { get; init; }

        /// <summary>
        /// Who sent it, as the BAAS user id the inbox names them by: 16 hex digits. Null off the
        /// website route, which only carries the name.
        /// </summary>
        public string SenderId { get; init; }

        /// <summary>When it was sent, or null on a route that carries no time.</summary>
        public DateTime? CreatedAt { get; init; }

        /// <summary>
        /// What the sender's game wrote, one text per language tag. Empty when it wrote nothing,
        /// which is the usual case: most titles send an invitation with no words at all.
        /// </summary>
        public IReadOnlyDictionary<string, string> Messages { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>One stored version of one title's save.</summary>
    public sealed record OpenPakSaveVersion(
        long Id,
        int Number,
        bool Conflict,
        long Size,
        string Sha256,
        string Device,
        DateTime CreatedAt)
    {
        /// <summary>Where the bytes sit: "local" is the OpenPak allowance, anything else is the account's own storage.</summary>
        public string Backend { get; init; }
    }

    /// <summary>Every version the cloud holds for one title on one platform.</summary>
    public sealed record OpenPakSave(
        string Platform,
        string TitleId,
        IReadOnlyList<OpenPakSaveVersion> Versions)
    {
        public OpenPakSaveVersion Newest => Versions.Count > 0 ? Versions[0] : null;

        /// <summary>
        /// What the catalogue calls the title, when the site could name it. The cloud holds saves
        /// for titles this machine has never had, and an id is not a name to anybody.
        /// </summary>
        public string Name { get; init; }

        /// <summary>The catalogue's icon for the title, site-relative, or null.</summary>
        public string IconUrl { get; init; }

        /// <summary>Every version together: what this one title costs the allowance.</summary>
        public long Size
        {
            get
            {
                long total = 0;

                foreach (OpenPakSaveVersion version in Versions)
                {
                    total += version.Size;
                }

                return total;
            }
        }
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

    /// <summary>
    /// One service on the status page, as it answered its last check. The wording is the
    /// server's: the status box already explains each service to a person, and saying it
    /// differently here would be two explanations to keep in step.
    /// </summary>
    public sealed record OpenPakService(
        string Group,
        string Name,
        string Blurb,
        bool Up,
        double Uptime,
        string Latency,
        string Checked);

    /// <summary>Everything the status box knows, as one answer.</summary>
    public sealed record OpenPakHealth(
        string Headline,
        string Summary,
        string State,
        string Generated,
        IReadOnlyList<OpenPakService> Services);

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
