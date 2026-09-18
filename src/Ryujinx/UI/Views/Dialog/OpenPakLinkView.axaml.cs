using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    /// <summary>
    /// The screen a console shows when it links an account, shown here instead of in a browser.
    ///
    /// Both halves, because both are worth having and a console only lacks one of them for want of
    /// a keyboard: a QR and a code for whoever would rather use their phone, and an e-mail and
    /// password for whoever is already sitting at a keyboard. The server renders the QR, so this is
    /// the same screen every OpenPak client shows.
    /// </summary>
    public partial class OpenPakLinkView : UserControl
    {
        private readonly CancellationTokenSource _cancellation = new();

        private FAContentDialog _dialog;
        private string _code;
        private string _codeDisplay;
        private bool _linked;

        public OpenPakLinkView()
        {
            InitializeComponent();

            SignInButton.Click += async (_, _) => await SignInAsync();
            ApproveButton.Click += async (_, _) => await ApproveAsync();
            PasswordBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter && SignInButton.IsEnabled)
                {
                    e.Handled = true;

                    await SignInAsync();
                }
            };
        }

        /// <summary>Shows the dialog and returns whether an account ended up linked.</summary>
        public static async Task<bool> Show()
        {
            OpenPakLinkView view = new();

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_LinkTitle],
                PrimaryButtonText = string.Empty,
                SecondaryButtonText = string.Empty,
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                Content = view,
            };

            view._dialog = dialog;

            _ = view.BeginAsync();

            await ContentDialogHelper.ShowAsync(dialog);

            view._cancellation.Cancel();

            return view._linked;
        }

        /// <summary>Ask for a code, draw it, then wait for a phone to use it.</summary>
        private async Task BeginAsync()
        {
            try
            {
                OpenPakSession.LinkInvitation invitation =
                    await OpenPakSession.Instance.StartLinkAsync(_cancellation.Token);

                if (invitation == null)
                {
                    Status(LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_NoServer]);

                    return;
                }

                _code = invitation.Code;
                _codeDisplay = invitation.CodeDisplay;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (invitation.Qr != null)
                    {
                        QrImage.Source = new Bitmap(new MemoryStream(invitation.Qr));

                        Instructions.Text = LocaleManager.Instance.UpdateAndGetDynamicValue(
                            LocaleKeys.MenuBar_OpenPak_LinkScan, invitation.LinkUrl);
                    }
                    else
                    {
                        // The server has nowhere to send a phone, so this shows no square at all
                        // rather than one that would take a phone somewhere else entirely.
                        PhonePanel.IsVisible = false;

                        Instructions.Text = LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_LinkNoPhone];
                    }
                });

                string account = await OpenPakSession.Instance.AwaitClaimAsync(_code, RevealCode, _cancellation.Token);

                if (account == null)
                {
                    Status(LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_LinkExpired]);

                    return;
                }

                // Claimed is not linked: whoever is sitting here still says yes, which is the one
                // step that a stranger with the code cannot take for them.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusBar.Severity = FAInfoBarSeverity.Informational;
                    StatusBar.Message = LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.MenuBar_OpenPak_LinkClaimed, account);
                    StatusBar.IsOpen = ApproveButton.IsVisible = true;
                });
            }
            catch (OperationCanceledException)
            {
                // The dialog was closed. Nothing to say about it.
            }
        }

        /// <summary>
        /// Called the moment a phone signs in, and not a moment before: until then this screen
        /// shows a square and nothing else worth photographing.
        /// </summary>
        private void RevealCode() => Dispatcher.UIThread.Post(() =>
        {
            CodeText.Text = _codeDisplay;
            CodeText.IsVisible = true;

            Instructions.Text = LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_LinkTypeCode];
        });

        private async Task ApproveAsync()
        {
            ApproveButton.IsEnabled = false;

            Finish(await OpenPakSession.Instance.ApproveAsync(_code, _cancellation.Token));
        }

        private async Task SignInAsync()
        {
            SignInButton.IsEnabled = false;
            Status(string.Empty);

            bool linked = await OpenPakSession.Instance.LinkAsync(
                EmailBox.Text ?? string.Empty, PasswordBox.Text ?? string.Empty, _cancellation.Token);

            if (!linked)
            {
                SignInButton.IsEnabled = true;

                Status(LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_LinkWrongCredentials]);

                return;
            }

            Finish(true);
        }

        private void Finish(bool linked)
        {
            _linked = linked;

            if (linked)
            {
                _cancellation.Cancel();
                _dialog?.Hide();
            }
            else
            {
                ApproveButton.IsEnabled = true;

                Status(LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_LinkFailed]);
            }
        }

        private void Status(string message) => Dispatcher.UIThread.Post(() =>
        {
            StatusBar.Severity = FAInfoBarSeverity.Error;
            StatusBar.Message = message;
            StatusBar.IsOpen = message.Length > 0;
        });
    }
}
