using Avalonia.Media;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.OpenPak;
using System;
using System.Linq;

namespace Ryujinx.Ava.UI.ViewModels
{
    /// <summary>
    /// One row in an OpenPak list, already in the words it should appear in.
    ///
    /// The formatting lives here rather than in a value converter because every one of these
    /// strings is a sentence a translator has to be able to move around — "Playing {0}" is not
    /// two bindings glued together in a StackPanel.
    /// </summary>
    public class OpenPakFriendModel : BaseModel
    {
        public OpenPakFriendModel(OpenPakFriend friend, string titleName)
        {
            AccountId = friend.AccountId;
            DisplayName = friend.DisplayName;
            Online = friend.Online;

            Status = !friend.Online
                ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_FriendsOffline]
                : string.IsNullOrEmpty(titleName)
                    ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_FriendsOnline]
                    : LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_FriendsPlaying, titleName);
        }

        public string AccountId { get; }
        public string DisplayName { get; }
        public bool Online { get; }
        public string Status { get; }

        /// <summary>Stands in for an avatar, which the friend list does not carry.</summary>
        public string Initial => string.IsNullOrEmpty(DisplayName)
            ? "?"
            : System.Globalization.StringInfo.GetNextTextElement(DisplayName).ToUpperInvariant();

        /// <summary>The presence dot. Green reads as "there", and grey as "not", at a glance.</summary>
        public IBrush PresenceBrush => Online
            ? new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50))
            : new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A));
    }

    /// <summary>A pending friend request, in whichever direction it is going.</summary>
    public class OpenPakRequestModel : BaseModel
    {
        public OpenPakRequestModel(OpenPakRequest request)
        {
            AccountId = request.AccountId;
            DisplayName = request.DisplayName;
            Incoming = request.Incoming;

            Status = LocaleManager.Instance[request.Incoming
                ? LocaleKeys.Dialog_OpenPak_FriendsIncoming
                : LocaleKeys.Dialog_OpenPak_FriendsOutgoing];
        }

        public string AccountId { get; }
        public string DisplayName { get; }
        public bool Incoming { get; }
        public string Status { get; }

        /// <summary>Only an incoming request can be accepted; an outgoing one can only be withdrawn.</summary>
        public bool CanAccept => Incoming;

        /// <summary>
        /// The same call to the core either way, but not the same act to a person: refusing
        /// someone else's request is not the same as taking back your own.
        /// </summary>
        public string DeclineText => LocaleManager.Instance[Incoming
            ? LocaleKeys.Dialog_OpenPak_FriendsDecline
            : LocaleKeys.Dialog_OpenPak_FriendsCancel];
    }

    /// <summary>An invitation waiting for this account.</summary>
    public class OpenPakInvitationModel : BaseModel
    {
        public OpenPakInvitationModel(OpenPakInvitation invitation, string titleName)
        {
            InvitationId = invitation.InvitationId;
            TitleId = invitation.TitleId;
            TitleName = string.IsNullOrEmpty(titleName) ? invitation.TitleId : titleName;

            From = LocaleManager.Instance.UpdateAndGetDynamicValue(
                LocaleKeys.Dialog_OpenPak_InvitationsFrom, invitation.From);

            Expires = LocaleManager.Instance.UpdateAndGetDynamicValue(
                LocaleKeys.Dialog_OpenPak_InvitationsExpires, invitation.ExpiresAt.ToLocalTime());
        }

        public string InvitationId { get; }
        public string TitleId { get; }
        public string TitleName { get; }
        public string From { get; }
        public string Expires { get; }
    }

    /// <summary>Every cloud version of one title's save, summarised.</summary>
    public class OpenPakSaveModel : BaseModel
    {
        public OpenPakSaveModel(OpenPakSave save, ApplicationData application, string localDetail)
        {
            TitleId = save.TitleId;
            TitleName = application?.Name ?? save.TitleId;
            Application = application;
            LocalDetail = localDetail;

            OpenPakSaveVersion newest = save.Newest;

            NewestVersion = newest?.Number.ToString();

            Detail = newest == null
                ? string.Empty
                : LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_SavesVersion, newest.Number) +
                    (string.IsNullOrEmpty(newest.Device)
                        ? string.Empty
                        : " " + LocaleManager.Instance.UpdateAndGetDynamicValue(
                            LocaleKeys.Dialog_OpenPak_SavesFrom, newest.Device)) +
                    $" — {newest.CreatedAt.ToLocalTime():g}";

            // A conflict is the one thing here that a person has to decide about, so it says so
            // rather than quietly picking a side.
            Conflict = save.Versions.Any(version => version.Conflict);
        }

        public string TitleId { get; }
        public string TitleName { get; }
        public string NewestVersion { get; }
        public string Detail { get; }
        public bool Conflict { get; }

        /// <summary>The installed title, or null when the cloud holds a save for one that is not here.</summary>
        public ApplicationData Application { get; }

        /// <summary>What is on this machine for the title: when it was last written and what it was synced with.</summary>
        public string LocalDetail { get; }

        public bool Installed => Application != null;
    }

    /// <summary>One published mod, with what this install has done about it.</summary>
    public class OpenPakModModel : BaseModel
    {
        private bool _favourite;
        private bool _installed;

        public OpenPakModModel(OpenPakMod mod, bool favourite, bool installed)
        {
            Mod = mod;
            _favourite = favourite;
            _installed = installed;

            Detail = LocaleManager.Instance.UpdateAndGetDynamicValue(
                LocaleKeys.Dialog_OpenPak_ModsBy, mod.Author, mod.Licence);
        }

        public OpenPakMod Mod { get; }

        public string Id => Mod.Id;
        public string Name => Mod.Name;
        public string Version => Mod.Version;
        public string Summary => Mod.Summary;
        public string Detail { get; }

        public bool Favourite
        {
            get => _favourite;
            set
            {
                _favourite = value;

                OnPropertyChanged();
            }
        }

        public bool Installed
        {
            get => _installed;
            set
            {
                _installed = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(NotInstalled));
            }
        }

        public bool NotInstalled => !_installed;
    }

    /// <summary>One file in a title's news dataset.</summary>
    public class OpenPakNewsFileModel : BaseModel
    {
        public OpenPakNewsFileModel(OpenPakNewsFile file)
        {
            File = file;
            Detail = $"{file.Size:N0} bytes";
        }

        public OpenPakNewsFile File { get; }

        public string Path => File.Path;
        public string Detail { get; }
    }

    /// <summary>How many people are on one title, or on one console's network.</summary>
    public class OpenPakPopulationModel(string name, int players) : BaseModel
    {
        public string Name { get; } = name;
        public int Players { get; } = players;
        public string PlayersText { get; } = players.ToString();
    }
}
