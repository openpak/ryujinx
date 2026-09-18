using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Views.User;
using Ryujinx.Common;
using Ryujinx.Common.Helper;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.OpenPak;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    /// <summary>
    /// Setting a profile up: sign in to OpenPak, create an account and then sign in, or stay
    /// offline. The first launch runs it on the profile that is already there; *Add account* on the
    /// startup picker runs it for a new one.
    ///
    /// A profile signed in here takes the account's name and picture, once — after that the
    /// profile is the person's own to rename, and nothing keeps them in step.
    /// </summary>
    public static class OpenPakSetup
    {
        private enum Choice
        {
            SignIn,
            Create,
            Offline,
            Cancel,
        }

        /// <summary>
        /// Run the wizard. With <paramref name="addAccount"/> it makes a new profile and opens it;
        /// otherwise it sets up the open one — on a fresh install the placeholder the account
        /// service always keeps, which the wizard turns into the person's own either way: named
        /// after the account they sign in with, or by the name they give for playing offline.
        /// </summary>
        public static async Task RunAsync(AccountManager accounts, bool addAccount)
        {
            UserId previous = accounts.LastOpenedUser.UserId;

            while (true)
            {
                Choice choice = await AskAsync(addAccount);

                if (choice == Choice.Cancel)
                {
                    return;
                }

                if (choice == Choice.Offline)
                {
                    string name = await AskNameAsync(addAccount ? null : accounts.LastOpenedUser.Name);

                    if (name == null)
                    {
                        continue;
                    }

                    if (!addAccount)
                    {
                        accounts.SetUserName(accounts.LastOpenedUser.UserId, name);

                        return;
                    }

                    UserId offline = NewUserId();

                    accounts.AddUser(name, DefaultImage, offline);
                    accounts.OpenUser(offline);

                    return;
                }

                if (choice == Choice.Create)
                {
                    OpenHelper.OpenUrl(OpenPakConfig.WebsiteUrl + "/register");
                }

                // The bearer is kept under the open profile, so a new one is opened before the
                // sign-in and taken away again if nobody signs in.
                UserId created = default;

                if (addAccount)
                {
                    created = NewUserId();

                    accounts.AddUser("OpenPak", DefaultImage, created);
                    accounts.OpenUser(created);
                }

                string intro = choice == Choice.Create ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SetupVerify] : null;

                if (await OpenPakSignInView.Show(intro))
                {
                    await AdoptAccountAsync(accounts);

                    return;
                }

                if (addAccount)
                {
                    accounts.DeleteUser(created);
                    accounts.OpenUser(previous);
                }
            }
        }

        private static async Task<Choice> AskAsync(bool addAccount)
        {
            Choice choice = Choice.Cancel;

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[addAccount ? LocaleKeys.Dialog_OpenPak_SetupAddTitle : LocaleKeys.Dialog_OpenPak_SetupTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SetupSignIn],
                SecondaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SetupOffline],
                // The first launch has no "cancel": the profile is set up one way or the other.
                CloseButtonText = addAccount ? LocaleManager.Instance[LocaleKeys.Cancel] : string.Empty,
                DefaultButton = FAContentDialogButton.Primary,
            };

            HyperlinkButton create = new()
            {
                Padding = new Avalonia.Thickness(0),
                Content = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SetupCreate],
            };

            create.Click += (_, _) =>
            {
                choice = Choice.Create;

                dialog.Hide();
            };

            dialog.Content = new StackPanel
            {
                Width = 380,
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SetupIntro], TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    create,
                },
            };

            FAContentDialogResult result = await ContentDialogHelper.ShowAsync(dialog);

            return choice == Choice.Create ? Choice.Create : result switch
            {
                FAContentDialogResult.Primary => Choice.SignIn,
                FAContentDialogResult.Secondary => Choice.Offline,
                // Escape on the first launch is the offline path, which still asks for a name.
                _ => addAccount ? Choice.Cancel : Choice.Offline,
            };
        }

        private static async Task<string> AskNameAsync(string current)
        {
            TextBox box = new() { MaxLength = (int)UserEditorView.MaxProfileNameLength, Text = current };

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SetupProfileName],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SetupOffline],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                DefaultButton = FAContentDialogButton.Primary,
                Content = box,
            };

            dialog.Opened += (_, _) =>
            {
                box.Focus();
                box.SelectAll();
            };

            FAContentDialogResult result = await ContentDialogHelper.ShowAsync(dialog);

            string name = box.Text?.Trim();

            return result == FAContentDialogResult.Primary && !string.IsNullOrEmpty(name) ? name : null;
        }

        /// <summary>The profile takes the account's name and picture, the way a console's user does when linked.</summary>
        private static async Task AdoptAccountAsync(AccountManager accounts)
        {
            UserId profile = accounts.LastOpenedUser.UserId;

            try
            {
                OpenPakProfile me = await OpenPakApi.Instance.MeAsync(CancellationToken.None);

                if (!string.IsNullOrWhiteSpace(me?.DisplayName))
                {
                    string name = me.DisplayName.Trim();

                    accounts.SetUserName(profile, name[..Math.Min(name.Length, (int)UserEditorView.MaxProfileNameLength)]);
                }

                byte[] avatar = await OpenPakApi.Instance.ImageAsync(me?.AvatarUrl, CancellationToken.None);

                if (avatar is { Length: > 0 })
                {
                    accounts.SetUserImage(profile, UserProfileImageSelectorView.ProcessProfileImage(avatar));
                }
            }
            catch (Exception exception)
            {
                // The profile keeps its own name and picture; being signed in is what mattered.
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not copy the account to the profile: {exception.Message}");
            }
        }

        private static UserId NewUserId() => new(Guid.NewGuid().ToString("N"));

        private static byte[] DefaultImage => EmbeddedResources.Read("Ryujinx.HLE/HOS/Services/Account/Acc/DefaultUserImage.jpg");
    }
}
