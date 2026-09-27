using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Services.Nim.ShopServiceAccessServerInterface.ShopServiceAccessServer.ShopServiceAccessor;
using Ryujinx.Horizon.Common;
using System;

namespace Ryujinx.HLE.HOS.Services.Nim.ShopServiceAccessServerInterface.ShopServiceAccessServer
{
    class IShopServiceAccessor : IpcService
    {
        private readonly Horizon _system;

        public IShopServiceAccessor(Horizon system)
        {
            _system = system;
        }

        [CommandCmif(0)]
        // CreateAsyncInterface(u64) -> (handle<copy>, object<nn::ec::IShopServiceAsync>)
        public ResultCode CreateAsyncInterface(ServiceCtx context)
        {
            IShopServiceAsync async = new(_system);

            MakeObject(context, async);

            if (context.Process.HandleTable.GenerateHandle(async.CompletionEvent, out int eventHandle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            // The reply carries both: the completion event as a copy handle AND the object itself.
            // On a session (rather than a domain) MakeObject returns the object as a MOVE handle in
            // this same descriptor, so overwriting the descriptor with MakeCopy -- which is what
            // this did until 2026-09-27 -- threw the object away. The guest then had a null
            // interface, and nn::ec::ShopServiceAccessor::Request dereferenced it: "Invalid memory
            // access at virtual address 0x0", which reached the player as a frozen emulator.
            // Don't Starve Together does this the moment its Klei login succeeds.
            int[] move = context.Response.HandleDesc?.ToMove ?? [];

            context.Response.HandleDesc = new IpcHandleDesc([eventHandle], move);

            Logger.Stub?.PrintStub(LogClass.ServiceNim);

            return ResultCode.Success;
        }
    }
}
