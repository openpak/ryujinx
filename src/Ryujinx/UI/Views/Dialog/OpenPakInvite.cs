using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    /// <summary>
    /// The two halves of an online-play invitation the console's MyPage and overlay would show:
    /// picking who to invite, and being asked whether to join.
    /// </summary>
    public static class OpenPakInvite
    {
        /// <summary>
        /// The friend picker a game opens with StartFriendInvitation. At most
        /// <paramref name="max"/> can be ticked — with one, ticking another moves the tick, as a
        /// radio list would. Null when cancelled.
        /// </summary>
        public static async Task<IReadOnlyList<OpenPakFriend>> PickFriendsAsync(int max, Func<string, string> titleName)
        {
            // Online first: an invitation to somebody who is not there is one nobody answers.
            List<OpenPakFriend> friends = OpenPakAccount.Instance.Friends
                .OrderByDescending(friend => friend.Online)
                .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            List<(OpenPakFriend Friend, CheckBox Box)> rows = [];

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteSend],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Cancel],
                DefaultButton = FAContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };

            StackPanel list = new() { Spacing = 4 };

            using CancellationTokenSource closing = new();

            foreach (OpenPakFriend friend in friends)
            {
                OpenPakFriendModel model = new(friend, string.IsNullOrEmpty(friend.TitleId) ? null : titleName(friend.TitleId));

                Border avatar = new()
                {
                    CornerRadius = new CornerRadius(17),
                    ClipToBounds = true,
                    Background = Brush(Application.Current, "ThemeControlBorderColor"),
                    Child = new TextBlock
                    {
                        Text = model.Initial,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };

                CheckBox box = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children =
                        {
                            new Panel
                            {
                                Width = 34,
                                Height = 34,
                                Children =
                                {
                                    avatar,
                                    new Ellipse
                                    {
                                        Width = 11,
                                        Height = 11,
                                        HorizontalAlignment = HorizontalAlignment.Right,
                                        VerticalAlignment = VerticalAlignment.Bottom,
                                        Stroke = Brush(Application.Current, "ThemeDarkColor"),
                                        StrokeThickness = 2,
                                        Fill = model.PresenceBrush,
                                    },
                                },
                            },
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock { Text = model.DisplayName, FontWeight = FontWeight.SemiBold },
                                    new TextBlock { Text = model.Status, Opacity = 0.7 },
                                },
                            },
                        },
                    },
                };

                rows.Add((friend, box));
                list.Children.Add(box);

                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true && max == 1)
                    {
                        foreach ((_, CheckBox other) in rows.Where(row => row.Box != box))
                        {
                            other.IsChecked = false;
                        }
                    }

                    int picked = rows.Count(row => row.Box.IsChecked == true);

                    // Full is full: the rest wait until one is unticked.
                    if (max > 1)
                    {
                        foreach ((_, CheckBox other) in rows)
                        {
                            other.IsEnabled = other.IsChecked == true || picked < max;
                        }
                    }

                    dialog.IsPrimaryButtonEnabled = picked > 0;
                };

                _ = ShowAvatarAsync(friend, avatar, closing.Token);
            }

            dialog.Content = new StackPanel
            {
                Width = 400,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = friends.Count == 0
                            ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteNoFriends]
                            : LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_InviteHint, max),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new ScrollViewer
                    {
                        MaxHeight = 360,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = list,
                    },
                },
            };

            FAContentDialogResult result = await ContentDialogHelper.ShowAsync(dialog);

            closing.Cancel();

            if (result != FAContentDialogResult.Primary)
            {
                return null;
            }

            return rows.Where(row => row.Box.IsChecked == true).Select(row => row.Friend).Take(max).ToList();
        }

        /// <summary>"X invited you to play Y" with Join and Ignore. True for Join.</summary>
        public static async Task<bool> AskJoinAsync(string from, string title)
        {
            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteReceivedTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteJoin],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteIgnore],
                DefaultButton = FAContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Width = 380,
                    TextWrapping = TextWrapping.Wrap,
                    Text = LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_InviteReceived, from, title),
                },
            };

            return await ContentDialogHelper.ShowAsync(dialog) == FAContentDialogResult.Primary;
        }

        /// <summary>The initial stays until the picture arrives, and for good when there is none.</summary>
        private static async Task ShowAvatarAsync(OpenPakFriend friend, Border avatar, CancellationToken cancellationToken)
        {
            byte[] image = await Task.Run(() => OpenPakSession.Instance.FriendAvatarAsync(friend, cancellationToken));

            if (image is not { Length: > 0 } || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    avatar.Child = new Image
                    {
                        Source = new Bitmap(new MemoryStream(image)),
                        Stretch = Stretch.UniformToFill,
                    };
                }
                catch (Exception)
                {
                    // Not an image after all: the initial is still the right thing to show.
                }
            });
        }

        private static IBrush Brush(Application application, string key)
            => application != null && application.TryGetResource(key, application.ActualThemeVariant, out object value)
                ? value as IBrush ?? (value is Color color ? new SolidColorBrush(color) : null)
                : null;
    }
}
