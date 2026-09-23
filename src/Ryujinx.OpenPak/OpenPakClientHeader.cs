using Ryujinx.Common;
using System.Net.Http;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// Which emulator is asking. Every request to OpenPak — the console chain (dauth, aauth, BAAS,
    /// Five, NA), the website and the network profile — says <c>X-OpenPak-Client: ryujinx/&lt;version&gt;</c>,
    /// as Eden and Citron say theirs, so the servers can tell the emulators apart.
    /// </summary>
    public static class OpenPakClientHeader
    {
        public const string Name = "X-OpenPak-Client";

        public static string Value => $"ryujinx/{(string.IsNullOrWhiteSpace(ReleaseInformation.Version) ? "unknown" : ReleaseInformation.Version.Trim())}";

        public static HttpClient Apply(HttpClient client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(Name, Value);

            return client;
        }
    }
}
