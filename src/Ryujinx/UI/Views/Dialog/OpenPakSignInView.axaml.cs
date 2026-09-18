using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Common.Helper;
using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    /// <summary>
    /// Signing in to OpenPak with the account's own credentials.
    ///
    /// The password is used once, here, to mint a bearer, and is never written anywhere. The
    /// bearer goes into the OS password store and nowhere else — if there is no store on this
    /// machine the sign-in refuses rather than quietly writing the token to a config file, which
    /// is the same thing as leaving the account in a backup.
    /// </summary>
    public partial class OpenPakSignInView : UserControl
    {
        private FAContentDialog _dialog;
        private bool _signedIn;

        public OpenPakSignInView()
        {
            InitializeComponent();

            // The profile is in the name: each profile is its own sign-in, and the account's device
            // list should say which one a token belongs to before anyone revokes it.
            DeviceBox.Text = $"Ryujinx — {OpenPakConfig.ProfileName} on {Environment.MachineName}";

            PasswordBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    e.Handled = true;

                    await SignInAsync();
                }
            };
            CreateButton.Click += (_, _) => OpenHelper.OpenUrl(OpenPakConfig.WebsiteUrl + "/register");
        }

        /// <summary>
        /// Shows the dialog and returns whether somebody ended up signed in. The setup wizard shows
        /// the same dialog with a line saying what to do before signing in.
        /// </summary>
        public static async Task<bool> Show(string intro = null)
        {
            OpenPakSignInView view = new();

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignInTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignInSubmit],
                SecondaryButtonText = string.Empty,
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                DefaultButton = FAContentDialogButton.Primary,
                Content = view,
            };

            view._dialog = dialog;

            // The dialog stays up until a sign-in succeeds, so the click never closes it itself;
            // SignInAsync hides it when there is something to close for.
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;

                await view.SignInAsync();
            };

            if (!SecretStore.Available)
            {
                // Said before anything is typed: there is no point filling in a password that
                // cannot be turned into a token this machine is able to keep. The reason names
                // the package to install on this platform.
                dialog.IsPrimaryButtonEnabled = false;

                view.Status(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignInNoKeychain] + "\n\n" + SecretStore.UnavailableReason);
            }
            else if (intro != null)
            {
                view.IntroText.Text = intro;
                view.IntroText.IsVisible = true;
            }

            dialog.Opened += (_, _) => view.EmailBox.Focus();

            await ContentDialogHelper.ShowAsync(dialog);

            return view._signedIn;
        }

        private async Task SignInAsync()
        {
            // Enter in the password box and the dialog's own default-button handling can both
            // land here for one key press; the disabled button is what says one is in flight.
            if (!_dialog.IsPrimaryButtonEnabled)
            {
                return;
            }

            _dialog.IsPrimaryButtonEnabled = false;

            Status(string.Empty);

            string failure = await OpenPakApi.Instance.SignInAsync(
                EmailBox.Text ?? string.Empty,
                PasswordBox.Text ?? string.Empty,
                DeviceBox.Text ?? Environment.MachineName,
                CancellationToken.None);

            if (failure != null)
            {
                _dialog.IsPrimaryButtonEnabled = true;

                Status(failure);

                return;
            }

            // The cache the guest's friend list reads starts here, so a game launched straight
            // after signing in already has a list to be handed.
            OpenPakAccount.Instance.Start();

            // Binding the console follows from the sign-in: the website mints the token the
            // console's link page would have produced, so nothing is typed twice. A failure here
            // leaves the account page's link screen as the fallback.
            bool linked = await Ryujinx.HLE.HOS.Services.Account.OpenPak.OpenPakSession.Instance.LinkFromAccountAsync(
                CancellationToken.None);

            string email = EmailBox.Text ?? string.Empty;

            _signedIn = true;

            NotificationHelper.ShowSuccess(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                LocaleManager.Instance.UpdateAndGetDynamicValue(
                    linked ? LocaleKeys.Dialog_OpenPak_SignInDoneLinked : LocaleKeys.Dialog_OpenPak_SignInDone,
                    email));

            _dialog?.Hide();
        }

        private void Status(string message)
        {
            StatusBar.Message = message;
            StatusBar.IsOpen = message.Length > 0;
        }
    }
}
