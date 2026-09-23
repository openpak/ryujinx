using NUnit.Framework;
using Ryujinx.HLE.HOS;
using System;
using System.Linq;
using System.Security.Cryptography;

namespace Ryujinx.Tests.HLE
{
    public class OpenPakBuiltinPatchesTests
    {
        private const string CtrBuild = "1C689518406930512C13DDF4217E7676";

        private static OpenPakBuiltinPatches.Patch Ctr => OpenPakBuiltinPatches.Patches.Single();

        [Test]
        public void EmbeddedIpsIsTheShippedFile()
        {
            Assert.That(Ctr.Ips.Length, Is.EqualTo(271));
            Assert.That(Convert.ToHexString(SHA256.HashData(Ctr.Ips)),
                Is.EqualTo("5F44ED4E75889B035F4F16D49715211DFCFFCDC7876B1A91E28BFDA947F72C54"));
        }

        [Test]
        public void IpsOffsetsCountTheNsoHeader()
        {
            // The IPS record is at 0x40B4E61; without the 0x100 header that is 0x40B4D61 in Program.
            byte[] program = new byte[0x40B4D61 + 0x200];

            Assert.That(OpenPakBuiltinPatches.WriteIps(Ctr.Ips, program), Is.EqualTo(1));
            Assert.That(program.AsSpan(0x40B4D61, 256).ToArray(), Is.EqualTo(Ctr.Ips.AsSpan(11, 256).ToArray()));
            Assert.That(program[0x40B4D60], Is.Zero);
            Assert.That(program[0x40B4D61 + 256], Is.Zero);
        }

        [Test]
        public void UnexpectedKeyIsLeftAlone()
        {
            // Right build, but the embedded key is not Demonware's (all zeroes here).
            byte[] program = new byte[0x40B4D61 + 0x200];

            Assert.That(OpenPakBuiltinPatches.Apply(CtrBuild + new string('0', 32), program), Is.Zero);
            Assert.That(program.All(b => b == 0), Is.True);
        }

        [TestCase("1C689518406930512C13DDF4217E7677")]
        [TestCase("")]
        public void OtherBuildsAreLeftAlone(string buildId)
        {
            byte[] program = new byte[0x1000];

            Assert.That(OpenPakBuiltinPatches.Apply(buildId, program), Is.Zero);
            Assert.That(program.All(b => b == 0), Is.True);
        }

        [Test]
        public void ShortModuleIsLeftAlone()
        {
            byte[] program = new byte[0x1000];

            Assert.That(OpenPakBuiltinPatches.Apply(CtrBuild, program), Is.Zero);
            Assert.That(program.All(b => b == 0), Is.True);
        }
    }
}
