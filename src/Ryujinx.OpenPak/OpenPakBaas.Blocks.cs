using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// Blocking and unblocking, as the friends module does it (friends contract §A.8): 30400
    /// BlockUser, 30401/30403 BlockUserWithApplicationInfo(V2) and 30402 UnblockUser.
    /// </summary>
    public static partial class OpenPakBaas
    {
        /// <summary>The reasons a block carries, by the number the IPC call names them with.</summary>
        public static string BlockReason(int reason) => reason switch
        {
            1 => "BAD_FRIEND_REQUEST",
            2 => "BAD_FRIEND",
            3 => "IN_APP",
            4 => "IN_CAMPUS",
            _ => null,
        };

        /// <summary>
        /// A block was written or removed and the lists have been re-read. The request boxes live
        /// with the session, not here, so this is how it learns to re-read them too: the console
        /// re-syncs blocks, then the friend list, then the inbox.
        /// </summary>
        public static event Action BlockListChanged;

        /// <summary>
        /// The POST body (§A.8): <c>{"targetUserId":"%016llx","extras":{"self":{"reason":…}}}</c>,
        /// plus the route keys when the block came from inside a title (30401/30403), in the same
        /// spelling a friend request's route uses.
        /// </summary>
        public static string BlockBody(ulong targetId, string reason, BaasRoute route)
            => Json(writer =>
            {
                writer.WriteString("targetUserId", targetId.ToString("x16"));
                writer.WriteStartObject("extras");
                writer.WriteStartObject("self");
                writer.WriteString("reason", reason);

                if (route != null)
                {
                    writer.WriteString("route:appInfo:appId", route.ApplicationId.ToString("x16"));
                    writer.WriteNumber("route:appInfo:acdIndex", route.AcdIndex);
                    writer.WriteString("route:appInfo:presenceGroupId", route.PresenceGroupId.ToString("x16"));
                    writer.WriteString("route:name", route.Name ?? string.Empty);
                    writer.WriteString("route:name:language", route.Language ?? string.Empty);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            });

        /// <summary>
        /// POST /1.0.0/users/&lt;me&gt;/blocks. The block is in the cache before the request leaves,
        /// as the guest may read the list straight after asking; the sync that follows a success
        /// replaces it with the server's. Remaps 2031 → 2213 and 2061 → 2701, as the module does.
        /// </summary>
        public static async Task<int> BlockUserAsync(ulong targetId, int reason, BaasRoute route, CancellationToken cancellationToken)
        {
            if (!Ready)
            {
                return Unknown;
            }

            string reasonText = BlockReason(reason);

            if (reasonText == null || targetId == 0)
            {
                return InvalidArgument;
            }

            AddBlockLocally(targetId, reason, route);

            Reply reply = await SendAsync(HttpMethod.Post, Baas($"/1.0.0/users/{_userId}/blocks"), JsonType,
                BlockBody(targetId, reasonText, route), cancellationToken);

            if (!reply.Success)
            {
                // The optimistic entry goes: what the server holds is what is blocked.
                await SyncBlockListAsync(cancellationToken);

                return Remap(BaasError(reply), (2031, 2213), (2061, 2701));
            }

            await AfterBlockWriteAsync(cancellationToken);

            return Ok;
        }

        /// <summary>
        /// DELETE /1.0.0/users/&lt;me&gt;/blocks/&lt;id&gt;. A block the server does not know (404,
        /// or 2220–2229) is removed locally and reported as 2121-2711.
        /// </summary>
        public static async Task<int> UnblockUserAsync(ulong targetId, CancellationToken cancellationToken)
        {
            if (!Ready)
            {
                return Unknown;
            }

            RemoveBlockLocally(targetId);

            Reply reply = await SendAsync(HttpMethod.Delete, Baas($"/1.0.0/users/{_userId}/blocks/{targetId:x16}"),
                null, null, cancellationToken);

            if (reply.Success)
            {
                await AfterBlockWriteAsync(cancellationToken);

                return Ok;
            }

            int error = BaasError(reply);

            if (error == 2031 || error == 3404 || error is >= 2220 and <= 2229)
            {
                return 2711;
            }

            await SyncBlockListAsync(cancellationToken);

            return error;
        }

        /// <summary>Blocks, then the friend list (a block ends a friendship), then the request boxes.</summary>
        private static async Task AfterBlockWriteAsync(CancellationToken cancellationToken)
        {
            await SyncBlockListAsync(cancellationToken);
            await SyncFriendListAsync(true, cancellationToken);

            BlockListChanged?.Invoke();
        }

        private static void AddBlockLocally(ulong targetId, int reason, BaasRoute route)
        {
            BaasFriend friend = Friend(targetId);
            BaasUser user = friend == null ? User(targetId) : null;

            lock (_lock)
            {
                if (_blocks.Count >= 100)
                {
                    return;
                }

                foreach (BaasBlock block in _blocks)
                {
                    if (block.Id == targetId)
                    {
                        return;
                    }
                }

                _blocks = [.. _blocks, new BaasBlock(targetId,
                    friend?.Nickname ?? user?.Nickname ?? string.Empty,
                    friend?.ThumbnailUrl ?? user?.ThumbnailUrl ?? string.Empty)
                {
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Reason = reason,
                    Route = route,
                }];
            }
        }

        private static void RemoveBlockLocally(ulong targetId)
        {
            lock (_lock)
            {
                List<BaasBlock> blocks = [.. _blocks];

                if (blocks.RemoveAll(block => block.Id == targetId) > 0)
                {
                    _blocks = blocks;
                }
            }
        }
    }
}
