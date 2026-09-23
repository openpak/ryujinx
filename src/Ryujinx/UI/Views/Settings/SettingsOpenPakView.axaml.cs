using Avalonia.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.OpenPak;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Views.Dialog;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.OpenPak;
using System.Threading;

namespace Ryujinx.Ava.UI.Views.Settings
{
    /// <summary>
    /// Where OpenPak is configured (UX spec §3.13). Signing in is the only thing a person has to
    /// do here; the console address, the CA and the DNS redirect all come from the network
    /// profile at launch.
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

                RefreshNetworkButton.IsEnabled = false;

                // One conditional request, best-effort and off the UI thread: a new title on the
                // server works after this without a new build, and a fetch that fails changes nothing.
                string source = await OpenPakNetworkProfileService.RefreshAsync(CancellationToken.None);

                NetworkStatus.Text = LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SettingsNetworkRefreshed, source);

                RefreshNetworkButton.IsEnabled = true;

                Refresh();
            };

            SignInButton.Click += async (_, _) =>
            {
                if (DataContext is SettingsViewModel model)
                {
                    model.ApplyOpenPakAddresses();
                }

                await OpenPakWindow.SignInAsync();

                Refresh();
            };

            // The same confirmation as the menu and the Account page.
            SignOutButton.Click += async (_, _) =>
            {
                await OpenPakSignOut.ConfirmAsync();

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

        /// <summary>Say what is true right now, for the open profile: signed in or not.</summary>
        private void Refresh()
        {
            bool signedIn = OpenPakApi.Instance.SignedIn;
            bool running = OpenPakUi.GameRunning;

            AccountStatus.Text = signedIn
                ? LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SettingsAccountRow, OpenPakConfig.ProfileName,
                    OpenPakAccount.Instance.DisplayName ?? OpenPakConfig.WebsiteUrl)
                : LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SettingsAccountRowOut, OpenPakConfig.ProfileName);

            SignInButton.IsVisible = !signedIn;
            SignOutButton.IsVisible = signedIn;

            // Sign-in, sign-out and the switch itself wait for the running game to stop (§5.5).
            string stopFirst = running ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CommonStopGameFirst] : null;

            SignInButton.IsEnabled = SignOutButton.IsEnabled = EnableBox.IsEnabled = !running;

            ToolTip.SetTip(SignInButton, stopFirst);
            ToolTip.SetTip(SignOutButton, stopFirst);
        }
    }
}
