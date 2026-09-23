using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// Crash reports for openpak.org: `POST /api/v1/crash-reports`, a `meta` JSON part and the
    /// log as an `attachment`.
    ///
    /// Nothing is sent from here. A crash is the worst moment to ask anybody anything, and the
    /// process may be on its way out, so a report is only written to the data directory; the next
    /// launch offers what it finds (<see cref="Pending"/>) and sends it only when the person says
    /// so, or has said "always" before. "Never" stops them being written at all.
    /// </summary>
    public static class OpenPakCrashReports
    {
        public const string Ask = "ask";
        public const string Always = "always";
        public const string Never = "never";

        public const string Source = "ryujinx";

        /// <summary>The site refuses an attachment over this.</summary>
        public const int MaxAttachmentBytes = 20 * 1024 * 1024;

        /// <summary>How many log lines before the crash go with a report.</summary>
        public const int TailLines = 500;

        /// <summary>Reports kept waiting for an answer; the oldest go first.</summary>
        private const int MaxPending = 5;

        private static string _policy = Ask;

        /// <summary><see cref="Ask"/>, <see cref="Always"/> or <see cref="Never"/>; pushed in from the settings.</summary>
        public static string Policy
        {
            get => _policy;
            set => _policy = value is Always or Never ? value : Ask;
        }

        /// <summary>The running title, as 16 hex digits; empty when none is.</summary>
        public static string CurrentTitleId { get; set; } = string.Empty;

        /// <summary>The last lines the logger printed, whatever the file log is set to.</summary>
        public static LogTail Tail { get; } = new(TailLines);

        public static string Directory => Path.Combine(OpenPakConfig.DataDirectory, "crash-reports");

        /// <summary>A report on disk: the meta as it will be sent, and the attachment beside it.</summary>
        public sealed record Report(string MetaPath, string AttachmentPath, string Message, DateTime When);

        /// <summary>
        /// Write a report for the next launch to offer. Never throws: this runs inside an unhandled
        /// exception handler or a guest fatal, and a failure here must not become a second crash.
        /// </summary>
        public static void Record(string errorCode, string message, string details, string titleId = null,
            IReadOnlyDictionary<string, string> fields = null)
        {
            if (!OpenPakConfig.Enabled || Policy == Never)
            {
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";

                string meta = BuildMeta(Version, OperatingSystemName, titleId ?? CurrentTitleId, errorCode, message, fields);

                File.WriteAllText(Path.Combine(Directory, name + ".log"), BuildAttachment(details, Tail.Lines()));
                File.WriteAllText(Path.Combine(Directory, name + ".json"), meta);

                Prune();

                Logger.Info?.Print(LogClass.Application, $"[OpenPak] Crash report saved as {name}; it is offered at the next launch");
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not save a crash report: {exception.Message}");
            }
        }

        /// <summary>Reports waiting for an answer, oldest first.</summary>
        public static IReadOnlyList<Report> Pending()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return [];
                }

                List<Report> reports = [];

                foreach (string metaPath in System.IO.Directory.GetFiles(Directory, "*.json").Order(StringComparer.Ordinal))
                {
                    string message = string.Empty;

                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metaPath));

                        if (document.RootElement.TryGetProperty("message", out JsonElement element) && element.ValueKind == JsonValueKind.String)
                        {
                            message = element.GetString();
                        }
                    }
                    catch (JsonException)
                    {
                        // Half-written by a process that died writing it: nothing to send.
                        Discard(new Report(metaPath, Path.ChangeExtension(metaPath, ".log"), string.Empty, default));

                        continue;
                    }

                    reports.Add(new Report(metaPath, Path.ChangeExtension(metaPath, ".log"), message, File.GetLastWriteTimeUtc(metaPath)));
                }

                return reports;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"[OpenPak] Could not read the saved crash reports: {exception.Message}");

                return [];
            }
        }

        public static void Discard(Report report)
        {
            TryDelete(report.MetaPath);
            TryDelete(report.AttachmentPath);
        }

        public static void DiscardAll()
        {
            foreach (Report report in Pending())
            {
                Discard(report);
            }
        }

        public static string Version => string.IsNullOrWhiteSpace(ReleaseInformation.Version) ? "unknown" : ReleaseInformation.Version.Trim();

        public static string OperatingSystemName => $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";

        /// <summary>The `meta` part: the contract's field names, and nothing empty sent.</summary>
        public static string BuildMeta(string version, string os, string titleId, string errorCode, string message,
            IReadOnlyDictionary<string, string> fields)
        {
            using MemoryStream stream = new();

            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("source", Source);
                writer.WriteString("version", version ?? "unknown");
                writer.WriteString("os", os ?? "unknown");

                if (!string.IsNullOrWhiteSpace(titleId) && titleId.Trim().TrimStart('0').Length > 0)
                {
                    writer.WriteString("title_id", titleId.Trim().ToLowerInvariant());
                }

                if (!string.IsNullOrWhiteSpace(errorCode))
                {
                    writer.WriteString("error_code", errorCode);
                }

                if (!string.IsNullOrWhiteSpace(message))
                {
                    writer.WriteString("message", message.Length > 2000 ? message[..2000] : message);
                }

                if (fields is { Count: > 0 })
                {
                    writer.WriteStartObject("fields");

                    foreach ((string key, string value) in fields)
                    {
                        writer.WriteString(key, value ?? string.Empty);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// What was caught, then the log that led up to it. Kept under the site's limit by
        /// dropping the oldest log lines, never the crash itself.
        /// </summary>
        public static string BuildAttachment(string details, IReadOnlyList<string> lines)
        {
            StringBuilder builder = new();

            builder.AppendLine(details ?? string.Empty);
            builder.AppendLine();
            builder.AppendLine($"---- last {lines.Count} log lines ----");

            int budget = MaxAttachmentBytes - Encoding.UTF8.GetByteCount(builder.ToString()) - 1024;
            int first = lines.Count;

            while (first > 0 && (budget -= Encoding.UTF8.GetByteCount(lines[first - 1]) + 1) >= 0)
            {
                first--;
            }

            for (int i = first; i < lines.Count; i++)
            {
                builder.AppendLine(lines[i]);
            }

            return builder.ToString();
        }

        private static void Prune()
        {
            IReadOnlyList<Report> reports = Pending();

            for (int i = 0; i < reports.Count - MaxPending; i++)
            {
                Discard(reports[i]);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Gone already, or not ours to delete; either way nothing to send.
            }
        }

        /// <summary>
        /// A log target that keeps the last lines in memory. It is written to synchronously, so a
        /// report made right after an exception is printed already holds that line — the file log
        /// is behind an async queue that a dying process may never drain.
        /// </summary>
        public sealed class LogTail : ILogTarget
        {
            private readonly Queue<string> _lines;
            private readonly int _capacity;
            private readonly object _lock = new();

            public LogTail(int capacity)
            {
                _capacity = capacity;
                _lines = new Queue<string>(capacity);
            }

            public string Name => "openpak-crash-tail";

            public void Log(object sender, LogEventArgs args)
            {
                string line = $@"{args.Time:hh\:mm\:ss\.fff} |{args.Level.ToString()[0]}| {(args.ThreadName != null ? args.ThreadName + " " : string.Empty)}{args.Message}";

                lock (_lock)
                {
                    if (_lines.Count == _capacity)
                    {
                        _lines.Dequeue();
                    }

                    _lines.Enqueue(line);
                }
            }

            public IReadOnlyList<string> Lines()
            {
                lock (_lock)
                {
                    return _lines.ToList();
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
