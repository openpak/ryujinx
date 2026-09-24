using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using System;

namespace Ryujinx.HLE.HOS.Kernel.SupervisorCall
{
    // Not a supervisor call: this partial carries no SvcImpl attribute, so the syscall
    // generator does not turn it into guest-callable dispatch.
    partial class Syscall
    {
        Result ISyscallApi.GetEventSignaller(int writableHandle, out Action signal)
        {
            KProcess process = KernelStatic.GetCurrentProcess();

            KWritableEvent writableEvent = process.HandleTable.GetObject<KWritableEvent>(writableHandle);

            if (writableEvent == null)
            {
                signal = null;

                return KernelResult.InvalidHandle;
            }

            // KWritableEvent.Signal takes the kernel's critical section, which supports
            // foreign host threads (ServerBase's wake event and LDN's state event are
            // signalled that way), so the action is safe to invoke off any guest thread.
            signal = writableEvent.Signal;

            return Result.Success;
        }
    }
}
