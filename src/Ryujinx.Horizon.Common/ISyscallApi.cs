using Ryujinx.Memory;
using System;

namespace Ryujinx.Horizon.Common
{
    public interface ISyscallApi
    {
        Result SetHeapSize(out ulong address, ulong size);

        void SleepThread(long timeout);

        Result CloseHandle(int handle);

        Result WaitSynchronization(out int handleIndex, ReadOnlySpan<int> handles, long timeout);
        Result CancelSynchronization(int handle);

        Result GetProcessId(out ulong pid, int handle);

        Result ConnectToNamedPort(out int handle, string name);
        Result SendSyncRequest(int handle);
        Result CreateSession(out int serverSessionHandle, out int clientSessionHandle, bool isLight, string name);
        Result AcceptSession(out int sessionHandle, int portHandle);
        Result ReplyAndReceive(out int handleIndex, ReadOnlySpan<int> handles, int replyTargetHandle, long timeout);

        Result CreateEvent(out int writableHandle, out int readableHandle);
        Result SignalEvent(int handle);
        /// <summary>
        /// Resolves a writable event handle of the calling process to an action that signals it
        /// from any host thread. SignalEvent needs the kernel's thread-static context, which only
        /// a guest or service thread carries; the action does not.
        /// </summary>
        Result GetEventSignaller(int writableHandle, out Action signal);
        Result ClearEvent(int handle);
        Result ResetSignal(int handle);

        Result CreatePort(out int serverPortHandle, out int clientPortHandle, int maxSessions, bool isLight, string name);
        Result ManageNamedPort(out int handle, string name, int maxSessions);
        Result ConnectToPort(out int clientSessionHandle, int clientPortHandle);

        IExternalEvent GetExternalEvent(int handle);
        IVirtualMemoryManager GetMemoryManagerByProcessHandle(int handle);
        ulong GetTransferMemoryAddress(int handle);
    }
}
