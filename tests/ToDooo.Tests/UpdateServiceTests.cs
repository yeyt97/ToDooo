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
    public void IsNewer_HandlesCurrentWithFourParts()
    {
        Assert.False(UpdateService.IsNewer("v1.4.0", new Version(1, 4, 0, 0)));
        Assert.True(UpdateService.IsNewer("v1.4.1", new Version(1, 4, 0, 0)));
    }
}