using System.Diagnostics;
using System.Text.Json;

namespace AIWorkStation.Services;

public sealed class MihomoRuntimeConvergenceException : TimeoutException
{
    public MihomoRuntimeConvergenceException(
        string message,
        string? lastMismatchDetail,
        Exception? innerException = null)
        : base(message, innerException)
    {
        LastMismatchDetail = lastMismatchDetail;
    }

    public string? LastMismatchDetail { get; }
}

public sealed class MihomoRuntimeConvergence
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(400);

    private readonly TimeSpan _timeout;
    private readonly TimeSpan _interval;

    public MihomoRuntimeConvergence(TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _interval = interval ?? DefaultInterval;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync(
            async cancellationToken =>
            {
                await operation(cancellationToken);
                return true;
            },
            token);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var elapsed = Stopwatch.StartNew();
        Exception? lastTransient = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);

        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await operation(deadline.Token);
            }
            catch (Exception ex) when (IsTransient(ex, token, includeInvalidRuntimeSample: false))
            {
                lastTransient = ex;
            }

            if (!await DelayForRetryAsync(elapsed, token))
                throw CreateTimeoutException(lastMismatchDetail: null, lastTransient);
        }
    }

    public Task<RuntimeSemanticBaseline> WaitAsync(
        IMihomoRuntimeClient runtimeClient,
        Func<RuntimeSemanticBaseline, (bool IsConverged, string? MismatchDetail)> predicate,
        CancellationToken token = default)
        => WaitAsync(runtimeClient, predicate, reconcile: null, token);

    public async Task<RuntimeSemanticBaseline> WaitAsync(
        IMihomoRuntimeClient runtimeClient,
        Func<RuntimeSemanticBaseline, (bool IsConverged, string? MismatchDetail)> predicate,
        Func<RuntimeSemanticBaseline, CancellationToken, Task>? reconcile,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeClient);
        ArgumentNullException.ThrowIfNull(predicate);

        var elapsed = Stopwatch.StartNew();
        Exception? lastTransient = null;
        string? lastMismatchDetail = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);

        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var runtime = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(
                    runtimeClient,
                    deadline.Token);
                if (reconcile is not null) await reconcile(runtime, deadline.Token);
                var result = predicate(runtime);
                if (result.IsConverged) return runtime;

                lastMismatchDetail = result.MismatchDetail;
                lastTransient = null;
            }
            catch (Exception ex) when (IsTransient(ex, token, includeInvalidRuntimeSample: true))
            {
                lastTransient = ex;
            }

            if (!await DelayForRetryAsync(elapsed, token))
                throw CreateTimeoutException(lastMismatchDetail, lastTransient);
        }
    }

    public static bool IsTransient(
        Exception exception,
        CancellationToken callerToken,
        bool includeInvalidRuntimeSample = false)
    {
        if (exception is MihomoControllerException controllerException)
            return controllerException.StatusCode is 502 or 503 or 504;

        if (exception is OperationCanceledException)
            return !callerToken.IsCancellationRequested;

        if (exception is InvalidDataException)
            return includeInvalidRuntimeSample;

        if (exception is TimeoutException or EndOfStreamException or IOException)
            return true;

        return includeInvalidRuntimeSample &&
               exception is JsonException or YamlDotNet.Core.YamlException;
    }

    private async Task<bool> DelayForRetryAsync(Stopwatch elapsed, CancellationToken token)
    {
        var remaining = _timeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero) return false;

        await Task.Delay(remaining < _interval ? remaining : _interval, token);
        return elapsed.Elapsed < _timeout;
    }

    private static MihomoRuntimeConvergenceException CreateTimeoutException(
        string? lastMismatchDetail,
        Exception? lastTransient)
    {
        var detail = !string.IsNullOrWhiteSpace(lastMismatchDetail)
            ? lastMismatchDetail
            : lastTransient?.Message;
        var message = string.IsNullOrWhiteSpace(detail)
            ? "Mihomo 运行态未在限定时间内就绪。"
            : $"Mihomo 运行态未在限定时间内就绪：{detail}";
        return new(message, lastMismatchDetail, lastTransient);
    }
}
