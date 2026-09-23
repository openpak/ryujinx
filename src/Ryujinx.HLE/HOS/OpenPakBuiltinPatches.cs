using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.HLE.Loaders.Mods;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.Security.Cryptography;

namespace Ryujinx.HLE.HOS
{
    /// <summary>
    /// The IPS patches OpenPak applies by itself while it is enabled, whatever mods the user has
    /// (or has turned off). They are ordinary IPS32 files, run through the same <see cref="IpsPatcher"/>
    /// and <see cref="MemPatch"/> as the user's exefs patches and matched the same way, by the
    /// module's build id. Each one first checks that the bytes it replaces are the ones it was made
    /// for; anything else is logged and left as shipped.
    ///
    /// Offset basis: IPS offsets for an NSO count the 0x100-byte NSO header, as Atmosphère's
    /// patcher does. <see cref="NsoExecutable.Program"/> has no header in front (text starts at 0),
    /// so, exactly like <see cref="ModLoader.ApplyNsoPatches"/>, the patch is applied with a
    /// protected offset of 0x100 and IPS offset X lands at Program[X - 0x100]. The check offsets
    /// below are written in the IPS basis too and converted the same way.
    /// </summary>
    internal static class OpenPakBuiltinPatches
    {
        private const int NsoHeaderSize = 0x100;

        /// <summary>
        /// One built-in IPS file: the build it is for (IPS file name, hex, zero padding ignored),
        /// and the SHA-1 of <paramref name="CheckedLength"/> bytes at <paramref name="CheckedOffset"/>
        /// (IPS basis) that must hold before it is applied.
        /// </summary>
        internal sealed record Patch(string Title, string BuildId, int CheckedOffset, int CheckedLength, string CheckedSha1, byte[] Ips);

        // 1C689518406930512C13DDF4217E767600000000000000000000000000000000.ips, 271 bytes,
        // sha256 5f44ed4e75889b035f4f16d49715211dfcffcdc7876b1a91e28bfda947f72c54
        // (servers/demonware/tools/ctr-key-patch). One IPS32 record: 256 bytes at 0x40B4E61.
        private static readonly byte[] _ctrKeyIps =
        [
            0x49, 0x50, 0x53, 0x33, 0x32, 0x04, 0x0B, 0x4E, 0x61, 0x01, 0x00, 0xA1, 0x62, 0x25, 0x17, 0xE7,
            0xB8, 0x24, 0x46, 0xA0, 0xCE, 0xFE, 0x88, 0x8C, 0x91, 0x0C, 0x77, 0x7D, 0x60, 0x99, 0xD2, 0x4D,
            0xED, 0x6C, 0x29, 0x4C, 0x04, 0x64, 0x9B, 0x31, 0x20, 0xF7, 0x83, 0x02, 0x36, 0xA4, 0x19, 0x68,
            0x4F, 0x47, 0x06, 0x9C, 0xD7, 0xB3, 0xDF, 0x14, 0x1C, 0x7D, 0xFB, 0x26, 0xD2, 0x05, 0x19, 0x74,
            0x91, 0xF1, 0xA0, 0x54, 0x4A, 0x80, 0x5F, 0x72, 0x66, 0x3F, 0x43, 0x42, 0x6A, 0xB9, 0xDC, 0xAF,
            0x94, 0x20, 0x8E, 0x4E, 0x34, 0x9E, 0x29, 0xDB, 0x1F, 0xD6, 0x44, 0x21, 0x7F, 0xE7, 0x80, 0xAD,
            0xA6, 0x57, 0x26, 0x4F, 0xE0, 0x4F, 0x8F, 0xC3, 0x1F, 0xFC, 0xE9, 0x9D, 0xAE, 0xC4, 0xC2, 0x44,
            0xAA, 0xC3, 0x51, 0x96, 0x66, 0x52, 0xAB, 0x72, 0x51, 0xFB, 0x57, 0x22, 0x43, 0xD0, 0x70, 0x75,
            0x75, 0x9B, 0x64, 0xC7, 0x76, 0x8D, 0x10, 0xC8, 0xDB, 0x57, 0x62, 0x55, 0xBA, 0x8F, 0xE3, 0x30,
            0x70, 0x2B, 0x8C, 0xB1, 0xFA, 0x6D, 0xBB, 0x6E, 0x36, 0x2F, 0x28, 0x9F, 0x1E, 0x74, 0x8E, 0x11,
            0x71, 0xF1, 0xC4, 0x35, 0x72, 0x76, 0xD3, 0xB6, 0x80, 0x43, 0x79, 0x27, 0xC6, 0xC8, 0x55, 0x41,
            0x17, 0xD2, 0xAD, 0x09, 0x3E, 0xFF, 0xE0, 0x60, 0xE1, 0x84, 0x29, 0xBC, 0xF8, 0xA1, 0xFE, 0xEC,
            0x64, 0x89, 0x56, 0xDF, 0xAB, 0x17, 0xF5, 0x84, 0x21, 0x80, 0xAB, 0xEE, 0x9C, 0x6E, 0x93, 0xD1,
            0x13, 0x1B, 0x26, 0xA1, 0x71, 0x67, 0xC0, 0x59, 0x90, 0xCE, 0x27, 0x3C, 0xEE, 0xCA, 0xEE, 0x59,
            0x20, 0xA9, 0x8A, 0xC8, 0x15, 0x15, 0xFD, 0x60, 0xC3, 0x6E, 0x37, 0xA0, 0x36, 0xE4, 0x26, 0x9A,
            0xFF, 0xF1, 0xE0, 0xC1, 0xC5, 0x30, 0x59, 0xAC, 0xF7, 0xB0, 0x67, 0xC5, 0x0B, 0x62, 0x2C, 0xB1,
            0xD4, 0x1A, 0x04, 0x4D, 0xBB, 0x26, 0x14, 0x20, 0xAC, 0xFF, 0x6F, 0x45, 0x45, 0x4F, 0x46,
        ];

