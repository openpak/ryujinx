using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.OpenPak;
using System;

namespace Ryujinx.HLE.HOS
{
    /// <summary>
    /// Instruction changes a few game builds need before they will accept an OpenPak server.
    ///
    /// Some NPLN titles do not stop at the system's trust store: they pin the certificate of the
    /// server they expect and check the peer's name themselves, so a server chaining to the
    /// OpenPak CA is refused no matter what the console trusts. For those builds a handful of
    /// instructions are swapped while the module is loaded. The user's files are never touched.
    ///
    /// Semantics every OpenPak emulator follows (Ryujinx is the reference, Citron and Eden match):
    ///   - only while OpenPak is enabled;
    ///   - only for the exact title id and main-module build id listed;
    ///   - every original instruction is checked first, and if any one differs the whole entry is
    ///     skipped and logged, so a half-patched module can never run;
    ///   - built-in changes go in before the user's own IPS/IPSwitch patches, so the check sees the
    ///     module as it shipped.
    ///
    /// Offset basis: offsets here are positions in the decompressed module image,
    /// <see cref="IExecutable.Program"/>, where the text segment begins at 0. An IPS/IPS32 file
    /// for the same change counts the 0x100-byte NSO header as well, so each of its offsets is
    /// exactly 0x100 higher than the one written here.
    /// </summary>
    internal static class OpenPakCodePatches
    {
        /// <summary>One instruction-sized change at an offset into the decompressed image.</summary>
        internal readonly record struct Change(int Offset, byte[] Expected, byte[] Written);

        /// <summary>A single build of a title and the changes it needs.</summary>
        internal sealed record Build(string Title, ulong TitleId, string MainBuildId, Change[] Changes);

        private static readonly byte[] _nop = [0x1F, 0x20, 0x03, 0xD5];

        /// <summary>Every build OpenPak changes. A new title is one more entry.</summary>
        internal static readonly Build[] Builds =
        [
            new("Super Mario Bros. Wonder 1.2.1", 0x010015100B514000, "FF773E90972D544EB79406EAA65396D53C43EFB9",
            [
                // LDRB W10, [X21, #0x38] -> MOV W10, #1: take the path that trusts the configured
                // local certificate instead of the pinned one.
                new(0xB03528, [0xAA, 0xE2, 0x40, 0x39], [0x2A, 0x00, 0x80, 0x52]),
                // CBNZ W0, +0x80 -> NOP: do not branch to the peer-name rejection.
                new(0xB02BBC, [0x00, 0x04, 0x00, 0x35], _nop),
                // B.NE +0x1B8 -> NOP: do not branch to the peer-name rejection.
                new(0xB02AA4, [0xC1, 0x0D, 0x00, 0x54], _nop),
            ]),
        ];

        /// <summary>Load-time entry point: applies whatever matches the modules being loaded.</summary>
        public static void Apply(ulong titleId, ReadOnlySpan<IExecutable> programs)
        {
            foreach (IExecutable program in programs)
            {
                if (program is NsoExecutable nso)
                {
                    Apply(OpenPakConfig.Enabled, titleId, nso.Name, Convert.ToHexString(nso.BuildId), nso.Program);
                }
            }
        }

        /// <summary>
        /// Applies the entry matching this module to <paramref name="image"/> (the decompressed
        /// module). Returns the number of instructions changed: zero when nothing matches, when
        /// OpenPak is off, or when the original bytes are not what the entry expects.
        /// </summary>
        internal static int Apply(bool enabled, ulong titleId, string moduleName, string buildId, Span<byte> image)
        {
            if (!enabled || !string.Equals(moduleName, "main", StringComparison.Ordinal))
            {
                return 0;
            }

            Build build = Find(titleId, buildId);

            if (build == null)
            {
                return 0;
            }

            foreach (Change change in build.Changes)
            {
                bool inRange = change.Offset >= 0 && change.Offset <= image.Length - change.Expected.Length;

                if (!inRange || !image.Slice(change.Offset, change.Expected.Length).SequenceEqual(change.Expected))
                {
                    string found = inRange ? Convert.ToHexString(image.Slice(change.Offset, change.Expected.Length)) : "out of range";

                    Logger.Warning?.Print(LogClass.ModLoader,
                        $"[OpenPak] {build.Title}: expected {Convert.ToHexString(change.Expected)} at 0x{change.Offset:X}, found {found}; leaving the module unchanged");

                    return 0;
                }
            }

            foreach (Change change in build.Changes)
            {
                change.Written.CopyTo(image[change.Offset..]);
            }

            Logger.Info?.Print(LogClass.ModLoader,
                $"[OpenPak] {build.Title}: changed {build.Changes.Length} instruction(s) so it accepts the OpenPak server");

            return build.Changes.Length;
        }

        private static Build Find(ulong titleId, string buildId)
        {
            // Build ids are 32 bytes on disk and usually 20 in use; the tail is zero padding.
            string trimmed = (buildId ?? string.Empty).TrimEnd('0');

            foreach (Build build in Builds)
            {
                if (build.TitleId == titleId && string.Equals(build.MainBuildId.TrimEnd('0'), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return build;
                }
            }

            return null;
        }
    }
}
