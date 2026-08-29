using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using AIWorkStation.Models;

namespace AIWorkStation.Services;

public class StaticExitTester
{
    public const int DefaultDirectSampleCount = 3;

    private readonly IReadOnlyList<Uri> _providers;
    private readonly TimeSpan _timeout;
    private readonly int _sampleCount;
    private readonly Func<StaticExitConfig, CancellationToken, Task<StaticExitTestResult>>? _sampleOverride;

    public StaticExitTester(IReadOnlyList<Uri>? providers = null, TimeSpan? timeout = null, int? sampleCount = null)
    {
        _providers = providers ?? PublicIpDetector.DefaultProviders;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        // 自定义 Provider 构造器只用于隔离诊断；正式默认路径固定执行三次独立采样。
        _sampleCount = Math.Max(1, sampleCount ?? (providers is null ? DefaultDirectSampleCount : 1));
    }

    internal StaticExitTester(
        Func<StaticExitConfig, CancellationToken, Task<StaticExitTestResult>> sampleOverride,
        int sampleCount = DefaultDirectSampleCount)
    {
        _providers = PublicIpDetector.DefaultProviders;
        _timeout = TimeSpan.FromSeconds(10);
        _sampleCount = Math.Max(1, sampleCount);
        _sampleOverride = sampleOverride ?? throw new ArgumentNullException(nameof(sampleOverride));
    }

    public virtual async Task<StaticExitTestResult> TestAsync(StaticExitConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            config.Validate();
            var samples = new List<(StaticExitTestResult Result, TimeSpan Elapsed)>(_sampleCount);
            for (var index = 0; index < _sampleCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                var sample = _sampleOverride is null
                    ? await TestSingleSampleAsync(config, cancellationToken)
                    : await _sampleOverride(config, cancellationToken);
                stopwatch.Stop();
                samples.Add((sample, stopwatch.Elapsed));

                // 认证失败是确定性的凭据错误，不能继续采样，也不能被上层当成网络失败切换链式。
                if (sample.FailureCode == FailureCode.StaticProxyAuthenticationFailed) return sample;
            }
            return AssessSamples(samples, _sampleCount);
        }
        catch (ProxyAuthenticationException)
        {
            return new(false, null, FailureCode.StaticProxyAuthenticationFailed, "代理认证被拒绝。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, FailureCode.StaticProxyTimeout, "静态代理请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or ArgumentException)
        {
            var code = IsAuthenticationError(ex)
                ? FailureCode.StaticProxyAuthenticationFailed
                : FailureCode.StaticProxyConnectionFailed;
            return new(false, null, code, Sanitize(ex.Message, config));
        }
    }

    private async Task<StaticExitTestResult> TestSingleSampleAsync(StaticExitConfig config, CancellationToken cancellationToken)
    {
        // 每次采样使用独立 Handler、Client 和 timeout，避免一次偶然成功掩盖间歇性网络故障。
        using var handler = CreateHandler(config);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AIWorkStation", "1.0"));
        var providerFailureCount = 0;
        var providerTimedOut = false;
        var providerTransportFailed = false;
        foreach (var provider in _providers)
        {
            try
            {
                using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerTimeout.CancelAfter(_timeout);
                var value = (await client.GetStringAsync(provider, providerTimeout.Token)).Trim();
                if (IPAddress.TryParse(value, out var ip))
                    return new(true, ip.ToString(), FailureCode.None, null);
                providerFailureCount++;
            }
            catch (ProxyAuthenticationException)
            {
                return new(false, null, FailureCode.StaticProxyAuthenticationFailed, "代理认证被拒绝。");
            }
            catch (HttpRequestException ex) when (IsAuthenticationError(ex))
            {
                return new(false, null, FailureCode.StaticProxyAuthenticationFailed, "代理认证被拒绝。");
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ProxyTunnelError)
            {
                return new(false, null, FailureCode.StaticProxyConnectionFailed,
                    "静态代理无法建立到公网出口查询服务的隧道连接。");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is not null)
            {
                // Provider 的 HTTP 错误不代表静态代理不可用；继续尝试下一个独立 Provider。
                providerFailureCount++;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 单个 Provider 超时属于探测源不可用，不能据此判定代理超时。
                providerFailureCount++;
                providerTimedOut = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                if (IsAuthenticationError(ex))
                    return new(false, null, FailureCode.StaticProxyAuthenticationFailed, "代理认证被拒绝。");
                providerFailureCount++;
                providerTransportFailed = true;
            }
        }
        if (providerTransportFailed)
            return new(false, null, FailureCode.StaticProxyConnectionFailed,
                "静态代理无法连接任一公网出口查询服务。");
        if (providerTimedOut &&
            !await IsProxyEndpointReachableAsync(config.Server, config.Port, cancellationToken))
            return new(false, null, FailureCode.StaticProxyConnectionFailed, "无法连接静态代理服务器。");

        return new(false, null, FailureCode.ExitIpLookupFailed,
            $"静态代理端点可达，但 {providerFailureCount}/{_providers.Count} 个出口 IP Provider 均不可用。");
    }

