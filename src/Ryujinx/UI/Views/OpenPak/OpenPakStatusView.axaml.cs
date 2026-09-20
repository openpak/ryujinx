using Avalonia.Controls;
using Avalonia.Threading;
using Ryujinx.Ava.UI.ViewModels;
using System;

namespace Ryujinx.Ava.UI.Views.OpenPak
{
    /// <summary>
    /// What is up, who is online, and what this machine's own session looks like.
    ///
    /// Public on purpose, and it stays readable when nothing else here is: the person most
    /// likely to open this is the one whose sign-in is the part that is broken.
    /// </summary>
    public partial class OpenPakStatusView : UserControl
    {
        // The status box checks every minute and this window is watched rather than read, so a
        // page that never moves is a page somebody leaves open believing a stale answer.
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

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

            // Only while this page is the one on screen: the window keeps every page alive, and
            // six of them polling behind the one being looked at is six pointless requests.
            _timer.Tick += async (_, _) =>
            {
                if (DataContext is OpenPakViewModel model)
                {
                    await model.RefreshStatusAsync();
                }
            };

            AttachedToVisualTree += (_, _) => _timer.Start();
            DetachedFromVisualTree += (_, _) => _timer.Stop();
        }
    }
}
