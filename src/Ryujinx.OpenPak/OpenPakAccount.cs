using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The signed-in account as everything else sees it: one cache, refreshed in the background,
    /// read synchronously by whoever asks.
    ///
    /// The reason it is a cache and not a set of calls is the guest. `friend:u` commands are IPC
    /// handlers on the game's own thread — a title asking for its friend list expects an answer in
    /// microseconds, and a title that is answered with an HTTP round trip stutters or times out.
    /// So the network happens here, on a timer, and the sysmodule reads whatever the last refresh
    /// left behind.
    /// </summary>
    public sealed class OpenPakAccount
    {
        public static OpenPakAccount Instance { get; } = new();

        private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);

        private readonly Lock _lock = new();

        private CancellationTokenSource _refresh;
        private IReadOnlyList<OpenPakFriend> _friends = [];
        private IReadOnlyList<OpenPakRequest> _requests = [];
        private IReadOnlyList<OpenPakInvitation> _invitations = [];
        private byte[] _avatar;
        private bool _presenceBaselineTaken;

        private OpenPakAccount()
        {
            OpenPakApi.Instance.SignedInChanged += () =>
            {
                if (OpenPakApi.Instance.SignedIn)
                {
                    Start();
                }
                else
                {
                    Stop();
                }
            };
        }

        /// <summary>Raised whenever the cached view changed, on a background thread.</summary>
        public event Action Changed;

        /// <summary>
        /// Raised on the refresh thread when a friend has just appeared on the network without a
        /// title. Presence that was already known when the account signed in announces nothing:
        /// the first refresh after Start is the baseline, not news.
        /// </summary>
        public event Action<OpenPakFriend> FriendCameOnline;

        /// <summary>Raised like <see cref="FriendCameOnline"/>, for a friend now inside a title.</summary>
        public event Action<OpenPakFriend> FriendStartedPlaying;

        /// <summary>The account's own profile, or null before the first refresh.</summary>
        public OpenPakProfile Profile { get; private set; }

        /// <summary>The Switch identity the adapter minted for this account, or null.</summary>
        public OpenPakSwitchIdentity Identity { get; private set; }

        /// <summary>Whether someone is signed in, whatever the caches hold.</summary>
        public bool SignedIn => OpenPakApi.Instance.SignedIn;

        /// <summary>The name to show for this account, or null when there is nothing to show.</summary>
        public string DisplayName => Profile?.DisplayName ?? Identity?.Username;

        /// <summary>The friend code other players would type, or null.</summary>
        public string FriendCode => Identity?.FriendCode ?? Profile?.FriendCode;

        /// <summary>The friend list as of the last refresh. Never null.</summary>
        public IReadOnlyList<OpenPakFriend> Friends
        {
            get
            {
                lock (_lock)
                {
                    return _friends;
                }
            }
        }

        /// <summary>Pending requests in both directions as of the last refresh. Never null.</summary>
        public IReadOnlyList<OpenPakRequest> Requests
        {
            get
            {
                lock (_lock)
                {
                    return _requests;
                }
            }
        }

        /// <summary>Invitations waiting as of the last refresh. Never null.</summary>
        public IReadOnlyList<OpenPakInvitation> Invitations
        {
            get
            {
                lock (_lock)
                {
                    return _invitations;
                }
            }
        }

        /// <summary>
        /// The account's picture as of the last refresh, or null. Cached here so a dialog can
        /// paint it the moment it opens instead of waiting on its own round trip.
        /// </summary>
        public byte[] AvatarData
        {
            get
            {
                lock (_lock)
                {
                    return _avatar;
                }
            }
        }

        /// <summary>Begin refreshing, if signed in and not already running.</summary>
        public void Start()
        {
            if (!OpenPakApi.Instance.SignedIn || _refresh != null)
            {
                return;
            }

            _refresh = new CancellationTokenSource();

            CancellationToken token = _refresh.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await RefreshAsync(token);

                    try
                    {
                        await Task.Delay(_refreshInterval, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, token);
        }

        /// <summary>Stop refreshing and forget everything cached. Signing out must leave nothing.</summary>
        public void Stop()
        {
            _refresh?.Cancel();
            _refresh?.Dispose();
            _refresh = null;

            Profile = null;
            Identity = null;

            lock (_lock)
            {
                _friends = [];
                _requests = [];
                _invitations = [];
                _avatar = null;
            }

            _presenceBaselineTaken = false;

            Changed?.Invoke();
        }

        /// <summary>
        /// Pull everything once, now. Safe to call from a dialog that has just changed something
        /// and wants the change on screen without waiting for the timer.
        /// </summary>
        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            if (!OpenPakApi.Instance.SignedIn)
            {
                return;
            }

            // A token restored from the password store signs nobody in loudly, so nothing else
            // may have started the timer; a refresh reaching here means somebody is listening.
            Start();

            try
            {
                OpenPakApi api = OpenPakApi.Instance;

                Profile = await api.MeAsync(cancellationToken) ?? Profile;

                OpenPakSwitchIdentity identity = await api.SwitchIdentityAsync(cancellationToken);

                if (identity != null)
                {
                    Identity = identity;
                }

                IReadOnlyList<OpenPakFriend> friends = await api.FriendsAsync(cancellationToken);
                IReadOnlyList<OpenPakRequest> requests = await api.RequestsAsync(cancellationToken);
                IReadOnlyList<OpenPakInvitation> invitations = await api.InvitationsAsync(cancellationToken);

                byte[] avatar = Profile?.AvatarUrl is null ? null : await api.ImageAsync(Profile.AvatarUrl, cancellationToken);

                List<OpenPakFriend> cameOnline = [];
                List<OpenPakFriend> startedPlaying = [];

                lock (_lock)
                {
                    // The adapter knows pids and the core does not, so the graph the guest sees is
                    // the adapter's when it answered, and the core's otherwise. Either is a
                    // complete list; only one of them can be handed to a game.
                    IReadOnlyList<OpenPakFriend> previous = _friends;

                    _friends = identity is { Friends.Count: > 0 } ? identity.Friends : friends;
                    _requests = requests.Count > 0 ? requests : identity?.Requests ?? [];
                    _invitations = invitations;

                    if (avatar != null)
                    {
                        _avatar = avatar;
                    }

                    // Presence is only news when it changed while somebody watched. The first
                    // refresh after signing in shows the network as it already was.
                    if (_presenceBaselineTaken)
                    {
                        foreach (OpenPakFriend friend in _friends)
                        {
                            if (!friend.Online)
                            {
                                continue;
                            }

                            OpenPakFriend was = previous.FirstOrDefault(other => other.AccountId == friend.AccountId);

                            bool newlyOnline = was is null || !was.Online;
                            bool newlyPlaying = !string.IsNullOrEmpty(friend.TitleId) &&
                                (newlyOnline || friend.TitleId != was.TitleId);

                            if (newlyPlaying)
                            {
                                startedPlaying.Add(friend);
                            }
                            else if (newlyOnline)
                            {
                                cameOnline.Add(friend);
                            }
                        }
                    }

                    _presenceBaselineTaken = true;
                }

                Changed?.Invoke();

                foreach (OpenPakFriend friend in cameOnline)
                {
                    FriendCameOnline?.Invoke(friend);
                }

                foreach (OpenPakFriend friend in startedPlaying)
                {
                    FriendStartedPlaying?.Invoke(friend);
                }
            }
            catch (OperationCanceledException)
            {
                // Signed out, or the window closed. Nothing to say about it.
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.Application, $"[OpenPak] Refresh failed: {exception.Message}");
            }
        }
    }
}
