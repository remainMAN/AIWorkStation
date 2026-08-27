using System.Text.Json;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class RouteVerifierConnectionBaselineTests
{
    [Fact]
    public async Task PreExistingWrongConnection_IsIgnored()
    {
        var oldWrong = Wrong("old-1");
        var result = await VerifyAsync([[oldWrong], [oldWrong]], TimeSpan.FromMilliseconds(10));

        Assert.True(result.Success);
        Assert.Equal(ApplicationRouteStatus.NoTrafficObserved, Assert.Single(result.ApplicationResults!).Status);
    }

    [Fact]
    public async Task NewCorrectAfterOldWrong_IsVerified()
    {
        var oldWrong = Wrong("old-1");
        var result = await VerifyAsync([[oldWrong], [oldWrong, Correct("new-1")]], TimeSpan.FromSeconds(1));

        Assert.True(result.Success);
        Assert.Equal(ApplicationRouteStatus.Verified, Assert.Single(result.ApplicationResults!).Status);
    }

    [Fact]
    public async Task NewWrongAfterVerification_StillFails()
    {
        var result = await VerifyAsync([[], [Wrong("new-1")]], TimeSpan.FromMilliseconds(10));

        Assert.False(result.Success);
        Assert.Equal(FailureCode.ApplicationRouteMismatch, result.FailureCode);
    }

    [Fact]
    public async Task ConnectionState_RecomputedPerPoll()
    {
        var result = await VerifyAsync([[], [Wrong("new-1")], []], TimeSpan.FromMilliseconds(900));

        Assert.True(result.Success);
        Assert.Equal(ApplicationRouteStatus.NoTrafficObserved, Assert.Single(result.ApplicationResults!).Status);
    }

    [Fact]
    public async Task OldWrongNotPermanentlyAccumulated()
    {
        var result = await VerifyAsync([[], [Wrong("new-1")], [Correct("new-2")]], TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Equal(ApplicationRouteStatus.Verified, Assert.Single(result.ApplicationResults!).Status);
    }

    private static Task<RouteVerifyResult> VerifyAsync(
        IReadOnlyList<IReadOnlyList<RouteObservation>> snapshots,
        TimeSpan timeout)
        => new RouteVerifier().VerifyAsync(
            new SequencedObservationClient(snapshots),
            [new ApplicationTarget("Codex", "codex.exe", string.Empty, true, "fixture")],
            timeout,
            RouteScriptBuilder.DirectStaticExitName);

    private static RouteObservation Correct(string id)
        => new("codex.exe", "ProcessName",
            [RouteScriptBuilder.StaticGroupName, RouteScriptBuilder.DirectStaticExitName], null)
        {
            ConnectionId = id
        };

    private static RouteObservation Wrong(string id)
        => new("codex.exe", "Match", ["FlyintPro"], null) { ConnectionId = id };

    private sealed class SequencedObservationClient(
        IReadOnlyList<IReadOnlyList<RouteObservation>> snapshots) : IMihomoApplyClient
    {
        private int _index;

        public Task<IReadOnlyList<RouteObservation>> GetRouteObservationsAsync(CancellationToken token = default)
        {
            var index = Math.Min(_index++, snapshots.Count - 1);
            return Task.FromResult(snapshots[index]);
        }

        public Task PutInlineConfigAsync(string yamlPayload, CancellationToken token = default) => Task.CompletedTask;
        public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProxySelection>> GetProxySelectionsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ProxySelection>>([]);
        public Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default) => Task.FromResult(1);
        public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default)
            => Task.FromResult(JsonDocument.Parse("{}"));
        public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default)
            => Task.FromResult(JsonDocument.Parse("{\"proxies\":{}}"));
        public Task<JsonDocument> GetRulesAsync(CancellationToken token = default)
            => Task.FromResult(JsonDocument.Parse("{\"rules\":[]}"));
    }
}
