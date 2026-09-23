using Avalonia.Controls;
using Avalonia.Interactivity;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.OpenPak;
using Ryujinx.Ava.UI.ViewModels;
using System;
using System.Linq;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// Invitations waiting for this account.
    ///
    /// Joining one that is not for the running game launches the title and nothing more: the
    /// invitation itself is delivered to the guest by the adapter, through the same channel a
    /// console receives it on, so there is nothing for the emulator to hand over. This read never consumes one either — the endpoint
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

        private async void OnSignIn(object sender, RoutedEventArgs args) => await Windows.OpenPakWindow.SignInAsync();

        /// <summary>
        /// Join: handed to the game when it is the one running, otherwise the window closes and
        /// the game starts (the invitation waits in its inbox for it).
        /// </summary>
        private async void OnJoin(object sender, RoutedEventArgs args)
        {
            if (Model == null || (sender as Control)?.DataContext is not OpenPakInvitationModel invitation)
            {
                return;
            }

            if (OpenPakUi.IsRunning(invitation.TitleId))
            {
                (TopLevel.GetTopLevel(this) as Window)?.Close();

                await RyujinxApp.MainWindow.JoinInvitationAsync(invitation.Invitation);

                return;
            }

            ApplicationData application = Model.Titles.FirstOrDefault(title =>
                title.IdString.Equals(invitation.TitleId, StringComparison.OrdinalIgnoreCase));

            if (application == null)
            {
                Model.Message = LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_InvitationsNotInstalled, invitation.TitleName);

                return;
            }

            // This window is modal over the main one; a game starting behind it is unreachable.
            (TopLevel.GetTopLevel(this) as Window)?.Close();

            await RyujinxApp.MainWindow.ViewModel.LoadApplication(application);
        }

        private async void OnIgnore(object sender, RoutedEventArgs args)
        {
            if (Model != null && (sender as Control)?.DataContext is OpenPakInvitationModel invitation)
            {
                await Model.DeclineInvitationAsync(invitation);
            }
        }
    }
}
