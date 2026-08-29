using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class ProxyTunnelClassificationTests
{
    private static readonly Uri[] HttpsProviders =
    [
        new("https://provider-one.test/ip"),
        new("https://provider-two.test/ip")
    ];

    [Fact]
    public async Task StaticExit_ProxyTunnel503_IsConnectionFailureWithoutProviderFallback()
    {
        await using var proxy = new RejectingConnectProxy(HttpStatusCode.ServiceUnavailable);
        var credential = new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Http,
            Server = "127.0.0.1",
            Port = proxy.Port
        };

        var result = await new StaticExitTester(HttpsProviders, TimeSpan.FromSeconds(1), 1)
            .TestAsync(credential);

        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyConnectionFailed, result.FailureCode);
        Assert.Equal(1, proxy.RequestCount);
    }

    [Fact]
    public async Task LocalExit_ProxyTunnel504_IsConnectionFailureWithoutProviderFallback()
    {
        await using var proxy = new RejectingConnectProxy(HttpStatusCode.GatewayTimeout);

        var result = await new MihomoLocalProxyExitTester(HttpsProviders, TimeSpan.FromSeconds(1))
            .TestAsync(proxy.Port, null, null, null);

        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyConnectionFailed, result.FailureCode);
        Assert.Equal(1, proxy.RequestCount);
    }

    private sealed class RejectingConnectProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Task _server;
        private int _requestCount;

        public RejectingConnectProxy(HttpStatusCode status)
        {
            _listener.Start();
            _server = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _handlers.Add(HandleAsync(client, status, _stop.Token));
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task HandleAsync(TcpClient client, HttpStatusCode status, CancellationToken token)
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
                        if (Encoding.ASCII.GetString(received.ToArray())
                            .Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }

                    Interlocked.Increment(ref _requestCount);
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {(int)status} {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, token);
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
