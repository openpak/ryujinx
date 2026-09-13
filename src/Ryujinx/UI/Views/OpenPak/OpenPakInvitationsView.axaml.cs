using Avalonia.Controls;
using Avalonia.Interactivity;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using System;
using System.Linq;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// Invitations waiting for this account.
    ///
    /// Accepting one launches the title and nothing more: the invitation itself is delivered to
    /// the guest by the adapter, through the same channel a console receives it on, so there is
    /// nothing for the emulator to hand over. This read never consumes one either — the endpoint
    /// is deliberately non-consuming, so looking at the list here cannot take an invitation away
    /// from the console that has to receive it.
    /// </summary>
    public partial class OpenPakInvitationsView : UserControl
    {
        public OpenPakInvitationsView()
        {
            InitializeComponent();

            RefreshButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshAsync();
                }
            };
        }

        private OpenPakViewModel Model => DataContext as OpenPakViewModel;

        private async void OnPlay(object sender, RoutedEventArgs args)
        {
            if (Model == null || (sender as Control)?.DataContext is not OpenPakInvitationModel invitation)
            {
                return;
            }

            ApplicationData application = Model.Titles.FirstOrDefault(title =>
                title.IdString.Equals(invitation.TitleId, StringComparison.OrdinalIgnoreCase));

            if (application == null)
            {
                NotificationHelper.ShowWarning(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_InvitationsNotInstalled, invitation.TitleName));

                return;
            }

            await RyujinxApp.MainWindow.ViewModel.LoadApplication(application);
        }

        private async void OnDecline(object sender, RoutedEventArgs args)
        {
            if (Model != null && (sender as Control)?.DataContext is OpenPakInvitationModel invitation)
            {
                await Model.DeclineInvitationAsync(invitation);
            }
        }
    }
}
