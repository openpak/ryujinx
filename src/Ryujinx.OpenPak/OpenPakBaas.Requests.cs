using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// The friend-request calls, as the friends module makes them (friends contract §A.4):
    /// the URLs it builds and the bodies it writes. No transport here — the bearer lives a
    /// project up, with the session — so these are pure functions of their arguments, and
    /// testable as such.
    /// </summary>
    public static partial class OpenPakBaas
    {
        /// <summary>
        /// A request box page: <c>GET …/users/&lt;me&gt;/friend_requests/{inbox|outbox}</c>
        /// (§A.4.2). The server honours <paramref name="offset"/>/<paramref name="count"/>
        /// and returns only PENDING items; the console trusts the filter.
        /// </summary>
        public static string RequestBoxUrl(string host, string userId, bool inbox, int offset, int count)
            => $"https://{host}/2.0.0/users/{userId}/friend_requests/{(inbox ? "inbox" : "outbox")}" +
               $"?offset={offset}&count={count}&sort=createdAt:desc&filter.state.$eq=PENDING";

        /// <summary>One request: <c>PATCH …/friend_requests/&lt;id&gt;</c> (§A.4.4).</summary>
        public static string RequestUrl(string host, ulong requestId)
            => $"https://{host}/2.0.0/friend_requests/{requestId:x16}";

        /// <summary>
        /// Answer or cancel a request (§A.4.4): <paramref name="state"/> is
        /// <c>"CANCELED"</c> (one L), <c>"AUTHORIZED"</c> or <c>"REJECTED"</c>.
        /// </summary>
        public static string AnswerRequestBody(string state)
        {
            if (state is not ("CANCELED" or "AUTHORIZED" or "REJECTED"))
            {
                throw new ArgumentOutOfRangeException(nameof(state), state, "A request answer is CANCELED, AUTHORIZED or REJECTED.");
            }

            return JsonArray(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("op", "replace");
                writer.WriteString("path", "/state");
                writer.WriteString("value", state);
                writer.WriteEndObject();
            });
        }

        /// <summary>Mark a received request read (30205): the badge recounts afterwards (§A.4.4).</summary>
        public static string ReadRequestBody()
            => JsonArray(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("op", "add");
                writer.WriteString("path", "/extras/receiver/read");
                writer.WriteBoolean("value", true);
                writer.WriteEndObject();
            });

        /// <summary>
        /// Send a request (§A.4.1), plain variant: the channel, the two BAAS ids as
        /// <c>%016llx</c>, and the sender-only extras. Over 0x400 bytes the call fails
        /// before it is sent.
        /// </summary>
        public static string SendRequestBody(string channel, ulong senderId, ulong receiverId)
            => Json(writer =>
            {
                writer.WriteStartArray("channels");
                writer.WriteStringValue(channel);
                writer.WriteEndArray();
                writer.WriteString("senderId", senderId.ToString("x16"));
                writer.WriteString("receiverId", receiverId.ToString("x16"));
                writer.WriteStartObject("extras");
                writer.WriteStartObject("sender");
                writer.WriteString("route:sender", "me");
                writer.WriteEndObject();
                writer.WriteEndObject();
            });

        /// <summary>
        /// Send a request (§A.4.1), app variant: <paramref name="targetName"/> is the
        /// target's name as the sender saw it (screen name #1, sender-only),
        /// <paramref name="ownName"/> the sender's own in-app name (screen name #2,
        /// visible to both).
        /// </summary>
        public static string SendRequestBody(string channel, ulong senderId, ulong receiverId,
            ulong applicationId, byte acdIndex, ulong presenceGroupId,
            string targetName, string targetLanguage, string ownName, string ownLanguage)
            => Json(writer =>
            {
                writer.WriteStartArray("channels");
                writer.WriteStringValue(channel);
                writer.WriteEndArray();
                writer.WriteString("senderId", senderId.ToString("x16"));
                writer.WriteString("receiverId", receiverId.ToString("x16"));
                writer.WriteStartObject("extras");
                writer.WriteStartObject("sender");
                writer.WriteString("route:sender", "me");
                writer.WriteString("route:name", targetName);
                writer.WriteString("route:name:language", targetLanguage);
                writer.WriteEndObject();
                writer.WriteStartObject("senderAndReceiver");
                writer.WriteString("route:appInfo:appId", applicationId.ToString("x16"));
                writer.WriteNumber("route:appInfo:acdIndex", acdIndex);
                writer.WriteString("route:appInfo:presenceGroupId", presenceGroupId.ToString("x16"));
                writer.WriteString("route:name", ownName);
                writer.WriteString("route:name:language", ownLanguage);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });

        /// <summary>
        /// Send a request (§A.4.1), catalog variant (30215): the route names an external
        /// application catalog id rather than a title.
        /// </summary>
        public static string SendRequestCatalogBody(string channel, ulong senderId, ulong receiverId,
            string catalogId, string targetName, string targetLanguage, string ownName, string ownLanguage)
            => Json(writer =>
            {
                writer.WriteStartArray("channels");
                writer.WriteStringValue(channel);
                writer.WriteEndArray();
                writer.WriteString("senderId", senderId.ToString("x16"));
                writer.WriteString("receiverId", receiverId.ToString("x16"));
                writer.WriteStartObject("extras");
                writer.WriteStartObject("sender");
                writer.WriteString("route:sender", "me");
                writer.WriteString("route:name", targetName);
                writer.WriteString("route:name:language", targetLanguage);
                writer.WriteEndObject();
                writer.WriteStartObject("senderAndReceiver");
                writer.WriteString("route:candidate:catalogId", catalogId);
                writer.WriteString("route:name", ownName);
                writer.WriteString("route:name:language", ownLanguage);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });

        /// <summary>
        /// Send a request (§A.4.1), NNID variant (30217): the sender's own Mii name and image
        /// parameter, in both halves of the extras.
        /// </summary>
        public static string SendRequestNnidBody(string channel, ulong senderId, ulong receiverId,
            string miiName, string miiImageUrlParam)
            => Json(writer =>
            {
                writer.WriteStartArray("channels");
                writer.WriteStringValue(channel);
                writer.WriteEndArray();
                writer.WriteString("senderId", senderId.ToString("x16"));
                writer.WriteString("receiverId", receiverId.ToString("x16"));
                writer.WriteStartObject("extras");
                writer.WriteStartObject("sender");
                writer.WriteString("route:sender", "me");
                writer.WriteString("route:nnid:miiName", miiName);
                writer.WriteString("route:nnid:miiImageUrlParam", miiImageUrlParam);
                writer.WriteEndObject();
                writer.WriteStartObject("senderAndReceiver");
                writer.WriteString("route:nnid:miiName", miiName);
                writer.WriteString("route:nnid:miiImageUrlParam", miiImageUrlParam);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });

        /// <summary>
        /// A 128-bit external catalog id (<c>%016llx%016llx</c>) as its two halves, for
        /// the route union. False when it is not 32 hex digits.
        /// </summary>
        public static bool TryCatalogId(string catalogId, out ulong hi, out ulong lo)
        {
            hi = 0;
            lo = 0;

            return catalogId != null && catalogId.Length == 32 &&
                TryHexText(catalogId.AsSpan(0, 16), out hi) &&
                TryHexText(catalogId.AsSpan(16, 16), out lo);
        }

        /// <summary>One patch document, written straight out: no reflection, so trimming cannot break it.</summary>
        private static string JsonArray(Action<Utf8JsonWriter> body)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartArray();
                body(writer);
                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

    }
}
