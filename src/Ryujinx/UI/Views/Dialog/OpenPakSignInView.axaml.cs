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

            DeviceBox.Text = $"Ryujinx on {Environment.MachineName}";

            SignInButton.Click += async (_, _) => await SignInAsync();
            CreateButton.Click += (_, _) => OpenHelper.OpenUrl(OpenPakConfig.WebsiteUrl + "/register");

            if (!SecretStore.Available)
            {
                // Said before anything is typed: there is no point filling in a password that
                // cannot be turned into a token this machine is able to keep.
                SignInButton.IsEnabled = false;

                Status(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignInNoKeychain]);
            }
        }

        /// <summary>Shows the dialog and returns whether somebody ended up signed in.</summary>
        public static async Task<bool> Show()
        {
            OpenPakSignInView view = new();

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignInTitle],
                PrimaryButtonText = string.Empty,
                SecondaryButtonText = string.Empty,
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                Content = view,
            };

            view._dialog = dialog;

            await ContentDialogHelper.ShowAsync(dialog);

            return view._signedIn;
        }

        private async Task SignInAsync()
        {
            SignInButton.IsEnabled = false;

            Status(string.Empty);

            string failure = await OpenPakApi.Instance.SignInAsync(
                EmailBox.Text ?? string.Empty,
                PasswordBox.Text ?? string.Empty,
                DeviceBox.Text ?? Environment.MachineName,
                CancellationToken.None);

            if (failure != null)
            {
                SignInButton.IsEnabled = true;

                Status(failure);

                return;
            }

            // The cache the guest's friend list reads starts here, so a game launched straight
            // after signing in already has a list to be handed.
            OpenPakAccount.Instance.Start();

            // Binding the console is the same credential typed once more, so it happens here and
            // now instead of asking for them again on the account page's link screen. A person
            // who signs in and then launches a game expects the game to know who they are; the
            // distinction between the account and the console's device identity is console
            // architecture, not something to make anybody manage. Nothing is stored: the
            // password is used for this one federation call and let go, exactly as above.
            //
            // Anything that fails here leaves linking to the account page's link screen, which
            // stays for the not-signed-in-yet case.
            string email = EmailBox.Text ?? string.Empty;
            string password = PasswordBox.Text ?? string.Empty;

            bool linked = false;

            try
            {
                linked = await Ryujinx.HLE.HOS.Services.Account.OpenPak.OpenPakSession.Instance.LinkAsync(
                    email, password, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc,
                    $"[OpenPak] Linking the console during sign-in failed: {exception.Message}");
            }

            _signedIn = true;

            NotificationHelper.ShowSuccess(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                LocaleManager.Instance.UpdateAndGetDynamicValue(
                    linked ? LocaleKeys.Dialog_OpenPak_SignInDoneLinked : LocaleKeys.Dialog_OpenPak_SignInDone,
                    email));

            _dialog?.Hide();
        }

        private void Status(string message)
        {
            StatusText.Text = message;
            StatusText.IsVisible = message.Length > 0;
        }
    }
}
