using ToDooo.Services;
using Xunit;

namespace ToDooo.Tests;

public class AccountMaskTests
{
    [Theory]
    [InlineData("yeyt97@163.com", "y*****@163.com")]
    [InlineData("alice@icloud.com", "a****@icloud.com")]
    [InlineData("abc@qq.com", "a**@qq.com")]
    [InlineData("ab@qq.com", "a*@qq.com")]
    [InlineData("a@qq.com", "a@qq.com")]
    [InlineData("noatsign", "n*******")]
    [InlineData("  yeyt97@163.com  ", "y*****@163.com")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Apply_KeepsFirstLetterAndDomain(string? account, string expected)
    {
        Assert.Equal(expected, AccountMask.Apply(account));
    }

    [Fact]
    public void Apply_NeverLeaksTheRestOfTheLocalPart()
    {
        var masked = AccountMask.Apply("yeyt97@163.com");

        Assert.DoesNotContain("eyt", masked);
        Assert.DoesNotContain("97", masked);
        Assert.EndsWith("@163.com", masked);
    }
}
