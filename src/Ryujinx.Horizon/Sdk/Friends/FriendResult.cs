using Ryujinx.Horizon.Common;

namespace Ryujinx.Horizon.Sdk.Friends
{
    static class FriendResult
    {
        private const int ModuleId = 121;

        public static Result InvalidArgument => new(ModuleId, 2);
        public static Result InternetRequestDenied => new(ModuleId, 6);
        public static Result NotificationQueueEmpty => new(ModuleId, 15);

        /// <summary>2121-0090: this port may not call that command (contract §B.1).</summary>
        public static Result PermissionDenied => new(ModuleId, 90);

        /// <summary>A friends-module description as a Result; 0 is success.</summary>
        public static Result From(int description) => description == 0 ? Result.Success : new Result(ModuleId, description);
    }
}
