using AIWorkStation.Models;
using AIWorkStation.Services;
using AIWorkStation.ViewModels;

namespace AIWorkStation.Tests;

public sealed class ThreeRealHostBugHotfixTests
{
    [Fact]
    public async Task ManualDialerSelection_IsNotOverwrittenByDirectSuccess()
    {
        using var temp = new TempDirectory();
        var viewModel = ViewModel(
            new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null), temp);
        await viewModel.InitializeAsync();
        ConfigureProxy(viewModel);
        viewModel.TransportPreference = StaticTransportPreference.DialerProxy;

        await viewModel.TestStaticExitCommand.ExecuteAsync(null);

        Assert.Equal(StaticTransportMode.DialerProxy, viewModel.TransportMode);
        Assert.Equal("连接方式：经 Clash 节点连接", viewModel.ConnectionModeSummary);
        Assert.Equal("前置策略组：FlyintPro", viewModel.FrontGroupSummary);
        Assert.Equal("当前前置节点：Hongkong 016", viewModel.CurrentFrontNodeSummary);
    }

    [Fact]
    public async Task AutoMode_CanChooseDirect()
    {
        using var temp = new TempDirectory();
        var viewModel = ViewModel(
            new StaticExitTestResult(true, "203.0.113.44", FailureCode.None, null), temp);
        await viewModel.InitializeAsync();
        ConfigureProxy(viewModel);

        await viewModel.TestStaticExitCommand.ExecuteAsync(null);

        Assert.Equal(StaticTransportPreference.Auto, viewModel.TransportPreference);
        Assert.Equal(StaticTransportMode.Direct, viewModel.TransportMode);
    }

    [Fact]
    public async Task AutoMode_CanFallbackToDialer()
    {
        using var temp = new TempDirectory();
        var viewModel = ViewModel(
            new StaticExitTestResult(false, null, FailureCode.StaticProxyTimeout, "fixture timeout"), temp);
        await viewModel.InitializeAsync();
        ConfigureProxy(viewModel);

        await viewModel.TestStaticExitCommand.ExecuteAsync(null);

        Assert.Equal(StaticTransportPreference.Auto, viewModel.TransportPreference);
        Assert.Equal(StaticTransportMode.DialerProxy, viewModel.TransportMode);
        Assert.True(viewModel.IsStaticExitReady);
    }

    [Fact]
    public void OpenAiPreset_AlwaysIncludesChatGPTAndCodex()
    {
        var targets = new OpenAIApplicationMatcher().CreatePresetTargets([]);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, target => target.ExecutableName == "ChatGPT.exe");
        Assert.Contains(targets, target => target.ExecutableName == "codex.exe");
    }

    [Fact]
    public void OpenAiPreset_GeneratesBothProcessRules()
    {
        var targets = new OpenAIApplicationMatcher().CreatePresetTargets([]);
        var script = new RouteScriptBuilder().Build(Route(targets));

        Assert.Contains("PROCESS-NAME,ChatGPT.exe,AI静态链", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PROCESS-NAME,codex.exe,AI静态链", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexPresetTarget_DoesNotRequireRunningProcess()
    {
        var codex = Assert.Single(new OpenAIApplicationMatcher().CreatePresetTargets([]),
            target => target.ExecutableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase));

        Assert.False(codex.RunningProcess);
        Assert.Equal(string.Empty, codex.ExecutablePath);
        Assert.Equal("OpenAI 预设", codex.Source);
    }

    private static MainViewModel ViewModel(StaticExitTestResult result, TempDirectory temp)
        => new(
            detectEnvironment: _ => Task.FromResult(Snapshot()),
            testStaticExit: (_, _) => Task.FromResult(result),
            credentialCache: new TemporaryCredentialCache(temp.File("cache.bin")));

    private static void ConfigureProxy(MainViewModel viewModel)
    {
        viewModel.ProxyServer = "proxy.example";
        viewModel.ProxyPort = "1080";
    }

    private static RouteConfiguration Route(IReadOnlyList<ApplicationTarget> targets)
        => new(targets, new StaticExitConfig
        {
            Protocol = StaticProxyProtocol.Socks5,
            Server = "proxy.example",
            Port = 1080
        }, "203.0.113.44", "FlyintPro");

    private static EnvironmentSnapshot Snapshot()
    {
        var clash = new ClashInfo(
            new ProcessInfo(1, null, @"C:\Apps\clash-verge.exe", "2.5.2"),
            new ProcessInfo(2, null, @"C:\Apps\verge-mihomo.exe", "1"),
            @"C:\Fixture", @"C:\Fixture\profiles.yaml", @"C:\Fixture\clash-verge.yaml",
            @"C:\Fixture\profiles", @"\\.\pipe\fixture", "rule", false, false, false,
            [new ProxySelection("FlyintPro", "Hongkong 016") { Members = ["Hongkong 016"] }]);
        var subscription = new SubscriptionInfo(
            "profile", "FlyintPro", "profile.yaml", @"C:\Fixture\profile.yaml", "hash", [],
            ExtensionOwnership.NoneOrEmpty, null, null, null);
        var machine = new MachineInfo(
            "Windows 11", "11", "26100", "x64", "UTC", "UTC", TimeSpan.Zero, true);
        return new EnvironmentSnapshot(
            EnvironmentSupport.Supported, "fixture", machine, clash, subscription, "198.51.100.1");
    }
}
