using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class StaticExitStabilityTests
{
    [Fact]
    public async Task DirectThreeStableSamples_SelectsDirect()
    {
        var calls = 0;
        var tester = new StaticExitTester((_, _) =>
        {
            calls++;
            return Task.FromResult(Success("203.0.113.61"));
        });

        var result = await tester.TestAsync(Credential());

        Assert.True(result.Success);
        Assert.Equal("203.0.113.61", result.ActualExitIp);
        Assert.Equal(3, calls);
        Assert.Contains("3/3", result.SanitizedDetail);
        Assert.DoesNotContain("警告", result.SanitizedDetail);
    }

    [Fact]
    public async Task DirectTwoOfThreeSamples_ReturnsWarning()
    {
        var samples = new Queue<StaticExitTestResult>(
        [Success("203.0.113.62"), Timeout(), Success("203.0.113.62")]);
        var tester = new StaticExitTester((_, _) => Task.FromResult(samples.Dequeue()));

        var result = await tester.TestAsync(Credential());

        Assert.True(result.Success);
        Assert.Equal("203.0.113.62", result.ActualExitIp);
        Assert.StartsWith("警告：", result.SanitizedDetail);
        Assert.Contains("2/3", result.SanitizedDetail);
    }

    [Fact]
    public async Task DirectIntermittentTimeout_SelectsDialer()
    {
        var samples = new Queue<StaticExitTestResult>(
        [Success("203.0.113.63"), Timeout(), Timeout()]);
        var tester = new StaticExitTester((_, _) => Task.FromResult(samples.Dequeue()));

        var result = await tester.TestAsync(Credential());

        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyTimeout, result.FailureCode);
        Assert.Contains("网络不稳定", result.SanitizedDetail);
        Assert.Contains("1/3", result.SanitizedDetail);
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotTryDialer()
    {
        var calls = 0;
        var tester = new StaticExitTester((_, _) =>
        {
            calls++;
            return Task.FromResult(new StaticExitTestResult(
                false, null, FailureCode.StaticProxyAuthenticationFailed, "代理认证被拒绝。"));
        });

        var result = await tester.TestAsync(Credential());

        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyAuthenticationFailed, result.FailureCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DifferentExitIps_AreRejectedAsUnstable()
    {
        var samples = new (StaticExitTestResult Result, TimeSpan Elapsed)[]
        {
            (Success("203.0.113.64"), TimeSpan.FromMilliseconds(20)),
            (Success("203.0.113.65"), TimeSpan.FromMilliseconds(25)),
            (Success("203.0.113.64"), TimeSpan.FromMilliseconds(22))
        };

        var result = StaticExitTester.AssessSamples(samples);

        Assert.False(result.Success);
        Assert.Equal(FailureCode.ExitIpMismatch, result.FailureCode);
        Assert.Contains("IP 不一致", result.SanitizedDetail);
    }

    private static StaticExitTestResult Success(string ip) => new(true, ip, FailureCode.None, null);

    private static StaticExitTestResult Timeout() => new(
        false, null, FailureCode.StaticProxyTimeout, "静态代理请求超时。");

    private static StaticExitConfig Credential() => new()
    {
        Protocol = StaticProxyProtocol.Socks5,
        Server = "proxy.test.invalid",
        Port = 1080,
        Username = "fixture-user",
        Password = "fixture-password"
    };
}
