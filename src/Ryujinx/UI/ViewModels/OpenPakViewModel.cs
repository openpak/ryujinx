using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.OpenPak;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.ViewModels
{
    /// <summary>
    /// One view model behind every OpenPak page, because they are one account: a friend accepted
    /// on the friends page changes what the account page says it has, and a sign-out has to empty
    /// all of them at once.
    ///
    /// Everything network-facing here is a command a person pressed. The polling that keeps the
    /// guest's friend list warm lives in <see cref="OpenPakAccount"/>, and this listens to it.
    /// </summary>
    public class OpenPakViewModel : BaseModel, IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ApplicationLibrary _library;

        private bool _busy;
        private string _message;
        private Bitmap _avatar;
        private string _friendCodeEntry = string.Empty;
        private ApplicationData _selectedTitle;
        private OpenPakSaveUsage _usage = new(0, 0, 0);

        public OpenPakViewModel() : this(null)
        {
        }

        public OpenPakViewModel(ApplicationLibrary library)
        {
            _library = library;

            Titles = library == null
                ? []
                : [.. library.Applications.Items.OrderBy(application => application.Name)];

            SelectedTitle = Titles.FirstOrDefault();

            OpenPakAccount.Instance.Changed += OnAccountChanged;
        }

        // ---- state everything reads ----

        /// <summary>A request is in flight; the pages disable what would race with it.</summary>
        public bool Busy
        {
            get => _busy;
            set
            {
                _busy = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(Ready));
            }
        }

        /// <summary>The inverse of <see cref="Busy"/>, for binding a control's IsEnabled.</summary>
        public bool Ready => !_busy;

        /// <summary>The last thing worth telling the person, or empty.</summary>
        public string Message
        {
            get => _message;
            set
            {
                _message = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasMessage));
            }
        }

        public bool HasMessage => !string.IsNullOrEmpty(_message);

        public bool SignedIn => OpenPakApi.Instance.SignedIn;

        public bool SignedOut => !SignedIn;

        public string DisplayName => OpenPakAccount.Instance.DisplayName
            ?? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_None];

        public string FriendCode => OpenPakAccount.Instance.FriendCode
            ?? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_None];

        public string ServerAddress => OpenPakConfig.ResolvedConsoleServer;

        public string SwitchPid => OpenPakAccount.Instance.Identity is { Pid: not 0 } identity
            ? LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_AccountPid, identity.Pid)
            : LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_None];

        public string LinkedConsoles => OpenPakAccount.Instance.Profile is { LinkedPlatforms.Count: > 0 } profile
            ? string.Join(", ", profile.LinkedPlatforms)
            : LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_None];

        public Bitmap Avatar
        {
            get => _avatar;
            private set
            {
                _avatar = value;

                OnPropertyChanged();
            }
        }

        // ---- the lists the pages show ----

        public ObservableCollection<OpenPakFriendModel> Friends { get; } = [];
        public ObservableCollection<OpenPakRequestModel> Requests { get; } = [];
        public ObservableCollection<OpenPakInvitationModel> Invitations { get; } = [];
        public ObservableCollection<OpenPakSaveModel> Saves { get; } = [];
        public ObservableCollection<OpenPakModModel> Mods { get; } = [];
        public ObservableCollection<OpenPakNewsFileModel> NewsFiles { get; } = [];
        public ObservableCollection<OpenPakPopulationModel> TitlePopulations { get; } = [];
        public ObservableCollection<OpenPakPopulationModel> NetworkPopulations { get; } = [];

        /// <summary>Every title in the library, for the pages that are about one title.</summary>
        public ObservableCollection<ApplicationData> Titles { get; }

        public ApplicationData SelectedTitle
        {
            get => _selectedTitle;
            set
            {
                _selectedTitle = value;

                OnPropertyChanged();
            }
        }

        public string FriendCodeEntry
        {
            get => _friendCodeEntry;
            set
            {
                _friendCodeEntry = value;

                OnPropertyChanged();
            }
        }

        public string PlayersOnline { get; private set; } = string.Empty;

        public string UsageText => LocaleManager.Instance.UpdateAndGetDynamicValue(
            LocaleKeys.Dialog_OpenPak_SavesUsage, Bytes(_usage.AllowanceUsed), Bytes(_usage.Allowance));

        public bool FriendsEmpty => Friends.Count == 0;
        public bool RequestsEmpty => Requests.Count == 0;
        public bool InvitationsEmpty => Invitations.Count == 0;
        public bool SavesEmpty => Saves.Count == 0;
        public bool ModsEmpty => Mods.Count == 0;
        public bool NewsEmpty => NewsFiles.Count == 0;

        // ---- account ----

        /// <summary>Pull everything the account pages show, then redraw them.</summary>
        public async Task RefreshAsync()
        {
            if (!SignedIn)
            {
                ProjectSignedOut();

                return;
            }

            await Guarded(async () =>
            {
                await OpenPakAccount.Instance.RefreshAsync(_cancellation.Token);
                await OpenPakSession.Instance.RefreshInvitationsAsync(_cancellation.Token);

                await ProjectAsync();
            });
        }

        public async Task SignOutAsync()
        {
            await Guarded(async () =>
            {
                await OpenPakApi.Instance.SignOutAsync(_cancellation.Token);

                OpenPakAccount.Instance.Stop();

                ProjectSignedOut();

                Message = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignOutDone];
            });
        }

        // ---- friends ----

        public async Task AddFriendAsync()
        {
            string code = FriendCodeEntry?.Trim();

            if (string.IsNullOrEmpty(code))
            {
                Message = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_FriendsNoCode];

                return;
            }

            await Guarded(async () =>
            {
                // The website's own route resolves the code through the adapter, so this works
                // whether or not the other person is on a Switch.
                string failure = await OpenPakApi.Instance.SendFriendRequestAsync(code, _cancellation.Token);

                Message = failure ?? LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_FriendsAdded, code);

                if (failure == null)
                {
                    FriendCodeEntry = string.Empty;

                    await OpenPakAccount.Instance.RefreshAsync(_cancellation.Token);
                    await ProjectAsync();
                }
            });
        }

        public Task AcceptRequestAsync(OpenPakRequestModel request)
            => MutateAsync(() => OpenPakApi.Instance.AcceptFriendAsync(request.AccountId, _cancellation.Token));

        /// <summary>Declining a request and removing a friend are the same call to the core.</summary>
        public Task DeclineRequestAsync(OpenPakRequestModel request)
            => MutateAsync(() => OpenPakApi.Instance.RemoveFriendAsync(request.AccountId, _cancellation.Token));

        public Task RemoveFriendAsync(OpenPakFriendModel friend)
            => MutateAsync(() => OpenPakApi.Instance.RemoveFriendAsync(friend.AccountId, _cancellation.Token));

        public Task BlockFriendAsync(OpenPakFriendModel friend)
            => MutateAsync(() => OpenPakApi.Instance.BlockAsync(friend.AccountId, _cancellation.Token));

        /// <summary>
        /// Decline the core's, dismiss the native one: a native invitation has no decline to send,
        /// only read state, and the core has never heard of its id.
        /// </summary>
        public Task DeclineInvitationAsync(OpenPakInvitationModel invitation)
            => OpenPakSession.Instance.Invitations.Any(native => native.InvitationId == invitation.InvitationId)
                ? MutateAsync(async () =>
                {
                    await OpenPakSession.Instance.DismissInvitationAsync(invitation.InvitationId, _cancellation.Token);

                    return null;
                })
                : MutateAsync(() => OpenPakApi.Instance.DeclineInvitationAsync(invitation.InvitationId, _cancellation.Token));

        // ---- cloud saves ----

        public async Task RefreshSavesAsync()
        {
            if (!SignedIn)
            {
                return;
            }

            await Guarded(async () =>
            {
                (IReadOnlyList<OpenPakSave> saves, OpenPakSaveUsage usage) =
                    await OpenPakApi.Instance.SavesAsync(_cancellation.Token);

                _usage = usage;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Saves.Clear();

                    foreach (OpenPakSave save in saves)
                    {
                        Saves.Add(new OpenPakSaveModel(save, NameOf(save.TitleId)));
                    }

                    OnPropertyChanged(nameof(SavesEmpty));
                    OnPropertyChanged(nameof(UsageText));
                });
            });
        }

        /// <summary>
        /// Pull the newest cloud save into the title's savedata, keeping what was there.
        ///
        /// The local copy is moved aside rather than deleted: a save is the only thing in this
        /// whole stack that cannot be fetched again if it turns out the cloud had the older one.
        /// </summary>
        public async Task DownloadSaveAsync(ApplicationData application)
        {
            if (application == null)
            {
                return;
            }

            await Guarded(async () =>
            {
                OpenPakSaveDownload download = await OpenPakApi.Instance.DownloadSaveAsync(
                    "switch", application.IdString, _cancellation.Token);

                if (download == null)
                {
                    Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_SavesNoCloud, application.Name);

                    return;
                }

                if (!ApplicationHelper.TryGetUserSaveDirectory(application, out string directory))
                {
                    Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_SavesNoLocal, application.Name);

                    return;
                }

                Backup(directory);

                SaveArchive.Unpack(download.Data, directory);

                OpenPakSaves.Remember(directory, download.Version);

                Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_SavesDownloaded, application.Name);
            });
        }

        public async Task UploadSaveAsync(ApplicationData application)
        {
            if (application == null)
            {
                return;
            }

            await Guarded(async () =>
            {
                if (!ApplicationHelper.TryGetUserSaveDirectory(application, out string directory) ||
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_SavesNoLocal, application.Name);

                    return;
                }

                byte[] packed = SaveArchive.Pack(directory);

                OpenPakSaveModel existing = Saves.FirstOrDefault(save =>
                    save.TitleId.Equals(application.IdString, StringComparison.OrdinalIgnoreCase));

                (string failure, string version) = await OpenPakApi.Instance.UploadSaveAsync("switch", application.IdString,
                    packed, existing?.NewestVersion, Environment.MachineName, _cancellation.Token);

                Message = failure ?? LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_SavesUploaded, application.Name);

                if (failure == null)
                {
                    OpenPakSaves.Remember(directory, version);

                    await RefreshSavesAsync();
                }
            });
        }

        // ---- mods ----

        public async Task RefreshModsAsync()
        {
            if (SelectedTitle == null)
            {
                return;
            }

            await Guarded(async () =>
            {
                IReadOnlyList<OpenPakMod> mods = await OpenPakApi.Instance.ModsAsync(
                    SelectedTitle.IdString, _cancellation.Token);

                IReadOnlyList<OpenPakMod> favourites = SignedIn
                    ? await OpenPakApi.Instance.FavouriteModsAsync(_cancellation.Token)
                    : [];

                HashSet<string> favourite = [.. favourites.Select(mod => mod.Id)];

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Mods.Clear();

                    foreach (OpenPakMod mod in mods)
                    {
                        Mods.Add(new OpenPakModModel(mod, favourite.Contains(mod.Id),
                            OpenPakMods.IsInstalled(SelectedTitle.IdString, mod)));
                    }

                    OnPropertyChanged(nameof(ModsEmpty));
                });
            });
        }

        public async Task InstallModAsync(OpenPakModModel mod)
        {
            await Guarded(async () =>
            {
                byte[] package = await OpenPakApi.Instance.ModPackageAsync(mod.Mod, _cancellation.Token);

                if (package == null)
                {
                    // Null here is either a failed download or a hash that did not match, and the
                    // second is the one worth naming: it means the bytes were not what the
                    // catalogue published.
                    Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_ModsInstallFailed, mod.Name);

                    return;
                }

                if (!OpenPakMods.Install(SelectedTitle.IdString, mod.Mod, package, out string failure))
                {
                    Message = failure;

                    return;
                }

                mod.Installed = true;

                Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_ModsInstalledToast, mod.Name);
            });
        }

        public async Task FavouriteModAsync(OpenPakModModel mod)
        {
            if (!SignedIn)
            {
                Message = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_NeedSignIn];

                return;
            }

            await Guarded(async () =>
            {
                if (await OpenPakApi.Instance.FavouriteModAsync(mod.Id, !mod.Favourite, _cancellation.Token))
                {
                    mod.Favourite = !mod.Favourite;
                }
            });
        }

        // ---- news ----

        public async Task RefreshNewsAsync()
        {
            if (SelectedTitle == null)
            {
                return;
            }

            await Guarded(async () =>
            {
                OpenPakNewsManifest manifest = await OpenPakApi.Instance.NewsManifestAsync(
                    SelectedTitle.IdString, _cancellation.Token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NewsFiles.Clear();

                    foreach (OpenPakNewsFile file in manifest?.Files ?? [])
                    {
                        NewsFiles.Add(new OpenPakNewsFileModel(file));
                    }

                    NewsValidFrom = manifest == null
                        ? string.Empty
                        : LocaleManager.Instance.UpdateAndGetDynamicValue(
                            LocaleKeys.Dialog_OpenPak_NewsValidFrom, manifest.ValidFrom.ToLocalTime());

                    OnPropertyChanged(nameof(NewsEmpty));
                    OnPropertyChanged(nameof(NewsValidFrom));
                });
            });
        }

        public string NewsValidFrom { get; private set; } = string.Empty;

        /// <summary>Write the title's current news dataset into a directory, file by file.</summary>
        public async Task SaveNewsAsync(string directory)
        {
            await Guarded(async () =>
            {
                int written = 0;

                foreach (OpenPakNewsFileModel file in NewsFiles)
                {
                    byte[] data = await OpenPakApi.Instance.NewsFileAsync(file.File, _cancellation.Token);

                    if (data == null)
                    {
                        continue;
                    }

                    string target = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    await File.WriteAllBytesAsync(target, data, _cancellation.Token);

                    written++;
                }

                Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_NewsSaved, written, directory);
            });
        }

        // ---- status ----

        public async Task RefreshStatusAsync()
        {
            await Guarded(async () =>
            {
                OpenPakStatus status = await OpenPakApi.Instance.StatusAsync(_cancellation.Token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TitlePopulations.Clear();
                    NetworkPopulations.Clear();

                    if (status == null)
                    {
                        PlayersOnline = LocaleManager.Instance.UpdateAndGetDynamicValue(
                            LocaleKeys.Dialog_OpenPak_Unreachable, OpenPakConfig.WebsiteUrl);

                        OnPropertyChanged(nameof(PlayersOnline));

                        return;
                    }

                    PlayersOnline = LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_StatusPlayers, status.PlayersOnline);

                    foreach (OpenPakPopulation population in status.Titles)
                    {
                        TitlePopulations.Add(new OpenPakPopulationModel(NameOf(population.Key), population.Players));
                    }

                    foreach (OpenPakPopulation population in status.Networks)
                    {
                        NetworkPopulations.Add(new OpenPakPopulationModel(population.Key, population.Players));
                    }

                    OnPropertyChanged(nameof(PlayersOnline));
                });
            });
        }

        // ---- plumbing ----

        /// <summary>A mutation, then a refresh: the list a person is looking at must show the result.</summary>
        private async Task MutateAsync(Func<Task<string>> mutation)
        {
            await Guarded(async () =>
            {
                Message = await mutation();

                await OpenPakAccount.Instance.RefreshAsync(_cancellation.Token);

                await ProjectAsync();
            });
        }

        /// <summary>
        /// Run something that touches the network with the page marked busy, and never let it
        /// throw into the UI thread: a failed request is a message, not a crash dialog.
        /// </summary>
        private async Task Guarded(Func<Task> work)
        {
            if (Busy)
            {
                return;
            }

            Busy = true;

            try
            {
                await work();
            }
            catch (OperationCanceledException)
            {
                // The window closed underneath it.
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] {exception.Message}");

                Message = exception.Message;
            }
            finally
            {
                Busy = false;
            }
        }

        private void OnAccountChanged() => Dispatcher.UIThread.Post(() => _ = ProjectAsync());

        /// <summary>Copy the cached account onto the observable collections the pages bind to.</summary>
        private async Task ProjectAsync()
        {
            OpenPakAccount account = OpenPakAccount.Instance;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Friends.Clear();

                foreach (OpenPakFriend friend in account.Friends)
                {
                    Friends.Add(new OpenPakFriendModel(friend, NameOf(friend.TitleId)));
                }

                Requests.Clear();

                foreach (OpenPakRequest request in account.Requests)
                {
                    Requests.Add(new OpenPakRequestModel(request));
                }

                Invitations.Clear();

                // Two sources, one list: the core knows the invitations this network arranged,
                // and the native inbox holds the ones a console sent, which the core never sees.
                foreach (OpenPakInvitation invitation in
                    account.Invitations.Concat(OpenPakSession.Instance.Invitations))
                {
                    Invitations.Add(new OpenPakInvitationModel(invitation, NameOf(invitation.TitleId)));
                }

                Redraw();
            });

            await LoadAvatarAsync();
        }

        private void ProjectSignedOut() => Dispatcher.UIThread.Post(() =>
        {
            Friends.Clear();
            Requests.Clear();
            Invitations.Clear();
            Saves.Clear();

            Avatar = null;

            Redraw();
        });

        private void Redraw()
        {
            OnPropertyChanged(nameof(SignedIn));
            OnPropertyChanged(nameof(SignedOut));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(FriendCode));
            OnPropertyChanged(nameof(ServerAddress));
            OnPropertyChanged(nameof(SwitchPid));
            OnPropertyChanged(nameof(LinkedConsoles));
            OnPropertyChanged(nameof(FriendsEmpty));
            OnPropertyChanged(nameof(RequestsEmpty));
            OnPropertyChanged(nameof(InvitationsEmpty));
            OnPropertyChanged(nameof(SavesEmpty));
        }

        private async Task LoadAvatarAsync()
        {
            // The account cache warmed on its own timer, so a dialog opening after sign-in can
            // paint the picture immediately; only a cache that has nothing yet costs a round trip.
            byte[] cached = OpenPakAccount.Instance.AvatarData;

            if (cached != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Avatar = new Bitmap(new MemoryStream(cached)));

                return;
            }

            string url = OpenPakAccount.Instance.Profile?.AvatarUrl;

            if (url == null)
            {
                return;
            }

            byte[] image = await OpenPakApi.Instance.ImageAsync(url, _cancellation.Token);

            if (image == null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => Avatar = new Bitmap(new MemoryStream(image)));
        }

        /// <summary>A title id as the person knows it, falling back to the id when it is not installed.</summary>
        private string NameOf(string titleId)
        {
            if (string.IsNullOrEmpty(titleId))
            {
                return string.Empty;
            }

            ApplicationData application = Titles.FirstOrDefault(title =>
                title.IdString.Equals(titleId, StringComparison.OrdinalIgnoreCase));

            return application?.Name ?? titleId.ToUpperInvariant();
        }

        /// <summary>Move a save directory aside before it is overwritten, keeping one generation.</summary>
        private static void Backup(string directory)
        {
            string backup = directory.TrimEnd(Path.DirectorySeparatorChar) + ".openpak-backup";

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            if (Directory.Exists(directory))
            {
                Directory.Move(directory, backup);
            }

            Directory.CreateDirectory(directory);
        }

        private static string Bytes(long value)
        {
            string[] units = ["B", "KB", "MB", "GB"];

            double size = value;
            int unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.#} {units[unit]}";
        }

        public void Dispose()
        {
            OpenPakAccount.Instance.Changed -= OnAccountChanged;

            _cancellation.Cancel();
            _cancellation.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
