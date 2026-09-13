using Avalonia.Controls;
using Avalonia.Input.Platform;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Views.Dialog;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using System.Threading;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>Who this emulator is signed in as, and the two things worth doing about it.</summary>
    public partial class OpenPakAccountView : UserControl
    {
        public OpenPakAccountView()
        {
            InitializeComponent();

            SignInButton.Click += async (_, _) =>
            {
                if (await OpenPakSignInView.Show() && DataContext is OpenPakViewModel model)
                {
                    await model.RefreshAsync();
                    await model.RefreshStatusAsync();
                }
            };

            RefreshButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshAsync();
                    await model.RefreshStatusAsync();
                }
            };

            SignOutButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.SignOutAsync();
                }
            };

            CopyCodeButton.Click += async (_, _) =>
            {
                if (DataContext is not OpenPakViewModel model ||
                    !RyujinxApp.IsClipboardAvailable(out IClipboard clipboard))
                {
                    return;
                }

                await clipboard.SetTextAsync(model.FriendCode);

                NotificationHelper.ShowInformation(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_AccountCopied]);
            };

            LinkConsoleButton.Click += async (_, _) =>
            {
                // The device account has to exist before there is anything to attach to a person.
                await OpenPakSession.Instance.EnsureAsync(CancellationToken.None);

                if (OpenPakSession.Instance.IdToken == null)
                {
                    NotificationHelper.ShowWarning(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                        LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_SignInFailed]);

                    return;
                }

                if (await OpenPakLinkView.Show())
                {
                    NotificationHelper.ShowSuccess(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                        LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.MenuBar_OpenPak_LinkDone,
                            OpenPakSession.Instance.Nickname));
                }

                RefreshConsole();
            };

            AttachedToVisualTree += (_, _) => RefreshConsole();
        }

        /// <summary>
        /// What the emulated console's own identity looks like right now.
        ///
        /// Hidden entirely when no console server is configured: without one there is nothing to
        /// link against, and an offer that cannot work is worse than no offer.
        /// </summary>
        private void RefreshConsole()
        {
            OpenPakSession session = OpenPakSession.Instance;

            ConsolePanel.IsVisible = session.Enabled;

            if (!session.Enabled)
            {
                return;
            }

            ConsoleStatus.Text = session.IsLinked
                ? LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.MenuBar_OpenPak_LinkedAs, session.Nickname)
                : LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.MenuBar_OpenPak_SignedIn, session.ServerAddress);

            LinkConsoleButton.IsVisible = !session.IsLinked;
        }
    }
}
