using System.Net;
using System.Text.Json;
using AIWorkStation.Models;
using AIWorkStation.Services;
using AIWorkStation.ViewModels;

namespace AIWorkStation.Tests;

public sealed class MainViewModelRemediationTests
{
    [Fact]
    public async Task DialerMode_UiShowsDialer()
    {
        var snapshot = Snapshot([]);
        snapshot = WithManagedSelection(snapshot, RouteScriptBuilder.DialerStaticExitName);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));

        await viewModel.InitializeAsync();

        Assert.Equal(StaticTransportMode.DialerProxy, viewModel.TransportMode);
        Assert.Equal("连接方式：经 Clash 节点连接", viewModel.ConnectionModeSummary);
    }

    [Fact]
    public async Task DirectMode_UiShowsDirect()
    {
        var snapshot = WithManagedSelection(Snapshot([]), RouteScriptBuilder.DirectStaticExitName);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));

        await viewModel.InitializeAsync();

        Assert.Equal(StaticTransportMode.Direct, viewModel.TransportMode);
        Assert.Equal("连接方式：直连", viewModel.ConnectionModeSummary);
    }

    [Fact]
    public async Task DialerMode_ShowsFrontGroupAndNode()
    {
        var snapshot = WithManagedSelection(Snapshot([], profileName: "FlyintPro",
            selectionGroup: "FlyintPro", currentNode: "Hongkong 016"),
            RouteScriptBuilder.DialerStaticExitName);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));

        await viewModel.InitializeAsync();

        Assert.Equal("FlyintPro", viewModel.DialerProxyGroup);
        Assert.Equal("前置策略组：FlyintPro", viewModel.FrontGroupSummary);
        Assert.Contains("Hongkong 016", viewModel.CurrentFrontNodeSummary, StringComparison.Ordinal);
    }
    [Fact]
    public async Task EditingProxyAfterSuccess_InvalidatesValidation()
    {
        using var temp = new TempDirectory();
        var viewModel = new MainViewModel(
            testStaticExit: (_, _) => Task.FromResult(
                new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null)),
            credentialCache: new TemporaryCredentialCache(temp.File("cache.bin")));
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
        viewModel.ProxyPassword = "fixture-password";
        await viewModel.TestStaticExitCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsStaticExitReady);

        viewModel.ProxyServer = "changed.example";

        Assert.False(viewModel.IsStaticExitReady);
        Assert.Null(viewModel.ActualExitIp);
        Assert.Equal("尚未验证", viewModel.StaticExitSummary);
    }

    [Fact]
    public async Task EditingProxyDuringValidation_DoesNotRestoreReadiness()
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validationResult = new TaskCompletionSource<StaticExitTestResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainViewModel(
            testStaticExit: (_, _) =>
            {
                entered.TrySetResult();
                return validationResult.Task;
            },
            credentialCache: new TemporaryCredentialCache(temp.File("cache.bin")));
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";

        var validation = viewModel.TestStaticExitCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.ProxyServer = "changed.example";
        validationResult.SetResult(
            new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null));
        await validation;

        Assert.False(viewModel.IsStaticExitReady);
        Assert.Null(viewModel.ActualExitIp);
        Assert.Equal("尚未验证", viewModel.StaticExitSummary);
        Assert.Contains("代理信息已变更", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviousSuccess_DoesNotMarkNewDialerAsVerified()
    {
        using var temp = new TempDirectory();
        var snapshot = Snapshot([]);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            testStaticExit: (_, _) => Task.FromResult(
                new StaticExitTestResult(false, null, FailureCode.StaticProxyTimeout, "fixture timeout")),
            credentialCache: new TemporaryCredentialCache(temp.File("cache.bin")),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        await viewModel.InitializeAsync();
        viewModel.ApplyResult = ApplyResult.Ok("203.0.113.1");
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";

        await viewModel.TestStaticExitCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsStaticExitReady);
        Assert.Equal(StaticTransportMode.DialerProxy, viewModel.TransportMode);
        Assert.DoesNotContain("已验证", viewModel.StaticExitSummary, StringComparison.Ordinal);
        Assert.Contains("将在应用时", viewModel.StaticExitSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModelPassword_IsClearedAfterApply()
    {
        using var temp = new TempDirectory();
        var cache = new TemporaryCredentialCache(temp.File("credential-cache.bin"));
        StaticExitConfig? appliedConfig = null;
        var snapshot = Snapshot([new ProxyNodeInfo("当前节点", "ss", "node.example", [])]);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            testStaticExit: (_, _) => Task.FromResult(
                new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null)),
            apply: (context, _, _) =>
            {
                appliedConfig = context.Route.StaticExit;
                return Task.FromResult(ApplyResult.Ok("203.0.113.44"));
            },
            credentialCache: cache,
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        await viewModel.InitializeAsync();
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
        viewModel.ProxyUsername = "fixture-user";
        viewModel.ProxyPassword = "fixture-password";
        viewModel.SelectedTargets.Add(Target());
        await viewModel.TestStaticExitCommand.ExecuteAsync(null);
        viewModel.CurrentStep = 2;

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("fixture-password", appliedConfig?.Password);
        Assert.Equal(string.Empty, viewModel.ProxyPassword);
        Assert.Equal(3, viewModel.CurrentStep);
    }

    [Fact]
    public async Task ApplyInProgress_DisablesPreviousStep()
    {
        using var temp = new TempDirectory();
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyResult = new TaskCompletionSource<ApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(Snapshot([])),
            testStaticExit: (_, _) => Task.FromResult(
                new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null)),
            apply: (_, _, _) =>
            {
                applyEntered.TrySetResult();
                return applyResult.Task;
            },
            credentialCache: new TemporaryCredentialCache(temp.File("cache.bin")),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        await viewModel.InitializeAsync();
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
        viewModel.SelectedTargets.Add(Target());
        await viewModel.TestStaticExitCommand.ExecuteAsync(null);
        viewModel.CurrentStep = 2;

        var applying = viewModel.ApplyCommand.ExecuteAsync(null);
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsApplying);
        Assert.False(viewModel.PreviousStepCommand.CanExecute(null));
        viewModel.PreviousStepCommand.Execute(null);
        Assert.Equal(2, viewModel.CurrentStep);

        applyResult.SetResult(ApplyResult.Ok("203.0.113.44"));
        await applying;
    }

    [Fact]
    public async Task EnvironmentRefresh_DisablesNextUntilNewSnapshot()
    {
        var initial = Snapshot([], publicIp: "198.51.100.1");
        var refreshed = Snapshot([], publicIp: "203.0.113.200");
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshResult = new TaskCompletionSource<EnvironmentSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detection = 0;
        var viewModel = new MainViewModel(
            detectEnvironment: _ =>
            {
                if (detection++ == 0) return Task.FromResult(initial);
                refreshEntered.TrySetResult();
                return refreshResult.Task;
            },
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        await viewModel.InitializeAsync();
        Assert.True(viewModel.NextStepCommand.CanExecute(null));

        var refresh = viewModel.RecheckCommand.ExecuteAsync(null);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(UiState.Checking, viewModel.State);
        Assert.Null(viewModel.Environment);
        Assert.False(viewModel.NextStepCommand.CanExecute(null));

        refreshResult.SetResult(refreshed);
        await refresh;
        Assert.Same(refreshed, viewModel.Environment);
        Assert.True(viewModel.NextStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task ContinueConfiguration_RefreshesEnvironmentAndReloadsEncryptedCache()
    {
        using var temp = new TempDirectory();
        var cache = new TemporaryCredentialCache(temp.File("credential-cache.bin"));
        await cache.SaveAsync(new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Http,
            Server = "cached.example",
            Port = 8080,
            Username = "cached-user",
            Password = "cached-password"
        });
        var refreshed = Snapshot([], publicIp: "203.0.113.200", currentNode: "新节点");
        var detections = 0;
        var viewModel = new MainViewModel(
            detectEnvironment: _ =>
            {
                detections++;
                return Task.FromResult(refreshed);
            },
            credentialCache: cache,
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        viewModel.CurrentStep = 3;
        viewModel.ProxyPassword = "old-memory-value";
        viewModel.ApplyResult = ApplyResult.Ok("203.0.113.44");
        viewModel.SelectedTargets.Add(Target());

        await viewModel.ReturnToRoutingCommand.ExecuteAsync(null);

        Assert.Equal(1, detections);
        Assert.Same(refreshed, viewModel.Environment);
        Assert.Equal(1, viewModel.CurrentStep);
        Assert.Equal("cached.example", viewModel.ProxyServer);
        Assert.Equal("cached-password", viewModel.ProxyPassword);
        Assert.Equal("203.0.113.200", viewModel.Environment?.CurrentPublicIp);
        Assert.Single(viewModel.SelectedTargets);
    }

    [Fact]
    public async Task NoSafeFrontGroup_ShowsDirectOnlyScopeMessage()
    {
        var snapshot = Snapshot([], profileName: "主策略",
            selectionGroup: RouteScriptBuilder.StaticGroupName,
            currentNode: RouteScriptBuilder.DirectStaticExitName);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));

        await viewModel.InitializeAsync();

        Assert.Null(viewModel.DialerProxyGroup);
        Assert.Equal("当前没有可用于链式连接的 Clash 策略组。", viewModel.FrontGroupSupportMessage);
        Assert.Equal("当前前置节点：未确定", viewModel.CurrentFrontNodeSummary);
    }

    [Fact]
    public async Task MultipleSafeSelectors_AreAvailableForSelection()
    {
        var snapshot = Snapshot([]);
        var selections = new[]
        {
            new ProxySelection("节点选择", "Taiwan 031") { Members = ["Taiwan 031"] },
            new ProxySelection("自动选择", "Hongkong 01") { Members = ["Hongkong 01"] }
        };
        snapshot = snapshot with { Clash = snapshot.Clash! with { ProxySelections = selections } };
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));

        await viewModel.InitializeAsync();
        viewModel.SelectedDialerProxySelection = selections[1];

        Assert.Equal(2, viewModel.DialerProxySelections.Count);
        Assert.Equal("自动选择", viewModel.DialerProxyGroup);
        Assert.Equal("前置策略组：自动选择", viewModel.FrontGroupSummary);
        Assert.Equal("当前前置节点：Hongkong 01", viewModel.CurrentFrontNodeSummary);
    }

    [Fact]
    public async Task CurrentNodeDisplay_ComesFromActualSafeFrontGroupNotFirstSelector()
    {
        var snapshot = Snapshot([], currentNode: "台湾 031", includeUnrelatedFirst: true);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));

        await viewModel.InitializeAsync();

        Assert.Equal("主策略", viewModel.DialerProxyGroup);
        Assert.Equal("前置策略组：主策略", viewModel.FrontGroupSummary);
        Assert.Equal("当前前置节点：台湾 031", viewModel.CurrentFrontNodeSummary);
        Assert.DoesNotContain("无关节点", viewModel.CurrentFrontNodeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedNodeAutoTest_AndTestAllCommandRemainObservational()
    {
        var selected = new ProxyNodeInfo("当前节点", "ss", "node.example", [IPAddress.Parse("198.51.100.2")]);
        var other = new ProxyNodeInfo("其他节点", "ss", "other.example", [IPAddress.Parse("198.51.100.3")]);
        var snapshot = Snapshot([other, selected], currentNode: selected.Name);
        var client = new DelayRuntimeClient();
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(client));

        await viewModel.InitializeAsync();

        Assert.Equal(LatencyStatus.Available, selected.LatencyStatus);
        Assert.Equal(LatencyStatus.NotTested, other.LatencyStatus);
        Assert.Equal(selected, viewModel.Nodes[0]);
        Assert.True(viewModel.TestAllNodesCommand.CanExecute(null));
        await viewModel.TestAllNodesCommand.ExecuteAsync(null);
        Assert.All(viewModel.Nodes, node => Assert.Equal(LatencyStatus.Available, node.LatencyStatus));
        Assert.Equal(EnvironmentSupport.Supported, viewModel.Environment?.Support);
    }

    [Fact]
    public async Task ClearSavedInformationCommand_DeletesCacheAndClearsInputs()
    {
        using var temp = new TempDirectory();
        var cache = new TemporaryCredentialCache(temp.File("credential-cache.bin"));
        await cache.SaveAsync(new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Socks5,
            Server = "cached.example",
            Port = 1080,
            Username = "fixture-user",
            Password = "fixture-password"
        });
        var viewModel = new MainViewModel(
            credentialCache: cache,
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()))
        {
            ProxyServer = "cached.example",
            ProxyPort = "1080",
            ProxyUsername = "fixture-user",
            ProxyPassword = "fixture-password"
        };

        await viewModel.ClearSavedInformationCommand.ExecuteAsync(null);

        Assert.Null(await cache.LoadAsync());
        Assert.Equal(string.Empty, viewModel.ProxyServer);
        Assert.Equal(string.Empty, viewModel.ProxyPort);
        Assert.Equal(string.Empty, viewModel.ProxyUsername);
        Assert.Equal(string.Empty, viewModel.ProxyPassword);
    }

    [Fact]
    public async Task UncheckedSave_DoesNotReloadOldCache()
    {
        using var temp = new TempDirectory();
        var cache = new TemporaryCredentialCache(temp.File("credential-cache.bin"));
        await cache.SaveAsync(new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Socks5,
            Server = "cached.example",
            Port = 1080,
            Password = "fixture-password"
        });
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(Snapshot([])),
            credentialCache: cache,
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()))
        {
            SaveProxyTemporarily = false
        };
        await viewModel.InitializeAsync();

        await viewModel.NextStepCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.ProxyServer);
        Assert.Equal(string.Empty, viewModel.ProxyPassword);
        Assert.Null(await cache.LoadAsync());
    }

    [Fact]
    public async Task OptingOutWhenCacheDeleteFails_ShowsHonestWarning()
    {
        using var temp = new TempDirectory();
        var cache = new TemporaryCredentialCache(temp.File("credential-cache.bin"));
        await cache.SaveAsync(new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Socks5,
            Server = "cached.example",
            Port = 1080,
            Password = "fixture-password"
        });
        var viewModel = new MainViewModel(credentialCache: cache);
        var warning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StatusText) &&
                viewModel.StatusText.Contains("无法删除现有临时缓存", StringComparison.Ordinal))
                warning.TrySetResult();
        };

        await using var cacheLock = new FileStream(
            cache.CachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        viewModel.SaveProxyTemporarily = false;
        await warning.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.SaveProxyTemporarily);
        Assert.True(File.Exists(cache.CachePath));
        Assert.Contains("已停止继续保存", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("清除已保存信息", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderSelectedNode_ShowsTemporarilyUntestable()
    {
        var snapshot = Snapshot([
            new ProxyNodeInfo("内联节点", "ss", "node.example", [])
        ], currentNode: "Provider 节点");
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));

        await viewModel.InitializeAsync();

        Assert.Equal("当前前置选择暂不可测试（可能来自 Provider）。", viewModel.CurrentNodeLatencyNotice);
        Assert.All(viewModel.Nodes, node => Assert.Equal(LatencyStatus.NotTested, node.LatencyStatus));
    }

    [Fact]
    public async Task AutoSelectedLatency_CanBeCancelledOnClose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new ProxyNodeInfo("当前节点", "ss", "node.example", []);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(Snapshot([selected])),
            latencyTesterFactory: _ => new NodeLatencyTester(new BlockingDelayRuntimeClient(entered)));

        var initialize = viewModel.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsTestingLatency);
        viewModel.CancelLatencyTests(detach: true);
        await initialize;

        Assert.False(viewModel.IsTestingLatency);
        Assert.Equal(LatencyStatus.NotTested, selected.LatencyStatus);
    }

    [Fact]
    public async Task ApplyCacheWarning_IsVisibleOnResult()
    {
        using var temp = new TempDirectory();
        var snapshot = Snapshot([]);
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(snapshot),
            testStaticExit: (_, _) => Task.FromResult(
                new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null)),
            apply: (_, _, _) => Task.FromResult(ApplyResult.Ok("203.0.113.44")),
            credentialCache: new TemporaryCredentialCache(temp.Path),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        await viewModel.InitializeAsync();
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
        viewModel.SelectedTargets.Add(Target());
        await viewModel.TestStaticExitCommand.ExecuteAsync(null);
        viewModel.CurrentStep = 2;

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("无法临时保存代理信息，本次仍可继续配置。", viewModel.ResultNotice);
        Assert.Equal(3, viewModel.CurrentStep);
    }

    [Fact]
    public async Task NoTrafficSuccess_ShowsNotObservedMessage()
    {
        using var temp = new TempDirectory();
        var routeResults = new[]
        {
            new ApplicationRouteResult("ChatGPT.exe", ApplicationRouteStatus.NoTrafficObserved, null)
        };
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(Snapshot([])),
            testStaticExit: (_, _) => Task.FromResult(
                new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null)),
            apply: (_, _, _) => Task.FromResult(ApplyResult.Ok(
                "203.0.113.44", detail: "not observed", applicationResults: routeResults)),
            credentialCache: new TemporaryCredentialCache(temp.File("cache.bin")),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        await viewModel.InitializeAsync();
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
        viewModel.SelectedTargets.Add(Target());
        await viewModel.TestStaticExitCommand.ExecuteAsync(null);
        viewModel.CurrentStep = 2;

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.True(viewModel.ApplyResult!.Success);
        Assert.Equal("配置已应用", viewModel.ResultMessage!.TitleZh);
        Assert.Contains("还没有完成实际程序流量验证", viewModel.ResultMessage.MessageZh, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostWriteTimeout_RecoversWithAccurateMessage()
    {
        using var temp = new TempDirectory();
        var viewModel = new MainViewModel(
            detectEnvironment: _ => Task.FromResult(Snapshot([])),
            testStaticExit: (_, _) => Task.FromResult(
                new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null)),
            apply: (_, _, _) => Task.FromResult(ApplyResult.Fail(
                FailureCode.PostWriteVerificationFailed,
                "Recover",
                "应用后的静态出口验证超时。",
                modified: true,
                recoveryAttempted: true,
                recoverySucceeded: true)),
            credentialCache: new TemporaryCredentialCache(temp.File("cache.bin")),
            latencyTesterFactory: _ => new NodeLatencyTester(new DelayRuntimeClient()));
        await viewModel.InitializeAsync();
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
        viewModel.SelectedTargets.Add(Target());
        await viewModel.TestStaticExitCommand.ExecuteAsync(null);
        viewModel.CurrentStep = 2;

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("应用后的静态出口验证超时，原配置已经恢复。", viewModel.ResultMessage!.MessageZh);
        Assert.DoesNotContain("被其他程序修改", viewModel.ResultMessage.MessageZh, StringComparison.Ordinal);
    }

    private static ApplicationTarget Target()
        => new("Codex", "codex.exe", @"C:\Apps\codex.exe", true, "fixture");

    private static EnvironmentSnapshot WithManagedSelection(EnvironmentSnapshot snapshot, string selectedExit)
        => snapshot with
        {
            Clash = snapshot.Clash! with
            {
                ProxySelections = snapshot.Clash.ProxySelections.Concat([
                    new ProxySelection(RouteScriptBuilder.StaticGroupName, selectedExit)
                    {
                        Members = [RouteScriptBuilder.DirectStaticExitName, RouteScriptBuilder.DialerStaticExitName]
                    }
                ]).ToArray()
            }
        };

    private static EnvironmentSnapshot Snapshot(
        IReadOnlyList<ProxyNodeInfo> nodes,
        string publicIp = "198.51.100.1",
        string profileName = "主策略",
        string selectionGroup = "主策略",
        string currentNode = "当前节点",
        bool includeUnrelatedFirst = false)
    {
        var selections = new List<ProxySelection>();
        if (includeUnrelatedFirst)
            selections.Add(new ProxySelection("无关策略", "无关节点") { Members = ["无关节点"] });
        selections.Add(new ProxySelection(selectionGroup, currentNode) { Members = [currentNode] });
        var clash = new ClashInfo(
            new(1, null, @"C:\fixture\clash-verge.exe", "2.5.2"),
            new(2, null, @"C:\fixture\verge-mihomo.exe", "1"),
            @"C:\fixture", @"C:\fixture\profiles.yaml", @"C:\fixture\clash-verge.yaml",
            @"C:\fixture\profiles", @"\\.\pipe\fixture", "rule", false, false, false,
            selections);
        var subscription = new SubscriptionInfo(
            "current", profileName, "current.yaml", @"C:\fixture\profiles\current.yaml",
            "fixture-hash", nodes, ExtensionOwnership.NoneOrEmpty, null, null, null);
        var machine = new MachineInfo(
            "Windows 11", "11", "26100", "x64", "UTC", "UTC", TimeSpan.Zero, true);
        return new(EnvironmentSupport.Supported, "fixture ready", machine, clash, subscription, publicIp);
    }

    private sealed class DelayRuntimeClient : IMihomoRuntimeClient
    {
        public Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default)
            => Task.FromResult(32);

        public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default)
            => Task.FromResult(JsonDocument.Parse("{}"));

        public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default)
            => Task.FromResult(JsonDocument.Parse("{\"proxies\":{}}"));

        public Task<JsonDocument> GetRulesAsync(CancellationToken token = default)
            => Task.FromResult(JsonDocument.Parse("{\"rules\":[]}"));
    }

    private sealed class BlockingDelayRuntimeClient(TaskCompletionSource entered) : IMihomoRuntimeClient
    {
        public async Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 1;
        }

        public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<JsonDocument> GetRulesAsync(CancellationToken token = default) => throw new NotSupportedException();
    }
}
