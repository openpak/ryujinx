using Ryujinx.Ava.Common.Locale;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Ryujinx.Ava.Systems.OpenPak
{
    /// <summary>
    /// Putting a catalogue mod where Ryujinx's own mod loader will find it.
    ///
    /// Ryujinx reads `mods/contents/&lt;title id&gt;/&lt;name&gt;/romfs` (or `exefs`), so an install is
    /// unpacking the published zip into a directory named after the mod. Nothing is registered
    /// anywhere: the existing Manage Mods window lists what is on disk, so a mod installed here
    /// appears there and can be disabled or deleted there, which is where a person would look.
    /// </summary>
    public static class OpenPakMods
    {
        /// <summary>The directory this mod would occupy, whether or not it is there yet.</summary>
        private static string DirectoryFor(string titleId, OpenPakMod mod)
            => Path.Combine(ModLoader.GetApplicationDir(ModLoader.GetModsBasePath(), titleId.ToLowerInvariant()),
                Sanitise(mod.Slug ?? mod.Name));

        /// <summary>Whether this install already has the mod on disk.</summary>
        public static bool IsInstalled(string titleId, OpenPakMod mod)
        {
            try
            {
                string directory = DirectoryFor(titleId, mod);

                return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Unpack a verified package into the title's mod directory, replacing an older copy of
        /// the same mod. The caller has already checked the package against its published hash.
        /// </summary>
        public static bool Install(string titleId, OpenPakMod mod, byte[] package, out string failure)
        {
            failure = null;

            try
            {
                string directory = DirectoryFor(titleId, mod);
                string root = Path.GetFullPath(directory);

                // An upgrade replaces the mod rather than merging into it: files the new version
                // dropped would otherwise stay behind and keep being applied.
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                Directory.CreateDirectory(root);

                using MemoryStream stream = new(package);
                using ZipArchive archive = new(stream, ZipArchiveMode.Read);

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.Name.Length == 0)
                    {
                        continue;
                    }

                    string target = Path.GetFullPath(Path.Combine(root,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

                    // The package came off the network. An entry naming its way out of the mod
                    // directory is refused, and the half-written mod goes with it.
                    if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        Directory.Delete(root, recursive: true);

                        failure = LocaleManager.Instance.UpdateAndGetDynamicValue(
                            LocaleKeys.Dialog_OpenPak_ModsRefused, mod.Name);

                        return false;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    entry.ExtractToFile(target, overwrite: true);
                }

                Logger.Info?.Print(LogClass.Application,
                    $"[OpenPak] Installed {mod.Name} {mod.Version} for {titleId} into {root}");

                return true;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not install {mod.Name}: {exception.Message}");

                failure = LocaleManager.Instance.UpdateAndGetDynamicValue(
                    LocaleKeys.Dialog_OpenPak_ModsInstallFailed, mod.Name);

                return false;
            }
        }

        /// <summary>Remove the mod's directory: the same folder Manage Mods would delete.</summary>
        public static bool Uninstall(string titleId, OpenPakMod mod, out string failure)
        {
            failure = null;

            try
            {
                string directory = DirectoryFor(titleId, mod);

                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                Logger.Info?.Print(LogClass.Application, $"[OpenPak] Uninstalled {mod.Name} for {titleId}");

                return true;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not uninstall {mod.Name}: {exception.Message}");

                failure = OpenPakText.Failed;

                return false;
            }
        }

        /// <summary>A slug is already tame, but it is still going on somebody's filesystem.</summary>
        private static string Sanitise(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '-');
            }

            return name.Trim().Length == 0 ? "openpak-mod" : name.Trim();
        }
    }
}
