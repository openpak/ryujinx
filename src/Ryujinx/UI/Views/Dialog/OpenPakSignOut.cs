using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.OpenPak;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    /// <summary>
    /// Signing out, asked about first (UX spec §3.5): it revokes the token and takes the profile
    /// offline, and undoing it means typing the password again. The one confirmation every place
    /// that offers sign-out uses — the menu, the Account page and the settings tab.
    /// </summary>
    public static class OpenPakSignOut
    {
        /// <summary>Ask, then sign out. True when the profile is now signed out.</summary>
        public static async Task<bool> ConfirmAsync()
        {
            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignOutTitle],
                Content = new TextBlock
                {
                    Text = LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SignOutBody, OpenPakConfig.ProfileName),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignOutConfirm],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                // Cancel is the default and what Escape does: the destructive answer is never
                // the one a stray Enter gives.
                DefaultButton = FAContentDialogButton.Close,
            };

            if (await ContentDialogHelper.ShowAsync(dialog) != FAContentDialogResult.Primary)
            {
                return false;
            }

            await OpenPakApi.Instance.SignOutAsync(CancellationToken.None);

            OpenPakAccount.Instance.Stop();

            OpenPakToast.Show(OpenPakToast.Category.OpenPak, LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SignOutDone]);

            return true;
        }
    }
}
