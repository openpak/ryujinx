using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems.OpenPak
{
    /// <summary>
    /// The launch after a crash: offer what <see cref="OpenPakCrashReports"/> saved, and send it
    /// only on a yes — this time, or always. "Never" throws them away and stops them being saved.
    /// </summary>
    public static class OpenPakCrashReportOffer
    {
        public static async Task RunAsync()
        {
            IReadOnlyList<OpenPakCrashReports.Report> reports = OpenPakCrashReports.Pending();

            if (reports.Count == 0)
            {
                return;
            }

            ConfigurationState.OpenPakSection config = ConfigurationState.Instance.OpenPak;

            if (config.CrashReports.Value == OpenPakCrashReports.Never)
            {
                OpenPakCrashReports.DiscardAll();

                return;
            }

            if (config.CrashReports.Value != OpenPakCrashReports.Always)
            {
                (bool send, bool remember) = await AskAsync(reports);

                if (remember)
                {
                    config.CrashReports.Value = send ? OpenPakCrashReports.Always : OpenPakCrashReports.Never;
                    ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);
                }

                if (!send)
                {
                    OpenPakCrashReports.DiscardAll();

                    return;
                }
            }

            int failed = 0;

            foreach (OpenPakCrashReports.Report report in reports)
            {
                if (await Task.Run(() => OpenPakApi.Instance.SendCrashReportAsync(report, CancellationToken.None)))
                {
                    OpenPakCrashReports.Discard(report);
                }
                else
                {
                    failed++;
                }
            }

            Logger.Info?.Print(LogClass.Application, $"[OpenPak] Crash reports sent: {reports.Count - failed} of {reports.Count}");

            if (failed == 0)
            {
                NotificationHelper.ShowSuccess(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CrashReportSent]);
            }
            else
            {
                NotificationHelper.ShowWarning(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CrashReportFailed]);
            }
        }

        private static async Task<(bool Send, bool Remember)> AskAsync(IReadOnlyList<OpenPakCrashReports.Report> reports)
        {
            string last = reports.LastOrDefault(report => report.Message.Length > 0)?.Message ?? "-";

            CheckBox remember = new()
            {
                Content = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CrashReportRemember],
                Margin = new Thickness(0, 10, 0, 0),
            };

            StackPanel content = new() { Spacing = 8, MaxWidth = 480 };

            content.Children.Add(new TextBlock
            {
                Text = LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_CrashReportMessage, reports.Count),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.SemiBold,
            });
            content.Children.Add(new TextBlock
            {
                Text = LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_CrashReportDetails, last),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(remember);

            FAContentDialog dialog = new()
            {
                Title = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CrashReportTitle],
                Content = content,
                PrimaryButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CrashReportSend],
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_CrashReportDontSend],
                DefaultButton = FAContentDialogButton.Close,
            };

            bool send = await ContentDialogHelper.ShowAsync(dialog) == FAContentDialogResult.Primary;

            return (send, remember.IsChecked == true);
        }
    }
}
