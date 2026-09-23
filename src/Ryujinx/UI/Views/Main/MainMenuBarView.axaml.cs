using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Gommon;
using LibHac.Common;
using LibHac.Ns;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Views.Dialog;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common;
using Ryujinx.Common.Helper;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.HOS.Services.Nfc.AmiiboDecryption;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenPakAccount = Ryujinx.OpenPak.OpenPakAccount;
using OpenPakApi = Ryujinx.OpenPak.OpenPakApi;
using OpenPakConfig = Ryujinx.OpenPak.OpenPakConfig;
using OpenPakLinks = Ryujinx.OpenPak.OpenPakLinks;

namespace Ryujinx.Ava.UI.Views.Main
{
    public partial class MainMenuBarView : RyujinxControl<MainWindowViewModel>
    {
        public MainWindow Window { get; private set; }

        public MainMenuBarView()
        {
            InitializeComponent();

            ToggleFileTypesMenuItem.ItemsSource = GenerateToggleFileTypeItems();
            ChangeLanguageMenuItem.ItemsSource = GenerateLanguageMenuItems();
            MiiEditorMenuItem.Command = Commands.Create(OpenMiiEditor);
            CloseRyujinxMenuItem.Command = Commands.Create(() => Window?.Close());
            OpenSettingsMenuItem.Command = Commands.Create(OpenSettings);
            PauseEmulationMenuItem.Command = Commands.Create(() => ViewModel.AppHost?.Pause());
            ResumeEmulationMenuItem.Command = Commands.Create(() => ViewModel.AppHost?.Resume());
            StopEmulationMenuItem.Command = Commands.Create(() => ViewModel.AppHost?.ShowExitPrompt().OrCompleted());
            RestartEmulationMenuItem.Command = Commands.Create(() => ViewModel.RestartEmulation());
            XCITrimmerMenuItem.Command = Commands.Create(XciTrimmerView.Show);
            AboutWindowMenuItem.Command = Commands.Create(AboutView.Show);
            CompatibilityListMenuItem.Command = Commands.Create(() => CompatibilityListWindow.Show());
            LdnGameListMenuItem.Command = Commands.Create(() => LdnGamesListWindow.Show());

            OpenPakAccountMenuItem.Command = Commands.Create(async () =>
            {
                // OpenPak turned off: the way back is the setting that turns it on.
                if (!OpenPakConfig.Enabled)
                {
                    await OpenSettingsAt(OpenPakSettingsPage);

                    return;
                }

                if (!OpenPakApi.Instance.SignedIn)
                {
                    // A profile that never had an account gets the setup dialog; one whose
                    // sign-in lapsed only needs to sign in again.
                    if (OpenPakLinks.Get(OpenPakConfig.ProfileId) == null && Window?.AccountManager != null)
                    {
                        await OpenPakSetup.RunAsync(Window.AccountManager, addAccount: false);
                    }
                    else
                    {
                        await OpenPakSignInView.Show();
                    }

                    RefreshOpenPakStatus();

                    return;
                }

                await OpenPakWindow.Show(OpenPakWindow.Page.Account);
            });
            OpenPakFriendsMenuItem.Command = Commands.Create(() => OpenPakWindow.Show(OpenPakWindow.Page.Friends));
            OpenPakInvitationsMenuItem.Command = Commands.Create(() => OpenPakWindow.Show(OpenPakWindow.Page.Invitations));
            OpenPakSavesMenuItem.Command = Commands.Create(() => OpenPakWindow.Show(OpenPakWindow.Page.Saves));
            OpenPakModsMenuItem.Command = Commands.Create(() => OpenPakWindow.Show(OpenPakWindow.Page.Mods));
            OpenPakNewsMenuItem.Command = Commands.Create(() => OpenPakWindow.Show(OpenPakWindow.Page.News));
            OpenPakStatusMenuItem.Command = Commands.Create(() => OpenPakWindow.Show(OpenPakWindow.Page.Status));
            OpenPakSettingsMenuItem.Command = Commands.Create(() => OpenSettingsAt(OpenPakSettingsPage));
            OpenPakSignOutMenuItem.Command = Commands.Create(async () =>
            {
                await OpenPakSignOut.ConfirmAsync();

                RefreshOpenPakStatus();
            });
            OpenPakWebsiteMenuItem.Command = Commands.Create(() => OpenHelper.OpenUrl(OpenPakConfig.WebsiteUrl));
            OpenPakMenuItem.SubmenuOpened += (_, _) => RefreshOpenPakStatus();

            UpdateMenuItem.Command = MainWindowViewModel.UpdateCommand;

            FaqMenuItem.Command =
                SetupGuideMenuItem.Command =
                    LdnGuideMenuItem.Command = Commands.Create<string>(OpenHelper.OpenUrl);

            WindowSize720PMenuItem.Command =
                WindowSize1080PMenuItem.Command =
                    WindowSize1440PMenuItem.Command =
                        WindowSize2160PMenuItem.Command = Commands.Create<string>(ChangeWindowSize);

            LocaleManager.Instance.LocaleChanged += OnLocaleChanged;
        }

