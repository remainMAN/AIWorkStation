using System.Text.Json;
using AIWorkStation.Models;

namespace AIWorkStation.Services;

public sealed class NodeLatencyTester
{
    public const int DefaultMaxConcurrency = 4;
    public static readonly TimeSpan DefaultNodeTimeout = TimeSpan.FromSeconds(5);

    private readonly IMihomoRuntimeClient _client;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _nodeTimeout;
    private readonly Func<DateTimeOffset> _utcNow;

    public NodeLatencyTester(
        IMihomoRuntimeClient client,
        int maxConcurrency = DefaultMaxConcurrency,
        TimeSpan? nodeTimeout = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, DefaultMaxConcurrency);
        _nodeTimeout = nodeTimeout ?? DefaultNodeTimeout;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task TestCurrentSelectedAsync(
        IReadOnlyList<ProxyNodeInfo> nodes,
        string? currentSelectedNode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentSelectedNode)) return;
        var selected = nodes.FirstOrDefault(node =>
            node.Name.Equals(currentSelectedNode, StringComparison.Ordinal));
        if (selected is null) return;

        using var concurrency = new SemaphoreSlim(1, 1);
        await TestOneAsync(selected, concurrency, cancellationToken);
    }

    public async Task TestAllAsync(
        IReadOnlyList<ProxyNodeInfo> nodes,
        string? currentSelectedNode,
        CancellationToken cancellationToken = default)
    {
        // 当前前置节点排在队首；延迟只是本次观测，不写回订阅，也不参与 Apply 判定。
        var ordered = nodes
            .OrderByDescending(node => !string.IsNullOrWhiteSpace(currentSelectedNode) &&
                                       node.Name.Equals(currentSelectedNode, StringComparison.Ordinal))
            .ToArray();
        using var concurrency = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var tasks = ordered.Select(node => TestOneAsync(node, concurrency, cancellationToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task TestOneAsync(
        ProxyNodeInfo node,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            node.LatencyMs = null;
            node.LatencyTestedAt = null;
            node.LatencyStatus = LatencyStatus.Testing;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_nodeTimeout);
            try
            {
                var delay = await _client.GetProxyDelayAsync(node.Name, timeout.Token);
                if (delay <= 0) throw new IOException("Mihomo 返回了无效延迟。");
                node.LatencyMs = delay;
                node.LatencyTestedAt = _utcNow();
                node.LatencyStatus = LatencyStatus.Available;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                node.LatencyMs = null;
                node.LatencyTestedAt = _utcNow();
                node.LatencyStatus = LatencyStatus.Timeout;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ResetCanceledNode(node);
                throw;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or JsonException or HttpRequestException)
            {
                node.LatencyMs = null;
                node.LatencyTestedAt = _utcNow();
                node.LatencyStatus = LatencyStatus.Failed;
            }
        }
        finally
        {
            concurrency.Release();
        }
    }

    private static void ResetCanceledNode(ProxyNodeInfo node)
    {
        node.LatencyMs = null;
        node.LatencyTestedAt = null;
        node.LatencyStatus = LatencyStatus.NotTested;
    }
}
