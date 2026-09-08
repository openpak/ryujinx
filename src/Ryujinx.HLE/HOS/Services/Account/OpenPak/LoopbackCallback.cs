using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// The address a browser comes back to after signing in to OpenPak, and the one request it
    /// makes when it does.
    ///
    /// Deliberately a socket rather than an HttpListener: the port has to be known before the
    /// browser is sent anywhere, and asking the operating system for a free one and then binding it
    /// separately leaves a gap where something else can take it. Holding the socket from the start
    /// closes that. It also keeps the reply ours, which matters — it is the last thing the person
    /// sees before they come back to the emulator.
    /// </summary>
    sealed class LoopbackCallback : IDisposable
    {
        private const string Path = "/callback";

        // Long enough to find a password; short enough that a forgotten window does not sit here.
        private static readonly TimeSpan _patience = TimeSpan.FromMinutes(5);

        private readonly TcpListener _listener;

        /// <summary>Loopback only: nothing off this machine may deliver an authorization code.</summary>
        public LoopbackCallback()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{Path}";

        /// <summary>
        /// The authorization code from the redirect, or null if it never came, was refused, or came
        /// back carrying a state this flow did not issue.
        /// </summary>
        public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(_patience);

            try
            {
                using TcpClient browser = await _listener.AcceptTcpClientAsync(timeout.Token);
                using NetworkStream stream = browser.GetStream();
                using StreamReader reader = new(stream, Encoding.UTF8, false, 8192, leaveOpen: true);

                // Only the request line matters, and it carries the whole redirect.
                string requestLine = await reader.ReadLineAsync(timeout.Token) ?? string.Empty;
                string[] parts = requestLine.Split(' ');
                string query = parts.Length > 1 && parts[1].Contains('?') ? parts[1][(parts[1].IndexOf('?') + 1)..] : string.Empty;

                string code = Parameter(query, "code");
                string state = Parameter(query, "state");

                // Anything on this machine can reach a loopback port, so the browser being the one
                // that arrived is not something to assume: the state proves it is this flow's redirect.
                bool ok = code != null && state == expectedState;

                if (!ok)
                {
                    Logger.Warning?.Print(LogClass.ServiceAcc, state != expectedState
                        ? "[OpenPak] A sign-in redirect arrived with the wrong state and was ignored."
                        : $"[OpenPak] The sign-in was refused: {Parameter(query, "error") ?? "no code"}");
                }

                await ReplyAsync(stream, ok, timeout.Token);

                return ok ? code : null;
            }
            catch (OperationCanceledException)
            {
                Logger.Info?.Print(LogClass.ServiceAcc, "[OpenPak] Gave up waiting for the browser to come back.");

                return null;
            }
        }

        private static async Task ReplyAsync(Stream stream, bool ok, CancellationToken cancellationToken)
        {
            string message = ok
                ? "<h1>Signed in</h1><p>You can close this tab and go back to the emulator.</p>"
                : "<h1>That did not work</h1><p>Go back to the emulator and try again.</p>";

            byte[] body = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\"><title>OpenPak</title>" +
                "<body style=\"font:16px system-ui;margin:4rem auto;max-width:28rem;text-align:center\">" + message);

            byte[] response = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(response, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static string Parameter(string query, string name)
        {
            foreach (string pair in query.Split('&'))
            {
                string[] parts = pair.Split('=', 2);

                if (parts.Length == 2 && parts[0] == name)
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return null;
        }

        public void Dispose() => _listener.Dispose();
    }
}
