using ToDooo.Services;
using Xunit;

namespace ToDooo.Tests;

public sealed class UpdateServiceTests
{
    private static readonly Version Current = new(1, 4, 0);

    [Theory]
    [InlineData("v1.4.1", true)]
    [InlineData("1.5.0", true)]
    [InlineData("v2.0.0", true)]
    [InlineData("v1.4.0", false)]
    [InlineData("v1.3.9", false)]
    [InlineData("v1.4.0-beta.1", false)]
    [InlineData("v1.4.1-beta.1", true)]
    [InlineData("", false)]
    [InlineData("latest", false)]
    public void IsNewer_ComparesVersions(string tag, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(tag, Current));
    }

    [Fact]
    public void ParseFeed_ReadsLatestReleaseTagAndUrl()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Release notes from ToDooo</title>
              <entry>
                <id>tag:github.com,2008:Repository/1466414667/v1.8.0</id>
                <updated>2026-11-25T01:23:20Z</updated>
                <link rel="alternate" type="text/html" href="https://github.com/yeyt97/ToDooo/releases/tag/v1.8.0"/>
                <title>ToDooo v1.8.0</title>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/1466414667/v1.7.0</id>
                <link rel="alternate" type="text/html" href="https://github.com/yeyt97/ToDooo/releases/tag/v1.7.0"/>
                <title>ToDooo v1.7.0</title>
              </entry>
            </feed>
            """;

        var parsed = UpdateService.ParseFeed(xml);

        Assert.NotNull(parsed);
        Assert.Equal("v1.8.0", parsed!.Value.Tag);
        Assert.Equal("https://github.com/yeyt97/ToDooo/releases/tag/v1.8.0", parsed.Value.Url);
        Assert.True(UpdateService.IsNewer(parsed.Value.Tag, Current));
    }

    [Fact]
    public void ParseFeed_ReturnsNullOnEmptyOrBrokenFeed()
    {
        Assert.Null(UpdateService.ParseFeed("<feed xmlns=\"http://www.w3.org/2005/Atom\"></feed>"));
        Assert.Null(UpdateService.ParseFeed("not xml at all"));
    }

    [Fact]
    public void IsNewer_HandlesCurrentWithFourParts()
    {
        Assert.False(UpdateService.IsNewer("v1.4.0", new Version(1, 4, 0, 0)));
        Assert.True(UpdateService.IsNewer("v1.4.1", new Version(1, 4, 0, 0)));
    }
}