using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The caches the guest reads synchronously: the invitation inbox and the groups behind it
    /// (22000–22003), and the users 10500/20500 ask about by id.
    ///
    /// Everything here is filled on the session's poll, never on the game's thread. A command
    /// that finds nothing cached answers with what it has and warms the cache for the next call,
    /// which is what a console's own "cache exists / ensure available" pair amounts to.
    /// </summary>
    public static partial class OpenPakBaas
    {
        private static readonly Lock _cacheLock = new();

        private static IReadOnlyList<BaasInvitation> _invitations = [];
        private static readonly Dictionary<ulong, BaasInvitationGroup> _groups = new();
        private static readonly Dictionary<ulong, BaasUser> _users = new();
        private static readonly HashSet<ulong> _warming = [];

        /// <summary>The invitation inbox as of the last poll, read and unread. Never null.</summary>
        public static IReadOnlyList<BaasInvitation> Invitations
        {
            get
            {
                lock (_cacheLock)
                {
                    return _invitations;
                }
            }
        }

        /// <summary>One invitation group out of the cache, or null when it has not been fetched.</summary>
        public static BaasInvitationGroup InvitationGroup(ulong groupId)
        {
            lock (_cacheLock)
            {
                return _groups.TryGetValue(groupId, out BaasInvitationGroup group) ? group : null;
            }
        }

        /// <summary>One user out of the cache, or null.</summary>
        public static BaasUser User(ulong id)
        {
            lock (_cacheLock)
            {
                return _users.TryGetValue(id, out BaasUser user) ? user : null;
            }
        }

        /// <summary>
        /// The invitation inbox and every group it names (§A.5). Groups are fetched once each: a
        /// group is immutable once sent, so the one cached is the one 22001 should answer with.
        /// </summary>
        public static async Task<int> SyncInvitationsAsync(CancellationToken cancellationToken)
        {
            (int error, List<BaasInvitation> invitations) = await InvitationsAsync(cancellationToken);

            if (error != Ok)
            {
                return error;
            }

            lock (_cacheLock)
            {
                _invitations = invitations;
            }

            foreach (BaasInvitation invitation in invitations)
            {
                bool known;

                lock (_cacheLock)
                {
                    known = _groups.ContainsKey(invitation.GroupId);
                }

                if (known)
                {
                    continue;
                }

                (int groupError, BaasInvitationGroup group) = await InvitationGroupAsync(invitation.GroupId, cancellationToken);

                if (groupError == Ok && group != null)
                {
                    lock (_cacheLock)
                    {
                        _groups[group.Id] = group;
                    }
                }
            }

            return Ok;
        }

        /// <summary>
        /// Fetch users the guest asked about and does not have cached, once per id, in the
        /// background. The call that asked is answered from whatever is already there; the next
        /// one gets the rest, as a console's own user cache behaves.
        /// </summary>
        public static void WarmUsers(IEnumerable<ulong> ids)
        {
            List<ulong> wanted = [];

            lock (_cacheLock)
            {
                foreach (ulong id in ids)
                {
                    if (id != 0 && !_users.ContainsKey(id) && _warming.Add(id))
                    {
                        wanted.Add(id);
                    }
                }
            }

            if (wanted.Count == 0 || !Ready)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                (int error, List<BaasUser> users) = await UsersAsync(wanted, CancellationToken.None);

                lock (_cacheLock)
                {
                    if (error == Ok)
                    {
                        foreach (BaasUser user in users)
                        {
                            _users[user.Id] = user;
                        }
                    }

                    // Either way the id may be asked for again: a failure that is never retried
                    // is a name that never appears.
                    foreach (ulong id in wanted)
                    {
                        _warming.Remove(id);
                    }
                }
            });
        }

        /// <summary>Signed out, or another profile: every cache above belongs to the last user.</summary>
        private static void ForgetCaches()
        {
            lock (_cacheLock)
            {
                _invitations = [];
                _groups.Clear();
                _users.Clear();
                _warming.Clear();
            }
        }
    }
}
