using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using SkiaSharp;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// An OpenPak avatar becomes a profile picture: whatever the account's image is, the profile
    /// stores the 256x256 JPEG a console's user image is.
    /// </summary>
    public class ProfileImageTests
    {
        [Test]
        public void APngBecomesA256JpegRegardlessOfItsSize()
        {
            using SKBitmap source = new(64, 32);

            source.Erase(SKColors.Teal);

            using SKImage image = SKImage.FromBitmap(source);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);

            using SKBitmap stored = SKBitmap.Decode(AccountManager.ProfileImage(png.ToArray()));

            Assert.Multiple(() =>
            {
                Assert.That(stored.Width, Is.EqualTo(256));
                Assert.That(stored.Height, Is.EqualTo(256));
            });
        }

        [Test]
        public void SomethingThatIsNotAnImageIsNotAPicture()
            => Assert.That(AccountManager.ProfileImage("this is not an avatar"u8.ToArray()), Is.Empty);
    }
}
