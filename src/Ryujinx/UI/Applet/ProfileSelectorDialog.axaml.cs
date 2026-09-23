using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using UserProfile = Ryujinx.Ava.UI.Models.UserProfile;
using UserProfileSft = Ryujinx.HLE.HOS.Services.Account.Acc.UserProfile;

namespace Ryujinx.Ava.UI.Applet
{
    public partial class ProfileSelectorDialog : RyujinxControl<ProfileSelectorDialogViewModel>
    {
        //Fix compiler warning
        public ProfileSelectorDialog()
        {
            
        }
        
        public ProfileSelectorDialog(ProfileSelectorDialogViewModel viewModel)
        {
            DataContext = ViewModel = viewModel;

            InitializeComponent();
        }

        private void Grid_PointerEntered(object sender, PointerEventArgs e)
        {
            if (sender is Grid { DataContext: UserProfile profile })
            {
                profile.IsPointerOver = true;
            }
        }

        private void Grid_OnPointerExited(object sender, PointerEventArgs e)
        {
            if (sender is Grid { DataContext: UserProfile profile })
            {
                profile.IsPointerOver = false;
            }
        }

        private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                int selectedIndex = listBox.SelectedIndex;

                if (selectedIndex >= 0 && selectedIndex < ViewModel.Profiles.Count)
                {
                    if (ViewModel.Profiles[selectedIndex] is UserProfile userProfile)
                    {
                        ViewModel.SelectedUserId = userProfile.UserId;
                        Logger.Info?.Print(LogClass.UI, $"Selected: {userProfile.UserId}", "ProfileSelector");

                        ObservableCollection<BaseModel> newProfiles = [];

                        foreach (BaseModel item in ViewModel.Profiles)
                        {
                            if (item is UserProfile originalItem)
                            {
                                UserProfileSft profile = new(originalItem.UserId, originalItem.Name, originalItem.Image);

                                if (profile.UserId == ViewModel.SelectedUserId)
                                {
                                    profile.AccountState = AccountState.Open;
                                }

                                newProfiles.Add(new UserProfile(profile, new NavigationDialogHost()));
                            }
                        }

                        ViewModel.Profiles = newProfiles;
                    }
                }
            }
        }

        /// <summary>
        /// The picker the emulator opens with when there is more than one profile and it was told
        /// to ask. Not skipped by the setting that skips a title's own picker: this is the one
        /// place the profile is chosen. Null when closed, which keeps the last used profile.
        /// </summary>
        public static async Task<(UserId Id, bool AddAccount, bool Remember)> ShowStartupDialog(ProfileSelectorDialogViewModel viewModel)
        {
            // "Remember my choice" makes the pick the startup setting, so the question stops.
            CheckBox remember = new()
            {
                Content = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_PickerRemember],
                Margin = new Thickness(12, 8, 12, 0),
            };

            FAContentDialog contentDialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_PickerTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Continue],
                SecondaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_PickerAdd],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                DefaultButton = FAContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Children =
                    {
                        new ProfileSelectorDialog(viewModel),
                        remember,
                    },
                },
                Padding = new Thickness(0)
            };

            return await ContentDialogHelper.ShowAsync(contentDialog) switch
            {
                FAContentDialogResult.Primary => (viewModel.SelectedUserId, false, remember.IsChecked == true),
                FAContentDialogResult.Secondary => (UserId.Null, true, false),
                _ => (UserId.Null, false, false),
            };
        }

        public static async Task<(UserId Id, bool Result)> ShowInputDialog(ProfileSelectorDialogViewModel viewModel)
        {

            if (ConfigurationState.Instance.System.SkipUserProfilesManager)
            {
                UserId defaultId = viewModel.SelectedUserId;
                return (defaultId, true);
            }

            FAContentDialog contentDialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.UserProfileWindowTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Continue],
                SecondaryButtonText = string.Empty,
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                Content = new ProfileSelectorDialog(viewModel),
                Padding = new Thickness(0)
            };

            UserId result = UserId.Null;
            bool input = false;

            contentDialog.Closed += Handler;

            await ContentDialogHelper.ShowAsync(contentDialog);

            return (result, input);

            void Handler(FAContentDialog sender, FAContentDialogClosedEventArgs eventArgs)
            {
                if (eventArgs.Result == FAContentDialogResult.Primary)
                {
                    result = viewModel.SelectedUserId;
                    input = true;
                }
                else
                {
                    result = UserId.Null;
                    input = false;
                }
            }
        }
    }
}
