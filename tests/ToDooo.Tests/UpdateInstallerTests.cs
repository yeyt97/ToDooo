using ToDooo.Services;
using Xunit;

namespace ToDooo.Tests;

public class UpdateInstallerTests
{
    [Fact]
    public void PortableDownloadUrl_PointsAtZipAssetOfThatTag()
    {
        var url = UpdateService.PortableDownloadUrl("1.10.1");

        Assert.NotNull(url);
        Assert.Equal(
            "https://github.com/yeyt97/ToDooo/releases/download/v1.10.1/ToDooo-1.10.1-win-x64.zip",
            url!.ToString());
    }

    [Fact]
    public void SetupDownloadUrl_AndPortableUrl_ShareTheTag()
    {
        Assert.EndsWith("/v2.0.0/ToDooo-2.0.0-setup.exe", UpdateService.SetupDownloadUrl("2.0.0")!.ToString());
        Assert.EndsWith("/v2.0.0/ToDooo-2.0.0-win-x64.zip", UpdateService.PortableDownloadUrl("2.0.0")!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DownloadUrls_AreNullForBlankVersion(string version)
    {
        Assert.Null(UpdateService.PortableDownloadUrl(version));
        Assert.Null(UpdateService.SetupDownloadUrl(version));
    }

    [Fact]
    public void PortableUpdateScript_WaitsForExitThenExtractsAndRelaunches()
    {
        var script = UpdateInstaller.BuildPortableUpdateScript(
            @"C:\Temp\ToDooo-1.10.2-win-x64.zip",
            @"D:\apps\ToDooo\",
            @"D:\apps\ToDooo\ToDooo.exe",
            4321);

        Assert.Contains("$targetPid = 4321", script);
        Assert.Contains("while (Get-Process -Id $targetPid", script);
        Assert.Contains("$archive = [System.IO.Compression.ZipFile]::OpenRead('C:\\Temp\\ToDooo-1.10.2-win-x64.zip')", script);
        Assert.Contains("$dest = 'D:\\apps\\ToDooo\\'", script);
        Assert.Contains("ExtractToFile($entry, $target, $true)", script);
        Assert.Contains("Start-Process -FilePath 'D:\\apps\\ToDooo\\ToDooo.exe'", script);

        // Expand-Archive -Force 在 PowerShell 5.1 上没有可用重载，会直接抛 MethodException。
        Assert.DoesNotContain("Expand-Archive", script);

        // 先等我们退出，再解压，最后才有资格重新启动：顺序必须如此。
        Assert.True(
            script.IndexOf("while (Get-Process", StringComparison.Ordinal) <
            script.IndexOf("OpenRead", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("OpenRead", StringComparison.Ordinal) <
            script.IndexOf("Start-Process", StringComparison.Ordinal));
    }

    [Fact]
    public void PortableUpdateScript_EscapesSingleQuotesInPaths()
    {
        var script = UpdateInstaller.BuildPortableUpdateScript(
            @"C:\Temp\it's here.zip",
            @"D:\apps\ToDooo\",
            @"D:\apps\ToDooo\ToDooo.exe",
            1);

        Assert.Contains(@"OpenRead('C:\Temp\it''s here.zip')", script);
    }

    [Fact]
    public void Launcher_RefusesMissingFiles()
    {
        Assert.False(UpdateInstaller.TryLaunchPortableUpdate(
            @"C:\nope\missing.zip", AppContext.BaseDirectory, Environment.ProcessPath ?? "x.exe"));
    }

    [Fact]
    public void TestHost_IsNotAnInstalledCopy()
    {
        // 单测跑在 bin\Debug 下，不在 %LocalAppData%\Programs\ToDooo，应走绿色版更新。
        Assert.False(UpdateInstaller.IsInstalledCopy());
    }
}
