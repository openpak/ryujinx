using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.OpenPak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ryujinx.Tests.HLE
{
    /// <summary>What a crash report says, and that it waits on disk until somebody answers.</summary>
    public class OpenPakCrashReportsTests
    {
        private string _directory;
        private bool _enabled;
        private string _policy;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ryujinx-openpak-crash-" + Guid.NewGuid().ToString("n"));

            Directory.CreateDirectory(_directory);
            AppDataManager.Initialize(_directory);

            _enabled = OpenPakConfig.Enabled;
            _policy = OpenPakCrashReports.Policy;
            OpenPakConfig.Enabled = true;
            OpenPakCrashReports.Policy = OpenPakCrashReports.Ask;
        }

        [TearDown]
        public void TearDown()
        {
            OpenPakConfig.Enabled = _enabled;
            OpenPakCrashReports.Policy = _policy;

            Directory.Delete(_directory, true);
        }

        [Test]
        public void TheMetaCarriesTheContractFieldsAndLeavesOutEmptyOnes()
        {
            string meta = OpenPakCrashReports.BuildMeta("1.3.0", "Linux (x64)", "0100000000010000", "2001-0123", "boom",
                new Dictionary<string, string> { ["kind"] = "guest_fatal" });

            using JsonDocument document = JsonDocument.Parse(meta);
            JsonElement root = document.RootElement;

            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("source").GetString(), Is.EqualTo("ryujinx"));
                Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("1.3.0"));
                Assert.That(root.GetProperty("os").GetString(), Is.EqualTo("Linux (x64)"));
                Assert.That(root.GetProperty("title_id").GetString(), Is.EqualTo("0100000000010000"));
                Assert.That(root.GetProperty("error_code").GetString(), Is.EqualTo("2001-0123"));
                Assert.That(root.GetProperty("message").GetString(), Is.EqualTo("boom"));
                Assert.That(root.GetProperty("fields").GetProperty("kind").GetString(), Is.EqualTo("guest_fatal"));
            });

            using JsonDocument bare = JsonDocument.Parse(OpenPakCrashReports.BuildMeta("1", "os", "0000000000000000", null, "", null));

            Assert.Multiple(() =>
            {
                Assert.That(bare.RootElement.TryGetProperty("title_id", out _), Is.False);
                Assert.That(bare.RootElement.TryGetProperty("error_code", out _), Is.False);
                Assert.That(bare.RootElement.TryGetProperty("message", out _), Is.False);
                Assert.That(bare.RootElement.TryGetProperty("fields", out _), Is.False);
            });
        }

        [Test]
        public void TheAttachmentPutsTheCrashBeforeTheLog()
        {
            string attachment = OpenPakCrashReports.BuildAttachment("System.Exception: boom", ["one", "two"]);

            Assert.That(attachment.IndexOf("boom", StringComparison.Ordinal), Is.LessThan(attachment.IndexOf("one", StringComparison.Ordinal)));
            Assert.That(attachment.IndexOf("one", StringComparison.Ordinal), Is.LessThan(attachment.IndexOf("two", StringComparison.Ordinal)));
        }

        [Test]
        public void AReportWaitsOnDiskAndOnlyTheNewestFewAreKept()
        {
            for (int i = 0; i < 7; i++)
            {
                OpenPakCrashReports.Record("E", $"crash {i}", "details", "0100000000010000");
                System.Threading.Thread.Sleep(2);
            }

            IReadOnlyList<OpenPakCrashReports.Report> reports = OpenPakCrashReports.Pending();

            Assert.That(reports, Has.Count.EqualTo(5));
            Assert.That(reports[^1].Message, Is.EqualTo("crash 6"));
            Assert.That(File.Exists(reports[0].AttachmentPath), Is.True);

            OpenPakCrashReports.DiscardAll();

            Assert.That(OpenPakCrashReports.Pending(), Is.Empty);
        }

        [Test]
        public void NeverMeansNothingIsSaved()
        {
            OpenPakCrashReports.Policy = OpenPakCrashReports.Never;

            OpenPakCrashReports.Record("E", "crash", "details");

            Assert.That(OpenPakCrashReports.Pending(), Is.Empty);
        }
    }
}