        private void OnLocaleChanged()
        {
            ChangeLanguageMenuItem.ItemsSource = GenerateLanguageMenuItems();
            Menu.Close();
        }

        private IEnumerable<CheckBox> GenerateToggleFileTypeItems() =>
            Enum.GetValues<FileTypes>()
                .Select(it => (FileName: Enum.GetName(it)!, FileType: it))
                .Select(it =>
                    new CheckBox
                    {
                        Margin = new Thickness(10, 0, 0, 0),
                        Content = $".{it.FileName}",
                        IsChecked = it.FileType.GetConfigValue(ConfigurationState.Instance.UI.ShownFileTypes),
                        Command = Commands.Create(() => Window.ToggleFileType(it.FileName))
                    }
                );

        private static IEnumerable<MenuItem> GenerateLanguageMenuItems()
        {
            const string LanguagesPath = "Ryujinx/Assets/Languages.json";

            string languageJson = EmbeddedResources.ReadAllText(LanguagesPath);
            string currentLanguageCode = LocaleManager.Instance.CurrentLanguageCode;

            LanguagesJson languages = JsonHelper.Deserialize(languageJson, LanguagesJsonContext.Default.LanguagesJson);

            foreach ((string code, string language) in languages.Languages)
            {
                string languageName = string.IsNullOrEmpty(language) ? code : language;

                MenuItem menuItem = new()
                {
                    Padding = new Thickness(10, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Header = code == currentLanguageCode ? $"{languageName}  ✔" : languageName,
                    Command = Commands.Create(() => MainWindowViewModel.ChangeLanguage(code))
                };

                yield return menuItem;
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (TopLevel.GetTopLevel(this) is MainWindow window)
            {
                Window = window;
                DataContext = ViewModel = window.ViewModel;
            }
        }

        /// <summary>The settings tab OpenPak's own menu opens (UX spec §3.1, item 8).</summary>
        private const string OpenPakSettingsPage = "OpenPakPage";

        public Task OpenSettings() => OpenSettingsAt(null);

        /// <summary>The settings window, opened at the page with this tag when one is given.</summary>
        public async Task OpenSettingsAt(string page)
        {
            Window.SettingsWindow = new(Window.VirtualFileSystem, Window.ContentManager);

            if (page != null)
            {
                Window.SettingsWindow.SelectPage(page);
            }

            Rainbow.Enable();

            // A page asked for by name lives in the global settings, not a game's own.
            if (page != null)
            {
                await Window.SettingsWindow.ShowDialog(Window);
            }
            else if (ViewModel.SelectedApplication is null) // Checks if game data exists
            {
                await StyleableAppWindow.ShowAsync(Window.SettingsWindow);
            }
            else
            {
                bool customConfigExists = File.Exists(Program.GetDirGameUserConfig(ViewModel.SelectedApplication.IdString));

                if (!ViewModel.IsGameRunning || !customConfigExists)
                {
                    await Window.SettingsWindow.ShowDialog(Window); // The game is not running, or if the user configuration does not exist
                }
                else
                {
                    // If there is a custom configuration in the folder
                    await StyleableAppWindow.ShowAsync(new GameSpecificSettingsWindow(ViewModel, customConfigExists));
                }
            }

            Rainbow.Disable();
            Rainbow.Reset();

            Window.SettingsWindow = null;

            ViewModel.LoadConfigurableHotKeys();
        }

        public AppletMetadata MiiEditor => new(ViewModel.ContentManager, LocaleManager.Instance[LocaleKeys.MenuBar_Actions_MiiEditorButton], 0x0100000000001009);

        public async Task OpenMiiEditor()
        {
            if (!MiiEditor.CanStart(out ApplicationData appData, out BlitStruct<ApplicationControlProperty> nacpData))
                return;

            await ViewModel.LoadApplication(appData, ViewModel.IsFullScreen || ViewModel.StartGamesInFullscreen, nacpData);
        }

        private void ScanAmiiboMenuItem_AttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is MenuItem)
                ViewModel.IsAmiiboRequested = ViewModel.AppHost.Device.System.SearchingForAmiibo(out _);
        }

