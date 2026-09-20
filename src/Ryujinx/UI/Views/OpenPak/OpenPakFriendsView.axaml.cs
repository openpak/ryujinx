using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// The friend graph, as it is everywhere else: the same list the phone and a linked console
    /// show, because it is the same list.
    /// </summary>
    public partial class OpenPakFriendsView : UserControl
    {
        public OpenPakFriendsView()
        {
            InitializeComponent();

            AddButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.AddFriendAsync();
                }
            };

            RefreshButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshAsync();
                }
            };
        }

        private OpenPakViewModel Model => DataContext as OpenPakViewModel;

        /// <summary>A click on the row opens its detail panel in place; a second click closes it again.</summary>
        private void OnToggleExpand(object sender, TappedEventArgs args)
        {
            if ((sender as Control)?.DataContext is OpenPakFriendModel friend)
            {
                friend.IsExpanded = !friend.IsExpanded;
            }
        }

        private async void OnAccept(object sender, RoutedEventArgs args)
        {
            if (Model != null && (sender as Control)?.DataContext is OpenPakRequestModel request)
            {
                await Model.AcceptRequestAsync(request);
            }
        }

        private async void OnDecline(object sender, RoutedEventArgs args)
        {
            if (Model != null && (sender as Control)?.DataContext is OpenPakRequestModel request)
            {
                await Model.DeclineRequestAsync(request);
            }
        }

        /// <summary>
        /// Removing a friend is asked about first: it is the one action here that cannot be
        /// undone from this window — getting them back needs them to accept again.
        /// </summary>
        private async void OnRemove(object sender, RoutedEventArgs args)
        {
            if (Model == null || (sender as Control)?.DataContext is not OpenPakFriendModel friend)
            {
                return;
            }

            if (await Confirm(LocaleKeys.Dialog_OpenPak_FriendsRemoveConfirm, friend.DisplayName))
            {
                await Model.RemoveFriendAsync(friend);
            }
        }

        private async void OnBlock(object sender, RoutedEventArgs args)
        {
            if (Model == null || (sender as Control)?.DataContext is not OpenPakFriendModel friend)
            {
                return;
            }

            if (await Confirm(LocaleKeys.Dialog_OpenPak_FriendsBlockConfirm, friend.DisplayName))
            {
                await Model.BlockFriendAsync(friend);
            }
        }

        private static async Task<bool> Confirm(LocaleKeys question, string name)
            => await ContentDialogHelper.CreateChoiceDialog(
                LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                LocaleManager.Instance.UpdateAndGetDynamicValue(question, name),
                string.Empty);
    }
}