        /// <summary>
        /// Every built-in patch. There is one, and it is the one sanctioned game patch: a data-only
        /// key swap, the same file the console setup (openpak.nro) installs to
        /// /atmosphere/exefs_patches/openpak_ctr_key/.
        ///
        /// Crash Team Racing Nitro-Fueled (0100F9F00C696000), final update v983040, main build
        /// 1C689518406930512C13DDF4217E7676: the game verifies Demonware's auth replies against an
        /// RSA-2048 key embedded in main. The patch swaps that key's modulus for OpenPak's so the
        /// OpenPak Demonware server's signatures verify; the exponent, the verifier and every
        /// instruction stay as shipped. The check is the whole embedded SubjectPublicKeyInfo
        /// (294 bytes at 0x40B4E40, the modulus 0x21 bytes in), which must be Demonware's key.
        /// </summary>
        internal static readonly Patch[] Patches =
        [
            new("Crash Team Racing Nitro-Fueled v983040 Demonware key", "1C689518406930512C13DDF4217E7676",
                0x40B4E40, 294, "F474DDA2E170D9700E329935E35EF7B3E9DF9C1C", _ctrKeyIps),
        ];

        /// <summary>Load-time entry point: applies whatever matches the modules being loaded.</summary>
        public static int Apply(ReadOnlySpan<IExecutable> programs)
        {
            if (!OpenPakConfig.Enabled)
            {
                return 0;
            }

            int count = 0;

            foreach (IExecutable program in programs)
            {
                if (program is NsoExecutable nso)
                {
                    count += Apply(Convert.ToHexString(nso.BuildId), nso.Program);
                }
            }

            return count;
        }

        /// <summary>
        /// Applies the built-in patch for <paramref name="buildId"/> to <paramref name="program"/>
        /// (a decompressed module without its NSO header). Returns the number of IPS records
        /// written: zero when no patch is for this build or the checked bytes are not as expected.
        /// </summary>
        internal static int Apply(string buildId, byte[] program)
        {
            // Same matching as the user's IPS files: build ids are compared without zero padding.
            string trimmed = (buildId ?? string.Empty).TrimEnd('0');

            foreach (Patch patch in Patches)
            {
                if (trimmed.Length == 0 || !string.Equals(patch.BuildId.TrimEnd('0'), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int at = patch.CheckedOffset - NsoHeaderSize;
                string found = at >= 0 && at <= program.Length - patch.CheckedLength
                    ? Convert.ToHexString(SHA1.HashData(program.AsSpan(at, patch.CheckedLength)))
                    : "out of range";

                if (!string.Equals(found, patch.CheckedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning?.Print(LogClass.ModLoader,
                        $"[OpenPak] {patch.Title}: bytes at 0x{patch.CheckedOffset:X} have SHA-1 {found}, expected {patch.CheckedSha1}; not applying");

                    return 0;
                }

                Logger.Info?.Print(LogClass.ModLoader, $"[OpenPak] Applying built-in IPS patch: {patch.Title}");

                return WriteIps(patch.Ips, program);
            }

            return 0;
        }

        /// <summary>Runs <paramref name="ips"/> through the emulator's IPS patcher onto a headerless module.</summary>
        internal static int WriteIps(byte[] ips, byte[] program)
        {
            using BinaryReader reader = new(new MemoryStream(ips, writable: false));

            MemPatch memPatch = new();
            new IpsPatcher(reader).AddPatches(memPatch);

            return memPatch.Patch(program, NsoHeaderSize);
        }
    }
}
