using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Views.Dialog;
using Ryujinx.OpenPak;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Windows
{
    /// <summary>
    /// Everything OpenPak, in one window with the pages down the side (UX spec §3.6).
    ///
    /// One window rather than seven dialogs because it is one account: the friends list and the
    /// invitations and the saves are the same person's, and a menu that opened seven separate
    /// dialogs would make somebody close one to look at the next. The menu entries still name
    /// each page — they open this at that page.
    /// </summary>
    public partial class OpenPakWindow : StyleableAppWindow
    {
        /// <summary>Which page a menu entry asked for; also which page the pane starts on.</summary>
        public enum Page
        {
            Account,
            Friends,
            Invitations,
            Saves,
            Mods,
            News,
            Status,
        }

        private OpenPakViewModel _viewModel;

        // Friends and invitations move on their own; while the window is open it keeps up with
        // them every thirty seconds, as every OpenPak emulator's window does.
        private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(30) };

        public OpenPakWindow() : base(true)
        {
            Title = RyujinxApp.FormatTitle(LocaleKeys.Dialog_OpenPak_Title);

            DataContext = _viewModel = new OpenPakViewModel();

            InitializeComponent();

            DetachPages();
        }

        public OpenPakWindow(ApplicationLibrary library) : base(true)
        {
            Title = RyujinxApp.FormatTitle(LocaleKeys.Dialog_OpenPak_Title);

            DataContext = _viewModel = new OpenPakViewModel(library);

            InitializeComponent();

            DetachPages();

            CloseButton.Click += (_, _) => Close();

            NavPanel.SelectionChanged += OnPageChanged;

            _viewModel.Requests.CollectionChanged += OnRequestsChanged;

            OpenPakApi.Instance.SignedInChanged += OnSignedInChanged;

            _poll.Tick += async (_, _) =>
            {
                if (_viewModel.SignedIn)
                {
                    await _viewModel.RefreshAsync();
                }
            };

            // Ctrl+PgUp / Ctrl+PgDn walk the pages, the keyboard's L and R.
            AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            Opened += (_, _) => _poll.Start();

            Closed += (_, _) =>
            {
                _poll.Stop();

                OpenPakApi.Instance.SignedInChanged -= OnSignedInChanged;
                _viewModel.Requests.CollectionChanged -= OnRequestsChanged;

                _viewModel.Dispose();
            };
        }

        /// <summary>
        /// The pages are declared in the window's own tree so their named fields exist, but the
        /// NavigationView is what shows them, and a control can only have one visual parent: hand
        /// them over by taking them out of the grid first, as SettingsWindow does.
        /// </summary>
        private void DetachPages()
        {
            Pages.Children.Clear();
        }

        /// <summary>Open the window at <paramref name="page"/>, and load what that page shows.</summary>
        public static async Task Show(Page page = Page.Account)
        {
            OpenPakWindow window = new(RyujinxApp.MainWindow.ViewModel.ApplicationLibrary);

            window.Select(page);

            // Opening at the page the pane already starts on changes no selection, so nothing
            // would otherwise load it. ShowAsync is modal and returns on close, so this has to
            // hang off Opened rather than follow the await.
            window.Opened += async (_, _) => await window.RefreshPage(page);

            await ShowAsync(window);
        }

        /// <summary>
        /// The <c>Sign in...</c> every signed-out page offers: the setup dialog for a profile that
        /// never had an account, the sign-in dialog otherwise. The window reloads itself on success.
        /// </summary>
        public static async Task SignInAsync()
        {
            if (OpenPakLinks.Get(OpenPakConfig.ProfileId) == null && RyujinxApp.MainWindow?.AccountManager is { } accounts)
            {
                await OpenPakSetup.RunAsync(accounts, addAccount: false);
            }
            else
            {
                await OpenPakSignInView.Show();
            }
        }

        /// <summary>Fetch everything <paramref name="page"/> displays, once, on demand.</summary>
        private Task RefreshPage(Page page) => page switch
        {
            Page.Account => Task.WhenAll(_viewModel.RefreshAsync(), _viewModel.RefreshStatusAsync()),
            Page.Friends or Page.Invitations => _viewModel.RefreshAsync(),
            Page.Saves => _viewModel.RefreshSavesAsync(),
            Page.Mods => _viewModel.RefreshModsAsync(),
            Page.News => _viewModel.RefreshNewsAsync(),
            Page.Status => _viewModel.RefreshStatusAsync(),
            _ => Task.CompletedTask,
        };

        private Page CurrentPage
            => NavPanel.SelectedItem is FANavigationViewItem { Tag: string tag } && PageFromTag(tag) is { } page ? page : Page.Account;

        /// <summary>Signing in or out changes what every page shows, so all of them load again.</summary>
        private void OnSignedInChanged() => Dispatcher.UIThread.Post(async () =>
        {
            _viewModel.Forget();

            await RefreshPage(CurrentPage);
        });

        private void OnRequestsChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            int incoming = _viewModel.Requests.Count(request => request.Incoming);

            RequestsBadge.IsVisible = incoming > 0;
            RequestsBadgeText.Text = incoming > 99 ? "99+" : incoming.ToString();
        }

        private void OnKeyDown(object sender, KeyEventArgs args)
        {
            if (!args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.Key is not (Key.PageUp or Key.PageDown))
            {
                return;
            }

            int count = Enum.GetValues<Page>().Length;
            int next = ((int)CurrentPage + (args.Key == Key.PageDown ? 1 : count - 1)) % count;

            Select((Page)next);

            args.Handled = true;
        }

        private void Select(Page page)
        {
            string tag = page + "Page";

            NavPanel.SelectedItem = NavPanel.MenuItems
                .OfType<FANavigationViewItem>()
                .FirstOrDefault(item => tag.Equals(item.Tag as string))
                ?? NavPanel.MenuItems.ElementAt(0);
        }

        /// <summary>
        /// Show the page, and fetch what it needs on the way in.
        ///
        /// Per-page rather than all at once: opening the window should not pull every title's mod
        /// catalogue, and a page nobody visits should cost nothing.
        /// </summary>
        private async void OnPageChanged(object sender, FANavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not FANavigationViewItem { Tag: string tag })
            {
                return;
            }

            NavPanel.Content = tag switch
            {
                "AccountPage" => AccountPage,
                "FriendsPage" => FriendsPage,
                "InvitationsPage" => InvitationsPage,
                "SavesPage" => SavesPage,
                "ModsPage" => ModsPage,
                "NewsPage" => NewsPage,
                _ => StatusPage,
            };

            // Show() selects the page before the window exists, and then fetches for itself; a
            // refresh here would be a second identical round trip behind an invisible window.
            if (!IsVisible)
            {
                return;
            }

            if (PageFromTag(tag) is { } page)
            {
                await RefreshPage(page);
            }
        }

        /// <summary>The menu entry's tag as the page it names, or null when it names nothing.</summary>
        private static Page? PageFromTag(string tag) => tag switch
        {
            "AccountPage" => Page.Account,
            "FriendsPage" => Page.Friends,
            "InvitationsPage" => Page.Invitations,
            "SavesPage" => Page.Saves,
            "ModsPage" => Page.Mods,
            "NewsPage" => Page.News,
            "StatusPage" => Page.Status,
            _ => null,
        };
    }
}
