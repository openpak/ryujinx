using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using Gommon;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Ava.Utilities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// Who this emulator is signed in as (UX spec §3.6, Account): the identity card with the
    /// picture, the name and the friend code, then the details, then the console link.
    /// </summary>
    public partial class OpenPakAccountView : UserControl
    {
        public OpenPakAccountView()
        {
            InitializeComponent();

            // The window reloads every page when the sign-in changes; nothing more to do here.
            SignInButton.Click += async (_, _) => await OpenPakWindow.SignInAsync();

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

            // Results go to the window's status line, not a toast: the window is where somebody is looking.
            CopyCodeButton.Click += async (_, _) =>
            {
                if (DataContext is not OpenPakViewModel model)
                {
                    return;
                }

                if (!RyujinxApp.IsClipboardAvailable(out IClipboard clipboard))
                {
                    model.Message = LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_AccountCopyFailed, model.FriendCode);

                    return;
                }

                await clipboard.SetTextAsync(model.FriendCode);

                model.Message = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_AccountCopied];
            };

            TryAgainButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RetryLinkAsync();
                }
            };

            AvatarButton.Click += async (_, _) => await ChangePictureAsync();
            ChangeNameButton.Click += async (_, _) => await ChangeNameAsync();
        }

        private async void OnChangePicture(object sender, Avalonia.Interactivity.RoutedEventArgs args) => await ChangePictureAsync();

        private async Task ChangePictureAsync()
        {
            if (DataContext is not OpenPakViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
            {
                return;
            }

            Optional<IStorageFile> file = await storageProvider.OpenSingleFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_AccountChangePicture],
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("PNG / JPEG")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg"],
                        AppleUniformTypeIdentifiers = ["public.png", "public.jpeg"],
                        MimeTypes = ["image/png", "image/jpeg"],
                    },
                },
            });

            if (file.HasValue)
            {
                await model.ChangePictureAsync(file.Value.Path.LocalPath);
            }
        }

        private async Task ChangeNameAsync()
        {
            if (DataContext is not OpenPakViewModel model)
            {
                return;
            }

            TextBox box = new() { Text = model.DisplayName, MaxLength = 16 };

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_AccountChangeNameTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.InputDialogOk],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                DefaultButton = FAContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Width = 340,
                    Spacing = 8,
                    Children =
                    {
                        box,
                        new TextBlock
                        {
                            Text = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_AccountNameRules],
                            Opacity = 0.65,
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };

            dialog.Opened += (_, _) =>
            {
                box.Focus();
                box.SelectAll();
            };

            if (await ContentDialogHelper.ShowAsync(dialog) != FAContentDialogResult.Primary)
            {
                return;
            }

            string name = box.Text?.Trim();

            if (!string.IsNullOrEmpty(name) && name != model.DisplayName)
            {
                await model.ChangeNameAsync(name);
            }
        }
    }
}
