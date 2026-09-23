using NUnit.Framework;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// The core project's user-facing sentences (UX spec §7): they come from the UI's locale file
    /// by the spec's key, the English fallback is the file's English, and server noise and HTTP
    /// codes never reach a person.
    /// </summary>
    [NonParallelizable]
    public class OpenPakTextTests
    {
        [TearDown]
        public void Reset() => OpenPakText.Lookup = null;

        [TestCase("error.credentials", "ErrorCredentials")]
        [TestCase("error.rate_limited", "ErrorRateLimited")]
        [TestCase("saves.full", "SavesFull")]
        [TestCase("toast.signed_in_linked", "ToastSignedInLinked")]
        public void KeysAreNamedTheWayTheLocaleFilesNameThem(string key, string pascal)
        {
            Assert.That(OpenPakText.PascalCase(key), Is.EqualTo(pascal));
        }

        [TestCase("no account has that friend code", true)]
        [TestCase("You are already friends.", true)]
        [TestCase("internal error", false)]
        [TestCase("invalid body", false)]
        [TestCase("Not Found", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("500", false)]
        public void OnlySentencesFromTheServerAreShown(string reason, bool shown)
        {
            Assert.That(OpenPakText.IsSentence(reason), Is.EqualTo(shown));

            Assert.That(OpenPakText.FromServer(reason), shown
                ? Is.EqualTo($"OpenPak said: {reason!.Trim()}")
                : Is.EqualTo(OpenPakText.Failed));
        }

        [Test]
        public void TheLookupWinsAndABrokenOneFallsBackToEnglish()
        {
            OpenPakText.Lookup = key => key == "error.credentials" ? "Mot de passe incorrect." : null;

            Assert.That(OpenPakText.Credentials, Is.EqualTo("Mot de passe incorrect."));
            Assert.That(OpenPakText.RateLimited, Is.EqualTo("Too many attempts. Wait a minute and try again."));

            OpenPakText.Lookup = _ => throw new InvalidOperationException();

            Assert.That(OpenPakText.Credentials, Is.EqualTo("Wrong email or password."));
        }

        /// <summary>
        /// Every key the core asks for is in Dialog_OpenPak.json, and the file's English is the
        /// core's fallback word for word: one table, not two that drift.
        /// </summary>
        [Test]
        public void EveryKeyIsInTheLocaleFileWithTheSameEnglish()
        {
            string file = FindLocaleFile();

            if (file == null)
            {
                Assert.Ignore("assets/Locales/Dialog_OpenPak.json is not reachable from the test directory");
            }

            Dictionary<string, string> english = [];

            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(file)))
            {
                foreach (JsonElement entry in document.RootElement.GetProperty("Locales").EnumerateArray())
                {
                    english[entry.GetProperty("ID").GetString()!] = entry.GetProperty("Translations").GetProperty("en_US").GetString();
                }
            }

            Func<string>[] sentences =
            [
                () => OpenPakText.Credentials,
                () => OpenPakText.RateLimited,
                () => OpenPakText.KeychainSave,
                () => OpenPakText.SignInExpired,
                () => OpenPakText.Failed,
                () => OpenPakText.Image,
                () => OpenPakText.Unreachable("https://openpak.org"),
                () => OpenPakText.ProfileTaken("Ana"),
                () => OpenPakText.SavesFull("https://openpak.org"),
                () => OpenPakText.FromServer("no account has that friend code"),
            ];

            List<string> asked = [];

            foreach (Func<string> sentence in sentences)
            {
                OpenPakText.Lookup = null;

                string fallback = sentence();

                OpenPakText.Lookup = key =>
                {
                    asked.Add(key);

                    return english.GetValueOrDefault(OpenPakText.PascalCase(key));
                };

                Assert.That(sentence(), Is.EqualTo(fallback));
            }

            foreach (string key in asked.Distinct())
            {
                Assert.That(english.ContainsKey(OpenPakText.PascalCase(key)), $"{key} is missing from Dialog_OpenPak.json");
            }
        }

        private static string FindLocaleFile()
        {
            for (DirectoryInfo directory = new(TestContext.CurrentContext.TestDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "assets", "Locales", "Dialog_OpenPak.json");

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
