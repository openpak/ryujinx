using NUnit.Framework;
using Ryujinx.HLE.HOS.Ipc;
using System.IO;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// nn::ec's CreateAsyncInterface answers with two things at once: the completion event as a
    /// copy handle and the async interface itself, which on a session is a move handle MakeObject
    /// has already put in the descriptor. Until 2026-09-27 the service overwrote that descriptor
    /// with MakeCopy(event) and the object was lost, so the guest dereferenced a null interface and
    /// the emulator froze on Don't Starve Together.
    ///
    /// The service now rebuilds the descriptor with both. This is the wire check that it can carry
    /// both, in the order the guest reads them — copy handles first, then move.
    /// </summary>
    public class ShopServiceHandleTests
    {
        [Test]
        public void ACopyAndAMoveHandleBothSurvive()
        {
            const int EventHandle = 0x42;
            const int ObjectHandle = 0x99;

            IpcHandleDesc desc = new([EventHandle], [ObjectHandle]);

            using MemoryStream ms = new();
            using (var stream = desc.GetStream())
            {
                stream.CopyTo(ms);
            }

            ms.Position = 0;
            IpcHandleDesc read = new(new BinaryReader(ms));

            Assert.That(read.ToCopy, Is.EqualTo(new[] { EventHandle }), "the completion event");
            Assert.That(read.ToMove, Is.EqualTo(new[] { ObjectHandle }), "the async interface");
        }

        /// <summary>
        /// The regression itself: the old line. MakeCopy builds a descriptor with no move handles,
        /// so assigning it over MakeObject's is what dropped the interface.
        /// </summary>
        [Test]
        public void MakeCopyAloneCarriesNoObject()
        {
            Assert.That(IpcHandleDesc.MakeCopy(0x42).ToMove, Is.Empty);
        }
    }
}
