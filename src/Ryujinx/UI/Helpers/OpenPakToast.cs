using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.Configuration;
using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.Ava.UI.Helpers
{
    /// <summary>
    /// OpenPak's toasts, as the UX spec (§3.10) draws them in every emulator: a card in the
    /// corner the settings name (bottom right unless changed), a category line in small caps
    /// over one line of text, six seconds each with hovering holding it, at most four at once,
    /// and a click that opens whatever the toast is about.
    ///
    /// Its own notification manager rather than Ryujinx's: the corner is a setting here, and the
    /// emulator's other notifications keep theirs.
    /// </summary>
    public static class OpenPakToast
    {
        public enum Category
        {
            FriendOnline,
            FriendRequest,
            Invite,
            OpenPak,
            Saves,
        }

        private const int MaxToasts = 4;

        private static readonly TimeSpan _duration = TimeSpan.FromSeconds(6);

        private static WindowNotificationManager _manager;
        private static Window _host;
        private static bool _ready;
        private static readonly List<Action> _early = [];

        /// <summary>Put the toasts on the main window. Called once, beside Ryujinx's own notifications.</summary>
        public static void Attach(Window host)
        {
            _host = host;

            _manager = new WindowNotificationManager(host) { MaxItems = MaxToasts };

            Place(ConfigurationState.Instance.OpenPak.NotificationCorner.Value);

            ConfigurationState.Instance.OpenPak.NotificationCorner.Event += (_, e) =>
                Dispatcher.UIThread.Post(() => Place(e.NewValue));

            // Anything shown before the manager has a template would be lost; startup toasts
            // (a sign-in that expired) wait for it instead.
            _manager.TemplateApplied += (_, _) =>
            {
                _ready = true;

                foreach (Action show in _early)
                {
                    show();
                }

                _early.Clear();
            };
        }

        private static void Place(string corner)
        {
            if (_manager == null)
            {
                return;
            }

            (_manager.Position, _manager.Margin) = corner switch
            {
                ConfigurationState.OpenPakSection.CornerBottomLeft => (NotificationPosition.BottomLeft, new Thickness(15, 0, 0, 40)),
                ConfigurationState.OpenPakSection.CornerTopRight => (NotificationPosition.TopRight, new Thickness(0, 40, 15, 0)),
                ConfigurationState.OpenPakSection.CornerTopLeft => (NotificationPosition.TopLeft, new Thickness(15, 40, 0, 0)),
                _ => (NotificationPosition.BottomRight, new Thickness(0, 0, 15, 40)),
            };
        }

        /// <summary>
        /// Show one toast. Safe from any thread. Nothing shows while the main window is minimised
        /// or <c>Show notifications</c> is off; <paramref name="onClick"/> opens the toast's target.
        /// </summary>
        public static void Show(Category category, string text, Action onClick = null,
            NotificationType type = NotificationType.Information, byte[] picture = null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_manager == null)
                {
                    return;
                }

                if (!_ready)
                {
                    _early.Add(() => Present(category, text, onClick, type, picture));

                    return;
                }

                Present(category, text, onClick, type, picture);
            });
        }

        private static void Present(Category category, string text, Action onClick, NotificationType type, byte[] picture)
        {
            if (!ConfigurationState.Instance.OpenPak.ShowNotifications.Value || _host?.WindowState == WindowState.Minimized)
            {
                return;
            }

            Control content = Build(category, text, picture);

            // Never expires by itself: the timer below closes it, and pauses while the pointer
            // is over it so a name and a game can be read to the end.
            _manager.Show(content, type, TimeSpan.Zero, onClick);

            DispatcherTimer timer = new() { Interval = _duration };

            timer.Tick += (_, _) =>
            {
                timer.Stop();

                _manager.Close(content);
            };

            content.PointerEntered += (_, _) => timer.Stop();
            content.PointerExited += (_, _) => timer.Start();

            timer.Start();
        }

        private static Control Build(Category category, string text, byte[] picture)
        {
            Grid line = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };

            if (Decode(picture) is { } bitmap)
            {
                line.Children.Add(new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(16),
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill },
                });
            }

            TextBlock message = new()
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(message, line.Children.Count);
            line.Children.Add(message);

            return new StackPanel
            {
                Spacing = 4,
                Background = Brushes.Transparent,
                Children =
                {
                    new TextBlock
                    {
                        Text = LocaleManager.Instance[CategoryKey(category)],
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        LetterSpacing = 0.6,
                        Opacity = 0.75,
                    },
                    line,
                },
            };
        }

        private static LocaleKeys CategoryKey(Category category) => category switch
        {
            Category.FriendOnline => LocaleKeys.Dialog_OpenPak_ToastCatFriendOnline,
            Category.FriendRequest => LocaleKeys.Dialog_OpenPak_ToastCatFriendRequest,
            Category.Invite => LocaleKeys.Dialog_OpenPak_ToastCatInvite,
            Category.Saves => LocaleKeys.Dialog_OpenPak_ToastCatSaves,
            _ => LocaleKeys.Dialog_OpenPak_ToastCatOpenPak,
        };

        private static Bitmap Decode(byte[] picture)
        {
            if (picture is not { Length: > 0 })
            {
                return null;
            }

            try
            {
                return new Bitmap(new MemoryStream(picture));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
