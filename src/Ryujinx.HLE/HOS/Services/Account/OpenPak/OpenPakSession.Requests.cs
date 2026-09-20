using Ryujinx.Common.Logging;
using Ryujinx.OpenPak;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.OpenPak
{
    /// <summary>
    /// Friend requests on the route a console's friends module takes: the inbox and outbox
    /// boxes (§A.4.2) polled beside the invitation inbox, and the send/answer/read writes
    /// (§A.4.1, §A.4.4) the guest's 30xxx commands fire at.
    /// </summary>
    public partial class OpenPakSession
    {
        /// <summary>
        /// What is waiting in the received and sent request boxes. One malformed item is
        /// skipped by the parser, never the box: the rest are still requests somebody made.
        /// </summary>
        public async Task RefreshFriendRequestsAsync(CancellationToken cancellationToken)
        {
            if (!Enabled || _userId == null || _applicationToken == null)
            {
                return;
            }

            try
            {
                using JsonDocument inbox = await GetAsync(
                    OpenPakBaas.RequestBoxUrl(BaasHost, _userId, inbox: true, offset: 0, count: 100),
                    cancellationToken);

                using JsonDocument outbox = await GetAsync(
                    OpenPakBaas.RequestBoxUrl(BaasHost, _userId, inbox: false, offset: 0, count: 100),
                    cancellationToken);

                OpenPakAccount.Instance.SetFriendRequests(
                    OpenPakBaas.ParseRequestList(inbox.RootElement, inbox: true),
                    OpenPakBaas.ParseRequestList(outbox.RootElement, inbox: false));

                OpenPakAccount.Instance.AnswerFriendRequest ??= AnswerFriendRequestAsync;
                OpenPakAccount.Instance.ReadFriendRequest ??= ReadFriendRequestAsync;
                OpenPakAccount.Instance.SendFriendRequest ??= SendFriendRequestAsync;
            }
            catch (Exception exception)
            {
                Logger.Debug?.Print(LogClass.ServiceAcc, $"[OpenPak] Friend request boxes: {exception.Message}");
            }
        }

        /// <summary>
        /// Answer or withdraw a request (30202–30204): PATCH the state, then re-read both
        /// boxes, as the console's inline sync does after every such write.
        /// </summary>
        private async Task AnswerFriendRequestAsync(ulong requestId, string state)
        {
            await EnsureAsync(CancellationToken.None);

            if (!Enabled || _applicationToken == null)
            {
                return;
            }

            try
            {
                using JsonDocument reply = await PatchAsync(
                    OpenPakBaas.RequestUrl(BaasHost, requestId),
                    OpenPakBaas.AnswerRequestBody(state), CancellationToken.None);

                // The reply is the request object (§A.4.4); an AUTHORIZED one is what tells
                // the console to sync at once, which the refresh below does either way.
                OpenPakBaas.ParseRequest(reply.RootElement, inbox: state != "CANCELED");

                await RefreshFriendRequestsAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc,
                    $"[OpenPak] Answering request {requestId:x16} with {state}: {exception.Message}");
            }
        }

        /// <summary>Mark a received request read (30205), then recount from the boxes.</summary>
        private async Task ReadFriendRequestAsync(ulong requestId)
        {
            await EnsureAsync(CancellationToken.None);

            if (!Enabled || _applicationToken == null)
            {
                return;
            }

            try
            {
                using JsonDocument reply = await PatchAsync(
                    OpenPakBaas.RequestUrl(BaasHost, requestId),
                    OpenPakBaas.ReadRequestBody(), CancellationToken.None);

                OpenPakBaas.ParseRequest(reply.RootElement, inbox: true);

                await RefreshFriendRequestsAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc,
                    $"[OpenPak] Marking request {requestId:x16} read: {exception.Message}");
            }
        }

        /// <summary>
        /// Send a request (30200/30201/10200): resolve the guest's id to the BAAS user the
        /// server names, POST the variant body, and sync at once when the reply says the
        /// receiver had already asked (AUTHORIZED).
        /// </summary>
        private async Task SendFriendRequestAsync(BaasFriendRequestSend send)
        {
            await EnsureAsync(CancellationToken.None);

            if (!Enabled || _userId == null || _applicationToken == null)
            {
                return;
            }

            try
            {
                string receiver = await ReceiverIdAsync(send.TargetId, CancellationToken.None);

                if (!OpenPakBaas.TryHexText(_userId.AsSpan(), out ulong senderId) ||
                    receiver == null || !OpenPakBaas.TryHexText(receiver.AsSpan(), out ulong receiverId))
                {
                    Logger.Warning?.Print(LogClass.ServiceAcc,
                        $"[OpenPak] Invitation not sent: no BAAS user for {send.TargetId:x16}");

                    return;
                }

                // One variant per send command (§A.4.1): the catalog and NNID routes each carry
                // their own extras, and everything else is the plain or the app route.
                string body = send switch
                {
                    { CatalogId: not null } => OpenPakBaas.SendRequestCatalogBody(send.Channel, senderId, receiverId,
                        send.CatalogId, send.TargetName, send.TargetLanguage, send.OwnName, send.OwnLanguage),
                    { MiiName: not null } => OpenPakBaas.SendRequestNnidBody(send.Channel, senderId, receiverId,
                        send.MiiName, send.MiiImageUrlParam),
                    { ApplicationId: 0 } => OpenPakBaas.SendRequestBody(send.Channel, senderId, receiverId),
                    _ => OpenPakBaas.SendRequestBody(send.Channel, senderId, receiverId,
                        send.ApplicationId, send.AcdIndex, send.PresenceGroupId,
                        send.TargetName, send.TargetLanguage, send.OwnName, send.OwnLanguage),
                };

                // The module refuses a body it cannot fit in its own buffer before it sends it.
                if (Encoding.UTF8.GetByteCount(body) > 0x400)
                {
                    Logger.Warning?.Print(LogClass.ServiceAcc, "[OpenPak] Friend request not sent: the body is over 0x400 bytes");

                    return;
                }

                using JsonDocument reply = await PostAsync($"https://{BaasHost}/2.0.0/friend_requests",
                    new StringContent(body, Encoding.UTF8, "application/json"),
                    _applicationToken, CancellationToken.None);

                if (OpenPakBaas.ParseRequest(reply.RootElement, inbox: false)?.State == 3)
                {
                    await RefreshFriendRequestsAsync(CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceAcc, $"[OpenPak] Friend request not sent: {exception.Message}");
            }
        }

        private async Task<JsonDocument> PatchAsync(string url, string body, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Patch, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json-patch+json"),
            };

            request.Headers.Add("Authorization", "Bearer " + _applicationToken);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            string text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{request.RequestUri.AbsolutePath} returned {(int)response.StatusCode}: {text}");
            }

            return JsonDocument.Parse(text);
        }
    }
}
