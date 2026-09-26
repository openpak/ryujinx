using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Account.OpenPak;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Impl;
using Ryujinx.HLE.HOS.Services.Sockets.Bsd.Proxy;
using Ryujinx.HLE.HOS.Services.Ssl.Types;
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Ryujinx.HLE.HOS.Services.Ssl.SslService
{
    class SslManagedSocketConnection : ISslConnectionBase
    {
        public int SocketFd { get; }

        /// <inheritdoc cref="ISslConnectionBase.DoNotCloseSocket" />
        public bool DoNotCloseSocket { get; set; }

        public ISocket Socket { get; }

        private readonly BsdContext _bsdContext;
        private readonly SslVersion _sslVersion;
        private SslStream _stream;
        private bool _isBlockingSocket;
        private int _previousReadTimeout;

        // SslStream decrypts a whole TLS record into an internal buffer, and a
        // caller-sized read — the Battle.net gateway layer reads WebSocket
        // framing in two-byte pieces — can leave most of that record sitting
        // there while Socket.Poll over the raw socket reports nothing to read.
        // The guest then stalls until the next inbound TCP write pushes the
        // poll. A read that returned exactly the requested count is the only
        // visible trace that plaintext may remain, and while it may, Poll's
        // verdict on the raw socket cannot be trusted. Skipping Poll is safe
        // for non-blocking sockets: StartSslReadOperation pins ReadTimeout to
        // 1 ms, so an empty buffer surfaces as WSAETIMEDOUT → WouldBlock
        // instead of a hang.
        private bool _sslMayHoldBufferedPlaintext;

        // Plaintext a Peek has pulled off the wire but nobody has consumed yet.
        // ponytail: one buffer, grown to the largest peek asked for, no pooling.
        private byte[] _peeked = [];
        private int _peekedLength;

        public SslManagedSocketConnection(BsdContext bsdContext, SslVersion sslVersion, int socketFd, ISocket socket)
        {
            _bsdContext = bsdContext;
            _sslVersion = sslVersion;

            SocketFd = socketFd;
            Socket = socket;
        }

        private void StartSslOperation()
        {
            // Save blocking state
            _isBlockingSocket = Socket.Blocking;

            // Force blocking for SslStream
            Socket.Blocking = true;
        }

        private void EndSslOperation()
        {
            // Restore blocking state
            Socket.Blocking = _isBlockingSocket;
        }

        private void StartSslReadOperation()
        {
            StartSslOperation();

            if (!_isBlockingSocket)
            {
                _previousReadTimeout = _stream.ReadTimeout;

                _stream.ReadTimeout = 1;
            }
        }

        private void EndSslReadOperation()
        {
            if (!_isBlockingSocket)
            {
                _stream.ReadTimeout = _previousReadTimeout;
            }

            EndSslOperation();
        }

        // NOTE: We silence warnings about TLS 1.0 and 1.1 as games will likely use it.
#pragma warning disable SYSLIB0039
        private SslProtocols TranslateSslVersion(SslVersion version)
        {
            return (version & SslVersion.VersionMask) switch
            {
                SslVersion.Auto => SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13,
                SslVersion.TlsV10 => SslProtocols.Tls,
                SslVersion.TlsV11 => SslProtocols.Tls11,
                SslVersion.TlsV12 => SslProtocols.Tls12,
                SslVersion.TlsV13 => SslProtocols.Tls13,
                _ => throw new NotImplementedException(version.ToString()),
            };
        }
#pragma warning restore SYSLIB0039

        /// <summary>
        /// Retrieve the hostname of the current remote in case the provided hostname is null or empty.
        /// </summary>
        /// <param name="hostName">The current hostname</param>
        /// <returns>Either the resolved or provided hostname</returns>
        /// <remarks>
        /// This is done to avoid getting an <see cref="System.Security.Authentication.AuthenticationException"/>
        /// as the remote certificate will be rejected with <c>RemoteCertificateNameMismatch</c> due to an empty hostname.
        /// This is not what the switch does!
        /// It might just skip remote hostname verification if the hostname wasn't set with <see cref="ISslConnection.SetHostName"/> before.
        /// TODO: Remove this as soon as we know how the switch deals with empty hostnames
        /// </remarks>
        private string RetrieveHostName(string hostName)
        {
            if (!string.IsNullOrEmpty(hostName))
            {
                return hostName;
            }

            try
            {
                return Dns.GetHostEntry(Socket.RemoteEndPoint.Address).HostName;
            }
            catch (SocketException)
            {
                return hostName;
            }
        }

        /// <summary>
        /// Normal validation, plus the OpenPak CA when one is configured. OpenPak answers to
        /// Nintendo's own hostnames, which no public CA can issue for, so a title's TLS has
        /// nothing else it could trust; without this every online request fails the handshake.
        /// The pinned-CA rule is host-agnostic on purpose: any game whose redirected endpoints
        /// present an OpenPak-CA certificate just works, and nothing else is ever excused —
        /// a name mismatch or an unknown issuer is refused exactly as before.
        /// </summary>
        private static bool ValidateRemoteCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            return errors == SslPolicyErrors.None || (OpenPakServer.Current?.Validate(certificate, errors) ?? false);
        }

        public ResultCode Handshake(string hostName)
        {
            StartSslOperation();
            _stream = new SslStream(new NetworkStream(((DefaultSocket)((ManagedSocket)Socket).Socket).BaseSocket, false), false, ValidateRemoteCertificate, null);
            hostName = RetrieveHostName(hostName);
            try
            {
                _stream.AuthenticateAsClient(hostName, null, TranslateSslVersion(_sslVersion), false);
            }
            catch (Exception exception)
            {
                // A failed handshake is the guest's problem to retry, not the
                // emulator's to die on: real networks hand out refused
                // connections, resets and mid-handshake EOFs, and a title that
                // sees an SSL error shows its own retry UI. Before this caught,
                // a single dropped handshake took the whole process down
                // through the IPC dispatch.
                Logger.Warning?.Print(LogClass.ServiceSsl, $"Handshake to {hostName} failed: {exception.Message}");

                EndSslOperation();

                return exception.InnerException is SocketException socketException &&
                    socketException.SocketErrorCode == SocketError.ConnectionRefused
                    ? ResultCode.ConnectionAbort
                    : ResultCode.ConnectionReset;
            }
            EndSslOperation();

            return ResultCode.Success;
        }

        public ResultCode Peek(out int peekCount, Memory<byte> buffer)
        {
            // .NET's SslStream cannot peek, so a peek reads for real and parks the plaintext here
            // until Read consumes it. Answering a flat WouldBlock instead -- what this did before --
            // is invisible to a client that reads when it feels like it, and fatal to one that uses
            // Peek as its readiness check: Moving Out 2's websocket peeks every 5 ms and will not
            // call Read until a peek reports data.
            //
            // The park is topped up to the caller's buffer size while the wire still has something,
            // because a client that peeks for a whole frame and is handed part of one waits for the
            // rest -- and the rest may be sitting inside SslStream where the raw socket's Poll
            // cannot see it. Stopping at the first short read broke Moving Out 2 four frames in.
            while (_peekedLength < buffer.Length)
            {
                if (_peeked.Length < buffer.Length)
                {
                    byte[] grown = new byte[buffer.Length];
                    _peeked.AsSpan(0, _peekedLength).CopyTo(grown);
                    _peeked = grown;
                }

                if (ReadFromWire(out int filled, _peeked.AsMemory(_peekedLength, buffer.Length - _peekedLength)) != ResultCode.Success || filled <= 0)
                {
                    break;
                }

                _peekedLength += filled;
            }

            if (_peekedLength == 0)
            {
                // Nothing there. An idle connection answering WouldBlock is what Nintendo's libcurl
                // reads as "the connection is alive".
                peekCount = -1;

                return ResultCode.WouldBlock;
            }

            peekCount = Math.Min(_peekedLength, buffer.Length);
            _peeked.AsSpan(0, peekCount).CopyTo(buffer.Span);

            return ResultCode.Success;
        }

        public int Pending()
        {
            // nn::ssl::Connection::Pending is how a non-blocking client asks whether there is
            // anything to read, and answering a hardcoded 0 tells one that never reads at all. That
            // is what stalled Moving Out 2: it wrote its websocket upgrade request, asked Pending,
            // was told nothing was there, and sat on an open socket with the server's reply
            // undrained in the receive buffer -- no error, no retry, no second call, until the
            // game's own timeout. Titles that call Read directly, like Unity's HTTP path, were
            // unaffected, which is why only this one connection ever hung.
            //
            // ponytail: a truthful yes/no rather than a count. The real call reports plaintext
            // buffered inside the SSL object, which .NET does not expose; Poll sees ciphertext
            // waiting on the socket. A caller using this as a size hint reads one byte and asks
            // again, and the exactly-full-read heuristic then keeps it draining. Track real
            // decrypted counts only if some title turns out to need them.
            if (_sslMayHoldBufferedPlaintext)
            {
                return 1;
            }

            return Socket.Poll(0, SelectMode.SelectRead) ? 1 : 0;
        }

        private bool TryTranslateWinSockError(bool isBlocking, WsaError error, out ResultCode resultCode)
        {
            switch (error)
            {
                case WsaError.WSAETIMEDOUT:
                    resultCode = isBlocking ? ResultCode.Timeout : ResultCode.WouldBlock;
                    return true;
                case WsaError.WSAECONNABORTED:
                    resultCode = ResultCode.ConnectionAbort;
                    return true;
                case WsaError.WSAECONNRESET:
                    resultCode = ResultCode.ConnectionReset;
                    return true;
                default:
                    resultCode = ResultCode.Success;
                    return false;
            }
        }

        public ResultCode Read(out int readCount, Memory<byte> buffer)
        {
            // Anything a Peek pulled off the wire is handed out here first, in order.
            if (_peekedLength > 0)
            {
                readCount = Math.Min(_peekedLength, buffer.Length);
                _peeked.AsSpan(0, readCount).CopyTo(buffer.Span);
                _peekedLength -= readCount;

                if (_peekedLength > 0)
                {
                    _peeked.AsSpan(readCount, _peekedLength).CopyTo(_peeked.AsSpan(0));
                }

                return ResultCode.Success;
            }

            return ReadFromWire(out readCount, buffer);
        }

        // The read that actually touches the stream. Peek needs this rather than Read, or topping up
        // its own buffer would just hand back what it has already parked.
        private ResultCode ReadFromWire(out int readCount, Memory<byte> buffer)
        {
            if (!_sslMayHoldBufferedPlaintext && !Socket.Poll(0, SelectMode.SelectRead))
            {
                readCount = -1;

                return ResultCode.WouldBlock;
            }

            StartSslReadOperation();

            try
            {
                readCount = _stream.Read(buffer.Span);

                // Exactly-full read: the record's tail may still be buffered.
                // A short read or EOF means the internal buffer drained.
                _sslMayHoldBufferedPlaintext = readCount > 0 && readCount == buffer.Length;
            }
            catch (IOException exception)
            {
                _sslMayHoldBufferedPlaintext = false;
                readCount = -1;

                if (exception.InnerException is SocketException socketException)
                {
                    WsaError socketErrorCode = (WsaError)socketException.SocketErrorCode;

                    if (TryTranslateWinSockError(_isBlockingSocket, socketErrorCode, out ResultCode result))
                    {
                        return result;
                    }
                    else
                    {
                        throw socketException;
                    }
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                EndSslReadOperation();
            }

            return ResultCode.Success;
        }

        public ResultCode Write(out int writtenCount, ReadOnlyMemory<byte> buffer)
        {
            if (!Socket.Poll(0, SelectMode.SelectWrite))
            {
                writtenCount = 0;

                return ResultCode.WouldBlock;
            }

            StartSslOperation();

            try
            {
                _stream.Write(buffer.Span);
            }
            catch (IOException exception)
            {
                writtenCount = -1;

                if (exception.InnerException is SocketException socketException)
                {
                    WsaError socketErrorCode = (WsaError)socketException.SocketErrorCode;

                    if (TryTranslateWinSockError(_isBlockingSocket, socketErrorCode, out ResultCode result))
                    {
                        return result;
                    }
                    else
                    {
                        throw socketException;
                    }
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                EndSslOperation();
            }

            // .NET API doesn't provide the size written, assume all written.
            writtenCount = buffer.Length;

            return ResultCode.Success;
        }

        public ResultCode GetServerCertificate(string hostname, Span<byte> certificates, out uint storageSize, out uint certificateCount)
        {
            byte[] rawCertData = _stream.RemoteCertificate.GetRawCertData();

            storageSize = (uint)rawCertData.Length;
            certificateCount = 1;

            if (rawCertData.Length > certificates.Length)
            {
                return ResultCode.CertBufferTooSmall;
            }

            rawCertData.CopyTo(certificates);

            return ResultCode.Success;
        }

        public void Dispose()
        {
            // DoNotCloseSocket: the title keeps the descriptor and may dial again on it;
            // closing the bsd fd here would hand it a dead socket on the next attempt.
            if (!DoNotCloseSocket)
            {
                _bsdContext.CloseFileDescriptor(SocketFd);
            }
        }
    }
}
