using NUnit.Framework;
using Ryujinx.HLE.HOS;
using System;
using System.Linq;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The built-in instruction changes run on a game's own code, so the tests hold them to the
    /// promise: the exact build or nothing, the shipped bytes or nothing, and all of an entry or
    /// none of it.
    /// </summary>
    public class OpenPakCodePatchesTests
    {
        private static OpenPakCodePatches.Build Wonder => OpenPakCodePatches.Builds.Single(b => b.TitleId == 0x010015100B514000);

        /// <summary>A zeroed image large enough for the entry, with the shipped bytes in place.</summary>
        private static byte[] ShippedImage(OpenPakCodePatches.Build build)
        {
            byte[] image = new byte[build.Changes.Max(c => c.Offset + c.Expected.Length) + 0x100];

            foreach (OpenPakCodePatches.Change change in build.Changes)
            {
                change.Expected.CopyTo(image, change.Offset);
            }

            return image;
        }

        private static int Apply(bool enabled, ulong titleId, string module, string buildId, byte[] image)
            => OpenPakCodePatches.Apply(enabled, titleId, module, buildId, image);

        [Test]
        public void WonderOffsetsAreDecompressedImageOffsets()
        {
            // The IPS offsets for the same change are these plus the 0x100 NSO header.
            Assert.That(Wonder.Changes.Select(c => c.Offset), Is.EqualTo(new[] { 0xB03528, 0xB02BBC, 0xB02AA4 }));
        }

        [Test]
        public void MatchingBuildIsChanged()
        {
            OpenPakCodePatches.Build build = Wonder;
            byte[] image = ShippedImage(build);

            // Module build ids arrive as 32 bytes of hex with zero padding.
            string paddedId = build.MainBuildId + new string('0', 24);

            Assert.That(Apply(true, build.TitleId, "main", paddedId, image), Is.EqualTo(3));
            Assert.That(image.AsSpan(0xB03528, 4).ToArray(), Is.EqualTo(new byte[] { 0x2A, 0x00, 0x80, 0x52 }));
            Assert.That(image.AsSpan(0xB02BBC, 4).ToArray(), Is.EqualTo(new byte[] { 0x1F, 0x20, 0x03, 0xD5 }));
            Assert.That(image.AsSpan(0xB02AA4, 4).ToArray(), Is.EqualTo(new byte[] { 0x1F, 0x20, 0x03, 0xD5 }));
        }

        [Test]
        public void NothingChangesWhenOpenPakIsOff()
        {
            OpenPakCodePatches.Build build = Wonder;
            byte[] image = ShippedImage(build);
            byte[] before = (byte[])image.Clone();

            Assert.That(Apply(false, build.TitleId, "main", build.MainBuildId, image), Is.Zero);
            Assert.That(image, Is.EqualTo(before));
        }

        [TestCase(0x010015100B514000UL, "main", "FF773E90972D544EB79406EAA65396D53C43EFB8")]
        [TestCase(0x010015100B514800UL, "main", "FF773E90972D544EB79406EAA65396D53C43EFB9")]
        [TestCase(0x010015100B514000UL, "subsdk0", "FF773E90972D544EB79406EAA65396D53C43EFB9")]
        [TestCase(0x010015100B514000UL, "main", "")]
        public void OtherModulesAreLeftAlone(ulong titleId, string module, string buildId)
        {
            byte[] image = ShippedImage(Wonder);
            byte[] before = (byte[])image.Clone();

            Assert.That(Apply(true, titleId, module, buildId, image), Is.Zero);
            Assert.That(image, Is.EqualTo(before));
        }

        [Test]
        public void OneUnexpectedInstructionSkipsTheWholeEntry()
        {
            OpenPakCodePatches.Build build = Wonder;
            byte[] image = ShippedImage(build);

            // The last change checked differs; the first two must not have been written either.
            image[0xB02AA4] ^= 0xFF;
            byte[] before = (byte[])image.Clone();

            Assert.That(Apply(true, build.TitleId, "main", build.MainBuildId, image), Is.Zero);
            Assert.That(image, Is.EqualTo(before));
        }

        [Test]
        public void ShortImageIsLeftAlone()
        {
            OpenPakCodePatches.Build build = Wonder;
            byte[] image = new byte[0x1000];

            Assert.That(Apply(true, build.TitleId, "main", build.MainBuildId, image), Is.Zero);
            Assert.That(image.All(b => b == 0), Is.True);
        }
    }
}
