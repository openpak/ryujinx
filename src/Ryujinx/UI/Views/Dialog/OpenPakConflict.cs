using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.OpenPak;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    /// <summary>
    /// A cloud-save conflict (UX spec §3.9): this machine and the cloud each have a save and
    /// neither came from the other. Both sides are shown by date, and nothing is deleted whichever
    /// wins — taking the cloud's backs the local one up beside it, keeping this machine's uploads
    /// it as a new version over the cloud's.
    /// </summary>
    public static class OpenPakConflict
    {
        /// <summary>
        /// Ask which save to keep, then do it. Returns what happened, for a status line, or null
        /// when nothing did (<c>Decide later</c>). Opened from the Cloud saves page and from the
        /// conflict toast; the latter has no status line, so it toasts the answer itself.
        /// </summary>
        public static async Task<string> ResolveAsync(ApplicationData application, bool toastResult = true)
        {
            if (application == null)
            {
                return null;
            }

            OpenPakSaveVersion cloud = null;

            try
            {
                (IReadOnlyList<OpenPakSave> saves, _) = await OpenPakApi.Instance.SavesAsync(CancellationToken.None);

                cloud = saves.FirstOrDefault(save => save.TitleId.Equals(application.IdString, StringComparison.OrdinalIgnoreCase))?.Newest;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not read the cloud saves: {exception.Message}");
            }

            string none = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_None];

            DateTime? written = ApplicationHelper.TryGetUserSaveDirectory(application, out string directory)
                ? OpenPakSaves.LastWrite(directory)
                : null;

            string localLine = LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_ConflictLocalDetail,
                written is { } at ? OpenPakUi.Time(at) : none);

            string cloudLine = cloud == null
                ? none
                : LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_ConflictCloudDetail, cloud.Number,
                    string.IsNullOrEmpty(cloud.Device) ? none : cloud.Device, OpenPakUi.Time(cloud.CreatedAt));

            bool running = OpenPakUi.IsRunning(application.IdString);

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_ConflictTitle],
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_ConflictKeepLocal],
                SecondaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_ConflictTakeCloud],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_ConflictLater],
                // Deciding later is the default and what Escape does: a save is the one thing
                // here that cannot be fetched again.
                DefaultButton = FAContentDialogButton.Close,
                // The running game has the local save open; replacing it underneath is not safe.
                IsSecondaryButtonEnabled = !running && cloud != null,
                Content = BuildContent(application.Name, localLine, cloudLine, running),
            };

            FAContentDialogResult result = await ContentDialogHelper.ShowAsync(dialog);

            if (result is not (FAContentDialogResult.Primary or FAContentDialogResult.Secondary))
            {
                return null;
            }

            bool keepLocal = result == FAContentDialogResult.Primary;

            string done = LocaleManager.GetFormatted(keepLocal
                ? LocaleKeys.Dialog_OpenPak_SavesUploaded
                : LocaleKeys.Dialog_OpenPak_SavesDownloaded, application.Name);

            string failure;

            try
            {
                failure = keepLocal
                    ? await OpenPakSaves.UploadAsync(application, cloud?.Number.ToString(), CancellationToken.None)
                    : await OpenPakSaves.TakeCloudAsync(application, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Resolving the save conflict for {application.Name} failed: {exception.Message}");

                failure = OpenPakText.Failed;
            }

            string message = failure ?? done;

            if (toastResult)
            {
                OpenPakToast.Show(OpenPakToast.Category.Saves, message, null, failure == null
                    ? Avalonia.Controls.Notifications.NotificationType.Success
                    : Avalonia.Controls.Notifications.NotificationType.Warning);
            }

            return message;
        }

        private static Control BuildContent(string title, string localLine, string cloudLine, bool running)
        {
            Grid columns = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };

            columns.Children.Add(Column(0, LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_ConflictLocal], localLine));
            columns.Children.Add(Column(1, LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_ConflictCloud], cloudLine));

            StackPanel panel = new()
            {
                Width = 420,
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_ConflictBody, title),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    columns,
                },
            };

            if (running)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CommonStopGameFirst],
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            return panel;
        }

        private static Control Column(int column, string heading, string detail)
        {
            Border border = new()
            {
                Padding = new Avalonia.Thickness(12),
                CornerRadius = new Avalonia.CornerRadius(4),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = detail, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };

            Grid.SetColumn(border, column);

            return border;
        }
    }
}
