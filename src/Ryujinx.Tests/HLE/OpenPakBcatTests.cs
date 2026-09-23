using NUnit.Framework;
using Ryujinx.OpenPak;
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The delivery cache OpenPak writes for bcat:u: which manifest paths fit it, and the two
    /// meta files in the layout the bcat server reads (u32 1, then 0x40 / 0x80-byte entries).
    /// </summary>
    public class OpenPakBcatTests
    {
        [TestCase("event/season.bin", "event", "season.bin")]
        [TestCase("data/event_2026-09.byml", "data", "event_2026-09.byml")]
        public void APathIsADirectoryAndAFile(string path, string directory, string name)
        {
            Assert.That(OpenPakBcat.Split(path, out string dir, out string file), Is.True);
            Assert.That(dir, Is.EqualTo(directory));
            Assert.That(file, Is.EqualTo(name));
        }

        [TestCase("season.bin")]
        [TestCase("a/b/c.bin")]
        [TestCase("../x.bin")]
        [TestCase("event/")]
        [TestCase("event/a_name_that_is_far_too_long_for_bcat.bin")]
        [TestCase("ev ent/x.bin")]
        public void OtherPathsDoNotFit(string path)
        {
            Assert.That(OpenPakBcat.Split(path, out _, out _), Is.False);
        }

        [Test]
        public void TheMetasAreTheServersLayout()
        {
            OpenPakBcat.File[] files =
            [
                new("event", "season.bin", [1, 2, 3]),
                new("event", "rules.bin", [4]),
                new("news", "a.msbt", [5, 6]),
            ];

            var groups = OpenPakBcat.Group(files);

            Assert.That(groups.Select(group => group.Directory), Is.EqualTo(new[] { "event", "news" }));

            byte[] filesMeta = OpenPakBcat.FilesMeta(groups[0].Files);

            Assert.That(filesMeta.Length, Is.EqualTo(4 + (2 * 0x80)));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(filesMeta), Is.EqualTo(1));
            Assert.That(Encoding.ASCII.GetString(filesMeta, 4, 10), Is.EqualTo("season.bin"));
            Assert.That(filesMeta[4 + 10], Is.Zero);
            Assert.That(BinaryPrimitives.ReadInt64LittleEndian(filesMeta.AsSpan(4 + 0x28)), Is.EqualTo(3));
            Assert.That(filesMeta.AsSpan(4 + 0x30, 16).ToArray(), Is.EqualTo(MD5.HashData(new byte[] { 1, 2, 3 })));

            byte[] directoriesMeta = OpenPakBcat.DirectoriesMeta(groups);

            Assert.That(directoriesMeta.Length, Is.EqualTo(4 + (2 * 0x40)));
            Assert.That(Encoding.ASCII.GetString(directoriesMeta, 4 + 0x40, 4), Is.EqualTo("news"));
        }

        [Test]
        public void DuplicatesAndOverflowAreDropped()
        {
            OpenPakBcat.File[] files = [.. Enumerable.Range(0, 120).Select(i => new OpenPakBcat.File("d", $"f{i}", [])),
                new OpenPakBcat.File("D", "F0", [9])];

            Assert.That(OpenPakBcat.Group(files).Single().Files, Has.Count.EqualTo(OpenPakBcat.MaxEntries));
        }

        [Test]
        public void TheWindowIsHonoured()
        {
            DateTime now = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

            Assert.That(OpenPakBcat.InWindow("""{"valid_from":"2026-09-01T00:00:00Z","valid_until":null,"files":[]}""", now), Is.True);
            Assert.That(OpenPakBcat.InWindow("""{"valid_from":"2026-10-01T00:00:00Z","files":[]}""", now), Is.False);
            Assert.That(OpenPakBcat.InWindow("""{"valid_from":"2026-09-01T00:00:00Z","valid_until":"2026-09-20T00:00:00Z"}""", now), Is.False);
        }
    }
}
