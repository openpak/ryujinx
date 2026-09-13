using System;
using System.IO;
using System.IO.Compression;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// A title's savedata as one blob, because the cloud stores bytes and a Switch save is a
    /// directory tree.
    ///
    /// Zip, with no compression choice worth arguing about: saves are small, and what matters is
    /// that every emulator and the phone app can open what the other wrote. Paths are normalised
    /// to forward slashes so a save packed on Windows unpacks on Linux.
    /// </summary>
    public static class SaveArchive
    {
        /// <summary>Everything under <paramref name="directory"/>, as a zip.</summary>
        public static byte[] Pack(string directory)
        {
            using MemoryStream stream = new();

            using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');

                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                }
            }

            return stream.ToArray();
        }

        /// <summary>
        /// Write a packed save into <paramref name="directory"/>.
        ///
        /// Entries that would land outside it are refused rather than written: the archive came
        /// off the network, and `../` in a zip is the oldest trick there is.
        /// </summary>
        public static void Unpack(byte[] data, string directory)
        {
            string root = Path.GetFullPath(directory);

            Directory.CreateDirectory(root);

            using MemoryStream stream = new(data);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Name.Length == 0)
                {
                    continue;
                }

                string target = Path.GetFullPath(Path.Combine(root,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"the save archive tried to write outside the save directory: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));

                entry.ExtractToFile(target, overwrite: true);
            }
        }
    }
}
