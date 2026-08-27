using System.Net;
using System.Net.Sockets;
using System.Text;
using AIWorkStation.Models;

namespace AIWorkStation.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aiws-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { } }
}

internal sealed class FakeDnsResolver(params IPAddress[] addresses) : Services.IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(addresses);
}

internal sealed class FakeApplicationSource(params ApplicationTarget[] targets) : Services.IApplicationSource
{
    public IEnumerable<ApplicationTarget> FindAll() => targets;
}

internal sealed class SingleResponseProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start(HttpStatusCode status, string body)
    {
        _listener.Start();
        _server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var received = new List<byte>();
            while (received.Count < 64 * 1024)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0) break;
                received.AddRange(buffer.AsSpan(0, read).ToArray());
                if (Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} {status}\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(bytes, _stop.Token);
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_server is not null) try { await _server; } catch { }
        _stop.Dispose();
    }
}

internal sealed class FakeReloadService(bool result) : Services.ClashReloadService
{
    public override Task<bool> RestartAsync(string clashExecutable, string runtimeConfigPath, CancellationToken token = default)
        => Task.FromResult(result);
}
