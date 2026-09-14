using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.Acc.AsyncContext;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.Acc.AccountService
{
    /// <summary>
    /// nn::account::nas::IAuthorizationRequest — the title-driven half of the
    /// console authorization flow. A title that talks to a third-party network
    /// (Battle.net for Diablo II: Resurrected) asks the account system to
    /// authorize it against the signed-in user, then collects the proof: an
    /// authorization code, or the id_token itself, to hand to that network.
    ///
    /// OpenPak's answer is immediate and local. The user is already signed in
    /// — the whole point of the session — so there is nothing to interact over:
    /// invoking the request completes at once, the request reports authorized,
    /// and the code and id_token are the session's own id_token. A network
    /// that wants to verify any of it verifies OpenPak, which is the design.
    /// </summary>
    class IAuthorizationRequest : IpcService
    {
        private readonly UserId _userId;
        private readonly ulong _sessionId;

        public IAuthorizationRequest(UserId userId)
        {
            _userId = userId;
            _sessionId = (ulong)Random.Shared.NextInt64();
            _managerServer = new ManagerServer(userId);
        }

        [CommandCmif(0)]
        // GetSessionId() -> u64
        public ResultCode GetSessionId(ServiceCtx context)
        {
            context.ResponseData.Write(_sessionId);

            return ResultCode.Success;
        }

        [CommandCmif(10)]
        // InvokeWithoutInteractionAsync(pid, buffer<unknown, 5>) -> object<IAsyncContext>
        public ResultCode InvokeWithoutInteractionAsync(ServiceCtx context)
        {
            KEvent asyncEvent = new(context.Device.System.KernelContext);
            AsyncExecution asyncExecution = new(asyncEvent);

            // Nothing to wait on: the user is signed in, authorization is a
            // local yes. The async shape is kept because the title polls it.
            asyncExecution.Initialize(100, _ => Task.CompletedTask);

            MakeObject(context, new IAsyncContext(asyncExecution));

            return ResultCode.Success;
        }

        [CommandCmif(19)]
        // IsAuthorized() -> u8
        public ResultCode IsAuthorized(ServiceCtx context)
        {
            context.ResponseData.Write((byte)1);

            return ResultCode.Success;
        }

        private readonly ManagerServer _managerServer;

        [CommandCmif(20)]
        // GetAuthorizationCode() -> buffer<bytes, 6>
        public ResultCode GetAuthorizationCode(ServiceCtx context)
        {
            return WriteSessionToken(context, "AUTHORIZATION-CODE");
        }

        [CommandCmif(21)]
        // GetIdToken() -> buffer<bytes, 6>
        public ResultCode GetIdToken(ServiceCtx context)
        {
            return WriteSessionToken(context, "ID-TOKEN");
        }

        // Both proofs are the session id_token: it is the credential OpenPak's
        // own check_token resolves, so whichever one the title hands over
        // arrives as something the network can act on. Nothing here throws:
        // a handler that does takes the account service loop down with it,
        // and every later IPC call on the session hangs the title.
        private ResultCode WriteSessionToken(ServiceCtx context, string what)
        {
            byte[] data = Encoding.ASCII.GetBytes(_managerServer.SessionIdToken());

            if (context.Request.ReceiveBuff.Count == 0)
            {
                // A title that wants only the size asks with no buffer.
                context.ResponseData.Write(data.Length);

                return ResultCode.Success;
            }

            ulong bufferPosition = context.Request.ReceiveBuff[0].Position;
            ulong bufferSize = context.Request.ReceiveBuff[0].Size;

            if ((ulong)data.Length > bufferSize)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc,
                    $"[OpenPak] {what} is {data.Length} bytes, guest buffer is {bufferSize}");

                return ResultCode.InvalidBufferSize;
            }

            context.Memory.Write(bufferPosition, data);
            context.ResponseData.Write(data.Length);

            return ResultCode.Success;
        }

        [CommandCmif(22)]
        // GetState() -> u32
        public ResultCode GetState(ServiceCtx context)
        {
            // nn::account::nas::AuthorizationRequestState: Done.
            context.ResponseData.Write(3u);

            return ResultCode.Success;
        }
    }
}
