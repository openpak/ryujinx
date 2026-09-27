using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Text;

namespace Ryujinx.HLE.HOS.Services.Nim.ShopServiceAccessServerInterface.ShopServiceAccessServer.ShopServiceAccessor
{
    /// <summary>
    /// nn::ec's asynchronous request channel: the game prepares a shop URL, asks for it, waits on
    /// the completion event and then reads the body back.
    ///
    /// Nothing here talks to a shop — OpenPak runs none — but the interface has to answer, and it
    /// has to answer with a body. An empty one is not the same as none: nn::ec hands what it read
    /// to a JSON parser, and Don't Starve Together (which asks as soon as its Klei login
    /// succeeds) is one of the titles that take a zero-length body as a fault. `{}` is the
    /// smallest well-formed answer, the same one Eden settled on.
    /// </summary>
    class IShopServiceAsync : IpcService
    {
        // The response the game reads back. Well-formed and empty: there is no shop, and saying so
        // in the shape the caller parses is what keeps it moving.
        private static readonly byte[] _response = "{}"u8.ToArray();

        private readonly KEvent _completionEvent;

        private byte[] _data = [];
        private uint _errorCode;

        public IShopServiceAsync(Horizon system)
        {
            _completionEvent = new KEvent(system.KernelContext);
        }

        /// <summary>The event the accessor hands the guest, signalled when a request completes.</summary>
        public KReadableEvent CompletionEvent => _completionEvent.ReadableEvent;

        [CommandCmif(0)]
        // Cancel()
        public ResultCode Cancel(ServiceCtx context)
        {
            _data = [];
            _errorCode = 0;

            return ResultCode.Success;
        }

        [CommandCmif(1)]
        // GetSize() -> u64
        public ResultCode GetSize(ServiceCtx context)
        {
            context.ResponseData.Write((ulong)_data.Length);

            return ResultCode.Success;
        }

        [CommandCmif(2)]
        // Read(u64 offset) -> (u64 read_size, buffer<bytes, 0x22>)
        public ResultCode Read(ServiceCtx context)
        {
            ulong offset = context.RequestData.ReadUInt64();

            (ulong position, ulong size) = OutputBuffer(context);
            if (position == 0)
            {
                return ResultCode.Success;
            }

            ulong read = 0;
            if (offset < (ulong)_data.Length)
            {
                read = Math.Min(size, (ulong)_data.Length - offset);
                context.Memory.Write(position, _data.AsSpan((int)offset, (int)read));
            }

            context.ResponseData.Write(read);

            return ResultCode.Success;
        }

        [CommandCmif(3)]
        // GetErrorCode() -> u32
        public ResultCode GetErrorCode(ServiceCtx context)
        {
            context.ResponseData.Write(_errorCode);

            return ResultCode.Success;
        }

        [CommandCmif(4)]
        // Request()
        public ResultCode Request(ServiceCtx context)
        {
            _data = _response;
            _errorCode = 0;

            // Answered inline rather than on a worker: there is no network call behind it, and a
            // caller that is already waiting on the event must not find it clear.
            _completionEvent.ReadableEvent.Signal();

            Logger.Stub?.PrintStub(LogClass.ServiceNim);

            return ResultCode.Success;
        }

        [CommandCmif(5)]
        // Prepare(buffer<bytes, 0x21> url, buffer<bytes, 0x21> post_data)
        public ResultCode Prepare(ServiceCtx context)
        {
            // The URL is the only part worth seeing: it says which shop tier a title wanted, which
            // is how the next one gets served instead of stubbed.
            if (context.Request.SendBuff.Count > 0)
            {
                byte[] url = new byte[context.Request.SendBuff[0].Size];

                context.Memory.Read(context.Request.SendBuff[0].Position, url);

                Logger.Info?.Print(LogClass.ServiceNim, $"Shop request prepared for {Encoding.UTF8.GetString(url).TrimEnd('\0')}");
            }

            _completionEvent.ReadableEvent.Clear();

            return ResultCode.Success;
        }

        /// <summary>
        /// Where Read writes. The buffer is auto-select, so the caller picks: a mapped type-B
        /// buffer or a receive list. Taking only the first would leave half the callers writing
        /// nowhere.
        /// </summary>
        private static (ulong Position, ulong Size) OutputBuffer(ServiceCtx context)
        {
            if (context.Request.ReceiveBuff.Count > 0)
            {
                return (context.Request.ReceiveBuff[0].Position, context.Request.ReceiveBuff[0].Size);
            }

            if (context.Request.RecvListBuff.Count > 0)
            {
                return (context.Request.RecvListBuff[0].Position, context.Request.RecvListBuff[0].Size);
            }

            return (0, 0);
        }
    }
}
