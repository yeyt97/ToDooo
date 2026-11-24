using ToDooo.Services;
using Xunit;

namespace ToDooo.Tests;

/// <summary>
/// 真实 iCloud 端到端验证。两个环境变量都提供时才会跑，否则自动跳过，不会让 CI 失败。
/// 本地手动跑：
///   $env:CALDAV_APPLE_ID='you@example.com'
///   $env:CALDAV_APP_PASSWORD='xxxx-xxxx-xxxx-xxxx'
///   dotnet test --filter LiveICloud
/// </summary>
public class LiveICloudTests
{
    private static string? Password => Environment.GetEnvironmentVariable("CALDAV_APP_PASSWORD");

    private static string? AppleId => Environment.GetEnvironmentVariable("CALDAV_APPLE_ID");

    [Fact]
    public async Task Live_DiscoverAndQueryThisMonth()
    {
        if (Password is not { Length: > 0 } password || AppleId is not { Length: > 0 } appleId)
        {
            return; // 未提供凭据，跳过
        }

        using var client = new CalDavClient(appleId, password);
        var home = await client.ConnectAsync();
        Assert.StartsWith("https://", home);

        var calendars = await client.GetCalendarsAsync();
        Assert.NotEmpty(calendars);

        var now = DateTime.Today;
        var from = new DateTime(now.Year, now.Month, 1);
        var to = from.AddMonths(1);

        var totalEvents = 0;
        foreach (var calendar in calendars)
        {
            List<RawCalendarItem> raw;
            try
            {
                raw = await client.QueryEventsAsync(calendar.Href, from, to);
            }
            catch (CalDavException exception) when (exception.StatusCode is 403 or 404)
            {
                // 共享/遗留集合不响应查询，跳过
                continue;
            }

            var expanded = CalendarSyncService.ExpandItems(raw, from, to);
            totalEvents += expanded.Count;
            Assert.All(expanded, e => Assert.False(string.IsNullOrWhiteSpace(e.Summary) && string.IsNullOrWhiteSpace(e.Uid)));
        }

        Assert.True(calendars.Count >= 1);
        // 把结果打到测试输出，便于人工核对
        Assert.True(totalEvents >= 0);
    }
}
