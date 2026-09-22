using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;
using System;

namespace Ryujinx.HLE.HOS.Services.Npns
{
    // Push-notification subscription side (nn::npns::INpnsUser), as ACNH
    // actually speaks it. Command numbers and shapes from SwitchBrew's NPNS
    // services page (facts only); behavior is OpenPak's own: subscribe calls
    // succeed, the receive/state-change events exist but never signal, so the
    // game polls its servers instead of waiting on pushes that cannot arrive
    // without a notification backend.
    //
    // Added for ACNH online entry, which calls 26 (ListenToMyApplicationId)
    // right after its web-API handshake; without any method the call throws
    // ServiceNotImplementedException and takes the emulator down with it.
    [Service("npns:u")]
    class INpnsUser : IpcService
    {
        private readonly KEvent _receiveEvent;
        private readonly KEvent _stateChangeEvent;

        public INpnsUser(ServiceCtx context)
        {
            _receiveEvent = new KEvent(context.Device.System.KernelContext);
            _stateChangeEvent = new KEvent(context.Device.System.KernelContext);
        }

        [CommandCmif(1)]
        // ListenAll() -> void. The subscribe itself; pushes have nowhere to
        // come from here, so this only records the intent.
        public ResultCode ListenAll(ServiceCtx context)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNpns);

            return ResultCode.Success;
        }

        [CommandCmif(2)]
        // ListenTo(u64 program_id) -> void.
        public ResultCode ListenTo(ServiceCtx context)
        {
            ulong programId = context.RequestData.ReadUInt64();

            Logger.Stub?.PrintStub(LogClass.ServiceNpns, new { programId });

            return ResultCode.Success;
        }

        [CommandCmif(5)]
        // GetReceiveEvent() -> handle<copy>. Never signaled: no backend feeds
        // it, so the game waits on nothing and falls back to polling.
        public ResultCode GetReceiveEvent(ServiceCtx context)
        {
            if (context.Process.HandleTable.GenerateHandle(_receiveEvent.ReadableEvent, out int handle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandCmif(7)]
        // GetStateChangeEvent() -> handle<copy>. Same standing as the
        // receive event: present, never signaled.
        public ResultCode GetStateChangeEvent(ServiceCtx context)
        {
            if (context.Process.HandleTable.GenerateHandle(_stateChangeEvent.ReadableEvent, out int handle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandCmif(26)]
        // [5.0.0+] ListenToMyApplicationId(u64 unknown, PID) -> void.
        // ACNH's online entry subscribes its own application id here.
        public ResultCode ListenToMyApplicationId(ServiceCtx context)
        {
            ulong unknown = context.RequestData.ReadUInt64();
            ulong pid = context.Request.HandleDesc.PId;

            Logger.Stub?.PrintStub(LogClass.ServiceNpns, new { unknown, pid });

            return ResultCode.Success;
        }

        [CommandCmif(21)]
        // CreateToken(u64 unknown, PID) -> token bytes. ACNH calls this right
        // after subscribing; the token would feed push delivery, which has no
        // backend here, so this hands back a zeroed buffer of whatever size
        // the caller offered and logs the shape for the record.
        public ResultCode CreateToken(ServiceCtx context)
        {
            ulong unknown = context.RequestData.ReadUInt64();
            ulong pid = context.Request.HandleDesc.PId;

            int outSize = context.Request.ReceiveBuff.Count != 0 ? (int)context.Request.ReceiveBuff[0].Size : 0;

            Logger.Stub?.PrintStub(LogClass.ServiceNpns, new { unknown, pid, outSize });

            if (outSize > 0)
            {
                context.Memory.Write(context.Request.ReceiveBuff[0].Position, new byte[outSize]);
            }

            return ResultCode.Success;
        }

        [CommandCmif(23)]
        // DestroyToken(u64 unknown, PID) -> void. Nothing is stored, so there
        // is nothing to tear down.
        public ResultCode DestroyToken(ServiceCtx context)
        {
            ulong unknown = context.RequestData.ReadUInt64();
            ulong pid = context.Request.HandleDesc.PId;

            Logger.Stub?.PrintStub(LogClass.ServiceNpns, new { unknown, pid });

            return ResultCode.Success;
        }

        [CommandCmif(25)]
        // QueryIsTokenValid(...) -> bool. Answered true: the tokens handed out
        // here are the only ones in circulation, so any token the game holds
        // came from us.
        public ResultCode QueryIsTokenValid(ServiceCtx context)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNpns);

            context.ResponseData.Write((byte)1);

            return ResultCode.Success;
        }

        [CommandCmif(101)]
        // Suspend() -> void.
        public ResultCode Suspend(ServiceCtx context)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNpns);

            return ResultCode.Success;
        }

        [CommandCmif(102)]
        // Resume() -> void.
        public ResultCode Resume(ServiceCtx context)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNpns);

            return ResultCode.Success;
        }
    }
}
