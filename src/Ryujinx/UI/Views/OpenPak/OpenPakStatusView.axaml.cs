using Avalonia.Controls;
using Ryujinx.Ava.UI.ViewModels;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// Who is online, per title and per console network.
    ///
    /// Public on purpose, and it stays readable when nothing else here is: the person most
    /// likely to open this is the one whose sign-in is the part that is broken.
    /// </summary>
    public partial class OpenPakStatusView : UserControl
    {
        public OpenPakStatusView()
        {
            InitializeComponent();

            RefreshButton.Click += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshStatusAsync();
                }
            };
        }
    }
}
