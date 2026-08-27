using System.Net;
using System.Net.Http;

namespace AIWorkStation.Services;

public sealed class PublicIpDetector
{
    public static readonly Uri[] DefaultProviders =
    [
        new("https://api.ipify.org"),
        new("https://checkip.amazonaws.com")
    ];

    private readonly TimeSpan _timeout;
    private readonly IReadOnlyList<Uri> _providers;

    public PublicIpDetector(TimeSpan? timeout = null, IReadOnlyList<Uri>? providers = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(4);
        _providers = providers ?? DefaultProviders;
    }

    public async Task<string?> DetectAsync(HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        var ownsClient = client is null;
        client ??= new HttpClient();
        try
        {
            foreach (var provider in _providers)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_timeout);
                    var value = (await client.GetStringAsync(provider, timeout.Token)).Trim();
                    if (IPAddress.TryParse(value, out var address)) return address.ToString();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                }
            }
            return null;
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }
}
