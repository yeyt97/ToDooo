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
    // GitHub REST API 对未登录请求按出口 IP 限流（60 次/小时），
    // 共享网络或频繁检查时会被 403，所以主用不限流的 releases.atom，API 只作兜底。
    private const string LatestReleaseFeed = "https://github.com/yeyt97/ToDooo/releases.atom";
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
        var current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));

        // 先走 atom：不限流，取最新一条 release 的 tag 与页面地址。
        var feed = await TryFeedAsync().ConfigureAwait(false);
        if (feed is { } fromFeed)
        {
            return Evaluate(fromFeed.Tag, fromFeed.Url, current);
        }

        // atom 不可用时退回 REST API（可能被限流）。
        var api = await TryApiAsync().ConfigureAwait(false);
        return api is { } fromApi
            ? Evaluate(fromApi.Tag, fromApi.Url, current)
            : new UpdateCheckResult(false, null, null);
    }

    private static UpdateCheckResult Evaluate(string? tag, string? url, Version current)
    {
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
        {
            return new UpdateCheckResult(false, null, null);
        }

        var remote = Normalize(tag);
        return remote is not null && remote > current
            ? new UpdateCheckResult(true, remote.ToString(3), url)
            : new UpdateCheckResult(true, null, null);
    }

    /// <summary>解析 releases.atom 的第一条（最新）release。</summary>
    private static async Task<(string Tag, string Url)?> TryFeedAsync()
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseFeed).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseFeed(xml);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 releases.atom 文本，取第一条（最新）release 的 tag 与页面地址。</summary>
    internal static (string Tag, string Url)? ParseFeed(string xml)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Parse(xml);
            System.Xml.Linq.XNamespace atom = "http://www.w3.org/2005/Atom";
            var entry = document.Root?.Elements(atom + "entry").FirstOrDefault();
            if (entry is null)
            {
                return null;
            }

            // <id> 形如 tag:github.com,2008:Repository/1466414667/v1.7.0，末尾就是 tag 名。
            var id = entry.Element(atom + "id")?.Value ?? string.Empty;
            if (id.Length == 0)
            {
                return null;
            }

            var tag = id[(id.LastIndexOf('/') + 1)..];
            var url = entry.Elements(atom + "link")
                .FirstOrDefault(link => (string?)link.Attribute("rel") == "alternate")
                ?.Attribute("href")?.Value;
            return string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)
                ? null
                : (tag, url);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string Tag, string Url)?> TryApiAsync()
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            return string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)
                ? null
                : (tag!, url!);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>tag 形如 v1.4.1 / 1.4.1 / v2.0.0-beta.1；只有高于当前版本才算新版本。</summary>
    public static bool IsNewer(string? tag, Version current)
    {
        var remote = Normalize(tag);
        return remote is not null && remote > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
    }

    /// <summary>
    /// 安装包直链：GitHub Release 的固定路径规则，tag 与文件名都能推出来，不必再查一次 API。
    /// </summary>
    public static Uri? SetupDownloadUrl(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var tag = $"v{version}";
        var file = $"ToDooo-{version}-setup.exe";
        return Uri.TryCreate(
            $"https://github.com/yeyt97/ToDooo/releases/download/{tag}/{file}", UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    /// <summary>下载安装包到临时目录，返回本地路径；失败返回 null。</summary>
    public static async Task<string?> DownloadSetupAsync(
        string version, IProgress<double>? progress, CancellationToken token = default)
    {
        var uri = SetupDownloadUrl(version);
        if (uri is null)
        {
            return null;
        }

        var target = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ToDooo-{version}-setup.exe");

        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var destination = System.IO.File.Create(target);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    progress?.Report((double)received / total);
                }
            }

            return target;
        }
        catch
        {
            try
            {
                System.IO.File.Delete(target);
            }
            catch
            {
                // 清理失败不影响结果
            }

            return null;
        }
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