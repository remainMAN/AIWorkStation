using System.Net;
using System.Text.Json;
using AIWorkStation.Models;
using AIWorkStation.Services;
using AIWorkStation.ViewModels;

namespace AIWorkStation.Tests;

public sealed class ApplyEngineIntegrationTests
{
    // 临时 Runtime 只用于写盘前验证；退出该阶段时必须等价恢复，并准确暴露恢复状态。
    [Fact]
    public async Task TemporaryRuntimeRestore_VerifiesBaselineSemantics()
    {
        using var fixture = await ApplyFixture.CreateAsync();

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.True(fixture.Pipe.ConfigRequests >= 3);
        Assert.True(fixture.Pipe.ProxyRequests >= 3);
        Assert.True(fixture.Pipe.RuleRequests >= 3);
        Assert.Equal("baseline", fixture.Pipe.LastRestoredGeneration);
    }

    [Fact]
    public async Task TemporaryRuntimeRestorePutSuccessButMismatch_IsRecoveryFailed()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Pipe.RestoreMismatch = true;

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.RecoveryFailed, result.FailureCode);
        Assert.Equal("Recover", result.Stage);
        Assert.True(result.RecoveryAttempted);
        Assert.False(result.RecoverySucceeded);
        Assert.False(result.FilesModified);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.Reloader.RestartCalls);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public async Task TemporaryRuntimeRestoreFailure_DisablesImmediateRetry()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Pipe.RestoreMismatch = true;
        var result = await fixture.ApplyAsync();
        var viewModel = new MainViewModel { ApplyResult = result, State = UiState.RecoveryFailed };

        Assert.Equal(FailureCode.RecoveryFailed, result.FailureCode);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        Assert.False(viewModel.ReturnToRoutingCommand.CanExecute(null));
    }

    [Fact]
    public async Task TemporaryRuntimeRestoreSuccess_AllowsPipelineContinue()
    {
        using var fixture = await ApplyFixture.CreateAsync();

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.True(fixture.Writer.SuccessfulWrites > 0);
        Assert.Equal(1, fixture.Reloader.RestartCalls);
    }

    [Fact]
    public async Task ApplyDirectNetworkFailure_RebuildsWithDialerBeforeWrite()
    {
        using var fixture = await ApplyFixture.CreateAsync(StaticTransportMode.Direct);
        fixture.StaticExit.Result = new(false, null, FailureCode.StaticProxyTimeout, "fixture timeout");
        fixture.LocalExit.Result = new(true, "203.0.113.44", FailureCode.None, null);

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal(StaticTransportMode.DialerProxy, result.TransportMode);
        Assert.Contains("dialer-proxy", fixture.Pipe.LastCandidateYaml, StringComparison.Ordinal);
        Assert.True(fixture.Events.IndexOf("runtime-candidate") < fixture.Events.IndexOf("persistent-write"));
    }

    [Fact]
    public async Task ManualDirectSelection_DoesNotFallbackToDialer()
    {
        using var fixture = await ApplyFixture.CreateAsync(
            StaticTransportMode.Direct,
            transportPreference: StaticTransportPreference.Direct);
        fixture.StaticExit.Result = new(false, null, FailureCode.StaticProxyTimeout, "fixture timeout");

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyTimeout, result.FailureCode);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.LocalExit.Calls);
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotTryDialer()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.StaticExit.Result = new(false, null, FailureCode.StaticProxyAuthenticationFailed, "fixture auth");

        var result = await fixture.ApplyAsync();

        Assert.Equal(FailureCode.StaticProxyAuthenticationFailed, result.FailureCode);
        Assert.Equal(0, fixture.Pipe.PutCalls);
        Assert.Equal(0, fixture.LocalExit.Calls);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
    }

    [Fact]
    public async Task SelectedTransportMode_RemainsFixedAfterApply()
    {
        using var fixture = await ApplyFixture.CreateAsync(StaticTransportMode.DialerProxy, expectedExitIp: string.Empty);
        fixture.StaticExit.Result = new(false, null, FailureCode.StaticProxyConnectionFailed, "fixture unreachable");
        fixture.LocalExit.Result = new(true, "203.0.113.77", FailureCode.None, null);

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal(StaticTransportMode.DialerProxy, result.TransportMode);
        Assert.Equal(RouteScriptBuilder.DialerStaticExitName, fixture.Pipe.Selections.Last().Proxy);
        Assert.DoesNotContain(fixture.Pipe.Selections.SkipWhile(item => item.Proxy != RouteScriptBuilder.DialerStaticExitName),
            item => item.Proxy == RouteScriptBuilder.DirectStaticExitName);
    }

    [Fact]
    public async Task Reload_SelectsConfiguredTransportExplicitly()
    {
        using var fixture = await ApplyFixture.CreateAsync(StaticTransportMode.DialerProxy, expectedExitIp: string.Empty);
        fixture.StaticExit.Result = new(false, null, FailureCode.StaticProxyTimeout, "fixture timeout");
        fixture.LocalExit.Result = new(true, "203.0.113.77", FailureCode.None, null);

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.True(fixture.Pipe.Selections.Count(item =>
            item.Group == RouteScriptBuilder.StaticGroupName &&
            item.Proxy == RouteScriptBuilder.DialerStaticExitName) >= 2);
    }

    [Fact]
    public async Task DialerProxy_ActualIpIsReturnedToUi()
    {
        using var fixture = await ApplyFixture.CreateAsync(StaticTransportMode.DialerProxy, expectedExitIp: string.Empty);
        fixture.StaticExit.Result = new(false, null, FailureCode.StaticProxyTimeout, "fixture timeout");
        fixture.LocalExit.Result = new(true, "203.0.113.91", FailureCode.None, null);

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal("203.0.113.91", result.ActualExitIp);
    }

    [Fact]
    public async Task NoTraffic_DoesNotFailApply()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.RouteVerifier.Result = RouteVerifier.Assess(["codex.exe"],
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase),
            RouteScriptBuilder.DirectStaticExitName);

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal(FailureCode.None, result.FailureCode);
        Assert.True(result.RouteVerificationNotObserved);
    }

    [Fact]
    public async Task NoTraffic_DoesNotTriggerRecovery()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.RouteVerifier.Result = RouteVerifier.Assess(["codex.exe"],
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase),
            RouteScriptBuilder.DirectStaticExitName);

        var result = await fixture.ApplyAsync();

        Assert.False(result.RecoveryAttempted);
        Assert.False(result.RecoverySucceeded);
        Assert.Equal(1, fixture.Reloader.RestartCalls);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public async Task WrongRoute_StillFailsAndRecovers()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.RouteVerifier.Result = new(false, FailureCode.ApplicationRouteMismatch,
            "fixture route mismatch", []);
        var original = await File.ReadAllTextAsync(fixture.ScriptPath);

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.RecoverySucceeded);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.ScriptPath));
        Assert.Equal("baseline", fixture.Pipe.Generation);
        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.Empty(Directory.Exists(fixture.BackupRoot)
            ? Directory.EnumerateFileSystemEntries(fixture.BackupRoot)
            : []);
    }

    [Fact]
    public async Task PreWriteFailure_LeavesZeroWritesAndNoMarkerResidue()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Writer.FailBeforeFirstWrite = true;

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.WriteFailed, result.FailureCode);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.Reloader.RestartCalls);
        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.Empty(Directory.Exists(fixture.BackupRoot)
            ? Directory.EnumerateFileSystemEntries(fixture.BackupRoot)
            : []);
    }

    [Fact]
    public async Task SameConfig_ReturnsNoChangesWithoutBackupWriteOrRestart()
    {
        using var fixture = await ApplyFixture.CreateAsync(existingManagedScript: true);

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.True(result.NoChangesRequired);
        Assert.False(result.FilesModified);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.Reloader.RestartCalls);
        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.True(!Directory.Exists(fixture.BackupRoot) ||
                    !Directory.EnumerateFileSystemEntries(fixture.BackupRoot).Any());
    }

    // NoChanges 是需要重新证明的结论：目标指纹与当前 Runtime 受管语义缺一不可。
    [Fact]
    public async Task SameScriptButRuntimeMissing_DoesNotClaimNoChanges()
    {
        using var fixture = await ApplyFixture.CreateAsync(
            existingManagedScript: true, runtimeManaged: false);

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.PostWriteVerificationFailed, result.FailureCode);
        Assert.False(result.NoChangesRequired);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.Reloader.RestartCalls);
    }

    [Fact]
    public async Task NoChanges_RechecksProfileAndScriptFingerprints()
    {
        using var fixture = await ApplyFixture.CreateAsync(existingManagedScript: true);
        fixture.StaticExit.OnTest = () => File.AppendAllText(fixture.ProfilesPath, "\n# concurrent change\n");

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.TargetChanged, result.FailureCode);
        Assert.False(result.NoChangesRequired);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.Reloader.RestartCalls);
    }

    [Fact]
    public async Task NoChanges_NewScriptTargetCreatedAfterSnapshot_IsRejected()
    {
        using var fixture = await ApplyFixture.CreateAsync(
            existingManagedScript: true, scriptInitiallyMissing: true);
        fixture.StaticExit.OnTest = fixture.WriteExpectedScript;

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.TargetChanged, result.FailureCode);
        Assert.False(result.NoChangesRequired);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.Reloader.RestartCalls);
    }

    [Fact]
    public async Task NoChanges_RuntimeSelectionMismatch_IsRejected()
    {
        using var fixture = await ApplyFixture.CreateAsync(existingManagedScript: true);
        fixture.Pipe.OnConfigRequest = request =>
        {
            if (request == 4) fixture.Pipe.Selection = RouteScriptBuilder.DialerStaticExitName;
        };

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.PostWriteVerificationFailed, result.FailureCode);
        Assert.False(result.NoChangesRequired);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.Equal(0, fixture.Reloader.RestartCalls);
    }

    [Fact]
    public async Task RuntimeYamlChangeBeforeTemporaryLoad_BlocksWithoutPut()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.StaticExit.OnTest = () => File.AppendAllText(fixture.RuntimePath, "\n# concurrent change\n");

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.TemporaryRuntimeLoadFailed, result.FailureCode);
        Assert.Equal(0, fixture.Pipe.PutCalls);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
    }

    [Fact]
    public async Task RuntimeYamlChangeWhileCapturingBaseline_BlocksWithoutPut()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Pipe.OnConfigRequest = request =>
        {
            if (request == 1) File.AppendAllText(fixture.RuntimePath, "\n# changed during baseline capture\n");
        };

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.TemporaryRuntimeLoadFailed, result.FailureCode);
        Assert.Equal(1, fixture.Pipe.ConfigRequests);
        Assert.Equal(0, fixture.Pipe.PutCalls);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
    }

    [Fact]
    public async Task TemporaryRestoreProfileParseFailure_ReturnsCriticalResult()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Pipe.OnRestoreOriginal = () => File.WriteAllText(fixture.ProfilesPath, "current: [broken");

        var result = await fixture.ApplyAsync();

        Assert.Equal(FailureCode.RecoveryFailed, result.FailureCode);
        Assert.Equal("Recover", result.Stage);
        Assert.True(result.RecoveryAttempted);
        Assert.False(result.RecoverySucceeded);
        Assert.False(result.FilesModified);
        Assert.Equal(0, fixture.Writer.SuccessfulWrites);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    // Marker 是启动恢复的唯一指针；若它删不掉，加密备份必须保留供下次重试。
    [Fact]
    public async Task MarkerDeleteFailure_PreservesBackupWorkspace()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        FileStream? markerLock = null;
        fixture.Writer.FailBeforeFirstWrite = true;
        fixture.Writer.BeforeFailure = () =>
            markerLock = new FileStream(fixture.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var result = await fixture.ApplyAsync();

            Assert.Equal(FailureCode.WriteFailed, result.FailureCode);
            Assert.False(result.FilesModified);
            Assert.False(result.RecoveryAttempted);
            Assert.True(File.Exists(fixture.MarkerPath));
            var backupDirectory = Assert.Single(Directory.EnumerateDirectories(fixture.BackupRoot));
            Assert.True(File.Exists(Path.Combine(backupDirectory, "manifest.json")));
        }
        finally
        {
            if (markerLock is not null) await markerLock.DisposeAsync();
        }
    }

    [Fact]
    public async Task CodexTarget_IsIncludedInOpenAiPresetApply()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        var result = await fixture.ApplyAsync();
        Assert.True(result.Success);
        Assert.Contains("PROCESS-NAME,codex.exe,AI静态链", fixture.Pipe.LastCandidateYaml,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DialerSelected_PersistedScriptUsesDialerFirst()
    {
        using var fixture = await ApplyFixture.CreateAsync(
            StaticTransportMode.DialerProxy, expectedExitIp: string.Empty);
        var result = await fixture.ApplyAsync();
        var script = await File.ReadAllTextAsync(fixture.ScriptPath);
        Assert.True(result.Success);
        Assert.Contains($"\"proxies\":[\"{RouteScriptBuilder.DialerStaticExitName}\",\"{RouteScriptBuilder.DirectStaticExitName}\"]",
            script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControllerSelection_MatchesFinalTransportMode()
    {
        using var fixture = await ApplyFixture.CreateAsync(
            StaticTransportMode.DialerProxy, expectedExitIp: string.Empty);
        var result = await fixture.ApplyAsync();
        Assert.True(result.Success);
        Assert.Equal(RouteScriptBuilder.DialerStaticExitName, fixture.Pipe.Selections.Last().Proxy);
    }

    [Fact]
    public async Task TemporaryRuntimeWithoutDefinitionFields_UsesStableSemantics()
    {
        using var fixture = await ApplyFixture.CreateAsync(runtimeManaged: true);
        var result = await fixture.ApplyAsync();
        Assert.True(result.Success);
        Assert.True(fixture.Pipe.ProxyRequests > 0);
    }

    [Theory]
    [InlineData("server: proxy.example", "server: changed.example")]
    [InlineData("port: 1080", "port: 2080")]
    public async Task PostWrite_ProxyDefinitionMismatch_Recovers(string before, string after)
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Reloader.CandidateTransform = yaml => yaml.Replace(before, after, StringComparison.Ordinal);
        var result = await fixture.ApplyAsync();
        Assert.False(result.Success);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.RecoverySucceeded);
    }

    [Fact]
    public async Task PostWrite_DialerMismatch_Recovers()
    {
        using var fixture = await ApplyFixture.CreateAsync(
            StaticTransportMode.DialerProxy, expectedExitIp: string.Empty);
        fixture.Reloader.CandidateTransform = yaml =>
            yaml.Replace("dialer-proxy: 主策略", "dialer-proxy: 其他策略", StringComparison.Ordinal);
        var result = await fixture.ApplyAsync();
        Assert.False(result.Success);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.RecoverySucceeded);
    }

    [Fact]
    public async Task PostWrite_ExitIpMatches_Passes()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        var result = await fixture.ApplyAsync();
        Assert.True(result.Success);
        Assert.Equal("203.0.113.44", result.ActualExitIp);
        Assert.Equal(1, fixture.LocalExit.Calls);
        Assert.Contains(fixture.Pipe.InlinePayloads,
            yaml => PublicIpDetector.DefaultProviders.All(provider =>
                yaml.Contains($"DOMAIN,{provider.Host},{RouteScriptBuilder.StaticGroupName}", StringComparison.Ordinal)));
        Assert.DoesNotContain("DOMAIN,", fixture.Pipe.InlinePayloads[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostWriteHttpTimeout_IsNotTargetChanged()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.LocalExit.Exception = new TaskCanceledException("fixture HTTP timeout");

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.PostWriteVerificationFailed, result.FailureCode);
        Assert.NotEqual(FailureCode.TargetChanged, result.FailureCode);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.RecoverySucceeded);
        Assert.Contains("应用后的静态出口验证超时", result.SanitizedDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyRealHashChange_ReturnsTargetChanged()
    {
        using var fixture = await ApplyFixture.CreateAsync(existingManagedScript: true);
        fixture.StaticExit.OnTest = () => File.AppendAllText(fixture.ProfilesPath, "\n# real hash change\n");

        var result = await fixture.ApplyAsync();

        Assert.Equal(FailureCode.TargetChanged, result.FailureCode);
        Assert.Contains("SHA-256", result.SanitizedDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FailureCode.ExitIpMismatch, "203.0.113.45")]
    public async Task PostWrite_ExitFailure_Recovers(FailureCode code, string? actualIp)
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.LocalExit.Result = new(false, actualIp, code, "fixture post-write exit failure");
        var result = await fixture.ApplyAsync();
        Assert.False(result.Success);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.RecoverySucceeded);
    }

    [Fact]
    public async Task PostWrite_AllProvidersUnavailable_ReusesPrewriteExitAndContinuesRouteVerification()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.LocalExit.Result = new(
            false, null, FailureCode.ExitIpLookupFailed, "fixture providers unavailable");

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal("203.0.113.44", result.ActualExitIp);
        Assert.False(result.RecoveryAttempted);
        Assert.Contains("查询服务暂时不可用", result.SanitizedDetail, StringComparison.Ordinal);
        Assert.Equal(1, fixture.RouteVerifier.Calls);
    }

    [Fact]
    public async Task Dialer_AllProvidersUnavailable_ReusesExitConfirmedDuringThisPrewrite()
    {
        using var fixture = await ApplyFixture.CreateAsync(
            mode: StaticTransportMode.DialerProxy,
            expectedExitIp: string.Empty);
        fixture.LocalExit.Result = new(
            false, null, FailureCode.ExitIpLookupFailed, "fixture providers unavailable");

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal("203.0.113.44", result.ActualExitIp);
        Assert.Equal(StaticTransportMode.DialerProxy, result.TransportMode);
        Assert.False(result.RecoveryAttempted);
        Assert.Equal(1, fixture.RouteVerifier.Calls);
    }

    [Fact]
    public async Task LegacyV1_UsesSameApplyPipeline()
    {
        using var fixture = await ApplyFixture.CreateAsync(existingManagedScript: true, legacyScriptV1: true);
        var result = await fixture.ApplyAsync();
        Assert.True(result.Success);
        Assert.Contains(RouteScriptBuilder.ManagedVersionHeader,
            await File.ReadAllTextAsync(fixture.ScriptPath), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Reloader.RestartCalls);
    }

    [Fact]
    public async Task ShutdownOverwrite_IsPatchedAfterStop_AndPreservesLatestFields()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Reloader.AfterProcessesStopped = () => File.WriteAllText(fixture.ProfilesPath, """
            current: current
            shutdown-field: preserved
            items:
              - uid: current
                type: remote
                name: 主策略
                file: current.yaml
                option: {}
            """);

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        var profilesText = await File.ReadAllTextAsync(fixture.ProfilesPath);
        Assert.Contains("shutdown-field: preserved", profilesText, StringComparison.Ordinal);
        var profiles = new YamlDotNet.Serialization.DeserializerBuilder()
            .IgnoreUnmatchedProperties().Build().Deserialize<ProfilesDocument>(profilesText);
        Assert.Equal(ApplyFixture.ScriptUid, profiles.Items.Single(item => item.Uid == "current").Option?.Script);
        Assert.Single(profiles.Items, item => item.Uid == ApplyFixture.ScriptUid);
        Assert.Equal(2, fixture.Writer.SuccessfulWrites);
    }

    [Fact]
    public async Task ShutdownOverwrite_FinalPatch_IsCoveredByRecoveryManifest()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        var originalProfiles = await File.ReadAllTextAsync(fixture.ProfilesPath);
        fixture.Reloader.AfterProcessesStopped = () => File.WriteAllText(fixture.ProfilesPath, """
            current: current
            shutdown-field: transient
            items:
              - uid: current
                type: remote
                name: 主策略
                file: current.yaml
                option: {}
            """);
        fixture.RouteVerifier.Result = new(
            false, FailureCode.ApplicationRouteMismatch, "fixture wrong route", []);

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.RecoverySucceeded);
        Assert.Equal(originalProfiles, await File.ReadAllTextAsync(fixture.ProfilesPath));
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public async Task ShutdownTargetChanged_RestoresOnlyAiwsWrite_AndPreservesLatestProfiles()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        var originalScript = await File.ReadAllTextAsync(fixture.ScriptPath);
        fixture.Reloader.AfterProcessesStopped = () => File.WriteAllText(fixture.ProfilesPath, """
            current: external-current
            shutdown-field: external-latest
            items:
              - uid: external-current
                type: remote
                name: External Profile
                file: external.yaml
                option: {}
            """);

        var result = await fixture.ApplyAsync();

        Assert.False(result.Success);
        Assert.Equal(FailureCode.TargetChanged, result.FailureCode);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.RecoverySucceeded);
        Assert.Equal(originalScript, await File.ReadAllTextAsync(fixture.ScriptPath));
        var latestProfiles = await File.ReadAllTextAsync(fixture.ProfilesPath);
        Assert.Contains("current: external-current", latestProfiles, StringComparison.Ordinal);
        Assert.Contains("shutdown-field: external-latest", latestProfiles, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public async Task DirectMode_SlowRuntimeStartup_ConvergesAndKeepsExpectedExit()
    {
        using var fixture = await ApplyFixture.CreateAsync(mode: StaticTransportMode.Direct);
        fixture.Pipe.ReloadActivationDelaySnapshots = 2;

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal(StaticTransportMode.Direct, result.TransportMode);
        Assert.Equal("203.0.113.44", result.ActualExitIp);
        Assert.Contains(fixture.Pipe.Selections, selection =>
            selection.Proxy == RouteScriptBuilder.DirectStaticExitName);
    }

    [Fact]
    public async Task DialerMode_SlowRuntimeStartup_ConvergesAndKeepsChainSelection()
    {
        using var fixture = await ApplyFixture.CreateAsync(mode: StaticTransportMode.DialerProxy);
        fixture.Pipe.ReloadActivationDelaySnapshots = 2;

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.Equal(StaticTransportMode.DialerProxy, result.TransportMode);
        Assert.Equal("203.0.113.44", result.ActualExitIp);
        Assert.Contains(fixture.Pipe.Selections, selection =>
            selection.Proxy == RouteScriptBuilder.DialerStaticExitName);
    }

    [Fact]
    public async Task PostReload_Transient503And504_DoNotTriggerRecovery()
    {
        using var fixture = await ApplyFixture.CreateAsync();
        fixture.Reloader.AfterCandidateActivated = () =>
        {
            fixture.Pipe.ConfigFailures.Enqueue(new MihomoControllerException(503, "fixture 503"));
            fixture.Pipe.ConfigFailures.Enqueue(new MihomoControllerException(504, "fixture 504"));
        };

        var result = await fixture.ApplyAsync();

        Assert.True(result.Success);
        Assert.False(result.RecoveryAttempted);
        Assert.Equal(1, fixture.Reloader.RestartCalls);
        Assert.Empty(fixture.Pipe.ConfigFailures);
    }

    private sealed class ApplyFixture : IDisposable
    {
        internal const string ScriptUid = "sManaged00001";
        private readonly TempDirectory _temp;
        private readonly ApplyContext _context;
        private readonly ApplyEngine _engine;

        private ApplyFixture(
            TempDirectory temp,
            ApplyContext context,
            ApplyEngine engine,
            FakeApplyRuntimeClient pipe,
            StubStaticExitTester staticExit,
            StubLocalExitTester localExit,
            CountingWriter writer,
            SequencedReloadService reloader,
            StubRouteVerifier routeVerifier,
            string scriptPath,
            string profilesPath,
            string runtimePath,
            string markerPath,
            string backupRoot,
            List<string> events)
        {
            _temp = temp;
            _context = context;
            _engine = engine;
            Pipe = pipe;
            StaticExit = staticExit;
            LocalExit = localExit;
            Writer = writer;
            Reloader = reloader;
            RouteVerifier = routeVerifier;
            ScriptPath = scriptPath;
            ProfilesPath = profilesPath;
            RuntimePath = runtimePath;
            MarkerPath = markerPath;
            BackupRoot = backupRoot;
            Events = events;
        }

        public FakeApplyRuntimeClient Pipe { get; }
        public StubStaticExitTester StaticExit { get; }
        public StubLocalExitTester LocalExit { get; }
        public CountingWriter Writer { get; }
        public SequencedReloadService Reloader { get; }
        public StubRouteVerifier RouteVerifier { get; }
        public string ScriptPath { get; }
        public string ProfilesPath { get; }
        public string RuntimePath { get; }
        public string MarkerPath { get; }
        public string BackupRoot { get; }
        public List<string> Events { get; }

        public Task<ApplyResult> ApplyAsync() => _engine.ApplyAsync(_context);
        public void WriteExpectedScript() =>
            File.WriteAllText(ScriptPath, new RouteScriptBuilder().Build(_context.Route));

        public static async Task<ApplyFixture> CreateAsync(
            StaticTransportMode mode = StaticTransportMode.Direct,
            string expectedExitIp = "203.0.113.44",
            bool existingManagedScript = false,
            bool? runtimeManaged = null,
            bool scriptInitiallyMissing = false,
            bool legacyScriptV1 = false,
            StaticTransportPreference transportPreference = StaticTransportPreference.Auto)
        {
            var temp = new TempDirectory();
            var profilesDirectory = temp.File("profiles");
            Directory.CreateDirectory(profilesDirectory);
            var profilesPath = temp.File("profiles.yaml");
            var subscriptionPath = temp.File("profiles/current.yaml");
            var scriptPath = temp.File($"profiles/{ScriptUid}.js");
            var runtimePath = temp.File("clash-verge.yaml");
            var markerPath = temp.File("transaction.json");
            var backupRoot = temp.File("backups");

            await File.WriteAllTextAsync(profilesPath, $$"""
                current: current
                items:
                  - uid: current
                    type: remote
                    name: 主策略
                    file: current.yaml
                    option:
                      script: {{ScriptUid}}
                  - uid: {{ScriptUid}}
                    type: script
                    name: AI WorkStation
                    file: {{ScriptUid}}.js
                """);
            const string baselineYaml = """
                external-controller-pipe: '\\.\pipe\aiws-apply-test'
                mixed-port: 7890
                mode: rule
                proxies:
                  - name: 原节点
                    type: ss
                    server: 198.51.100.2
                    port: 443
                proxy-groups:
                  - name: 主策略
                    type: select
                    proxies:
                      - 原节点
                rules:
                  - MATCH,DIRECT
                """;
            await File.WriteAllTextAsync(subscriptionPath, baselineYaml);
            await File.WriteAllTextAsync(runtimePath, baselineYaml);

            var target = new ApplicationTarget("Codex", "codex.exe", @"C:\Apps\codex.exe", true, "fixture");
            var proxy = new StaticExitConfig
            {
                Protocol = StaticProxyProtocol.Socks5,
                Server = "proxy.example",
                Port = 1080,
                Username = "fixture-user",
                Password = "fixture-password"
            };
            var route = new RouteConfiguration([target], proxy, expectedExitIp, "主策略")
            {
                TransportMode = mode,
                TransportPreference = transportPreference,
                DialerProxyGroup = "主策略"
            };
            var builder = new RouteScriptBuilder();
            if (!scriptInitiallyMissing)
            {
                var initialScript = existingManagedScript
                    ? builder.Build(route)
                    : "function main(config, profileName) { return config; }";
                if (legacyScriptV1)
                    initialScript = initialScript.Replace(RouteScriptBuilder.ManagedVersionHeader,
                        RouteScriptBuilder.LegacyManagedVersionHeader, StringComparison.Ordinal);
                await File.WriteAllTextAsync(scriptPath, initialScript);
            }
            if (runtimeManaged ?? existingManagedScript)
                await File.WriteAllTextAsync(runtimePath, builder.BuildRuntimeCandidate(baselineYaml, route));

            var clash = new ClashInfo(
                new(1, null, temp.File("clash-verge.exe"), "2.5.2"),
                new(2, null, temp.File("verge-mihomo.exe"), "1"),
                temp.Path, profilesPath, runtimePath, profilesDirectory,
                @"\\.\pipe\aiws-apply-test", "rule", false, false, false,
                [new ProxySelection("主策略", "原节点") { Members = ["原节点"] }])
            {
                MixedPort = 7890,
                StoreSelected = true
            };
            var ownership = existingManagedScript && !scriptInitiallyMissing
                ? ExtensionOwnership.AIWorkStationManaged
                : ExtensionOwnership.NoneOrEmpty;
            var subscription = new SubscriptionInfo(
                "current", "主策略", "current.yaml", subscriptionPath,
                FileHash.Sha256(profilesPath), [], ownership,
                ScriptUid, scriptPath, File.Exists(scriptPath) ? FileHash.Sha256(scriptPath) : null);
            var machine = new MachineInfo("Windows 11", "11", "26100", "x64", "UTC", "UTC", TimeSpan.Zero, true);
            var environment = new EnvironmentSnapshot(
                EnvironmentSupport.Supported, "fixture", machine, clash, subscription, "198.51.100.1");
            var context = new ApplyContext(environment, route,
                new(profilesPath, FileHash.Sha256(profilesPath)),
                File.Exists(scriptPath) ? new(scriptPath, FileHash.Sha256(scriptPath)) : null);

            var events = new List<string>();
            var runtimeBaselineYaml = await File.ReadAllTextAsync(runtimePath);
            var pipe = new FakeApplyRuntimeClient(runtimeBaselineYaml, runtimeManaged ?? existingManagedScript, events);
            var staticExit = new StubStaticExitTester
            {
                Result = new(true, "203.0.113.44", FailureCode.None, "3/3 stable")
            };
            var localExit = new StubLocalExitTester
            {
                Result = new(true, "203.0.113.44", FailureCode.None, null)
            };
            var writer = new CountingWriter(events);
            var routeVerifier = new StubRouteVerifier
            {
                Result = new(true, FailureCode.None, "fixture verified", [])
            };
            var reloader = new SequencedReloadService(runtimePath, runtimeBaselineYaml, pipe);
            var markers = new TransactionMarkerService(markerPath);
            var convergence = new MihomoRuntimeConvergence(
                TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(5));
            var recovery = new RecoveryService(writer, reloader, markers, _ => pipe, convergence);
            var engine = new ApplyEngine(
                mihomoValidator: new AlwaysValidMihomoValidator(temp.File("validation")),
                backupService: new BackupService(backupRoot),
                writer: writer,
                reloader: reloader,
                recovery: recovery,
                markers: markers,
                routeVerifier: routeVerifier,
                staticExitTester: staticExit,
                localExitTester: localExit,
                pipeFactory: _ => pipe,
                environmentCheck: (_, _, _) => { },
                runtimeConvergence: convergence);
            return new ApplyFixture(temp, context, engine, pipe, staticExit, localExit,
                writer, reloader, routeVerifier, scriptPath, profilesPath, runtimePath,
                markerPath, backupRoot, events);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class AlwaysValidMihomoValidator(string directory) : MihomoValidator(directory)
    {
        public override Task<MihomoValidationResult> ValidateDeltaAsync(
            string mihomoPath,
            string dataDirectory,
            string effectiveBaselineYaml,
            string runtimeCandidateYaml,
            StaticExitConfig sensitiveConfig,
            IEnumerable<string> managedIdentifiers,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MihomoValidationResult(true, 0, string.Empty));
    }

    private sealed class StubStaticExitTester : StaticExitTester
    {
        public StaticExitTestResult Result { get; set; } = new(true, "203.0.113.44", FailureCode.None, null);
        public int Calls { get; private set; }
        public Action? OnTest { get; set; }

        public override Task<StaticExitTestResult> TestAsync(
            StaticExitConfig config,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            OnTest?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class StubLocalExitTester : MihomoLocalProxyExitTester
    {
        public StaticExitTestResult Result { get; set; } = new(true, "203.0.113.44", FailureCode.None, null);
        public Exception? Exception { get; set; }
        public int Calls { get; private set; }

        public override Task<StaticExitTestResult> TestAsync(
            int? mixedPort,
            int? httpPort,
            int? socksPort,
            string? expectedExitIp,
            CancellationToken token = default)
        {
            Calls++;
            if (Exception is not null) return Task.FromException<StaticExitTestResult>(Exception);
            return Task.FromResult(Result);
        }
    }

    private sealed class CountingWriter(List<string> events) : AtomicFileWriter
    {
        public bool FailBeforeFirstWrite { get; set; }
        public Action? BeforeFailure { get; set; }
        public int SuccessfulWrites { get; private set; }

        public override async Task WriteAsync(
            string targetPath,
            byte[] content,
            CancellationToken cancellationToken = default)
        {
            if (FailBeforeFirstWrite && SuccessfulWrites == 0)
            {
                BeforeFailure?.Invoke();
                throw new IOException("fixture write failure");
            }
            await base.WriteAsync(targetPath, content, cancellationToken);
            SuccessfulWrites++;
            events.Add("persistent-write");
        }
    }

    private sealed class SequencedReloadService(
        string runtimePath,
        string baselineYaml,
        FakeApplyRuntimeClient pipe) : ClashReloadService
    {
        public int RestartCalls { get; private set; }
        public Func<string, string>? CandidateTransform { get; set; }
        public Action? AfterProcessesStopped { get; set; }
        public Action? AfterCandidateActivated { get; set; }

        public override async Task<bool> RestartAsync(
            string clashExecutable,
            string runtimeConfigPath,
            CancellationToken token = default)
        {
            RestartCalls++;
            if (RestartCalls == 1)
            {
                var runtime = CandidateTransform?.Invoke(pipe.LastCandidateYaml) ?? pipe.LastCandidateYaml;
                await File.WriteAllTextAsync(runtimePath, runtime, token);
                pipe.ActivateCandidate();
            }
            else
            {
                await File.WriteAllTextAsync(runtimePath, baselineYaml, token);
                pipe.RestoreBaseline();
            }
            return true;
        }

        public override async Task<bool> RestartAsync(
            string clashExecutable,
            string runtimeConfigPath,
            Func<CancellationToken, Task> afterProcessesStopped,
            CancellationToken token = default)
        {
            RestartCalls++;
            AfterProcessesStopped?.Invoke();
            await afterProcessesStopped(token);
            var runtime = CandidateTransform?.Invoke(pipe.LastCandidateYaml) ?? pipe.LastCandidateYaml;
            await File.WriteAllTextAsync(runtimePath, runtime, token);
            pipe.ActivateCandidate();
            AfterCandidateActivated?.Invoke();
            return true;
        }
    }

    private sealed class StubRouteVerifier : RouteVerifier
    {
        public RouteVerifyResult Result { get; set; } = new(true, FailureCode.None, string.Empty, []);
        public int Calls { get; private set; }

        public override Task<RouteVerifyResult> VerifyAsync(
            IMihomoApplyClient client,
            IReadOnlyList<ApplicationTarget> targets,
            TimeSpan? waitTimeout = null,
            string? selectedExit = null,
            IProgress<string>? progress = null,
            CancellationToken token = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeApplyRuntimeClient : IMihomoApplyClient
    {
        private readonly string _originalYaml;
        private readonly List<string> _events;
        private bool _managed;
        private readonly bool _baselineManaged;
        private readonly string _baselineSelection;
        private int _unmanagedSnapshotsRemaining;

        public FakeApplyRuntimeClient(string originalYaml, bool baselineManaged, List<string> events)
        {
            _originalYaml = originalYaml;
            _events = events;
            _managed = baselineManaged;
            _baselineManaged = baselineManaged;
            _baselineSelection = baselineManaged ? RouteScriptBuilder.DirectStaticExitName : "原节点";
            LastCandidateYaml = originalYaml;
            Selection = _baselineSelection;
        }

        public bool RestoreMismatch { get; set; }
        public Action? OnRestoreOriginal { get; set; }
        public Action<int>? OnConfigRequest { get; set; }
        public int PutCalls { get; private set; }
        public int ConfigRequests { get; private set; }
        public int ProxyRequests { get; private set; }
        public int RuleRequests { get; private set; }
        public string Generation => _managed ? "managed" : "baseline";
        public string? LastRestoredGeneration { get; private set; }
        public string LastCandidateYaml { get; private set; }
        public List<string> InlinePayloads { get; } = [];
        public List<(string Group, string Proxy)> Selections { get; } = [];
        public string Selection { get; set; }
        public int ReloadActivationDelaySnapshots { get; set; }
        public Queue<Exception> ConfigFailures { get; } = new();

        public Task PutInlineConfigAsync(string yamlPayload, CancellationToken token = default)
        {
            PutCalls++;
            InlinePayloads.Add(yamlPayload);
            if (string.Equals(yamlPayload, _originalYaml, StringComparison.Ordinal))
            {
                OnRestoreOriginal?.Invoke();
                if (!RestoreMismatch)
                {
                    _managed = _baselineManaged;
                    Selection = _baselineSelection;
                }
                LastRestoredGeneration = Generation;
            }
            else
            {
                LastCandidateYaml = yamlPayload;
                _managed = true;
                _events.Add("runtime-candidate");
            }
            return Task.CompletedTask;
        }

        public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken token = default)
        {
            Selections.Add((groupName, proxyName));
            Selection = proxyName;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProxySelection>> GetProxySelectionsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ProxySelection>>(_managed
                ? [new ProxySelection(RouteScriptBuilder.StaticGroupName, Selection)]
                : [new ProxySelection("主策略", "原节点")]);

        public Task<IReadOnlyList<RouteObservation>> GetRouteObservationsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<RouteObservation>>([]);

        public Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default)
            => Task.FromResult(32);

        public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default)
        {
            ConfigRequests++;
            OnConfigRequest?.Invoke(ConfigRequests);
            if (ConfigFailures.TryDequeue(out var failure))
                return Task.FromException<JsonDocument>(failure);
            return Document(new { mode = "rule", generation = Generation });
        }

        public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default)
        {
            ProxyRequests++;
            var proxies = new Dictionary<string, object?>
            {
                ["主策略"] = new { type = "Selector", now = "原节点", all = new[] { "原节点" } }
            };
            if (_managed)
            {
                proxies[RouteScriptBuilder.DirectStaticExitName] = new
                {
                    type = "Socks5"
                };
                proxies[RouteScriptBuilder.DialerStaticExitName] = new
                {
                    type = "Socks5"
                };
                proxies[RouteScriptBuilder.StaticGroupName] = new
                {
                    type = "Selector",
                    now = Selection,
                    all = Selection == RouteScriptBuilder.DialerStaticExitName
                        ? new[] { RouteScriptBuilder.DialerStaticExitName, RouteScriptBuilder.DirectStaticExitName }
                        : new[] { RouteScriptBuilder.DirectStaticExitName, RouteScriptBuilder.DialerStaticExitName }
                };
            }
            return Document(new { proxies });
        }

        public Task<JsonDocument> GetRulesAsync(CancellationToken token = default)
        {
            RuleRequests++;
            object[] rules = _managed
                ? [new { type = "ProcessName", payload = "codex.exe", proxy = RouteScriptBuilder.StaticGroupName }]
                : [new { type = "Match", payload = "", proxy = "DIRECT" }];
            var result = Document(new { rules });
            if (_unmanagedSnapshotsRemaining > 0 && --_unmanagedSnapshotsRemaining == 0)
                _managed = true;
            return result;
        }

        public void ActivateCandidate()
        {
            _unmanagedSnapshotsRemaining = ReloadActivationDelaySnapshots;
            _managed = _unmanagedSnapshotsRemaining == 0;
        }

        public void RestoreBaseline()
        {
            _managed = _baselineManaged;
            Selection = _baselineSelection;
        }

        private static Task<JsonDocument> Document(object value)
            => Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(value)));
    }
}