    private static async Task<bool> IsProxyEndpointReachableAsync(string server, int port, CancellationToken token)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await socket.ConnectAsync(server, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return false;
        }
    }

    internal static StaticExitTestResult AssessSamples(
        IReadOnlyList<(StaticExitTestResult Result, TimeSpan Elapsed)> samples,
        int expectedSampleCount = DefaultDirectSampleCount)
    {
        if (samples.Count == 0) throw new ArgumentException("至少需要一条直连采样结果。", nameof(samples));

        var authenticationFailure = samples.Select(item => item.Result)
            .FirstOrDefault(result => result.FailureCode == FailureCode.StaticProxyAuthenticationFailed);
        if (authenticationFailure is not null) return authenticationFailure;

        var successes = samples.Where(item => item.Result.Success && !string.IsNullOrWhiteSpace(item.Result.ActualExitIp)).ToArray();
        var requiredSuccesses = expectedSampleCount >= DefaultDirectSampleCount ? 2 : 1;
        if (successes.Length >= requiredSuccesses)
        {
            var exitIps = successes.Select(item => item.Result.ActualExitIp!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (exitIps.Length != 1)
                return new(false, null, FailureCode.ExitIpMismatch,
                    $"直连网络不稳定：{successes.Length}/{expectedSampleCount} 次采样成功，但实际出口 IP 不一致。");

            var averageMs = Math.Max(0, (int)Math.Round(successes.Average(item => item.Elapsed.TotalMilliseconds)));
            if (successes.Length == expectedSampleCount)
                return new(true, exitIps[0], FailureCode.None,
                    $"直连采样 {successes.Length}/{expectedSampleCount} 成功，出口 IP 一致，平均耗时 {averageMs} ms。");

            return new(true, exitIps[0], FailureCode.None,
                $"警告：直连采样 {successes.Length}/{expectedSampleCount} 成功且出口 IP 一致；检测到网络波动，平均耗时 {averageMs} ms。");
        }

        var failures = samples.Select(item => item.Result).Where(result => !result.Success).ToArray();
        if (failures.Length > 0 && failures.All(result => result.FailureCode == FailureCode.ExitIpLookupFailed))
            return new(false, null, FailureCode.ExitIpLookupFailed, failures[0].SanitizedDetail);

        var failureCode = failures.Any(result => result.FailureCode == FailureCode.StaticProxyTimeout)
            ? FailureCode.StaticProxyTimeout
            : failures.Any(result => result.FailureCode == FailureCode.StaticProxyConnectionFailed)
                ? FailureCode.StaticProxyConnectionFailed
                : failures.FirstOrDefault()?.FailureCode ?? FailureCode.StaticProxyConnectionFailed;
        return new(false, null, failureCode,
            $"直连网络不稳定：{successes.Length}/{expectedSampleCount} 次采样成功，至少需要 {requiredSuccesses} 次成功且出口 IP 一致。");
    }

    private static SocketsHttpHandler CreateHandler(StaticExitConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromSeconds(15),
            UseCookies = false
        };
        if (config.Protocol == StaticProxyProtocol.Http)
        {
            var proxy = new WebProxy(new Uri($"http://{config.Server}:{config.Port}"));
            if (!string.IsNullOrEmpty(config.Username))
                proxy.Credentials = new NetworkCredential(config.Username, config.Password);
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
            handler.ConnectCallback = (context, token) => ConnectSocks5Async(config, context.DnsEndPoint, token);
        }
        return handler;
    }

    private static async ValueTask<Stream> ConnectSocks5Async(StaticExitConfig config, DnsEndPoint destination, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(config.Server, config.Port, token);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var hasCredentials = !string.IsNullOrEmpty(config.Username);
            await stream.WriteAsync(hasCredentials ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, token);
            var greeting = await ReadExactAsync(stream, 2, token);
            if (greeting[0] != 5 || greeting[1] == 0xFF) throw new ProxyAuthenticationException();
            if (greeting[1] == 2)
            {
                var user = Encoding.UTF8.GetBytes(config.Username ?? string.Empty);
                var pass = Encoding.UTF8.GetBytes(config.Password ?? string.Empty);
                byte[]? auth = null;
                try
                {
                    if (user.Length > 255 || pass.Length > 255) throw new ProxyAuthenticationException();
                    auth = new byte[3 + user.Length + pass.Length];
                    auth[0] = 1;
                    auth[1] = (byte)user.Length;
                    user.CopyTo(auth, 2);
                    auth[2 + user.Length] = (byte)pass.Length;
                    pass.CopyTo(auth, 3 + user.Length);
                    await stream.WriteAsync(auth, token);
                    var result = await ReadExactAsync(stream, 2, token);
                    if (result[1] != 0) throw new ProxyAuthenticationException();
                }
                finally
                {
                    // SOCKS 认证帧写出后不再需要，立即清零进程内的临时明文字节。
                    CryptographicOperations.ZeroMemory(user);
                    CryptographicOperations.ZeroMemory(pass);
                    if (auth is not null) CryptographicOperations.ZeroMemory(auth);
                }
            }

            var host = Encoding.ASCII.GetBytes(destination.Host);
            if (host.Length > 255) throw new IOException("目标主机名过长。");
            var request = new byte[7 + host.Length];
            request[0] = 5;
            request[1] = 1;
            request[2] = 0;
            request[3] = 3;
            request[4] = (byte)host.Length;
            host.CopyTo(request, 5);
            request[^2] = (byte)(destination.Port >> 8);
            request[^1] = (byte)destination.Port;
            await stream.WriteAsync(request, token);

            var header = await ReadExactAsync(stream, 4, token);
            if (header[1] != 0) throw new IOException($"SOCKS5 CONNECT 失败（{header[1]}）。");
            var addressLength = header[3] switch
            {
                1 => 4,
                4 => 16,
                3 => (await ReadExactAsync(stream, 1, token))[0],
                _ => throw new IOException("SOCKS5 返回了未知地址类型。")
            };
            await ReadExactAsync(stream, addressLength + 2, token);
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, count - offset), token);
            if (read == 0) throw new IOException("代理服务器提前关闭连接。");
            offset += read;
        }
        return result;
    }

    private static bool IsAuthenticationError(Exception exception)
        => exception is ProxyAuthenticationException ||
           exception is HttpRequestException
           {
               HttpRequestError: HttpRequestError.UserAuthenticationError
           } ||
           exception is HttpRequestException
           {
               StatusCode: HttpStatusCode.ProxyAuthenticationRequired
           } ||
           exception.InnerException is not null && IsAuthenticationError(exception.InnerException);

    private static string Sanitize(string detail, StaticExitConfig config)
    {
        if (!string.IsNullOrEmpty(config.Password)) detail = detail.Replace(config.Password, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(config.Username)) detail = detail.Replace(config.Username, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(config.Server)) detail = detail.Replace(config.Server, "***", StringComparison.OrdinalIgnoreCase);
        return detail;
    }

    private sealed class ProxyAuthenticationException : IOException { }
}
