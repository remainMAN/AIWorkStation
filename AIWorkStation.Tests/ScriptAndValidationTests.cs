using AIWorkStation.Models;
using AIWorkStation.Services;
using YamlDotNet.RepresentationModel;

namespace AIWorkStation.Tests;

public sealed class ScriptAndValidationTests
{
    private static readonly ApplicationTarget[] Targets =
    [
        new("ChatGPT", "ChatGPT.exe", @"C:\Apps\ChatGPT.exe", true, "test"),
        new("Codex", "codex.exe", @"C:\Apps\codex.exe", true, "test")
    ];

    [Fact]
    public void BuildsSingleStaticExit()
    {
        var candidate = Execute();
        Assert.Equal(1, Count(candidate, "name: AI静态出口"));
    }

    [Fact]
    public void BuildsSingleStaticGroup()
    {
        var candidate = Execute();
        Assert.Equal(1, Count(candidate, "name: AI静态链"));
    }

    [Fact]
    public void BuildsRulesForSelectedApps()
    {
        var candidate = Execute();
        Assert.Contains("PROCESS-NAME,ChatGPT.exe,AI静态链", candidate);
        Assert.Contains("PROCESS-NAME,codex.exe,AI静态链", candidate);
        Assert.True(candidate.IndexOf("PROCESS-NAME", StringComparison.Ordinal) < candidate.IndexOf("MATCH,DIRECT", StringComparison.Ordinal));
    }

    [Fact]
    public void SameInputProducesSameScript()
    {
        var builder = new RouteScriptBuilder();
        Assert.Equal(builder.Build(Route()), builder.Build(Route()));
    }

    [Fact]
    public void DoesNotDuplicateRules()
    {
        var builder = new RouteScriptBuilder();
        var validator = new ScriptValidator();
        var script = builder.Build(Route());
        var once = validator.Execute(script, BaseYaml, "FlyintPro");
        var twice = validator.Execute(script, once, "FlyintPro");
        Assert.Equal(1, Count(twice, "PROCESS-NAME,codex.exe,AI静态链"));
    }

    [Fact]
    public void InvalidScriptBlocksWrite()
    {
        var validator = new ScriptValidator();
        Assert.ThrowsAny<Exception>(() => validator.Execute("function main( {", BaseYaml, "test"));
    }

