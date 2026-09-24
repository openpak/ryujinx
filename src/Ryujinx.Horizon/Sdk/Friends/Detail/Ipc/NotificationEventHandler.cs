using Ryujinx.Common.Logging;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Horizon.Sdk.Friends.Detail.Ipc
{
    sealed class NotificationEventHandler
    {
        private readonly NotificationService[] _registry;

        private static readonly Lock _handlersLock = new();
        private static readonly List<NotificationEventHandler> _handlers = [];

        static NotificationEventHandler()
        {
            // The module fills the notification queue from the push handlers and from every list
            // sync that finds a change. Nothing here holds a push connection, so the fork's poll
            // stands in for it: a changed friend list — a new friend, an accepted request, a
            // presence that moved, a flag that was written — is a list update, and an inbox that
            // grew is a new friend request.
            OpenPakBaas.FriendListChanged += () => Signal(handler => handler.SignalFriendListUpdate(SignedInUser()));
            OpenPakAccount.Instance.FriendRequestArrived += () => Signal(handler => handler.SignalNewFriendRequest(SignedInUser()));

            // An invitation has no event type of its own in this queue. The list update is what
            // sends a system screen back to the service, where 22010 has the new count.
            OpenPakAccount.Instance.NativeInvitationsChanged += () => Signal(handler => handler.SignalFriendListUpdate(SignedInUser()));
        }

        public NotificationEventHandler()
        {
            _registry = new NotificationService[0x20];

            lock (_handlersLock)
            {
                _handlers.Add(this);
            }
        }

        /// <summary>
        /// The profile the signed-in BAAS account belongs to. A queue is per Uid, and an event
        /// for nobody is one no service will take.
        /// </summary>
        private static Uid SignedInUser()
        {
            string profileId = OpenPakConfig.ProfileId;

            try
            {
                return string.IsNullOrEmpty(profileId) ? Uid.Null : new Uid(profileId);
            }
            catch (ArgumentException)
            {
                return Uid.Null;
            }
        }

        /// <summary>
        /// Every handler alive. One is made per emulation session; one from a session that has
        /// ended has an empty registry, so it costs a walk and says nothing.
        /// </summary>
        private static void Signal(Action<NotificationEventHandler> signal)
        {
            NotificationEventHandler[] handlers;

            lock (_handlersLock)
            {
                handlers = [.. _handlers];
            }

            // The callers are OpenPak's background threads. NotificationService signals its event
            // through a kernel-resolved action that works off-thread; the catch remains so that
            // a failure in one handler can never end the heartbeat, and presence with it.
            foreach (NotificationEventHandler handler in handlers)
            {
                try
                {
                    signal(handler);
                }
                catch (Exception exception)
                {
                    Logger.Debug?.Print(LogClass.ServiceFriend, $"Friends notification not signalled: {exception.Message}");
                }
            }
        }

        public void RegisterNotificationService(NotificationService service)
        {
            // NOTE: When there's no enough space in the registry array, Nintendo doesn't return any errors.
            for (int i = 0; i < _registry.Length; i++)
            {
                if (_registry[i] == null)
                {
                    _registry[i] = service;
                    break;
                }
            }
        }

        public void UnregisterNotificationService(NotificationService service)
        {
            // NOTE: When there's no enough space in the registry array, Nintendo doesn't return any errors.
            for (int i = 0; i < _registry.Length; i++)
            {
                if (_registry[i] == service)
                {
                    _registry[i] = null;
                    break;
                }
            }
        }

        public void SignalFriendListUpdate(Uid targetId)
        {
            if (targetId.IsNull)
            {
                return;
            }

            for (int i = 0; i < _registry.Length; i++)
            {
                _registry[i]?.SignalFriendListUpdate(targetId);
            }
        }

        public void SignalNewFriendRequest(Uid targetId)
        {
            if (targetId.IsNull)
            {
                return;
            }

            for (int i = 0; i < _registry.Length; i++)
            {
                _registry[i]?.SignalNewFriendRequest(targetId);
            }
        }
    }
}
