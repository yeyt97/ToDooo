using System.Net.Http;
using System.Text.Json;

namespace ToDooo.Services;

public sealed record UpdateCheckResult(bool Succeeded, string? Version, string? Url)
{
    public bool HasUpdate => Succeeded && Version is not null;
}

/// <summary>通过 GitHub Release 检查新版本。网络异常一律静默失败，不阻塞启动。</summary>
public static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/yeyt97/ToDooo/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ToDooo-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateCheckResult> CheckAsync(Version currentVersion)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, null, null);
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
            {
                return new UpdateCheckResult(false, null, null);
            }

            var remote = Normalize(tag);
            var current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
            return remote is not null && remote > current
                ? new UpdateCheckResult(true, remote.ToString(3), url)
                : new UpdateCheckResult(true, null, null);
        }
        catch
        {
            return new UpdateCheckResult(false, null, null);
        }
    }

    /// <summary>tag 形如 v1.4.1 / 1.4.1 / v2.0.0-beta.1；只有高于当前版本才算新版本。</summary>
    public static bool IsNewer(string? tag, Version current)
    {
        var remote = Normalize(tag);
        return remote is not null && remote > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
    }

    private static Version? Normalize(string? tag)
    {
        var text = (tag ?? string.Empty).Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash];
        }

        return Version.TryParse(text, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
            : null;
    }
}