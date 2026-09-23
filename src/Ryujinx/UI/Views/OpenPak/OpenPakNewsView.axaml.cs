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
    /// This shows and saves the dataset. Delivery happens on its own: a title whose NACP asks for
    /// a delivery cache gets the same dataset written into it when it starts
    /// (OpenPakBcatDelivery), and reads it through bcat:u like a console would.
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
                    await model.RefreshNewsAsync(force: true);
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
