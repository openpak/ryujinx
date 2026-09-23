using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.OpenPak;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The Penne frontline frames (nx-baas docs/penne-record-shape-2026-09-16.md), read against
    /// frames nx-baas's own builders produced: a kind-1 message inside a PutRecord, the opening
    /// HandoverResult, and a Pong that is neither.
    /// </summary>
    public class OpenPakPushTests
    {
        // penne.AppendFrame(penne.BuildMessage(...)) for a friend_request_received delivery.
        private const string MessageFrame = "50010000180000000000000000000e002000070008000c00100018000e000000000000042c00000018000000803bb16a000000009049b16a0000000000000000020000004e58000008000c0004000b00080000000800000000000001010000000c00000008000c000700080008000000000000011400000000000e00140004000a000c00000010000e000000640000000000010088000000040000004e0000007b2274797065223a22667269656e645f726571756573745f7265636569766564222c226964223a22636f72653a37222c22667269656e644964223a2231316366336231343233653131383366227d000017000000667269656e645f726571756573745f72656365697665640000000e00100000000400080000000c000e0000001c000000100000000400000000000000000000000000000000000000120000003078303130303030303030303030303030650000";

        // penne.AppendFrame(penne.BuildHandoverResult(0)).
        private const string HandoverFrame = "280000000c00000008000c0007000800080000000000000c0c00000000000600080007000600000000000000";

        // penne.AppendFrame(penne.BuildPong(5)).
        private const string PongFrame = "30000000100000000000000008000c000700080008000000000000110c000000000006000c000400060000000500000000000000";

        private static async Task<byte[]> Frame(string hex)
        {
            using MemoryStream stream = new(Convert.FromHexString(hex));

            return await PenneFrames.ReadFrameAsync(stream, CancellationToken.None);
        }

        [Test]
        public async Task AMessageCarriesItsNameAndBody()
        {
            byte[] frame = await Frame(MessageFrame);

            Assert.That(PenneFrames.EnvelopeType(frame), Is.EqualTo(PenneFrames.TypePutRecord));
            Assert.That(PenneFrames.TryReadMessage(frame, out string name, out string body), Is.True);
            Assert.That(name, Is.EqualTo("friend_request_received"));
            Assert.That(body, Does.Contain("\"friendId\":\"11cf3b1423e1183f\""));
            Assert.That(OpenPakSession.PushKind(name, body), Is.EqualTo("friend_request_received"));
        }

        [Test]
        public async Task TheHandoverIsRecognisedAndIsNoMessage()
        {
            byte[] frame = await Frame(HandoverFrame);

            Assert.That(PenneFrames.EnvelopeType(frame), Is.EqualTo(PenneFrames.TypeHandoverResult));
            Assert.That(PenneFrames.TryReadMessage(frame, out _, out _), Is.False);
        }

        [Test]
        public async Task OtherFramesAreNotMessages()
        {
            Assert.That(PenneFrames.TryReadMessage(await Frame(PongFrame), out _, out _), Is.False);
        }

        [Test]
        public async Task FramesFollowOneAnother()
        {
            using MemoryStream stream = new(Convert.FromHexString(HandoverFrame + MessageFrame));

            Assert.That(PenneFrames.EnvelopeType(await PenneFrames.ReadFrameAsync(stream, CancellationToken.None)),
                Is.EqualTo(PenneFrames.TypeHandoverResult));
            Assert.That(PenneFrames.EnvelopeType(await PenneFrames.ReadFrameAsync(stream, CancellationToken.None)),
                Is.EqualTo(PenneFrames.TypePutRecord));
            Assert.That(await PenneFrames.ReadFrameAsync(stream, CancellationToken.None), Is.Null);
        }

        [Test]
        public void AZeroLengthOrTruncatedFrameIsAnError()
        {
            Assert.ThrowsAsync<InvalidDataException>(() => Frame("00000000"));
            Assert.ThrowsAsync<EndOfStreamException>(() => Frame(MessageFrame[..40]));
        }

        [Test]
        public void GarbageIsNeverAMessage()
        {
            byte[] garbage = Convert.FromHexString(MessageFrame[8..]);

            for (int cut = 0; cut < garbage.Length; cut += 7)
            {
                Assert.DoesNotThrow(() => PenneFrames.TryReadMessage(garbage.AsSpan(0, cut), out _, out _));
            }

            Assert.That(PenneFrames.TryReadMessage([0xff, 0xff, 0xff, 0x7f, 1, 2, 3, 4], out _, out _), Is.False);
        }

        [Test]
        public void TheKindFallsBackToTheName()
        {
            Assert.That(OpenPakSession.PushKind("friend_deleted", "not json"), Is.EqualTo("friend_deleted"));
            Assert.That(OpenPakSession.PushKind("x", "{\"type\":\"presence_updated\"}"), Is.EqualTo("presence_updated"));
        }
    }
}