    [Fact]
    public async Task MihomoValidationFailureBlocksWrite()
    {
        using var temp = new TempDirectory();
        var config = Route().StaticExit;
        var result = await new MihomoValidator().ValidateAsync(temp.File("missing-mihomo.exe"), temp.Path, BaseYaml, config);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task TemporaryRuntimeFailureBlocksWrite()
    {
        using var temp = new TempDirectory();
        var target = temp.File("profiles.yaml");
        await File.WriteAllTextAsync(target, "unchanged");
        await Assert.ThrowsAnyAsync<Exception>(() => new MihomoNamedPipeClient(@"\\.\pipe\aiws-missing-pipe", TimeSpan.FromMilliseconds(100)).PutInlineConfigAsync(BaseYaml));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public void InvalidUnusedSubscriptionNode_DoesNotBlockAiwsApply()
    {
        var baseline = new MihomoValidationResult(false, 1,
            "time=\"2026-08-20T07:00:00-07:00\" level=error msg=\"proxy 54: invalid REALITY short ID\"");
        var candidate = new MihomoValidationResult(false, 1,
            "time=\"2026-08-20T07:00:01-07:00\" level=error msg=\"proxy 55: invalid REALITY short ID\"");
        var result = MihomoValidator.AssessDelta(baseline, candidate,
            [RouteScriptBuilder.StaticExitName, RouteScriptBuilder.StaticGroupName, "codex.exe"]);
        Assert.True(result.Success);
        Assert.True(result.BaselineIssueIgnored);
    }

    [Fact]
    public void TimeoutUnusedNode_DoesNotBlockAiwsApply()
    {
        var baseline = new MihomoValidationResult(false, 1, "proxy 18: timeout after 5200ms while checking provider");
        var candidate = new MihomoValidationResult(false, 1, "proxy 19: timeout after 5.4s while checking provider");
        var result = MihomoValidator.AssessDelta(baseline, candidate, [RouteScriptBuilder.StaticExitName]);
        Assert.True(result.Success);
        Assert.Contains("不会使用", result.SanitizedDetail);
    }

    [Fact]
    public void RuntimeCandidateNewError_BlocksApply()
    {
        var baseline = new MihomoValidationResult(false, 1, "proxy 18: timeout while checking provider");
        var candidate = new MihomoValidationResult(false, 1,
            "proxy 19: timeout while checking provider\nrule parse error: invalid rule syntax");
        var result = MihomoValidator.AssessDelta(baseline, candidate, [RouteScriptBuilder.StaticExitName]);
        Assert.False(result.Success);
        Assert.False(result.BaselineIssueIgnored);
    }

    [Fact]
    public void ManagedStaticExitInvalid_BlocksBeforeWrite()
    {
        var invalid = new StaticExitConfig { Protocol = StaticProxyProtocol.Socks5, Server = "proxy.example", Port = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);
    }

    [Fact]
    public void ManagedRouteInvalid_BlocksBeforeWrite()
    {
        const string invalidCandidate = """
proxies: []
proxy-groups:
  - name: AI静态链
    type: select
    proxies:
      - AI静态出口
rules:
  - PROCESS-NAME,codex.exe,AI静态链
""";
        Assert.Throws<InvalidDataException>(() => new ScriptValidator().ValidateSemantics(invalidCandidate, [Targets[1]]));
    }

    [Fact]
    public void RuntimeCandidate_UsesCurrentEffectiveConfigAsBaseline()
    {
        const string rawSubscription = """
proxies:
  - name: 损坏但未使用
    type: vless
    server: invalid-subscription.example
    short-id: broken
proxy-groups: []
rules: []
""";
        const string effectiveRuntime = """
proxies:
  - name: 当前有效节点
    type: ss
    server: effective-runtime.example
    port: 443
proxy-groups: []
rules:
  - MATCH,DIRECT
""";
        var validator = new ScriptValidator();
        var builder = new RouteScriptBuilder();
        var route = Route();
        var candidates = ApplyEngine.BuildValidationCandidates(
            validator, builder, builder.Build(route), route,
            rawSubscription, effectiveRuntime, "FlyintPro");
        Assert.Contains("invalid-subscription.example", candidates.PersistenceCandidate);
        Assert.DoesNotContain("invalid-subscription.example", candidates.RuntimeCandidate);
        Assert.Contains("effective-runtime.example", candidates.RuntimeCandidate);
    }

    [Fact]
    public void UnusedSubscriptionNodes_ArePreservedUnchanged()
    {
        const string effectiveRuntime = """
proxies:
  - name: 原节点
    type: vless
    server: 198.51.100.2
    port: 443
    reality-opts:
      public-key: public-key-value
      short-id: 0000000000000001
proxy-groups:
  - name: 节点选择
    type: select
    proxies:
      - 原节点
rules:
  - MATCH,DIRECT
""";
        var candidate = new RouteScriptBuilder().BuildRuntimeCandidate(effectiveRuntime, Route());
        var original = FindProxy(candidate, "原节点");
        Assert.Equal("198.51.100.2", Scalar(original, "server"));
        Assert.Equal("vless", Scalar(original, "type"));
        var reality = Assert.IsType<YamlMappingNode>(original.Children[new YamlScalarNode("reality-opts")]);
        Assert.Equal("0000000000000001", Scalar(reality, "short-id"));
        Assert.Equal(2, Proxies(candidate).Children.Count);
    }

    private static string Execute()
    {
        var script = new RouteScriptBuilder().Build(Route());
        var candidate = new ScriptValidator().Execute(script, BaseYaml, "FlyintPro");
        new ScriptValidator().ValidateSemantics(candidate, Targets);
        return candidate;
    }

    private static RouteConfiguration Route() => new(Targets, new StaticExitConfig
    {
        Protocol = StaticProxyProtocol.Socks5, Server = "proxy.example", Port = 1080, Username = "user", Password = "pass"
    }, "203.0.113.1", "FlyintPro");

    private static int Count(string value, string token) => value.Split(token, StringSplitOptions.None).Length - 1;

    private static YamlSequenceNode Proxies(string yamlText)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(yamlText));
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        return Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode("proxies")]);
    }

    private static YamlMappingNode FindProxy(string yamlText, string name) => Proxies(yamlText).Children
        .OfType<YamlMappingNode>()
        .Single(proxy => Scalar(proxy, "name") == name);

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlScalarNode>(mapping.Children[new YamlScalarNode(key)]).Value;

    private const string BaseYaml = """
proxies:
  - name: 原节点
    type: ss
    server: 198.51.100.2
    port: 443
proxy-groups:
  - name: 节点选择
    type: select
    proxies:
      - 原节点
rules:
  - MATCH,DIRECT
""";
}
