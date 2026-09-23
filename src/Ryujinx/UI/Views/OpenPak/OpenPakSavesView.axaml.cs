using Avalonia.Controls;
using Avalonia.Interactivity;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// Cloud saves: the title's savedata, up and down, against the account's allowance.
    ///
    /// A download is asked about, because it replaces a save the person has been playing. The
    /// local copy is kept beside the savedata rather than deleted — a save is the only thing in
    /// this whole stack that cannot be fetched again if the cloud turns out to hold the older one.
    /// </summary>
    public partial class OpenPakSavesView : UserControl
    {
        public OpenPakSavesView()
        {
            InitializeComponent();

            RefreshButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshSavesAsync();
                }
            };

            UploadButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel { SelectedTitle: not null } model)
                {
                    await model.UploadSaveAsync(model.SelectedTitle);
                }
            };
        }

        private OpenPakViewModel Model => DataContext as OpenPakViewModel;

        private async void OnSignIn(object sender, RoutedEventArgs args) => await Windows.OpenPakWindow.SignInAsync();

        /// <summary>Both sides have a save: the conflict dialog decides (UX spec §3.9).</summary>
        private async void OnResolve(object sender, RoutedEventArgs args)
        {
            if (Model != null && (sender as Control)?.DataContext is OpenPakSaveModel row)
            {
                await Model.ResolveConflictAsync(row);
            }
        }

        /// <summary>Take the cloud copy: asked about, because it replaces the save being played.</summary>
        private async void OnDownload(object sender, RoutedEventArgs args)
        {
            if (Model == null || (sender as Control)?.DataContext is not OpenPakSaveModel { CanDownload: true } row)
            {
                return;
            }

            bool replace = await ContentDialogHelper.CreateChoiceDialog(
                LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SavesDownload],
                LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesOverwriteConfirm, row.TitleName),
                string.Empty);

            if (replace)
            {
                await Model.DownloadSaveAsync(row.Application);
            }
        }

        /// <summary>Keep the local copy: it goes up as the newest version, over whatever is there.</summary>
        private async void OnUpload(object sender, RoutedEventArgs args)
        {
            if (Model != null && (sender as Control)?.DataContext is OpenPakSaveModel { Application: not null } row)
            {
                await Model.UploadSaveAsync(row.Application);
            }
        }

        /// <summary>
        /// Clear the title's cloud save. Asked about, and the question says every version goes
        /// and that the save on this machine is not one of them: the cloud copy is the only one
        /// of the two that nobody can get back.
        /// </summary>
        private async void OnDelete(object sender, RoutedEventArgs args)
        {
            if (Model == null || (sender as Control)?.DataContext is not OpenPakSaveModel row)
            {
                return;
            }

            bool delete = await ContentDialogHelper.CreateChoiceDialog(
                LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_SavesDelete],
                LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesDeleteConfirm, row.TitleName),
                string.Empty);

            if (delete)
            {
                await Model.DeleteSaveAsync(row);
            }
        }
    }
}
