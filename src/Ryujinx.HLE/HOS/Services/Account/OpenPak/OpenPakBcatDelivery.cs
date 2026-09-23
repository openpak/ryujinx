using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// Puts a title's OpenPak BCAT data into its delivery cache before the title starts, so a game
    /// that reads bcat:u at runtime (schedules, event data) finds what the news service publishes
    /// for it. The cache is the title's BCAT save data — the one EnsureApplicationSaveData creates
    /// when the NACP asks for a delivery cache — written in the layout the bcat server reads.
    ///
    /// A console's bcat daemon downloads this in the background and a game's RequestSyncDeliveryCache
    /// waits on it; here the download happens at launch instead, and the sync the game asks for
    /// finds it done.
    /// </summary>
    public static class OpenPakBcatDelivery
    {
        private const string Mount = "opbcat";

        /// <summary>Titles delivered to in this run, so a relaunch does not re-download.</summary>
        private static readonly HashSet<ulong> _delivered = [];

        public static void Deliver(FileSystemClient fs, ulong applicationId, long deliveryCacheSize)
        {
            if (!OpenPakConfig.Enabled || deliveryCacheSize <= 0 || !_delivered.Add(applicationId))
            {
                return;
            }

            string titleId = applicationId.ToString("x16");

            (OpenPakBcat.Outcome outcome, IReadOnlyList<OpenPakBcat.File> files) result;

            try
            {
                // Bounded: a launch waits on this at most a little, as a console's would find the
                // cache in whatever state its last background download left it.
                Task<(OpenPakBcat.Outcome, IReadOnlyList<OpenPakBcat.File>)> fetch =
                    Task.Run(() => OpenPakBcat.FetchAsync(titleId, CancellationToken.None));

                if (!fetch.Wait(System.TimeSpan.FromSeconds(15)))
                {
                    _delivered.Remove(applicationId);

                    return;
                }

                result = fetch.Result;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceBcat, $"[OpenPak] BCAT data for {titleId}: {exception.Message}");

                return;
            }

            if (result.outcome == OpenPakBcat.Outcome.Unreachable)
            {
                _delivered.Remove(applicationId);

                return;
            }

            Result written = Write(fs, applicationId, result.files);

            if (written.IsFailure())
            {
                Logger.Warning?.Print(LogClass.ServiceBcat,
                    $"[OpenPak] Could not write the delivery cache of {titleId}: {written.ToStringWithName()}");

                return;
            }

            Logger.Info?.Print(LogClass.ServiceBcat, result.outcome == OpenPakBcat.Outcome.Dataset
                ? $"[OpenPak] Delivered {result.files.Count} BCAT file(s) to {titleId}"
                : $"[OpenPak] No BCAT data is published for {titleId}");
        }

        /// <summary>
        /// Replace what the delivery cache holds with <paramref name="files"/>. With no files, a
        /// cache OpenPak never wrote to is left alone; one it did write (it has a directories.meta)
        /// is emptied, since the service no longer publishes what it holds.
        /// </summary>
        private static Result Write(FileSystemClient fs, ulong applicationId, IReadOnlyList<OpenPakBcat.File> files)
        {
            Result result = fs.MountBcatSaveData(Mount.ToU8Span(), new LibHac.Ncm.ApplicationId(applicationId));

            if (result.IsFailure())
            {
                return result;
            }

            try
            {
                bool had = fs.GetEntryType(out _, $"{Mount}:/directories.meta".ToU8Span()).IsSuccess();

                if (files.Count == 0 && !had)
                {
                    return Result.Success;
                }

                if (fs.GetEntryType(out _, $"{Mount}:/directories".ToU8Span()).IsSuccess())
                {
                    result = fs.DeleteDirectoryRecursively($"{Mount}:/directories".ToU8Span());

                    if (result.IsFailure())
                    {
                        return result;
                    }
                }

                if (had)
                {
                    fs.DeleteFile($"{Mount}:/directories.meta".ToU8Span()).IgnoreResult();
                }

                if (files.Count > 0)
                {
                    List<(string Directory, List<OpenPakBcat.File> Files)> groups = OpenPakBcat.Group(files);

                    result = fs.CreateDirectory($"{Mount}:/directories".ToU8Span());

                    foreach ((string directory, List<OpenPakBcat.File> group) in groups)
                    {
                        string root = $"{Mount}:/directories/{directory}";

                        if (result.IsSuccess())
                        {
                            result = fs.CreateDirectory(root.ToU8Span());
                        }

                        if (result.IsSuccess())
                        {
                            result = fs.CreateDirectory($"{root}/files".ToU8Span());
                        }

                        foreach (OpenPakBcat.File file in group)
                        {
                            if (result.IsSuccess())
                            {
                                result = WriteFile(fs, $"{root}/files/{file.Name}", file.Data);
                            }
                        }

                        if (result.IsSuccess())
                        {
                            result = WriteFile(fs, $"{root}/files.meta", OpenPakBcat.FilesMeta(group));
                        }
                    }

                    if (result.IsSuccess())
                    {
                        result = WriteFile(fs, $"{Mount}:/directories.meta", OpenPakBcat.DirectoriesMeta(groups));
                    }

                    if (result.IsFailure())
                    {
                        return result;
                    }
                }

                return fs.Commit(Mount.ToU8Span());
            }
            finally
            {
                fs.Unmount(Mount.ToU8Span());
            }
        }

        private static Result WriteFile(FileSystemClient fs, string path, byte[] data)
        {
            Result result = fs.CreateFile(path.ToU8Span(), data.Length);

            if (result.IsFailure())
            {
                return result;
            }

            result = fs.OpenFile(out FileHandle handle, path.ToU8Span(), OpenMode.Write);

            if (result.IsFailure())
            {
                return result;
            }

            try
            {
                return fs.WriteFile(handle, 0, data, WriteOption.Flush);
            }
            finally
            {
                fs.CloseFile(handle);
            }
        }
    }
}
