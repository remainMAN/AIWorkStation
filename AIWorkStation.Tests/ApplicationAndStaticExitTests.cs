using System.Net;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class ApplicationAndStaticExitTests
{
    [Fact]
    public async Task FindsRunningApplication()
    {
        var expected = new ApplicationTarget("Codex", "codex.exe", @"C:\Apps\codex.exe", true, "正在运行");
        var results = await new ApplicationFinder([new FakeApplicationSource(expected)]).FindAsync("Codex");
        Assert.Single(results);
        Assert.True(results[0].RunningProcess);
    }

    [Fact]
    public async Task FindsInstalledApplication()
    {
        var expected = new ApplicationTarget("Chrome", "chrome.exe", @"C:\Apps\chrome.exe", false, "App Paths");
        var results = await new ApplicationFinder([new FakeApplicationSource(expected)]).FindAsync("chrome");
        Assert.Equal("chrome.exe", Assert.Single(results).ExecutableName);
    }

    [Fact]
    public void OpenAIPresetContainsChatGPTAndCodex()
    {
        var apps = new[]
        {
            new ApplicationTarget("ChatGPT", "ChatGPT.exe", @"C:\ChatGPT.exe", true, "test"),
            new ApplicationTarget("Codex", "codex.exe", @"C:\codex.exe", true, "test"),
            new ApplicationTarget("Chrome", "chrome.exe", @"C:\chrome.exe", true, "test")
        };
        var result = new OpenAIApplicationMatcher().Match(apps);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, app => app.ExecutableName == "ChatGPT.exe");
        Assert.Contains(result, app => app.ExecutableName == "codex.exe");
    }

    [Fact]
    public async Task RejectsInvalidProxy()
    {
        var result = await new StaticExitTester([new Uri("http://ip.test")], TimeSpan.FromSeconds(1)).TestAsync(new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Http, Server = "127.0.0.1", Port = 1
        });
        Assert.False(result.Success);
        Assert.Contains(result.FailureCode, new[] { FailureCode.StaticProxyConnectionFailed, FailureCode.StaticProxyTimeout });
    }

    [Fact]
    public async Task ReportsAuthenticationFailure()
    {
        await using var proxy = new SingleResponseProxy();
        proxy.Start(HttpStatusCode.ProxyAuthenticationRequired, string.Empty);
        var result = await new StaticExitTester([new Uri("http://ip.test")]).TestAsync(new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Http, Server = "127.0.0.1", Port = proxy.Port, Username = "bad", Password = "secret"
        });
        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyAuthenticationFailed, result.FailureCode);
        Assert.DoesNotContain("secret", result.SanitizedDetail ?? string.Empty);
    }

    [Fact]
    public async Task ReturnsActualExitIp()
    {
        await using var proxy = new SingleResponseProxy();
        proxy.Start(HttpStatusCode.OK, "203.0.113.44\n");
        var result = await new StaticExitTester([new Uri("http://ip.test")]).TestAsync(new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Http, Server = "127.0.0.1", Port = proxy.Port
        });
        Assert.True(result.Success);
        Assert.Equal("203.0.113.44", result.ActualExitIp);
    }
}
