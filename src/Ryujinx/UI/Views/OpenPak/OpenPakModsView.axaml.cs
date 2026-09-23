using Avalonia.Controls;
using Avalonia.Interactivity;
using Ryujinx.Ava.UI.ViewModels;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// The title's mod catalogue, installed straight into the folder Ryujinx already reads.
    ///
    /// Nothing new is registered: a mod installed here shows up in Manage Mods, which is where
    /// somebody would go to turn it off or delete it. Every package is checked against the hash
    /// the catalogue published before a single byte of it is written.
    /// </summary>
    public partial class OpenPakModsView : UserControl
    {
        public OpenPakModsView()
        {
            InitializeComponent();

            RefreshButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshModsAsync(force: true);
                }
            };

            TitleBox.SelectionChanged += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshModsAsync();
                }
            };
        }

        private async void OnInstall(object sender, RoutedEventArgs args)
        {
            if (DataContext is OpenPakViewModel model && (sender as Control)?.DataContext is OpenPakModModel mod)
            {
                await model.InstallModAsync(mod);
            }
        }

        private void OnUninstall(object sender, RoutedEventArgs args)
        {
            if (DataContext is OpenPakViewModel model && (sender as Control)?.DataContext is OpenPakModModel mod)
            {
                model.UninstallMod(mod);
            }
        }

        private async void OnFavourite(object sender, RoutedEventArgs args)
        {
            if (DataContext is OpenPakViewModel model && (sender as Control)?.DataContext is OpenPakModModel mod)
            {
                await model.FavouriteModAsync(mod);
            }
        }
    }
}
