using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Mods;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.HLE.HOS
{
    /// <summary>
    /// Built-in per-title patches for Stardew Valley's online SDK.
    ///
    /// Stardew's NPLN client ships its own TLS: static OpenSSL linked into the title, verifying
    /// against the certificate chain Nintendo pins — and it refuses any replacement chain, so no
    /// network configuration on our side can satisfy it. Two byte patches settle it:
    ///
    /// 1. Certificate chain (file offset 0x079B4D10, memory 0x079B4C10): X509_verify_cert is
    ///    identified by the shape of its control flow; forcing its return to 1 lets the handshake
    ///    finish. Original prologue (8 bytes): FE 57 BE A9 F4 4F 01 A9 — replaced by MOV W0, #1; RET.
    /// 2. The "certificate accepted" byte (file offset 0x0782F6D0, memory 0x0782F5D0): the SSL
    ///    callback installation reads a byte the title never sets and chooses between an always-OK
    ///    callback and one that refuses everything but expiry errors. Forcing that read to 1 picks
    ///    the always-OK path. Without it the TLS handshake completes but the authentication call
    ///    cancels before HEADERS (nn::Result 2321-4992, UNAVAILABLE).
    ///
    /// Offsets are flat NSO file offsets (0x100 header included), which MemPatch.Patch converts.
    /// </summary>
    internal static class StardewPatches
    {
        // X509 chain verification: accept everything. 23 bytes.
        private static readonly byte[] _acceptAllCertificates =
        [
            0x49, 0x50, 0x53, 0x33, 0x32,             // "IPS32"
            0x07, 0x9B, 0x4D, 0x10, 0x00, 0x08,       // offset 0x079B4D10 (memory 0x079B4C10), 8 bytes
            0x20, 0x00, 0x80, 0x52,                   // MOV W0, #1
            0xC0, 0x03, 0x5F, 0xD6,                   // RET
            0x45, 0x45, 0x4F, 0x46,                   // "EEOF"
        ];

        // "Certificate accepted" byte forced to 1: always-OK callback. 19 bytes.
        private static readonly byte[] _alwaysOkCallback =
        [
            0x49, 0x50, 0x53, 0x33, 0x32,             // "IPS32"
            0x07, 0x82, 0xF6, 0xD0, 0x00, 0x04,       // offset 0x0782F6D0 (memory 0x0782F5D0), 4 bytes
            0x2A, 0x00, 0x80, 0x52,                   // MOV W10, #1
            0x45, 0x45, 0x4F, 0x46,                   // "EEOF"
        ];

        /// <summary>NSO build id -> patches. An unknown id receives nothing.</summary>
        private static readonly Dictionary<string, byte[][]> _patchesByBuildId = new()
        {
            ["E7F845093E8CBC68DACF011CCB620D6667B5A20B"] = [_acceptAllCertificates, _alwaysOkCallback],
        };

        /// <summary>
        /// Pour the patches known for this build id into the target. Returns how many were poured
        /// (zero for an unknown build id, exactly like a disk patch whose name matched nothing).
        /// </summary>
        public static int Apply(string buildId, MemPatch target)
        {
            if (string.IsNullOrEmpty(buildId) || !_patchesByBuildId.TryGetValue(buildId, out byte[][] patches))
            {
                // Named so a user report can carry the id home: an unmatched build is the whole
                // reason a title would miss its patches.
                Logger.Info?.Print(LogClass.ModLoader,
                    $"[OpenPak] No built-in Stardew patch for build {buildId}");

                return 0;
            }

            foreach (byte[] patch in patches)
            {
                using MemoryStream stream = new(patch);
                using BinaryReader reader = new(stream);

                new IpsPatcher(reader).AddPatches(target);
            }

            Logger.Info?.Print(LogClass.ModLoader,
                $"[OpenPak] Stardew: poured {patches.Length} built-in patch(es) (build {buildId})");

            return patches.Length;
        }
    }
}
