using AIWorkStation.Models;

namespace AIWorkStation.Services;

public sealed record RouteVerifyResult(
    bool Success,
    FailureCode FailureCode,
    string Detail,
    IReadOnlyList<RouteObservation> Observations,
    IReadOnlyList<ApplicationRouteResult>? ApplicationResults = null);

public class RouteVerifier
{
    public virtual async Task<RouteVerifyResult> VerifyAsync(
        IMihomoApplyClient client,
        IReadOnlyList<ApplicationTarget> targets,
        TimeSpan? waitTimeout = null,
        string? selectedExit = null,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        selectedExit ??= RouteScriptBuilder.DirectStaticExitName;
        var required = targets.Select(target => target.ExecutableName).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<RouteObservation> baseline;
        try { baseline = await client.GetRouteObservationsAsync(token); }
        catch (Exception ex) when (ex is IOException or TimeoutException) { baseline = []; }
        var baselineIds = baseline
            .Select(observation => observation.ConnectionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var matched = new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase);
        var mismatches = new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + (waitTimeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<RouteObservation> observations;
            try { observations = await client.GetRouteObservationsAsync(token); }
            catch (Exception ex) when (ex is IOException or TimeoutException) { observations = []; }
            // 每轮只判断 Verify 开始后的当前连接；旧连接和上一轮已消失的连接都不能持续污染结果。
            matched = new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase);
            mismatches = new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase);
            foreach (var observation in observations)
            {
                if (!string.IsNullOrWhiteSpace(observation.ConnectionId) && baselineIds.Contains(observation.ConnectionId)) continue;
                var executable = required.FirstOrDefault(name => ProcessMatches(observation.Process, name));
                if (executable is null) continue;
                var correct = observation.Chains.Contains(RouteScriptBuilder.StaticGroupName, StringComparer.Ordinal) &&
                              observation.Chains.Contains(selectedExit, StringComparer.Ordinal);
                if (correct) matched[executable] = observation;
                else mismatches[executable] = observation;
            }
            if (required.All(matched.ContainsKey) && mismatches.Count == 0)
                return Assess(required, matched, mismatches, selectedExit);
            var waiting = required.Where(name => !matched.ContainsKey(name)).ToArray();
            progress?.Report($"正在等待目标软件产生网络请求：{string.Join("、", waiting)}");
            await Task.Delay(750, token);
        }

        return Assess(required, matched, mismatches, selectedExit);
    }

    internal static RouteVerifyResult Assess(
        IEnumerable<string> executables,
        IReadOnlyDictionary<string, RouteObservation> matched,
        IReadOnlyDictionary<string, RouteObservation> mismatches,
        string selectedExit)
    {
        var required = executables.ToArray();
        if (mismatches.Count > 0)
            return new(false, FailureCode.ApplicationRouteMismatch,
                $"以下程序产生了连接但未命中 AI静态链 → {selectedExit}：{string.Join("、", mismatches.Keys)}",
                mismatches.Values.ToArray(), BuildResults(required, matched, mismatches));
        if (matched.Count == required.Length)
            return new(true, FailureCode.None, $"所有目标程序均命中 AI静态链 → {selectedExit}。",
                matched.Values.ToArray(), BuildResults(required, matched, mismatches));
        if (matched.Count > 0)
        {
            // 已产生流量的程序必须走对路径；尚未产生流量只表示当前无法观察，不能等同于错误路由。
            var waiting = required.Where(name => !matched.ContainsKey(name)).ToArray();
            return new(true, FailureCode.None,
                $"已验证产生流量的目标程序；以下程序规则已配置，当前未检测到网络请求：{string.Join("、", waiting)}",
                matched.Values.ToArray(), BuildResults(required, matched, mismatches));
        }
        return new(true, FailureCode.None,
            "暂时没有检测到目标软件的网络请求，因此还没有完成实际程序流量验证。",
            [], BuildResults(required, matched, mismatches));
    }

    internal static IReadOnlyList<ApplicationRouteResult> Classify(
        IEnumerable<string> executables,
        IReadOnlyDictionary<string, RouteObservation> matched,
        IReadOnlyDictionary<string, RouteObservation> mismatches)
        => BuildResults(executables, matched, mismatches);

    private static IReadOnlyList<ApplicationRouteResult> BuildResults(
        IEnumerable<string> executables,
        IReadOnlyDictionary<string, RouteObservation> matched,
        IReadOnlyDictionary<string, RouteObservation> mismatches)
        => executables.Select(name => mismatches.TryGetValue(name, out var wrong)
            ? new ApplicationRouteResult(name, ApplicationRouteStatus.WrongRoute, wrong)
            : matched.TryGetValue(name, out var correct)
                ? new ApplicationRouteResult(name, ApplicationRouteStatus.Verified, correct)
                : new ApplicationRouteResult(name, ApplicationRouteStatus.NoTrafficObserved, null)).ToArray();

    private static bool ProcessMatches(string observed, string executableName)
        => observed.Equals(executableName, StringComparison.OrdinalIgnoreCase) ||
           observed.EndsWith("\\" + executableName, StringComparison.OrdinalIgnoreCase) ||
           observed.EndsWith("/" + executableName, StringComparison.OrdinalIgnoreCase) ||
           Path.GetFileName(observed).Equals(executableName, StringComparison.OrdinalIgnoreCase);
}