        private void ScanBinAmiiboMenuItem_AttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is MenuItem)
                ViewModel.IsAmiiboBinRequested = ViewModel.IsAmiiboRequested && AmiiboBinReader.HasAmiiboKeyFile;
        }

        private void SkylanderMenuItem_AttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is MenuItem)
            {
                ViewModel.IsSkylanderRequested = ViewModel.AppHost.Device.System.SearchingForSkylander(out _);
                ViewModel.HasSkylander = ViewModel.AppHost.Device.System.HasSkylander(out _);
            }
        }

        private void ChangeWindowSize(string resolution)
        {
            (int resolutionWidth, int resolutionHeight) = resolution.Split(' ', 2)
                .Into(parts =>
                    (int.Parse(parts[0]), int.Parse(parts[1]))
                );

            // Correctly size window when 'TitleBar' is enabled (Nov. 14, 2024)
            double barsHeight = ((Window.StatusBarHeight + Window.MenuBarHeight) +
                (ConfigurationState.Instance.ShowOldUI ? (int)Window.TitleBar.Height : 0));

            double windowWidthScaled = (resolutionWidth * Program.WindowScaleFactor);
            double windowHeightScaled = ((resolutionHeight + barsHeight) * Program.WindowScaleFactor);

            Dispatcher.UIThread.Post(() =>
            {
                ViewModel.WindowState = WindowState.Normal;

                Window.Width = windowWidthScaled;
                Window.Height = windowHeightScaled;
            });
        }
        /// <summary>
        /// The OpenPak menu: the account first, then the seven pages, then the housekeeping.
        ///
        /// Every page entry opens the same window at that page — one account, one window — and
        /// the account line says who is signed in, so the menu answers "am I online" without
        /// anything being opened at all.
        /// </summary>
        private void RefreshOpenPakStatus()
        {
            bool enabled = OpenPakConfig.Enabled;
            bool signedIn = enabled && OpenPakApi.Instance.SignedIn;
            bool running = ViewModel?.IsGameRunning ?? false;

            OpenPakAccountMenuItem.Header = signedIn
                ? LocaleManager.GetFormatted(LocaleKeys.MenuBar_OpenPak_SignedInAs,
                    OpenPakAccount.Instance.DisplayName ?? OpenPakConfig.WebsiteUrl)
                : LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_SignInPrompt];

            // Signing in switches the profile's identity under a running game, so it waits for
            // the game to stop; with OpenPak off the header only leads to the setting, which is
            // always fine.
            bool headerBlocked = enabled && !signedIn && running;

            OpenPakAccountMenuItem.IsEnabled = !headerBlocked;
            ToolTip.SetTip(OpenPakAccountMenuItem, headerBlocked
                ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CommonStopGameFirst]
                : null);

            // The pages that are about this account are pointless without one; mods, news and
            // status are public and stay reachable, which is the point of them being public.
            OpenPakFriendsMenuItem.IsEnabled = signedIn;
            OpenPakInvitationsMenuItem.IsEnabled = signedIn;
            OpenPakSavesMenuItem.IsEnabled = signedIn;
            OpenPakSignOutMenuItem.IsEnabled = signedIn && !running;
            ToolTip.SetTip(OpenPakSignOutMenuItem, signedIn && running
                ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CommonStopGameFirst]
                : null);

            if (signedIn)
            {
                _ = ShowAvatarAsync();
            }
            else
            {
                OpenPakAccountMenuItem.Icon = null;
            }
        }

        /// <summary>Their own picture in the menu, once it has been fetched; the glyph until then.</summary>
        private async Task ShowAvatarAsync()
        {
            byte[] avatar = await OpenPakApi.Instance.ImageAsync(
                OpenPakAccount.Instance.Profile?.AvatarUrl, CancellationToken.None);

            if (avatar == null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OpenPakAccountMenuItem.Icon = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,
                    Child = new Image
                    {
                        Source = new Bitmap(new MemoryStream(avatar)),
                        Stretch = Stretch.UniformToFill,
                    },
                };
            });
        }
    }
}
