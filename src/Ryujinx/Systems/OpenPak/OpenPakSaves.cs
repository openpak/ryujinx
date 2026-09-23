using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems.OpenPak
{
    /// <summary>
    /// Cloud saves without anyone pressing anything: the newest cloud copy comes down before a
    /// title starts, and the local copy goes up when it exits.
    ///
    /// One marker file beside the save directory records which cloud version the local copy was
    /// last in step with. That is enough to tell the three cases apart: nothing changed in the
    /// cloud (start on what is here), the cloud moved on from another machine (take it, keep the
    /// old local copy beside it), and both sides have a save with no shared history. That last one
    /// is a conflict: the game starts on its local save, a second marker pauses automatic sync
    /// for the title, and the choice is left to the conflict dialog (UX spec §3.9).
    /// </summary>
    public static class OpenPakSaves
    {
        private const string Platform = "switch";

        // ponytail: the upload happens on every exit; skip-when-unchanged needs a content hash
        // of the directory, add it if the allowance turns out to fill with identical versions.
        public static async Task PushAsync(ApplicationData application)
        {
            if (application == null || !Ready())
            {
                return;
            }

            try
            {
                if (!ApplicationHelper.TryGetUserSaveDirectory(application, out string directory) ||
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                // Paused until somebody chooses: uploading now would bury the cloud copy the
                // conflict dialog is about to offer.
                if (IsConflicted(directory))
                {
                    return;
                }

                (string failure, string version) = await OpenPakApi.Instance.UploadSaveAsync(Platform, application.IdString,
                    SaveArchive.Pack(directory), Read(directory), Environment.MachineName, CancellationToken.None);

                if (failure != null)
                {
                    OpenPakToast.Show(OpenPakToast.Category.Saves,
                        LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesPushFailed, application.Name, failure),
                        () => _ = OpenPakWindow.Show(OpenPakWindow.Page.Saves),
                        Avalonia.Controls.Notifications.NotificationType.Warning);

                    return;
                }

                Remember(directory, version);

                OpenPakToast.Show(OpenPakToast.Category.Saves,
                    LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesPushed, application.Name),
                    () => _ = OpenPakWindow.Show(OpenPakWindow.Page.Saves),
                    Avalonia.Controls.Notifications.NotificationType.Success);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Save upload for {application.Name} failed: {exception.Message}");
            }
        }

        public static async Task PullAsync(ApplicationData application)
        {
            if (application == null || !Ready())
            {
                return;
            }

            try
            {
                if (!ApplicationHelper.TryGetUserSaveDirectory(application, out string directory))
                {
                    return;
                }

                if (IsConflicted(directory))
                {
                    AnnounceConflict(application);

                    return;
                }

                // Never blocks a launch for long (UX spec §5.1): on timeout the game starts on
                // what is here.
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

                OpenPakSaveDownload download = await OpenPakApi.Instance.DownloadSaveAsync(Platform, application.IdString, timeout.Token);

                if (download == null)
                {
                    return;
                }

                string known = Read(directory);
                bool localEmpty = !Directory.EnumerateFileSystemEntries(directory).Any();

                if (download.Version == known && !localEmpty)
                {
                    return;
                }

                if (known == null && !localEmpty)
                {
                    File.WriteAllText(ConflictMarker(directory), download.Version ?? string.Empty);

                    AnnounceConflict(application);

                    return;
                }

                Unpack(directory, download);

                OpenPakToast.Show(OpenPakToast.Category.Saves,
                    LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesPulled, application.Name),
                    () => _ = OpenPakWindow.Show(OpenPakWindow.Page.Saves));
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Save download for {application.Name} failed: {exception.Message}");
            }
        }

        private static void AnnounceConflict(ApplicationData application)
            => OpenPakToast.Show(OpenPakToast.Category.Saves,
                LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesBothExist, application.Name),
                () => _ = UI.Views.Dialog.OpenPakConflict.ResolveAsync(application),
                Avalonia.Controls.Notifications.NotificationType.Warning);

        /// <summary>
        /// Take the cloud's copy: the newest version replaces the local save, which is moved aside
        /// as a backup first. Null when it worked, otherwise why not.
        /// </summary>
        public static async Task<string> TakeCloudAsync(ApplicationData application, CancellationToken cancellationToken)
        {
            OpenPakSaveDownload download = await OpenPakApi.Instance.DownloadSaveAsync(Platform, application.IdString, cancellationToken);

            if (download == null)
            {
                return LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesNoCloud, application.Name);
            }

            if (!ApplicationHelper.TryGetUserSaveDirectory(application, out string directory))
            {
                return LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesNoLocal, application.Name);
            }

            Unpack(directory, download);

            return null;
        }

        /// <summary>
        /// Keep this machine's copy: it goes up as the newest version on top of
        /// <paramref name="cloudVersion"/>, so the cloud's copy stays behind it as a version.
        /// Null when it worked, otherwise why not.
        /// </summary>
        public static async Task<string> UploadAsync(ApplicationData application, string cloudVersion, CancellationToken cancellationToken)
        {
            if (!ApplicationHelper.TryGetUserSaveDirectory(application, out string directory) ||
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return LocaleManager.GetFormatted(LocaleKeys.Dialog_OpenPak_SavesNoLocal, application.Name);
            }

            (string failure, string version) = await OpenPakApi.Instance.UploadSaveAsync(Platform, application.IdString,
                SaveArchive.Pack(directory), cloudVersion, Environment.MachineName, cancellationToken);

            if (failure != null)
            {
                return failure;
            }

            Remember(directory, version);
            ClearConflict(directory);

            return null;
        }

        /// <summary>Whether automatic sync is paused for this title until a conflict is resolved.</summary>
        public static bool IsConflicted(ApplicationData application)
            => application != null && ApplicationHelper.TryGetUserSaveDirectory(application, out string directory) && IsConflicted(directory);

        private static bool IsConflicted(string directory) => File.Exists(ConflictMarker(directory));

        private static void ClearConflict(string directory)
        {
            if (File.Exists(ConflictMarker(directory)))
            {
                File.Delete(ConflictMarker(directory));
            }
        }

        /// <summary>Move the local save aside (one generation kept) and unpack the cloud's in its place.</summary>
        private static void Unpack(string directory, OpenPakSaveDownload download)
        {
            string backup = directory.TrimEnd(Path.DirectorySeparatorChar) + ".openpak-backup";

            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            {
                if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, recursive: true);
                }

                Directory.Move(directory, backup);
            }

            Directory.CreateDirectory(directory);

            SaveArchive.Unpack(download.Data, directory);

            Remember(directory, download.Version);
            ClearConflict(directory);
        }

        /// <summary>Record that the save in this directory matches this cloud version.</summary>
        public static void Remember(string directory, string version)
        {
            if (version != null)
            {
                File.WriteAllText(Marker(directory), version);
            }
        }

        /// <summary>The cloud version the save in this directory last matched, or null.</summary>
        public static string Read(string directory)
            => File.Exists(Marker(directory)) ? File.ReadAllText(Marker(directory)).Trim() : null;

        /// <summary>When anything in the save was last written, or null for an empty one.</summary>
        public static DateTime? LastWrite(string directory)
        {
            DateTime? latest = null;

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                DateTime written = File.GetLastWriteTime(file);

                if (latest == null || written > latest)
                {
                    latest = written;
                }
            }

            return latest;
        }

        // Beside the `0` slot, not inside it: the guest owns everything inside.
        private static string Marker(string directory)
            => Path.Combine(Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))!, "openpak-version");

        private static string ConflictMarker(string directory)
            => Path.Combine(Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))!, "openpak-conflict");

        private static bool Ready()
            => OpenPakConfig.Enabled && OpenPakApi.Instance.SignedIn && ConfigurationState.Instance.OpenPak.CloudSync.Value;
    }
}
