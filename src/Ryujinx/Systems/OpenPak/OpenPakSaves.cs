using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.UI.Helpers;
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
    /// old local copy beside it), and both sides have a save with no shared history (touch
    /// nothing, say so, and leave the choice to the Cloud saves page).
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

                (string failure, string version) = await OpenPakApi.Instance.UploadSaveAsync(Platform, application.IdString,
                    SaveArchive.Pack(directory), Read(directory), Environment.MachineName, CancellationToken.None);

                if (failure != null)
                {
                    NotificationHelper.ShowWarning(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title], failure);

                    return;
                }

                Remember(directory, version);

                NotificationHelper.ShowSuccess(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_SavesPushed, application.Name));
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
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

                OpenPakSaveDownload download = await OpenPakApi.Instance.DownloadSaveAsync(Platform, application.IdString, timeout.Token);

                if (download == null || !ApplicationHelper.TryGetUserSaveDirectory(application, out string directory))
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
                    NotificationHelper.ShowWarning(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                        LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_SavesBothExist, application.Name));

                    return;
                }

                if (!localEmpty)
                {
                    string backup = directory.TrimEnd(Path.DirectorySeparatorChar) + ".openpak-backup";

                    if (Directory.Exists(backup))
                    {
                        Directory.Delete(backup, recursive: true);
                    }

                    Directory.Move(directory, backup);
                    Directory.CreateDirectory(directory);
                }

                SaveArchive.Unpack(download.Data, directory);

                Remember(directory, download.Version);

                NotificationHelper.ShowInformation(LocaleManager.Instance[LocaleKeys.Dialog_OpenPak_Title],
                    LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.Dialog_OpenPak_SavesPulled, application.Name));
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Save download for {application.Name} failed: {exception.Message}");
            }
        }

        /// <summary>Record that the save in this directory matches this cloud version.</summary>
        public static void Remember(string directory, string version)
        {
            if (version != null)
            {
                File.WriteAllText(Marker(directory), version);
            }
        }

        private static string Read(string directory)
            => File.Exists(Marker(directory)) ? File.ReadAllText(Marker(directory)).Trim() : null;

        // Beside the `0` slot, not inside it: the guest owns everything inside.
        private static string Marker(string directory)
            => Path.Combine(Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))!, "openpak-version");

        private static bool Ready() => OpenPakConfig.Enabled && OpenPakApi.Instance.SignedIn;
    }
}
