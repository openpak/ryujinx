using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// What the Penne frontline puts on the wire (nx-baas docs/penne-protocol.md,
    /// penne-record-shape-2026-09-16.md): a four-byte little-endian length, then a FlatBuffers
    /// envelope <c>{0: u8 type, 1: payload}</c>. A notification is a <c>PutRecord</c> (type 4)
    /// whose payload is <c>{0: [Entry]}</c>, an entry <c>{0: u8 kind = 1, 1: Message}</c> and a
    /// message <c>{0: string name, 4: string body}</c>, the body being the event's JSON.
    ///
    /// Only reading is here, and only as much FlatBuffers as those shapes need; nothing is
    /// verified beyond staying inside the buffer, because a frame that does not parse is simply
    /// not a notification.
    /// </summary>
    public static class PenneFrames
    {
        public const byte TypePutRecord = 0x04;
        public const byte TypeHandoverResult = 0x0c;
        public const byte TypeReset = 0x0d;

        /// <summary>The largest frame accepted; the console's own body bound is 4 KiB.</summary>
        public const int MaxFrame = 64 * 1024;

        /// <summary>One frame off the stream, or null at a clean end of stream.</summary>
        public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];

            if (!await FillAsync(stream, header, cancellationToken))
            {
                return null;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);

            // Zero and oversize are what the console's decoder refuses too (0x703, 0x303).
            if (length <= 0 || length > MaxFrame)
            {
                throw new InvalidDataException($"Penne frame length {length}");
            }

            byte[] frame = new byte[length];

            if (!await FillAsync(stream, frame, cancellationToken))
            {
                throw new EndOfStreamException("Penne frame cut short");
            }

            return frame;
        }

        private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int read = 0;

            while (read < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);

                if (count == 0)
                {
                    if (read == 0)
                    {
                        return false;
                    }

                    throw new EndOfStreamException("Penne frame cut short");
                }

                read += count;
            }

            return true;
        }

        /// <summary>The envelope's type byte, or null when the frame is not a readable envelope.</summary>
        public static byte? EnvelopeType(ReadOnlySpan<byte> frame)
            => Root(frame) is int root && U8(frame, root, 0) is byte type ? type : null;

        /// <summary>
        /// The notification a PutRecord carries: the message's name and its JSON body. False for
        /// any other frame, and for a PutRecord whose entry is a stored record (kind 2).
        /// </summary>
        public static bool TryReadMessage(ReadOnlySpan<byte> frame, out string name, out string body)
        {
            name = null;
            body = null;

            if (Root(frame) is not int root || U8(frame, root, 0) != TypePutRecord ||
                Table(frame, root, 1) is not int payload ||
                VectorTable(frame, payload, 0, 0) is not int entry ||
                U8(frame, entry, 0) != 1 ||
                Table(frame, entry, 1) is not int message)
            {
                return false;
            }

            name = String(frame, message, 0);
            body = String(frame, message, 4);

            return body != null;
        }

        // ---- FlatBuffers, read side ----

        private static int? Root(ReadOnlySpan<byte> buffer)
            => buffer.Length >= 4 ? Checked(buffer, (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer), 4) : null;

        private static int? Checked(ReadOnlySpan<byte> buffer, long position, int size)
            => position >= 0 && position + size <= buffer.Length ? (int)position : null;

        /// <summary>Where field <paramref name="slot"/> of the table at <paramref name="table"/> lives, or null.</summary>
        private static int? Field(ReadOnlySpan<byte> buffer, int table, int slot)
        {
            long vtable = table - (long)BinaryPrimitives.ReadInt32LittleEndian(buffer[table..]);

            if (Checked(buffer, vtable, 4) is not int start)
            {
                return null;
            }

            int vtableSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer[start..]);
            int entry = 4 + (2 * slot);

            if (entry + 2 > vtableSize || Checked(buffer, start + entry, 2) is not int at)
            {
                return null;
            }

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[at..]);

            return offset == 0 ? null : Checked(buffer, table + (long)offset, 1);
        }

        private static byte? U8(ReadOnlySpan<byte> buffer, int table, int slot)
            => Field(buffer, table, slot) is int at ? buffer[at] : null;

        private static int? Indirect(ReadOnlySpan<byte> buffer, int at)
            => Checked(buffer, at, 4) is int position
                ? Checked(buffer, position + (long)BinaryPrimitives.ReadUInt32LittleEndian(buffer[position..]), 4)
                : null;

        private static int? Table(ReadOnlySpan<byte> buffer, int table, int slot)
            => Field(buffer, table, slot) is int at ? Indirect(buffer, at) : null;

        private static string String(ReadOnlySpan<byte> buffer, int table, int slot)
        {
            if (Field(buffer, table, slot) is not int at || Indirect(buffer, at) is not int position)
            {
                return null;
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[position..]);

            return Checked(buffer, position + 4L, (int)Math.Min(length, int.MaxValue)) is int start
                ? Encoding.UTF8.GetString(buffer.Slice(start, (int)length))
                : null;
        }

        private static int? VectorTable(ReadOnlySpan<byte> buffer, int table, int slot, int index)
        {
            if (Field(buffer, table, slot) is not int at || Indirect(buffer, at) is not int vector)
            {
                return null;
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer[vector..]);

            return index < count ? Indirect(buffer, vector + 4 + (4 * index)) : null;
        }
    }
}
