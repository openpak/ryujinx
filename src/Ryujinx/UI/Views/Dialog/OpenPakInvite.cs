using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.OpenPak;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Globalization;
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
            // Online first, then by name: an invitation to somebody who is not there is one nobody answers.
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
                            : max == 1
                                ? LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteHintOne]
                                : LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_InviteHint, max),
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

        /// <summary>
        /// The offer to join, told the way the console's own overlay tells it: who is asking and
        /// what they look like, the game and its icon, anything they wrote, and when it
        /// was sent. Join and Ignore underneath; true for Join.
        /// </summary>
        public static async Task<bool> AskJoinAsync(OpenPakInvitation invitation, ApplicationData application)
        {
            Border avatar = new()
            {
                CornerRadius = new CornerRadius(24),
                ClipToBounds = true,
                Background = Brush(Application.Current, "ThemeControlBorderColor"),
                Child = new TextBlock
                {
                    Text = Initial(invitation.From),
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            StackPanel content = new()
            {
                Width = 380,
                Spacing = 14,
                Children =
                {
                    // Who is asking: their picture and their name, as a friend row reads.
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children =
                        {
                            new Panel { Width = 48, Height = 48, Children = { avatar } },
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock { Text = invitation.From, FontWeight = FontWeight.SemiBold },
                                    new TextBlock
                                    {
                                        Text = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteReceivedAction],
                                        Opacity = 0.7,
                                    },
                                },
                            },
                        },
                    },
                    TitleRow(invitation, application),
                },
            };

            // Their own words, when the game let them write any; most invitations carry none.
            if (MessageFor(invitation.Messages) is { } message)
            {
                content.Children.Add(new Border
                {
                    Background = Brush(Application.Current, "ThemeControlBorderColor"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8),
                    Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                });
            }

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteReceivedTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteJoin],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_InviteIgnore],
                DefaultButton = FAContentDialogButton.Primary,
                Content = content,
            };

            using CancellationTokenSource closing = new();

            _ = ShowAvatarAsync(invitation.SenderId, avatar, closing.Token);

            bool join = await ContentDialogHelper.ShowAsync(dialog) == FAContentDialogResult.Primary;

            closing.Cancel();

            return join;
        }

        /// <summary>
        /// The game, drawn the way the game list draws it. A title this install does not have is
        /// its application id and no icon: the invitation still says what it is for.
        /// </summary>
        private static Control TitleRow(OpenPakInvitation invitation, ApplicationData application)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 12 };

            if (application?.Icon is { Length: > 0 } icon)
            {
                row.Children.Add(new Border
                {
                    Width = 48,
                    Height = 48,
                    CornerRadius = new CornerRadius(4),
                    ClipToBounds = true,
                    Child = new Image { Source = new Bitmap(new MemoryStream(icon)), Stretch = Stretch.UniformToFill },
                });
            }

            StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };

            text.Children.Add(new TextBlock
            {
                Text = application?.Name ?? invitation.TitleId?.ToUpperInvariant(),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

            if (invitation.CreatedAt is { } sent)
            {
                text.Children.Add(new TextBlock
                {
                    Text = LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_InviteReceivedSent, OpenPakUi.Time(sent)),
                    Opacity = 0.7,
                });
            }

            row.Children.Add(text);

            return row;
        }

        /// <summary>
        /// What the sender wrote, in the language this install reads: the UI language first, then
        /// the module's own order. Null when they wrote nothing in any of them.
        /// </summary>
        internal static string MessageFor(IReadOnlyDictionary<string, string> messages)
        {
            if (messages is not { Count: > 0 })
            {
                return null;
            }

            string ui = LocaleManager.Instance.CurrentLanguageCode.Replace('_', '-');

            foreach (string language in new[] { ui, ui.Split('-')[0] }.Concat(OpenPakBaas.MessageLanguages))
            {
                if (messages.TryGetValue(language, out string text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        /// <summary>Stands in for the picture until it is here, and for good if it never arrives.</summary>
        private static string Initial(string name)
            => string.IsNullOrEmpty(name) ? "?" : StringInfo.GetNextTextElement(name).ToUpperInvariant();

        /// <summary>The initial stays until the picture arrives, and for good when there is none.</summary>
        private static Task ShowAvatarAsync(OpenPakFriend friend, Border avatar, CancellationToken cancellationToken)
            => ShowPictureAsync(() => OpenPakSession.Instance.FriendAvatarAsync(friend, cancellationToken), avatar, cancellationToken);

        /// <summary>The same, for somebody known only by the BAAS id an invitation names them by.</summary>
        private static Task ShowAvatarAsync(string senderId, Border avatar, CancellationToken cancellationToken)
            => ShowPictureAsync(() => OpenPakSession.Instance.SenderAvatarAsync(senderId, cancellationToken), avatar, cancellationToken);

        private static async Task ShowPictureAsync(Func<Task<byte[]>> fetch, Border avatar, CancellationToken cancellationToken)
        {
            byte[] image = await Task.Run(fetch);

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
