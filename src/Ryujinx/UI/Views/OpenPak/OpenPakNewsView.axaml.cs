using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Gommon;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.Utilities;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// The BCAT news a title would receive, as the news service currently holds it.
    ///
    /// This shows and saves the dataset; it does not deliver it. Delivery is a `bcat:*` service
    /// the emulator does not implement yet, so what this offers honestly is a look at what is
    /// there and a copy of it on disk, rather than a News channel that does not exist.
    /// </summary>
    public partial class OpenPakNewsView : UserControl
    {
        public OpenPakNewsView()
        {
            InitializeComponent();

            RefreshButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshNewsAsync();
                }
            };

            TitleBox.SelectionChanged += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshNewsAsync();
                }
            };

            SaveButton.Click += async (_, _) =>
            {
                if (DataContext is not OpenPakViewModel model ||
                    TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
                {
                    return;
                }

                Optional<IStorageFolder> folder = await storageProvider.OpenSingleFolderPickerAsync(
                    new FolderPickerOpenOptions { Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_NewsSave] });

                if (folder.HasValue)
                {
                    await model.SaveNewsAsync(folder.Value.Path.LocalPath);
                }
            };
        }
    }
}
