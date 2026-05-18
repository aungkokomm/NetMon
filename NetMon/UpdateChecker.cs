using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace NetMon;

/// <summary>
/// Polls GitHub releases for a newer version of NetMon.
///
/// Endpoint:    https://api.github.com/repos/aungkokomm/NetMon/releases/latest
/// Comparison:  parses the release's <c>tag_name</c> (e.g. "v1.6") against the
///              running assembly version. Returns <c>UpdateInfo</c> only when
///              the remote release is strictly newer.
///
/// Pure data-fetch helper — no UI side effects. Caller decides whether to
/// surface the result (e.g. via NotifyIcon balloon).
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/aungkokomm/NetMon/releases/latest";

    public sealed class UpdateInfo
    {
        public Version Version { get; init; } = new();
        public string  TagName { get; init; } = "";
        public string  HtmlUrl { get; init; } = "";
        public string  Name    { get; init; } = "";
    }

    /// <summary>
    /// Query GitHub for the latest release. Returns null when the network call
    /// fails, the response can't be parsed, or no newer version exists.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub REST API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetMon-UpdateChecker/1.0");
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            string json = await http.GetStringAsync(ApiUrl, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Skip drafts / prereleases — only stable releases trigger a toast.
            if (root.TryGetProperty("draft",      out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.GetProperty("html_url").GetString() ?? "";
            string nm  = root.TryGetProperty("name", out var n)
                ? (n.GetString() ?? tag)
                : tag;

            if (!Version.TryParse(Normalize(tag.TrimStart('v', 'V')), out var newVer))
                return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (newVer.CompareTo(current) <= 0) return null;

            return new UpdateInfo
            {
                Version = newVer,
                TagName = tag,
                HtmlUrl = url,
                Name    = nm
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateChecker: {ex.Message}");
            return null;
        }
    }

    /// <summary>Pad to four parts so <c>Version.TryParse</c> always succeeds.</summary>
    private static string Normalize(string s)
    {
        var parts = s.Split('.', '-', '+');
        var num   = new List<string>();
        foreach (var part in parts)
        {
            if (int.TryParse(part, out _)) num.Add(part);
            else break;   // stop at suffix like "-rc1"
        }
        while (num.Count < 4) num.Add("0");
        return string.Join('.', num);
    }
}
