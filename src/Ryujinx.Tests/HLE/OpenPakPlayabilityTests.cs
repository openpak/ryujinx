using NUnit.Framework;
using Ryujinx.OpenPak;
using System.Text.Json;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The website's per-emulator playability verdict, read off one catalogue row. The contract is
    /// that only silence means "no opinion": a missing field, an empty object, another emulator's
    /// key, or a word outside the five the compatibility list uses. Every one of those leaves the
    /// emulator on its own bundled docs/compatibility.csv row rather than downgrading the title.
    ///
    /// The fallback itself lives in Ryujinx.Ava (OpenPakCompatibility / ApplicationData), which
    /// this test project does not reference; what is checked here is the one decision that drives
    /// it — whether the site said anything at all.
    /// </summary>
    public class OpenPakPlayabilityTests
    {
        private static string Read(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);

            return OpenPakApi.PlayabilityOf(document.RootElement);
        }

        [Test]
        public void OurOwnVerdictIsRead()
        {
            Assert.That(Read("""{"playability":{"Ryujinx":"playable","Citron":"ingame"}}"""), Is.EqualTo("playable"));
            Assert.That(Read("""{"playability":{"Ryujinx":"ingame"}}"""), Is.EqualTo("ingame"));
            Assert.That(Read("""{"playability":{"Ryujinx":"menus"}}"""), Is.EqualTo("menus"));
            Assert.That(Read("""{"playability":{"Ryujinx":"boots"}}"""), Is.EqualTo("boots"));
            Assert.That(Read("""{"playability":{"Ryujinx":"nothing"}}"""), Is.EqualTo("nothing"));
        }

        [Test]
        public void AnotherEmulatorsVerdictIsNotOurs()
            => Assert.That(Read("""{"playability":{"Citron":"playable","Eden":"boots"}}"""), Is.Null);

        [Test]
        public void SilenceIsNotAVerdict()
        {
            // An older website that does not send the field at all.
            Assert.That(Read("""{"name":"Some Game","status":"live"}"""), Is.Null);

            // The field present but empty, which the contract says is always the case when nothing
            // is known.
            Assert.That(Read("""{"playability":{}}"""), Is.Null);

            // A word nobody has a label for, and a null where a word was expected.
            Assert.That(Read("""{"playability":{"Ryujinx":"perfect"}}"""), Is.Null);
            Assert.That(Read("""{"playability":{"Ryujinx":null}}"""), Is.Null);

            // Not even an object.
            Assert.That(Read("""{"playability":"playable"}"""), Is.Null);
        }

        [Test]
        public void VerdictsAreCaseInsensitive()
            => Assert.That(Read("""{"playability":{"Ryujinx":"Playable"}}"""), Is.EqualTo("playable"));
    }
}
