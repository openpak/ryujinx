using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Types;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.HLE
{
    // nn::websocket's worker selects on its wakeup event fd and only drains it when select says it
    // is readable. Select used to answer EOPNOTSUPP, so the fd was never readable, never drained,
    // and the worker free-ran instead of sleeping.
    public class EventFdSelectTests
    {
        private static List<PollEvent> ReadSet(EventFileDescriptor eventFd)
        {
            return [new PollEvent(new PollEventData { InputEvents = PollEventTypeMask.Input }, eventFd)];
        }

        [Test]
        public void Select_reports_nothing_on_an_undrained_event_fd()
        {
            using EventFileDescriptor eventFd = new(0, EventFdFlags.None);

            List<PollEvent> events = ReadSet(eventFd);

            Assert.That(EventFileDescriptorPollManager.Instance.Select(events, 0, out int updatedCount), Is.EqualTo(LinuxError.SUCCESS));
            Assert.That(updatedCount, Is.Zero);
            Assert.That(events[0].Data.OutputEvents, Is.EqualTo((PollEventTypeMask)0));
        }

        [Test]
        public void Select_reports_the_wakeup_and_a_read_clears_it()
        {
            using EventFileDescriptor eventFd = new(0, EventFdFlags.None);

            Span<byte> one = stackalloc byte[8];
            one[0] = 1;
            eventFd.Write(out _, one);

            List<PollEvent> events = ReadSet(eventFd);

            EventFileDescriptorPollManager.Instance.Select(events, 0, out int updatedCount);

            Assert.That(updatedCount, Is.EqualTo(1));
            Assert.That(events[0].Data.OutputEvents.HasFlag(PollEventTypeMask.Input), Is.True);

            Span<byte> drain = stackalloc byte[8];
            eventFd.Read(out _, drain);

            List<PollEvent> afterDrain = ReadSet(eventFd);

            EventFileDescriptorPollManager.Instance.Select(afterDrain, 0, out int afterCount);

            Assert.That(afterCount, Is.Zero);
        }
    }
}
