using Avalonia.Controls;
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

            DownloadButton.Click += async (_, _) =>
            {
                if (DataContext is not OpenPakViewModel { SelectedTitle: not null } model)
                {
                    return;
                }

                bool replace = await ContentDialogHelper.CreateChoiceDialog(
                    LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance.UpdateAndGetDynamicValue(
                        LocaleKeys.Dialog_OpenPak_SavesOverwriteConfirm, model.SelectedTitle.Name),
                    string.Empty);

                if (replace)
                {
                    await model.DownloadSaveAsync(model.SelectedTitle);
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
    }
}
