using System;
using System.Globalization;
using System.Linq;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The user-facing sentences this project hands back to the UI, by the key the OpenPak UX
    /// spec (§7.2) gives them.
    ///
    /// This project cannot see the UI's locale files, so the UI installs <see cref="Lookup"/> at
    /// start-up and every text comes out translated; without it (headless, tests) the spec's
    /// English is used. Exception text and HTTP codes never come through here: they go to the log.
    /// </summary>
    public static class OpenPakText
    {
        /// <summary>A spec key ("error.credentials") to its template in the UI language, or null.</summary>
        public static Func<string, string> Lookup { get; set; }

        public static string Credentials => Get("error.credentials", "Wrong email or password.");

        public static string RateLimited => Get("error.rate_limited", "Too many attempts. Wait a minute and try again.");

        public static string KeychainSave => Get("error.keychain_save",
            "Signed in, but the token could not be saved to the password store, so it was discarded.");

        public static string SignInExpired => Get("error.sign_in_expired", "That sign-in is no longer valid. Sign in again.");

        public static string Failed => Get("error.failed", "OpenPak could not do that. Try again in a moment.");

        public static string Image => Get("error.image", "Couldn't read that image.");

        public static string Unreachable(string where) => Get("error.unreachable", "Could not reach {0}.", where);

        public static string ProfileTaken(string profile) => Get("error.profile_taken",
            "This OpenPak account is already linked to the profile \"{0}\". Sign in there, or sign that profile out first.", profile);

        public static string SavesFull(string website) => Get("saves.full",
            "The OpenPak allowance is full. Connect your own storage at {0}/account/saves.", website);

        /// <summary>
        /// What the server said, when it said it as a sentence a person can act on ("no account
        /// has that friend code"), prefixed so it reads as the server's words; the generic
        /// failure otherwise. Machine words ("internal error", "invalid body") are not news.
        /// </summary>
        public static string FromServer(string reason)
            => IsSentence(reason) ? Get("error.server", "OpenPak said: {0}", reason.Trim()) : Failed;

        public static bool IsSentence(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 300)
            {
                return false;
            }

            string text = reason.Trim().TrimEnd('.').ToLowerInvariant();

            if (text is "internal error" or "invalid body" or "not found" or "bad request" or "unauthorized" or "forbidden")
            {
                return false;
            }

            return text.Any(char.IsLetter) && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
        }

        /// <summary>The key's template in the UI language (or <paramref name="english"/>), filled in.</summary>
        public static string Get(string key, string english, params object[] args)
        {
            string template = null;

            try
            {
                template = Lookup?.Invoke(key);
            }
            catch (Exception)
            {
                // A broken lookup is English, not a crash in the middle of a sign-in.
            }

            if (string.IsNullOrEmpty(template))
            {
                template = english;
            }

            return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
        }

        /// <summary>
        /// A spec key as the locale files name it: "error.rate_limited" is "ErrorRateLimited".
        /// Shared with the UI, which prefixes its file's name.
        /// </summary>
        public static string PascalCase(string key)
            => string.Concat(key.Split('.', '_').Where(part => part.Length > 0)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
