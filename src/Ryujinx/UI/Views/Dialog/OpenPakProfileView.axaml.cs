using Avalonia.Controls;
using Avalonia.Media.Imaging;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    /// <summary>Who this emulator is signed in as, the way the console's profile page shows it.</summary>
    public partial class OpenPakProfileView : UserControl
    {
        public OpenPakProfileView()
        {
            InitializeComponent();
        }

        public static async Task Show()
        {
            OpenPakSession session = OpenPakSession.Instance;
            OpenPakProfileView view = new();

            view.NameText.Text = session.Nickname;
            view.FriendCodeText.Text = session.FriendCode ?? "—";
            view.ServerText.Text = session.ServerAddress;

            byte[] avatar = await session.AvatarAsync(CancellationToken.None);

            if (avatar != null)
            {
                view.Avatar.Source = new Bitmap(new MemoryStream(avatar));
            }

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.MenuBar_OpenPak_Label],
                PrimaryButtonText = string.Empty,
                SecondaryButtonText = string.Empty,
                CloseButtonText = LocaleManager.Instance[LocaleKeys.InputDialogOk],
                Content = view,
            };

            await ContentDialogHelper.ShowAsync(dialog.ApplyStyles());
        }
    }
}
