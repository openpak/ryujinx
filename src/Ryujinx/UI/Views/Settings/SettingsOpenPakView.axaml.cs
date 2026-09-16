using Avalonia.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Views.Dialog;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.OpenPak;
using System.Threading;

namespace Ryujinx.Ava.UI.Views.Settings
{
    /// <summary>
    /// Where OpenPak is configured. Signing in is the only thing a person has to do here; the
    /// console address, the CA and the DNS redirect all come from the network profile at launch.
    /// </summary>
    public partial class SettingsOpenPakView : UserControl
    {
        public SettingsOpenPakView()
        {
            InitializeComponent();

            RefreshNetworkButton.Click += async (_, _) =>
            {
                if (DataContext is SettingsViewModel model)
                {
                    model.ApplyOpenPakAddresses();
                }

                // One conditional request, best-effort: a new title on the server works after
                // this without a new build, and a fetch that fails changes nothing.
                string source = await OpenPakNetworkProfileService.RefreshAsync(CancellationToken.None);

                NotificationHelper.Show(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_SettingsNetworkRefreshed, source),
                    source == "fetched"
                        ? Avalonia.Controls.Notifications.NotificationType.Success
                        : Avalonia.Controls.Notifications.NotificationType.Information);

                Refresh();
            };

            SignInButton.Click += async (_, _) =>
            {
                if (DataContext is SettingsViewModel model)
                {
                    model.ApplyOpenPakAddresses();
                }

                await OpenPakSignInView.Show();

                Refresh();
            };

            SignOutButton.Click += async (_, _) =>
            {
                await OpenPakApi.Instance.SignOutAsync(CancellationToken.None);

                OpenPakAccount.Instance.Stop();

                Refresh();
            };

            OpenWindowButton.Click += async (_, _) =>
            {
                if (DataContext is SettingsViewModel model)
                {
                    model.ApplyOpenPakAddresses();
                }

                await OpenPakWindow.Show();

                Refresh();
            };

            AttachedToVisualTree += (_, _) => Refresh();
        }

        /// <summary>Say what is true right now: signed in or not.</summary>
        private void Refresh()
        {
            AccountStatus.Text = OpenPakAccount.Instance.SignedIn
                ? LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.MenuBar_OpenPak_SignedInAs,
                    OpenPakAccount.Instance.DisplayName ?? OpenPakConfig.WebsiteUrl)
                : LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SettingsNotSignedIn];

            SignInButton.IsVisible = !OpenPakApi.Instance.SignedIn;
            SignOutButton.IsVisible = OpenPakApi.Instance.SignedIn;
        }
    }
}
