using System.Diagnostics;
using System.Text.Json;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class MihomoRuntimeConvergenceTests
{
    [Fact]
    public async Task ExecuteAsync_503Then504ThenSuccess_RetriesTransientStatuses()
    {
        var convergence = FastConvergence();
        var attempts = 0;

        var result = await convergence.ExecuteAsync(_ =>
        {
            attempts++;
            return attempts switch
            {
                1 => Task.FromException<int>(new MihomoControllerException(503, "HTTP 503")),
                2 => Task.FromException<int>(new MihomoControllerException(504, "HTTP 504")),
                _ => Task.FromResult(200)
            };
        });

        Assert.Equal(200, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_400_DoesNotRetry()
    {
        var convergence = FastConvergence();
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<MihomoControllerException>(() =>
            convergence.ExecuteAsync<int>(_ =>
            {
                attempts++;
                return Task.FromException<int>(new MihomoControllerException(400, "HTTP 400"));
            }));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_Persistent503_StopsAtDeadline()
    {
        var convergence = new MihomoRuntimeConvergence(
            TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(10));
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<MihomoRuntimeConvergenceException>(() =>
            convergence.ExecuteAsync<int>(_ =>
            {
                attempts++;
                return Task.FromException<int>(new MihomoControllerException(503, "HTTP 503"));
            }));

        Assert.True(attempts > 1);
        Assert.Contains("HTTP 503", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitAsync_ControllerAccessibleBeforeRuntime_ConvergesOnThirdCompleteSnapshot()
    {
        var client = new SequencedRuntimeClient { ManagedFromSnapshot = 3 };
        var convergence = FastConvergence();

        var runtime = await convergence.WaitAsync(
            client,
            snapshot => snapshot.ManagedGroupExists
                ? (true, null)
                : (false, "Managed Group Missing: AI静态链"));

        Assert.True(runtime.ManagedGroupExists);
        Assert.Equal(3, client.ConfigRequests);
        Assert.Equal(3, client.ProxyRequests);
        Assert.Equal(3, client.RuleRequests);
    }

    [Theory]
    [InlineData("JsonException")]
    [InlineData("InvalidDataException")]
    public async Task WaitAsync_IncompleteRuntimeSample_IsTransientAndEventuallySucceeds(string failureType)
    {
        var failure = failureType == "JsonException"
            ? new JsonException("Runtime JSON 暂未完整。")
            : new InvalidDataException("Runtime JSON 缺少 proxies。") as Exception;
        var client = new SequencedRuntimeClient();
        client.ConfigFailures.Enqueue(failure);
        var convergence = FastConvergence();

        var runtime = await convergence.WaitAsync(
            client,
            snapshot => snapshot.ManagedGroupExists
                ? (true, null)
                : (false, "Managed Group Missing: AI静态链"));

        Assert.True(runtime.ManagedGroupExists);
        Assert.Equal(2, client.ConfigRequests);
        Assert.Equal(1, client.ProxyRequests);
        Assert.Equal(1, client.RuleRequests);
    }

    [Fact]
    public async Task WaitAsync_PersistentSemanticMismatch_TimesOutWithExactLastDetail()
    {
        const string detail = "Process Rules Missing: codex.exe";
        var client = new SequencedRuntimeClient { ManagedFromSnapshot = int.MaxValue };
        var convergence = new MihomoRuntimeConvergence(
            TimeSpan.FromMilliseconds(90),
            TimeSpan.FromMilliseconds(10));

        var exception = await Assert.ThrowsAsync<MihomoRuntimeConvergenceException>(() =>
            convergence.WaitAsync(client, _ => (false, detail)));

        Assert.Equal(detail, exception.LastMismatchDetail);
        Assert.Contains(detail, exception.Message, StringComparison.Ordinal);
        Assert.True(client.ConfigRequests > 1);
    }

    [Fact]
    public async Task WaitAsync_ExternalCancellation_PropagatesImmediately()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new SequencedRuntimeClient
        {
            ConfigOverride = async token =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable after cancellation.");
            }
        };
        var convergence = new MihomoRuntimeConvergence(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();

        var pending = convergence.WaitAsync(client, _ => (false, "not ready"), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, client.ConfigRequests);
    }

    private static MihomoRuntimeConvergence FastConvergence()
        => new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));

    private sealed class SequencedRuntimeClient : IMihomoRuntimeClient
    {
        private bool _currentSnapshotManaged;

        public int ManagedFromSnapshot { get; init; } = 1;
        public Queue<Exception> ConfigFailures { get; } = new();
        public Func<CancellationToken, Task<JsonDocument>>? ConfigOverride { get; init; }
        public int ConfigRequests { get; private set; }
        public int ProxyRequests { get; private set; }
        public int RuleRequests { get; private set; }

        public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default)
        {
            ConfigRequests++;
            if (ConfigOverride is not null) return ConfigOverride(token);
            if (ConfigFailures.TryDequeue(out var failure))
                return Task.FromException<JsonDocument>(failure);

            _currentSnapshotManaged = ConfigRequests >= ManagedFromSnapshot;
            return Document(new { mode = "rule" });
        }

        public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default)
        {
            ProxyRequests++;
            var proxies = new Dictionary<string, object?>();
            if (_currentSnapshotManaged)
            {
                proxies[RouteScriptBuilder.DirectStaticExitName] = new { type = "Socks5" };
                proxies[RouteScriptBuilder.StaticGroupName] = new
                {
                    type = "Selector",
                    now = RouteScriptBuilder.DirectStaticExitName,
                    all = new[] { RouteScriptBuilder.DirectStaticExitName }
                };
            }
            return Document(new { proxies });
        }

        public Task<JsonDocument> GetRulesAsync(CancellationToken token = default)
        {
            RuleRequests++;
            object[] rules = _currentSnapshotManaged
                ? [new { type = "ProcessName", payload = "codex.exe", proxy = RouteScriptBuilder.StaticGroupName }]
                : [];
            return Document(new { rules });
        }

        public Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default)
            => Task.FromResult(1);

        private static Task<JsonDocument> Document(object value)
            => Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(value)));
    }
}
