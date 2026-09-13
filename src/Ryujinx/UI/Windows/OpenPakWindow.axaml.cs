using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.UI.ViewModels;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Windows
{
    /// <summary>
    /// Everything OpenPak, in one window with the pages down the side.
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

            Closed += (_, _) => _viewModel.Dispose();
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

            await ShowAsync(window);

            // Opening at the page the pane already starts on changes no selection, so nothing
            // would otherwise load it: pull what this page shows, now that the window is up.
            await window.RefreshPage(page);
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
        /// <summary>Show the page, and fetch what it needs on the way in.</summary>
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
