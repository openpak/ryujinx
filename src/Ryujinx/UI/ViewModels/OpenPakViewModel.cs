using Avalonia.Media;
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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
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
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ApplicationLibrary _library;

        private int _inFlight;
        private string _modsTitle;
        private string _newsTitle;
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

        /// <summary>Every service the status box watches, as it last found them.</summary>
        public ObservableCollection<OpenPakServiceModel> Services { get; } = [];

        /// <summary>This machine's own session, line by line.</summary>
        public ObservableCollection<OpenPakSessionModel> SessionState { get; } = [];

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

        /// <summary>The status box's one-line verdict, or why there is none.</summary>
        public string HealthHeadline { get; private set; } = string.Empty;

        public string HealthSummary { get; private set; } = string.Empty;

        /// <summary>Green for working, red for an outage, grey for no answer at all.</summary>
        public IBrush HealthBrush { get; private set; } = OpenPakBrushes.Down;

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

                // Built here rather than inside the dispatcher call: every row reads its local
                // save directory off the disk and decodes an icon, and a page of those is not
                // work to do on the thread that is drawing the page.
                List<OpenPakSaveModel> rows = [];

                foreach (OpenPakSave save in saves.OrderBy(save => save.TitleId, StringComparer.OrdinalIgnoreCase))
                {
                    ApplicationData application = ApplicationOf(save.TitleId);

                    rows.Add(new OpenPakSaveModel(save, application, LocalDetailOf(application)));
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Saves.Clear();

                    foreach (OpenPakSaveModel row in rows)
                    {
                        Saves.Add(row);
                    }

                    OnPropertyChanged(nameof(SavesEmpty));
                    OnPropertyChanged(nameof(UsageText));
                });

                // The catalogue's picture for the titles this machine does not have, after the
                // list is up and off this call's turn, as the friends page does with avatars.
                _ = LoadSaveIconsAsync(rows);
            });
        }

        /// <summary>
        /// The catalogue's icon for every row that had none of its own. Not awaited by the refresh:
        /// a save manager is readable without pictures, and its buttons should not wait for them.
        /// </summary>
        private async Task LoadSaveIconsAsync(IReadOnlyList<OpenPakSaveModel> rows)
        {
            try
            {
                await Task.WhenAll(rows.Select(row => row.LoadIconAsync(_cancellation.Token)));
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] {exception.Message}");
            }
        }

        /// <summary>
        /// Clear a title's cloud save: every stored version, one call each.
        ///
        /// The server deletes versions and nothing else — there is no route that empties a title
        /// in one go — so this is the whole of it. Nothing on this machine is touched: the local
        /// save is the copy that cannot be fetched again, and the page asked about the cloud one.
        /// </summary>
        public async Task DeleteSaveAsync(OpenPakSaveModel row)
        {
            if (row == null)
            {
                return;
            }

            await Guarded(async () =>
            {
                foreach (OpenPakSaveVersion version in row.Save.Versions)
                {
                    string failure = await OpenPakApi.Instance.DeleteSaveVersionAsync(version.Id, _cancellation.Token);

                    if (failure != null)
                    {
                        Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                            LocaleKeys.Dialog_OpenPak_SavesDeleteFailed, row.TitleName, failure);

                        return;
                    }
                }

                Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_SavesDeleted, row.TitleName);
            });

            await RefreshSavesAsync();
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

            await RefreshSavesAsync();
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

        public async Task RefreshModsAsync(bool force = false)
        {
            if (SelectedTitle == null || (!force && _modsTitle == SelectedTitle.IdString))
            {
                return;
            }

            _modsTitle = SelectedTitle.IdString;

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

        public async Task RefreshNewsAsync(bool force = false)
        {
            if (SelectedTitle == null || (!force && _newsTitle == SelectedTitle.IdString))
            {
                return;
            }

            _newsTitle = SelectedTitle.IdString;

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
                // Three answers from three machines, asked for together: the site's numbers, the
                // status box's verdict on every service, and whether the console edge picks up.
                // The status box is deliberately not the site, so one being down still answers.
                Task<OpenPakStatus> statusTask = OpenPakApi.Instance.StatusAsync(_cancellation.Token);
                Task<OpenPakHealth> healthTask = OpenPakApi.Instance.HealthAsync(_cancellation.Token);
                Task<long> edgeTask = ProbeEdgeAsync(_cancellation.Token);
                Task<OpenPakSessionModel> natTask = NatRowAsync(_cancellation.Token);

                await Task.WhenAll(statusTask, healthTask, edgeTask, natTask);

                OpenPakStatus status = statusTask.Result;
                OpenPakHealth health = healthTask.Result;

                List<OpenPakSessionModel> session = SessionRows(edgeTask.Result);

                session.Add(natTask.Result);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Services.Clear();

                    foreach (OpenPakService service in health?.Services ?? [])
                    {
                        Services.Add(new OpenPakServiceModel(service));
                    }

                    SessionState.Clear();

                    foreach (OpenPakSessionModel row in session)
                    {
                        SessionState.Add(row);
                    }

                    HealthHeadline = health?.Headline ?? LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_StatusNoHealth, OpenPakConfig.StatusUrl);

                    HealthSummary = health == null
                        ? string.Empty
                        : health.Summary + (string.IsNullOrEmpty(health.Generated)
                            ? string.Empty
                            : " " + LocaleManager.Instance.UpdateAndGetDynamicValue(
                                LocaleKeys.Dialog_OpenPak_StatusRefreshed, health.Generated));

                    // "some" is the status box's word for a partial outage; anything but "ok" is
                    // something somebody should see, and only a total one is worth alarming red.
                    HealthBrush = health?.State switch
                    {
                        "ok" => OpenPakBrushes.Up,
                        null => OpenPakBrushes.Down,
                        _ => OpenPakBrushes.Bad,
                    };

                    OnPropertyChanged(nameof(HealthHeadline));
                    OnPropertyChanged(nameof(HealthSummary));
                    OnPropertyChanged(nameof(HealthBrush));
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

        private static (DateTime At, OpenPakNatCheck.Result Result)? _nat;

        /// <summary>
        /// The console's NAT type test, run from this machine against the nncs pair the redirect
        /// points a game at. Kept five minutes: the page refreshes every thirty seconds, and a NAT
        /// does not change that often.
        /// </summary>
        private static async Task<OpenPakSessionModel> NatRowAsync(CancellationToken cancellationToken)
        {
            LocaleManager locale = LocaleManager.Instance;

            if (OpenPakNatCheck.Targets(OpenPakNetworkProfileService.Applied) is not { } targets)
            {
                return new OpenPakSessionModel(locale[LocaleKeys.Dialog_OpenPak_StatusNat],
                    locale[LocaleKeys.Dialog_OpenPak_StatusNatNoProfile], false);
            }

            if (_nat is not { } cached || DateTime.UtcNow - cached.At > TimeSpan.FromMinutes(5))
            {
                try
                {
                    _nat = cached = (DateTime.UtcNow, await OpenPakNatCheck.RunAsync(targets.Primary, targets.Secondary, cancellationToken));
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    cached = (DateTime.UtcNow, new OpenPakNatCheck.Result(OpenPakNatCheck.Mapping.Unknown, OpenPakNatCheck.Filtering.Unknown, null));
                }
            }

            OpenPakNatCheck.Result result = cached.Result;

            return new OpenPakSessionModel(locale[LocaleKeys.Dialog_OpenPak_StatusNat],
                result.Mapping == OpenPakNatCheck.Mapping.Unknown
                    ? locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusNatNoAnswer, targets.Primary, targets.Secondary)
                    : locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusNatType, result.Type,
                        result.Mapping, result.Filtering, result.External),
                result.Type is 'A' or 'B');
        }

        /// <summary>
        /// Whether the console-facing edge picks up, and how long it took, in milliseconds. -1
        /// when it did not answer, and -2 when there is nothing configured to ask.
        ///
        /// A plain TCP connect, not a request: everything that edge serves is under Nintendo's own
        /// hostnames behind the OpenPak CA, and a socket that opens is the whole of what this page
        /// can honestly claim from out here.
        /// </summary>
        private static async Task<long> ProbeEdgeAsync(CancellationToken cancellationToken)
        {
            string address = OpenPakConfig.ResolvedConsoleServer;

            if (string.IsNullOrEmpty(address))
            {
                return -2;
            }

            int colon = address.LastIndexOf(':');

            string host = colon > 0 ? address[..colon] : address;
            int port = colon > 0 && int.TryParse(address[(colon + 1)..], out int parsed) ? parsed : 443;

            try
            {
                using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                giveUp.CancelAfter(TimeSpan.FromSeconds(4));

                long started = Stopwatch.GetTimestamp();

                using TcpClient client = new();

                await client.ConnectAsync(host, port, giveUp.Token);

                return (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// What this machine's own session looks like: the parts that can be broken here while
        /// every service on the network is up, which nothing else on this page would show.
        /// </summary>
        private static List<OpenPakSessionModel> SessionRows(long edge)
        {
            LocaleManager locale = LocaleManager.Instance;
            OpenPakSession session = OpenPakSession.Instance;

            bool signedIn = OpenPakApi.Instance.SignedIn;
            string name = OpenPakAccount.Instance.DisplayName;

            string address = OpenPakConfig.ResolvedConsoleServer;

            string presence = session.PresenceState;

            int waiting = session.Invitations.Count;

            return
            [
                new OpenPakSessionModel(locale[LocaleKeys.Dialog_OpenPak_StatusAccount],
                    signedIn
                        ? locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusAccountSignedIn,
                            name ?? locale[LocaleKeys.Dialog_OpenPak_None])
                        : locale[LocaleKeys.Dialog_OpenPak_StatusAccountSignedOut],
                    signedIn),

                new OpenPakSessionModel(locale[LocaleKeys.Dialog_OpenPak_StatusLink],
                    session.IsLinked
                        ? locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusLinked,
                            session.Nickname, session.FriendCode ?? locale[LocaleKeys.Dialog_OpenPak_None])
                        : locale[LocaleKeys.Dialog_OpenPak_StatusNotLinked],
                    session.IsLinked),

                new OpenPakSessionModel(locale[LocaleKeys.Dialog_OpenPak_StatusEdge],
                    edge switch
                    {
                        -2 => locale[LocaleKeys.Dialog_OpenPak_StatusEdgeNone],
                        -1 => locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusEdgeDown, address),
                        _ => locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusEdgeUp, address, edge),
                    },
                    edge >= 0),

                new OpenPakSessionModel(locale[LocaleKeys.Dialog_OpenPak_StatusPresence],
                    presence == null
                        ? locale[LocaleKeys.Dialog_OpenPak_StatusPresenceNone]
                        : locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusPresencePublished, presence),
                    presence != null),

                // Said plainly rather than dressed up as a connection: a console is pushed its
                // invitations, and this asks for them, which is why one can arrive late here.
                new OpenPakSessionModel(locale[LocaleKeys.Dialog_OpenPak_StatusNotifications],
                    !session.Beating
                        ? locale[LocaleKeys.Dialog_OpenPak_StatusNotificationsOff]
                        : session.PushConnected
                            ? locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusNotificationsPushed, waiting)
                            : locale.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_StatusNotificationsPolled, waiting),
                    session.Beating),
            ];
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
            // Queued, not dropped: a page that asks for two things on the way in gets both.
            if (Interlocked.Increment(ref _inFlight) == 1)
            {
                Busy = true;
            }

            try
            {
                await _gate.WaitAsync(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Decrement(ref _inFlight);

                return;
            }

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
                _gate.Release();

                if (Interlocked.Decrement(ref _inFlight) == 0)
                {
                    Busy = false;
                }
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

                // Whoever is there to play with first; OrderBy is stable, so the server's order holds within each.
                foreach (OpenPakFriend friend in account.Friends.OrderByDescending(friend => friend.Online))
                {
                    Friends.Add(new OpenPakFriendModel(friend, NameOf(friend.TitleId), ApplicationOf(friend.TitleId)));
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

            // After the list is on screen, not before: a row with an initial in it beats an empty
            // page while a dozen pictures come down. Not awaited either, because this runs inside
            // the call that holds the page busy — a friend list of twenty would leave every button
            // on the page disabled until the twentieth picture arrived.
            _ = LoadFriendAvatarsAsync(Friends.ToArray());
            _ = LoadFriendGamesAsync(Friends.ToArray());
        }

        /// <summary>
        /// Every friend's picture, off the page's own turn. Each row paints itself when its own
        /// fetch lands, so a slow one costs nobody else their picture.
        /// </summary>
        private async Task LoadFriendAvatarsAsync(IReadOnlyList<OpenPakFriendModel> friends)
        {
            try
            {
                await Task.WhenAll(friends.Select(friend => friend.LoadAvatarAsync(_cancellation.Token)));
            }
            catch (Exception exception)
            {
                // A picture that did not arrive is a row with an initial in it, and nothing else.
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] {exception.Message}");
            }
        }

        /// <summary>
        /// Each playing friend's game, resolved off the page's own turn: the catalogue's name and
        /// icon for whoever this machine's own library could not already name.
        /// </summary>
        private async Task LoadFriendGamesAsync(IReadOnlyList<OpenPakFriendModel> friends)
        {
            try
            {
                await Task.WhenAll(friends.Select(friend => friend.LoadGameAsync(_cancellation.Token)));
            }
            catch (Exception exception)
            {
                // A game that could not be named stays "Playing" with no name, not this id.
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] {exception.Message}");
            }
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

        /// <summary>
        /// A title id as this machine's own library names it, or empty when it is not installed
        /// here. Never the id itself: a hex string is not a name to anybody looking at this page,
        /// and a caller with a catalogue to ask (<see cref="OpenPakFriendModel.LoadGameAsync"/>)
        /// falls back to that before it falls back to silence.
        /// </summary>
        private string NameOf(string titleId)
            => string.IsNullOrEmpty(titleId) ? string.Empty : ApplicationOf(titleId)?.Name ?? string.Empty;

        /// <summary>The installed title with this id, or null. The picture comes from here too.</summary>
        private ApplicationData ApplicationOf(string titleId)
            => string.IsNullOrEmpty(titleId)
                ? null
                : Titles.FirstOrDefault(title => title.IdString.Equals(titleId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The local side of a cloud save, so a conflict is a comparison and not a guess: when
        /// the files were last written, and which cloud version they were last in step with.
        /// </summary>
        private static string LocalDetailOf(ApplicationData application)
        {
            if (application == null || !ApplicationHelper.TryGetUserSaveDirectory(application, out string directory))
            {
                return LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SavesNoLocalCopy];
            }

            DateTime? written = OpenPakSaves.LastWrite(directory);

            if (written == null)
            {
                return LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SavesNoLocalCopy];
            }

            string synced = OpenPakSaves.Read(directory);

            return synced == null
                ? LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_SavesLocalNever, $"{written:g}")
                : LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_SavesLocal, $"{written:g}", synced);
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

        /// <summary>A size as a person reads it. Shared with the rows, which show the same figures.</summary>
        internal static string Bytes(long value)
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
            _gate.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
