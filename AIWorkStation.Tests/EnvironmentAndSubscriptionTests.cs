using System.Net;
using AIWorkStation.Models;
using AIWorkStation.Services;
using System.Text.Json;
using YamlDotNet.Core;

namespace AIWorkStation.Tests;

public sealed class EnvironmentAndSubscriptionTests
{
    [Fact]
    public void MalformedProfilesYaml_NoCrash()
        => Assert.Contains("配置格式异常",
            EnvironmentDetector.DescribeConfigReadFailure(new YamlException("fixture malformed profiles")),
            StringComparison.Ordinal);

    [Fact]
    public void MalformedSubscriptionYaml_NoCrash()
        => Assert.Contains("没有进行修改",
            EnvironmentDetector.DescribeConfigReadFailure(new YamlException("fixture malformed subscription")),
            StringComparison.Ordinal);

    [Fact]
    public async Task DuplicateUid_NoCrash()
    {
        using var fixture = CreateFixture();
        var profiles = await File.ReadAllTextAsync(fixture.File("profiles.yaml"));
        profiles += "\n  - uid: Rabcdefghijk\n    type: remote\n    file: duplicate.yaml\n";
        await File.WriteAllTextAsync(fixture.File("profiles.yaml"), profiles);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SubscriptionInspector().InspectAsync(fixture.Path));

