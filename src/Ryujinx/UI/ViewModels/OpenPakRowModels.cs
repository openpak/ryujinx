using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.OpenPak;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        public OpenPakFriendModel(OpenPakFriend friend, string titleName, ApplicationData application = null)
        {
            AccountId = friend.AccountId;
            DisplayName = friend.DisplayName;
            Online = friend.Online;

            // The title only counts while they are on it: a title id left on an offline friend
            // would put a game beside somebody who is not playing it.
            TitleIcon = friend.Online ? application?.Icon : null;

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

        /// <summary>
        /// The game they are in, as this machine's own library draws it. Null for a title that is
        /// not installed here — the line already says its name, and a wrong picture is worse than
        /// none.
        /// </summary>
        public byte[] TitleIcon { get; }

        public bool Playing => TitleIcon != null;

        /// <summary>Stands in for the avatar until it is here, and for good if it never arrives.</summary>
        public string Initial => string.IsNullOrEmpty(DisplayName)
            ? "?"
            : System.Globalization.StringInfo.GetNextTextElement(DisplayName).ToUpperInvariant();

        /// <summary>Their picture, once fetched. Null until then, which draws the initial instead.</summary>
        public Bitmap Avatar
        {
            get => _avatar;
            private set
            {
                _avatar = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The friends list carries no picture, but every account's is public at a known URL, so
        /// it costs one fetch per person. Kept for the session: the list re-projects on every
        /// presence poll, and nobody changes their avatar between two of those.
        /// </summary>
        public async Task LoadAvatarAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(AccountId))
            {
                return;
            }

            if (_avatars.TryGetValue(AccountId, out Bitmap cached))
            {
                await Dispatcher.UIThread.InvokeAsync(() => Avatar = cached);

                return;
            }

            byte[] image = await OpenPakApi.Instance.ImageAsync(
                $"/media/avatars/{AccountId}/256", cancellationToken);

            if (OpenPakImages.Decode(image) is not { } decoded)
            {
                return;
            }

            Bitmap bitmap = _avatars.GetOrAdd(AccountId, decoded);

            await Dispatcher.UIThread.InvokeAsync(() => Avatar = bitmap);
        }

        private static readonly ConcurrentDictionary<string, Bitmap> _avatars = new();
        private Bitmap _avatar;

        /// <summary>The presence dot. Green reads as "there", and grey as "not", at a glance.</summary>
        public IBrush PresenceBrush => Online ? OpenPakBrushes.Up : OpenPakBrushes.Down;
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
            Save = save;
            TitleId = save.TitleId;

            // The library's name first, the catalogue's second: one of them is the name on the
            // shelf here, and the other is the only name a title nobody installed ever had.
            TitleName = application?.Name ?? save.Name ?? save.TitleId;

            Application = application;
            LocalDetail = localDetail;

            Icon = OpenPakImages.Decode(application?.Icon);

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

            // Every version together, because every version is what the allowance is spent on:
            // a row showing only the newest would not add up to the figure at the top.
            SizeText = LocaleManager.Instance.UpdateAndGetDynamicValue(
                LocaleKeys.Dialog_OpenPak_SavesSize, OpenPakViewModel.Bytes(save.Size), save.Versions.Count);

            // A conflict is the one thing here that a person has to decide about, so it says so
            // rather than quietly picking a side.
            Conflict = save.Versions.Any(version => version.Conflict);
        }

        /// <summary>What the cloud answered with, for the calls that name versions by id.</summary>
        public OpenPakSave Save { get; }

        public string TitleId { get; }
        public string TitleName { get; }
        public string NewestVersion { get; }
        public string Detail { get; }
        public string SizeText { get; }
        public bool Conflict { get; }

        /// <summary>The installed title, or null when the cloud holds a save for one that is not here.</summary>
        public ApplicationData Application { get; }

        /// <summary>What is on this machine for the title: when it was last written and what it was synced with.</summary>
        public string LocalDetail { get; }

        public bool Installed => Application != null;

        public bool NotInstalled => Application == null;

        public int VersionCount => Save.Versions.Count;

        /// <summary>The title's picture: this machine's copy, or the catalogue's once it arrives.</summary>
        public Bitmap Icon
        {
            get => _icon;
            private set
            {
                _icon = value;

                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The catalogue's icon, for a title that is not installed here. Only those: an installed
        /// title already drew its own out of the library and owes the network nothing.
        /// </summary>
        public async Task LoadIconAsync(CancellationToken cancellationToken)
        {
            if (_icon != null || string.IsNullOrEmpty(Save.IconUrl))
            {
                return;
            }

            if (_icons.TryGetValue(TitleId, out Bitmap cached))
            {
                await Dispatcher.UIThread.InvokeAsync(() => Icon = cached);

                return;
            }

            byte[] image = await OpenPakApi.Instance.ImageAsync(Save.IconUrl, cancellationToken);

            if (OpenPakImages.Decode(image) is not { } bitmap)
            {
                return;
            }

            bitmap = _icons.GetOrAdd(TitleId, bitmap);

            await Dispatcher.UIThread.InvokeAsync(() => Icon = bitmap);
        }

        private static readonly ConcurrentDictionary<string, Bitmap> _icons = new();
        private Bitmap _icon;
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

    /// <summary>
    /// One OpenPak service as the status box last found it.
    ///
    /// The wording is the server's, not this window's: the status page already explains each
    /// service to a person, and saying it a second way here would be two texts to keep in step.
    /// </summary>
    public class OpenPakServiceModel : BaseModel
    {
        public OpenPakServiceModel(OpenPakService service)
        {
            Group = service.Group;
            Name = service.Name;
            Blurb = service.Blurb;
            Up = service.Up;

            StateText = LocaleManager.Instance[service.Up
                ? LocaleKeys.Dialog_OpenPak_StatusUp
                : LocaleKeys.Dialog_OpenPak_StatusDown];

            // Latency without uptime says how it is now and nothing about how it has been, which
            // is the half that tells a blip from a service that has been down all morning.
            Detail = LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusUptime,
                $"{service.Uptime:0.#}", service.Latency ?? string.Empty);
        }

        public string Group { get; }
        public string Name { get; }
        public string Blurb { get; }
        public bool Up { get; }
        public string StateText { get; }
        public string Detail { get; }

        public bool HasBlurb => !string.IsNullOrEmpty(Blurb);

        public IBrush StateBrush => Up ? OpenPakBrushes.Up : OpenPakBrushes.Down;
    }

    /// <summary>
    /// One line about this session rather than about the network: the parts that can be broken
    /// on this machine while every service is up, which is the case nothing else here would show.
    /// </summary>
    public class OpenPakSessionModel(string name, string detail, bool? up) : BaseModel
    {
        public string Name { get; } = name;
        public string Detail { get; } = detail;

        /// <summary>Null where up and down is not the question — a presence word, a count.</summary>
        public bool? Up { get; } = up;

        public bool HasState => Up != null;

        public IBrush StateBrush => Up == true ? OpenPakBrushes.Up : OpenPakBrushes.Down;
    }

    /// <summary>Bytes to a picture, or null: a server that answered with anything else is not news.</summary>
    internal static class OpenPakImages
    {
        public static Bitmap Decode(byte[] image)
        {
            if (image == null || image.Length == 0)
            {
                return null;
            }

            try
            {
                return new Bitmap(new MemoryStream(image));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The two colours every dot in this window uses. Shared so a friend who is online and a
    /// service that is up are the same green, which is the only reason a dot reads at a glance.
    /// </summary>
    internal static class OpenPakBrushes
    {
        public static readonly IBrush Up = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        public static readonly IBrush Down = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A));
        public static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(0xB5, 0x41, 0x41));
    }
}
