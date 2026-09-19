using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// What the running game said about itself through friend:u UpdateUserPresence (10610), per
    /// profile: whether an online-play session is declared open, and its app-field pairs as the
    /// JSON object the friends module publishes.
    ///
    /// The guest's friends module writes it (a project down) and the session that PATCHes presence
    /// reads it (a project up); neither can see the other, so it meets here, as the invitation
    /// count does on <see cref="OpenPakAccount"/>.
    /// </summary>
    public static class OpenPakPresence
    {
        /// <summary>One profile's declaration, for the title that made it.</summary>
        public sealed record State(string TitleId, bool SessionOpen, string AppField);

        private static readonly Lock _lock = new();
        private static readonly Dictionary<string, State> _states = new();

        /// <summary>Raised when a profile's declaration changed, so it is published now rather than at the next beat.</summary>
        public static event Action Changed;

        /// <summary>
        /// The declaration for this profile while <paramref name="titleId"/> runs. A declaration
        /// left by another title does not carry over: a launch is a fresh ONLINE with "{}", as on
        /// a console.
        /// </summary>
        public static State For(string profileId, string titleId)
        {
            lock (_lock)
            {
                return _states.TryGetValue(profileId ?? string.Empty, out State state) &&
                    string.Equals(state.TitleId, titleId, StringComparison.OrdinalIgnoreCase)
                        ? state
                        : new State(titleId, false, "{}");
            }
        }

        /// <summary>
        /// One UpdateUserPresence. <paramref name="declaration"/> is the struct's +0x18 byte: 1
        /// opens the session, 2 closes it, anything else leaves it as it was — the module keeps it
        /// sticky across commits that only change the description. A null
        /// <paramref name="appField"/> keeps the pairs already declared, which is what the
        /// standalone Declare commands (10600/10601) do.
        /// </summary>
        public static void Update(string profileId, string titleId, byte declaration, string appField)
        {
            bool changed;

            lock (_lock)
            {
                State previous = For(profileId, titleId);

                State next = previous with
                {
                    SessionOpen = declaration switch
                    {
                        1 => true,
                        2 => false,
                        _ => previous.SessionOpen,
                    },
                    AppField = appField ?? previous.AppField,
                };

                _states[profileId ?? string.Empty] = next;

                // The module publishes on a change of state or blob and on nothing else.
                changed = next != previous;
            }

            if (changed)
            {
                Changed?.Invoke();
            }
        }
    }
}
