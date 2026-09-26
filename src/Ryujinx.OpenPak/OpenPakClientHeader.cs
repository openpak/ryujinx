using Ryujinx.Common;
using System;
using System.Net.Http;

namespace Ryujinx.OpenPak
{
    /// <summary>
    /// Which emulator is asking, and on what. Every request to OpenPak — the console chain (dauth,
    /// aauth, BAAS, Five, NA), the website and the network profile — says
    /// <c>X-OpenPak-Client: ryujinx/&lt;version&gt; (&lt;os&gt;)</c>, as Eden and Citron say theirs, so the
    /// servers can tell the emulators and the platforms apart. On a platform outside the five known
    /// ones the <c> (&lt;os&gt;)</c> suffix is left off; servers cut at '/', so old ones read it the same.
    /// </summary>
    public static class OpenPakClientHeader
    {
        public const string Name = "X-OpenPak-Client";

        /// <summary>"windows", "macos", "linux", "android" or "ios"; null for anything else.</summary>
        public static string OsName =>
            OperatingSystem.IsAndroid() ? "android" :
            OperatingSystem.IsIOS() ? "ios" :
            OperatingSystem.IsMacOS() ? "macos" :
            OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsLinux() ? "linux" :
            null;

        public static string Value
        {
            get
            {
                string version = string.IsNullOrWhiteSpace(ReleaseInformation.Version) ? "unknown" : ReleaseInformation.Version.Trim();
                string os = OsName;

                return os == null ? $"ryujinx/{version}" : $"ryujinx/{version} ({os})";
            }
        }

        public static HttpClient Apply(HttpClient client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(Name, Value);

            return client;
        }
    }
}
