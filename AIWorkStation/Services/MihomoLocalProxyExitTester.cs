using System.Net;
using System.Net.Http.Headers;
using AIWorkStation.Models;

namespace AIWorkStation.Services;

public class MihomoLocalProxyExitTester
{
    private readonly IReadOnlyList<Uri> _providers;
    private readonly TimeSpan _timeout;

    public MihomoLocalProxyExitTester(IReadOnlyList<Uri>? providers = null, TimeSpan? timeout = null)
    {
        _providers = providers ?? PublicIpDetector.DefaultProviders;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public virtual async Task<StaticExitTestResult> TestAsync(
        int? mixedPort,
        int? httpPort,
        int? socksPort,
        string? expectedExitIp,
        CancellationToken token = default)
    {
        var proxyUri = mixedPort is > 0
            ? new Uri($"http://127.0.0.1:{mixedPort}")
            : httpPort is > 0
                ? new Uri($"http://127.0.0.1:{httpPort}")
                : socksPort is > 0
                    ? new Uri($"socks5://127.0.0.1:{socksPort}")
                    : null;
        if (proxyUri is null)
            return new(false, null, FailureCode.ExitIpLookupFailed, "当前 Mihomo 配置没有可用于链式验证的本地入站端口。");

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxyUri),
            UseProxy = true,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromSeconds(10)
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AIWorkStation", "1.0"));
        foreach (var provider in _providers)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_timeout);
                var value = (await client.GetStringAsync(provider, timeout.Token)).Trim();
                if (!IPAddress.TryParse(value, out var actual)) continue;
                var actualText = actual.ToString();
                if (!string.IsNullOrWhiteSpace(expectedExitIp) &&
                    !string.Equals(expectedExitIp, actualText, StringComparison.OrdinalIgnoreCase))
                    return new(false, actualText, FailureCode.ExitIpMismatch, "链式实际公网出口与预期静态出口 IP 不一致。");
                return new(true, actualText, FailureCode.None, null);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { }
        }
        return new(false, null, FailureCode.ExitIpLookupFailed, "链式连接可达，但无法确认实际公网出口。");
    }
}
