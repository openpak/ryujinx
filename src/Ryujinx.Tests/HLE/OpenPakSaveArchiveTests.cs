using NUnit.Framework;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// A save goes up as a zip and comes back down as one. The round trip matters because the
    /// bytes are somebody's progress; the traversal test matters because the archive arrives off
    /// the network, and an entry naming its way out of the save directory would be writing
    /// wherever it liked with the emulator's permissions.
    /// </summary>
    public class OpenPakSaveArchiveTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "openpak-save-tests-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Test]
        public void RoundTripKeepsNestedFilesAndContents()
        {
            string source = Path.Combine(_directory, "source");
            string nested = Path.Combine(source, "sub", "deeper");

            Directory.CreateDirectory(nested);

            File.WriteAllText(Path.Combine(source, "save.dat"), "top level");
            File.WriteAllText(Path.Combine(nested, "extra.bin"), "nested");

            byte[] packed = SaveArchive.Pack(source);

            string target = Path.Combine(_directory, "target");

            SaveArchive.Unpack(packed, target);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(target, "save.dat")), Is.EqualTo("top level"));
                Assert.That(File.ReadAllText(Path.Combine(target, "sub", "deeper", "extra.bin")), Is.EqualTo("nested"));
            });
        }

        [Test]
        public void UnpackOverwritesAnExistingSave()
        {
            string source = Path.Combine(_directory, "source");
            string target = Path.Combine(_directory, "target");

            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);

            File.WriteAllText(Path.Combine(source, "save.dat"), "new");
            File.WriteAllText(Path.Combine(target, "save.dat"), "old");

            SaveArchive.Unpack(SaveArchive.Pack(source), target);

            Assert.That(File.ReadAllText(Path.Combine(target, "save.dat")), Is.EqualTo("new"));
        }

        [Test]
        public void UnpackRefusesAnEntryThatEscapesTheSaveDirectory()
        {
            string target = Path.Combine(_directory, "target");
            string escaped = Path.Combine(_directory, "escaped.txt");

            using MemoryStream stream = new();

            using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry entry = archive.CreateEntry("../escaped.txt");

                using StreamWriter writer = new(entry.Open(), Encoding.UTF8);

                writer.Write("this must not be written");
            }

            Assert.Multiple(() =>
            {
                Assert.Throws<InvalidDataException>(() => SaveArchive.Unpack(stream.ToArray(), target));
                Assert.That(File.Exists(escaped), Is.False);
            });
        }
    }
}
