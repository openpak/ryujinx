using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.OpenPak;
using System;
using System.Globalization;

namespace Ryujinx.Ava.Systems.OpenPak
{
    /// <summary>
    /// The small rules every OpenPak surface shares (UX spec §5): one time format, one idea of
    /// "the game that is running", one set of console names, and the string table the core
    /// project reads its sentences from.
    /// </summary>
    public static class OpenPakUi
    {
        /// <summary>
        /// Every time OpenPak shows, in the locale's short date and time (.NET "g"). Absolute on
        /// purpose: no plural forms, nothing to re-render as it ages, one rule everywhere.
        /// </summary>
        public static string Time(DateTime value)
            => (value.Kind == DateTimeKind.Local ? value : value.ToLocalTime()).ToString("g", CultureInfo.CurrentCulture);

        /// <summary>A server timestamp string as <see cref="Time(DateTime)"/>, or the string itself when it is not one.</summary>
        public static string Time(string value)
            => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? Time(parsed.UtcDateTime)
                : value ?? string.Empty;

        /// <summary>Whether a game is running at all.</summary>
        public static bool GameRunning => RyujinxApp.MainWindow?.ViewModel?.IsGameRunning ?? false;

        /// <summary>Whether this title is the one running now: its save and its mods are in use.</summary>
        public static bool IsRunning(string titleId)
        {
            if (!GameRunning || string.IsNullOrEmpty(titleId) || RyujinxApp.MainWindow.ViewModel.AppHost is not { } host)
            {
                return false;
            }

            return string.Equals(host.ApplicationId.ToString("x16"), titleId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The running title, out of the library, or null.</summary>
        public static ApplicationData RunningApplication
        {
            get
            {
                if (!GameRunning || RyujinxApp.MainWindow.ViewModel.AppHost is not { } host)
                {
                    return null;
                }

                string id = host.ApplicationId.ToString("x16");

                foreach (ApplicationData application in RyujinxApp.MainWindow.ViewModel.ApplicationLibrary.Applications.Items)
                {
                    if (application.IdString.Equals(id, StringComparison.OrdinalIgnoreCase))
                    {
                        return application;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// A presence namespace as a person reads it: the consoles OpenPak serves by name, and
        /// whatever else calls itself in capitalised rather than raw.
        /// </summary>
        public static string PlatformName(string ns) => ns?.Trim().ToLowerInvariant() switch
        {
            null or "" => string.Empty,
            "switch" => LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_PlatformSwitch],
            "wiiu" => LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_PlatformWiiU],
            "3ds" => LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Platform3ds],
            _ => char.ToUpperInvariant(ns.Trim()[0]) + ns.Trim()[1..],
        };

        /// <summary>
        /// Hand the core project the UI's string table: a spec key ("error.credentials") is looked
        /// up as Dialog_OpenPak_ErrorCredentials, the naming rule of UX spec §7.1.
        /// </summary>
        public static void InstallStrings()
        {
            OpenPakText.Lookup = key =>
                Enum.TryParse($"Dialog_OpenPak_{OpenPakText.PascalCase(key)}", out LocaleKeys localeKey)
                    ? LocaleManager.GetUnformatted(localeKey) is { } text && text != localeKey.ToString() ? text : null
                    : null;
        }
    }
}
