using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class ProviderFallbackTests
{
    private static readonly Uri[] Providers =
    [
        new("http://provider-one.test/ip"),
        new("http://provider-two.test/ip")
    ];

    [Fact]
    public async Task StaticExit_ProviderServerError_FallsBackToSecondProvider()
    {
        await using var proxy = new SequencedProviderProxy(
            new(HttpStatusCode.ServiceUnavailable, string.Empty),
            new(HttpStatusCode.OK, "203.0.113.101\n"));

        var result = await StaticTester().TestAsync(Credential(proxy.Port));

        Assert.True(result.Success);
        Assert.Equal("203.0.113.101", result.ActualExitIp);
        Assert.Equal(2, proxy.RequestCount);
    }

    [Fact]
    public async Task StaticExit_ProviderTimeout_FallsBackToSecondProvider()
    {
        await using var proxy = new SequencedProviderProxy(
            new(HttpStatusCode.OK, "203.0.113.100", TimeSpan.FromMilliseconds(300)),
            new(HttpStatusCode.OK, "203.0.113.102\n"));

        var result = await new StaticExitTester(Providers, TimeSpan.FromMilliseconds(75), 1)
            .TestAsync(Credential(proxy.Port));

        Assert.True(result.Success);
        Assert.Equal("203.0.113.102", result.ActualExitIp);
        Assert.Equal(2, proxy.RequestCount);
    }

    [Fact]
    public async Task StaticExit_AllProvidersUnavailable_ReturnsLookupFailure()
    {
        await using var proxy = new SequencedProviderProxy(
            new(HttpStatusCode.BadGateway, string.Empty),
            new(HttpStatusCode.OK, "not-an-ip"));

        var result = await StaticTester().TestAsync(Credential(proxy.Port));

        Assert.False(result.Success);
        Assert.Equal(FailureCode.ExitIpLookupFailed, result.FailureCode);
        Assert.Contains("2/2", result.SanitizedDetail);
    }

    [Fact]
    public async Task StaticExit_AllProviderTimeouts_ReturnsLookupFailureWhenProxyIsReachable()
    {
        await using var proxy = new SequencedProviderProxy(
            new(HttpStatusCode.OK, "203.0.113.100", TimeSpan.FromMilliseconds(300)),
            new(HttpStatusCode.OK, "203.0.113.101", TimeSpan.FromMilliseconds(300)));

        var result = await new StaticExitTester(Providers, TimeSpan.FromMilliseconds(75), 1)
            .TestAsync(Credential(proxy.Port));

        Assert.False(result.Success);
        Assert.Equal(FailureCode.ExitIpLookupFailed, result.FailureCode);
        Assert.Equal(2, proxy.RequestCount);
    }

    [Fact]
    public async Task StaticExit_ExternalCancellation_Propagates()
    {
        await using var proxy = new SequencedProviderProxy(
            new ProviderResponse(HttpStatusCode.OK, "203.0.113.100", TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StaticExitTester([Providers[0]], TimeSpan.FromSeconds(2), 1)
                .TestAsync(Credential(proxy.Port), cancellation.Token));
    }

    [Fact]
    public async Task LocalExit_InvalidProviderBody_FallsBackToSecondProvider()
    {
        await using var proxy = new SequencedProviderProxy(
            new(HttpStatusCode.OK, "temporarily unavailable"),
            new(HttpStatusCode.OK, "203.0.113.103\n"));

        var result = await new MihomoLocalProxyExitTester(Providers)
            .TestAsync(proxy.Port, null, null, "203.0.113.103");

        Assert.True(result.Success);
        Assert.Equal("203.0.113.103", result.ActualExitIp);
        Assert.Equal(2, proxy.RequestCount);
    }

    [Fact]
    public async Task LocalExit_AllProvidersUnavailable_ReturnsLookupFailure()
    {
        await using var proxy = new SequencedProviderProxy(
            new(HttpStatusCode.GatewayTimeout, string.Empty),
            new(HttpStatusCode.OK, "not-an-ip"));

        var result = await new MihomoLocalProxyExitTester(Providers)
            .TestAsync(proxy.Port, null, null, "203.0.113.104");

        Assert.False(result.Success);
        Assert.Equal(FailureCode.ExitIpLookupFailed, result.FailureCode);
        Assert.Contains("2/2", result.SanitizedDetail);
    }

    [Fact]
    public async Task LocalExit_ProxyAuthenticationRequired_RemainsHardFailure()
    {
        await using var proxy = new SequencedProviderProxy(
            new(HttpStatusCode.ProxyAuthenticationRequired, string.Empty),
            new(HttpStatusCode.OK, "203.0.113.105"));

        var result = await new MihomoLocalProxyExitTester(Providers)
            .TestAsync(proxy.Port, null, null, null);

        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyAuthenticationFailed, result.FailureCode);
        Assert.Equal(1, proxy.RequestCount);
    }

    [Fact]
    public async Task LocalExit_NoIngress_RemainsHardFailure()
    {
        var result = await new MihomoLocalProxyExitTester(Providers)
            .TestAsync(null, null, null, null);

        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyConnectionFailed, result.FailureCode);
    }

    [Fact]
    public async Task LocalExit_ExitIpMismatch_RemainsHardFailure()
    {
        await using var proxy = new SequencedProviderProxy(
            new ProviderResponse(HttpStatusCode.OK, "203.0.113.106"));

        var result = await new MihomoLocalProxyExitTester([Providers[0]])
            .TestAsync(proxy.Port, null, null, "203.0.113.107");

        Assert.False(result.Success);
        Assert.Equal(FailureCode.ExitIpMismatch, result.FailureCode);
    }

    private static StaticExitTester StaticTester()
        => new(Providers, TimeSpan.FromSeconds(1), 1);

    private static StaticExitConfig Credential(int port) => new()
    {
        Protocol = StaticProxyProtocol.Http,
        Server = "127.0.0.1",
        Port = port
    };

    private sealed record ProviderResponse(HttpStatusCode Status, string Body, TimeSpan? Delay = null);

    private sealed class SequencedProviderProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Task _server;
        private int _requestCount;

        public SequencedProviderProxy(params ProviderResponse[] responses)
        {
            _listener.Start();
            _server = Task.Run(async () =>
            {
                try
                {
                    foreach (var response in responses)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _handlers.Add(HandleAsync(client, response, _stop.Token));
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task HandleAsync(TcpClient client, ProviderResponse response, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    var buffer = new byte[2048];
                    var received = new List<byte>();
                    while (received.Count < 32 * 1024)
                    {
                        var read = await stream.ReadAsync(buffer, token);
                        if (read == 0) break;
                        received.AddRange(buffer.AsSpan(0, read).ToArray());
                        if (Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                            break;
                    }

                    Interlocked.Increment(ref _requestCount);
                    if (response.Delay is { } delay) await Task.Delay(delay, token);
                    var bodyLength = Encoding.ASCII.GetByteCount(response.Body);
                    var bytes = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {(int)response.Status} {response.Status}\r\nContent-Length: {bodyLength}\r\nConnection: close\r\n\r\n{response.Body}");
                    await stream.WriteAsync(bytes, token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _server; } catch { }
            try { await Task.WhenAll(_handlers); } catch { }
            _stop.Dispose();
        }
    }
}
