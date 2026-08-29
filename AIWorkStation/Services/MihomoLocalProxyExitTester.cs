using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
            return new(false, null, FailureCode.StaticProxyConnectionFailed, "当前 Mihomo 配置没有可用于链式验证的本地入站端口。");

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
        var providerFailureCount = 0;
        var providerTimedOut = false;
        var providerTransportFailed = false;
        foreach (var provider in _providers)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_timeout);
                var value = (await client.GetStringAsync(provider, timeout.Token)).Trim();
                if (!IPAddress.TryParse(value, out var actual))
                {
                    providerFailureCount++;
                    continue;
                }
                var actualText = actual.ToString();
                if (!string.IsNullOrWhiteSpace(expectedExitIp) &&
                    !string.Equals(expectedExitIp, actualText, StringComparison.OrdinalIgnoreCase))
                    return new(false, actualText, FailureCode.ExitIpMismatch, "链式实际公网出口与预期静态出口 IP 不一致。");
                return new(true, actualText, FailureCode.None, null);
            }
            catch (HttpRequestException ex) when (IsAuthenticationError(ex))
            {
                return new(false, null, FailureCode.StaticProxyAuthenticationFailed, "代理认证被拒绝。");
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ProxyTunnelError)
            {
                return new(false, null, FailureCode.StaticProxyConnectionFailed,
                    "AI静态链无法建立到公网出口查询服务的代理隧道。");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is not null)
            {
                providerFailureCount++;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                providerFailureCount++;
                providerTimedOut = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                providerFailureCount++;
                providerTransportFailed = true;
            }
        }
        if (providerTransportFailed)
            return new(false, null, FailureCode.StaticProxyConnectionFailed,
                "AI静态链无法连接任一公网出口查询服务。");
        if (providerTimedOut && !await IsLocalIngressReachableAsync(proxyUri.Port, token))
            return new(false, null, FailureCode.StaticProxyConnectionFailed, "无法连接 Mihomo 本地代理入站。");

        return new(false, null, FailureCode.ExitIpLookupFailed,
            $"Mihomo 本地入站可达，但 {providerFailureCount}/{_providers.Count} 个出口 IP Provider 均不可用。");
    }

    private static async Task<bool> IsLocalIngressReachableAsync(int port, CancellationToken token)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsAuthenticationError(HttpRequestException exception)
        => exception.HttpRequestError == HttpRequestError.UserAuthenticationError ||
           exception.StatusCode == HttpStatusCode.ProxyAuthenticationRequired;
}
