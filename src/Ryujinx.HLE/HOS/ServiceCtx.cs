using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;
using System.IO;

namespace Ryujinx.HLE.HOS
{
    class ServiceCtx
    {
        public Switch Device { get; }
        public KProcess Process { get; }
        public IVirtualMemoryManager Memory { get; }
        public KThread Thread { get; }
        public IpcMessage Request { get; }
        public IpcMessage Response { get; }
        public BinaryReader RequestData { get; }
        public BinaryWriter ResponseData { get; }
        public ulong ClientProcessId => Request.HandleDesc is { HasPId: true } ? Request.HandleDesc.PId : Process.Pid;

        // Deferred Bsd Poll support. A poll that would block parks itself instead of spinning the
        // single Bsd thread: the handler snapshots the pollfd array here, marks the request as
        // deferred, and the server loop re-runs it non-blocking until it is ready or overdue.
        public bool PollDeferRequested;
        public long PollDeadlineMs;
        public bool PollForceNonBlocking;
        public byte[] PollInputSnapshot;
        public int PollResult;

        public ServiceCtx(
            Switch device,
            KProcess process,
            IVirtualMemoryManager memory,
            KThread thread,
            IpcMessage request,
            IpcMessage response,
            BinaryReader requestData,
            BinaryWriter responseData)
        {
            Device = device;
            Process = process;
            Memory = memory;
            Thread = thread;
            Request = request;
            Response = response;
            RequestData = requestData;
            ResponseData = responseData;
        }
    }
}