        Assert.Contains("重复", EnvironmentDetector.DescribeConfigReadFailure(error), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJson_NoCrash()
        => Assert.Contains("异常 JSON",
            EnvironmentDetector.DescribeConfigReadFailure(new JsonException("fixture malformed json")),
            StringComparison.Ordinal);

    [Fact]
    public void UnauthorizedConfig_NoCrash()
        => Assert.Contains("无法读取 Clash 配置目录",
            EnvironmentDetector.DescribeConfigReadFailure(new UnauthorizedAccessException("fixture denied")),
            StringComparison.Ordinal);

    [Fact]
    public void GlobalMode_WarningOnly()
        => Assert.Contains("未处于规则模式", EnvironmentDetector.DescribeSupportedEnvironment("global"),
            StringComparison.Ordinal);

    [Fact]
    public void GlobalMode_DoesNotBlockApply()
    {
        var snapshot = new EnvironmentSnapshot(
            EnvironmentSupport.Supported,
            EnvironmentDetector.DescribeSupportedEnvironment("global"),
            EnvironmentDetector.DetectMachine(), null, null, null);

        Assert.Equal(EnvironmentSupport.Supported, snapshot.Support);
    }
    [Fact]
    public void DetectsSupportedWindows()
    {
        var machine = EnvironmentDetector.DetectMachine();
        Assert.Equal("x64", machine.Architecture);
        Assert.True(machine.IsSupported);
        Assert.NotEmpty(machine.TimeZoneId);
    }

    [Fact]
    public void DetectsClashVerge252()
    {
        Assert.True(ClashVergeDetector.VersionMatches("2.5.2", ClashVergeDetector.SupportedVersion));
        Assert.True(ClashVergeDetector.VersionMatches("2.5.2.0", ClashVergeDetector.SupportedVersion));
        Assert.False(ClashVergeDetector.VersionMatches("2.5.1", ClashVergeDetector.SupportedVersion));
    }

    [Fact]
    public void DoesNotSelectOtherClashClient()
    {
        Assert.Null(ClashVergeDetector.FindSingleProcess("FlClashCore-aiws-definitely-not-running"));
    }

    [Fact]
    public async Task ResolvesCurrentProfile()
    {
        using var fixture = CreateFixture();
        var info = await new SubscriptionInspector(new FakeDnsResolver(IPAddress.Parse("203.0.113.8"))).InspectAsync(fixture.Path);
        Assert.Equal("Rabcdefghijk", info.Uid);
        Assert.Equal("FlyintPro", info.Name);
        Assert.EndsWith("Rabcdefghijk.yaml", info.FilePath);
    }

    [Fact]
    public async Task ReadsSubscriptionNodes()
    {
        using var fixture = CreateFixture();
        var info = await new SubscriptionInspector(new FakeDnsResolver(IPAddress.Parse("203.0.113.8"))).InspectAsync(fixture.Path);
        Assert.Equal(3, info.Nodes.Count);
        Assert.Contains(info.Nodes, node => node.Name == "新加坡 01" && node.Protocol == "ss");
        Assert.Contains(info.Nodes, node => node.Name == "未来协议" && node.Protocol == "Unknown" && node.Server == "unknown.example");
    }

    [Fact]
    public async Task ResolvesNodeServerIps()
    {
        using var fixture = CreateFixture();
        var info = await new SubscriptionInspector(new FakeDnsResolver(IPAddress.Parse("2001:db8::7"))).InspectAsync(fixture.Path);
        Assert.Equal("198.51.100.4", info.Nodes.Single(node => node.Name == "IP 节点").ResolvedServerIp);
        Assert.Equal("2001:db8::7", info.Nodes.Single(node => node.Name == "新加坡 01").ResolvedServerIp);
    }

    [Fact]
    public async Task DetectsUnknownCustomConfiguration()
    {
        using var fixture = CreateFixture("option:\n      script: sUserScript01");
        await File.WriteAllTextAsync(fixture.File("profiles/sUserScript01.js"), "function main(config) { config.rules.unshift('MATCH,DIRECT'); return config; }");
        var profiles = await File.ReadAllTextAsync(fixture.File("profiles.yaml"));
        profiles = profiles.Replace("  - uid: Rabcdefghijk", "  - uid: sUserScript01\n    type: script\n    file: sUserScript01.js\n  - uid: Rabcdefghijk", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.File("profiles.yaml"), profiles);
        var info = await new SubscriptionInspector().InspectAsync(fixture.Path);
        Assert.Equal(ExtensionOwnership.UnknownUserLogic, info.ExtensionOwnership);
    }

    [Fact]
    public async Task TreatsCanonicalBoundExtensionsAsEmpty()
    {
        using var fixture = CreateFixture("""
option:
      merge: mEmpty0000001
      script: sEmpty0000001
      rules: rEmpty0000001
      proxies: pEmpty0000001
      groups: gEmpty0000001
""");
        var profiles = await File.ReadAllTextAsync(fixture.File("profiles.yaml"));
        var items = """
  - uid: mEmpty0000001
    type: merge
    file: mEmpty0000001.yaml
  - uid: sEmpty0000001
    type: script
    file: sEmpty0000001.js
  - uid: rEmpty0000001
    type: rules
    file: rEmpty0000001.yaml
  - uid: pEmpty0000001
    type: proxies
    file: pEmpty0000001.yaml
  - uid: gEmpty0000001
    type: groups
    file: gEmpty0000001.yaml
""";
        profiles = profiles.Replace("  - uid: Rabcdefghijk", items + "\n  - uid: Rabcdefghijk", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.File("profiles.yaml"), profiles);
        await File.WriteAllTextAsync(fixture.File("profiles/mEmpty0000001.yaml"), "# empty\n");
        await File.WriteAllTextAsync(fixture.File("profiles/sEmpty0000001.js"), "function main(config, profileName) {}\n");
        foreach (var prefix in new[] { "r", "p", "g" })
            await File.WriteAllTextAsync(fixture.File($"profiles/{prefix}Empty0000001.yaml"), "prepend: []\nappend: []\ndelete: []\n");
        var info = await new SubscriptionInspector().InspectAsync(fixture.Path);
        Assert.Equal(ExtensionOwnership.NoneOrEmpty, info.ExtensionOwnership);
    }

    [Fact]
    public async Task UnusedProfileCustomScript_DoesNotBlockCurrentProfile()
    {
        using var fixture = CreateFixture();
        var profiles = await File.ReadAllTextAsync(fixture.File("profiles.yaml"));
        profiles += "\n  - uid: sUnused000001\n    type: script\n    file: sUnused000001.js\n";
        await File.WriteAllTextAsync(fixture.File("profiles.yaml"), profiles);
        await File.WriteAllTextAsync(fixture.File("profiles/sUnused000001.js"),
            "function main(config) { config.rules.unshift('MATCH,DIRECT'); return config; }");
        var info = await new SubscriptionInspector().InspectAsync(fixture.Path);
        Assert.Equal(ExtensionOwnership.NoneOrEmpty, info.ExtensionOwnership);
    }

    private static TempDirectory CreateFixture(string option = "option: {}")
    {
        var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("profiles"));
        File.WriteAllText(temp.File("profiles.yaml"), $"""
current: Rabcdefghijk
items:
  - uid: Rabcdefghijk
    type: remote
    name: FlyintPro
    file: Rabcdefghijk.yaml
    {option}
""");
        File.WriteAllText(temp.File("profiles/Rabcdefghijk.yaml"), """
proxies:
  - name: 新加坡 01
    type: ss
    server: node.example
    port: 443
  - name: IP 节点
    type: vless
    server: 198.51.100.4
    port: 8443
  - name: 未来协议
    type: future-protocol
    server: unknown.example
proxy-groups: []
rules:
  - MATCH,DIRECT
""");
        return temp;
    }
}
