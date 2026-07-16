using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;

namespace SteamP2PInfo
{
    internal static class VersionCheck
    {
        internal const string ReleasesPageUrl = "https://github.com/wardriven/steamp2pinfo-revised/releases";

        private const string LatestVersionUrl = "https://raw.githubusercontent.com/wardriven/steamp2pinfo-revised/master/version.md";
        private const string CacheBustingQueryParameter = "cacheBust";
        private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        internal static readonly Version CurrentVersion = GetCurrentVersion();

        internal static string CurrentVersionDisplay => FormatDisplayVersion(CurrentVersion);

        internal static async Task<Version> FetchLatestVersionAsync()
        {
            try
            {
                using (HttpRequestMessage request = CreateLatestVersionRequest(Guid.NewGuid().ToString("N")))
                using (HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;

                    string versionText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return TryParseVersion(versionText, out Version version) ? version : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static HttpRequestMessage CreateLatestVersionRequest(string cacheBuster)
        {
            if (string.IsNullOrWhiteSpace(cacheBuster))
                throw new ArgumentException("A cache-busting value is required.", nameof(cacheBuster));

            Uri requestUri = new UriBuilder(LatestVersionUrl)
            {
                Query = CacheBustingQueryParameter + "=" + Uri.EscapeDataString(cacheBuster)
            }.Uri;

            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            return request;
        }

        internal static bool TryParseVersion(string versionText, out Version version)
        {
            version = null;
            return !string.IsNullOrWhiteSpace(versionText)
                && Version.TryParse(versionText.Trim(), out version);
        }

        internal static bool IsRemoteVersionNewer(Version localVersion, Version remoteVersion)
        {
            return localVersion != null
                && remoteVersion != null
                && remoteVersion > localVersion;
        }

        internal static string FormatDisplayVersion(Version version)
        {
            if (version == null)
                throw new ArgumentNullException(nameof(version));

            return "v" + (version.Build >= 0 ? version.ToString(3) : version.ToString());
        }

        private static Version GetCurrentVersion()
        {
            var versionAttribute = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
                Assembly.GetExecutingAssembly(),
                typeof(AssemblyFileVersionAttribute));

            if (versionAttribute == null || !TryParseVersion(versionAttribute.Version, out Version version))
                throw new InvalidOperationException("The assembly file version must be a valid version number.");

            return version;
        }
    }
}
