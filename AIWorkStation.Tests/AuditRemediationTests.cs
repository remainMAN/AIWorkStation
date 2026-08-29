using System.IO.Pipes;
using System.Net;
using System.Text;
using AIWorkStation.Models;
using AIWorkStation.Services;
using YamlDotNet.RepresentationModel;

namespace AIWorkStation.Tests;

public sealed class AuditRemediationTests
{
    private static readonly ApplicationTarget Target = new("Codex", "codex.exe", @"C:\Apps\codex.exe", true, "test");

    [Fact]
    public void TransportScript_BuildsDirectAndDialerProxyWithoutUdpOrProcessPath()
    {
        var route = Route("主策略", StaticTransportMode.DialerProxy);
        var candidate = new RouteScriptBuilder().BuildRuntimeCandidate(BaseYaml, route);
        var direct = FindNamed(candidate, "proxies", RouteScriptBuilder.DirectStaticExitName);
        var chained = FindNamed(candidate, "proxies", RouteScriptBuilder.DialerStaticExitName);
        Assert.False(direct.Children.ContainsKey(new YamlScalarNode("dialer-proxy")));
        Assert.Equal("主策略", Scalar(chained, "dialer-proxy"));
        Assert.False(direct.Children.ContainsKey(new YamlScalarNode("udp")));
        Assert.DoesNotContain("PROCESS-PATH", candidate);
        Assert.Equal([RouteScriptBuilder.DialerStaticExitName, RouteScriptBuilder.DirectStaticExitName],
            Sequence(FindNamed(candidate, "proxy-groups", RouteScriptBuilder.StaticGroupName), "proxies")
                .Children.OfType<YamlScalarNode>().Select(item => item.Value!).ToArray());
        new ScriptValidator().ValidateSemantics(candidate, [Target], route);
    }

    [Fact]
    public void NoSafeFrontGroup_KeepsDirectAvailable()
    {
        var route = Route(null, StaticTransportMode.Direct);
        var candidate = new RouteScriptBuilder().BuildRuntimeCandidate(BaseYaml, route);
        _ = FindNamed(candidate, "proxies", RouteScriptBuilder.DirectStaticExitName);
        Assert.DoesNotContain(RouteScriptBuilder.DialerStaticExitName, candidate);
    }

    [Fact]
    public void DialerMode_PutsDialerFirstInGroup()
    {
        var candidate = new RouteScriptBuilder().BuildRuntimeCandidate(BaseYaml, Route("主策略", StaticTransportMode.DialerProxy));
        Assert.Equal(RouteScriptBuilder.DialerStaticExitName,
            Sequence(FindNamed(candidate, "proxy-groups", RouteScriptBuilder.StaticGroupName), "proxies")
                .Children.OfType<YamlScalarNode>().First().Value);
    }

    [Fact]
    public void DirectMode_PutsDirectFirstInGroup()
    {
        var candidate = new RouteScriptBuilder().BuildRuntimeCandidate(BaseYaml, Route("主策略", StaticTransportMode.Direct));
        Assert.Equal(RouteScriptBuilder.DirectStaticExitName,
            Sequence(FindNamed(candidate, "proxy-groups", RouteScriptBuilder.StaticGroupName), "proxies")
                .Children.OfType<YamlScalarNode>().First().Value);
    }

    [Fact]
    public void SimulatedRestart_FallsBackToFirstConfiguredTransport()
    {
        var dialer = new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.DialerProxy));
        Assert.Contains($"\"proxies\":[\"{RouteScriptBuilder.DialerStaticExitName}\",\"{RouteScriptBuilder.DirectStaticExitName}\"]", dialer);
    }

    [Fact]
    public void SameExecutableName_GeneratesOneProcessNameRule()
    {
        var duplicate = Target with { ExecutablePath = @"D:\Other\codex.exe" };
        var route = Route("主策略", StaticTransportMode.Direct) with { Targets = [Target, duplicate] };
        var script = new RouteScriptBuilder().Build(route);
        Assert.Equal(1, script.Split("PROCESS-NAME,codex.exe,AI静态链", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ExactManagedHeader_IsOwned()
        => Assert.True(RouteScriptBuilder.IsStrictlyOwnedScript(new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.Direct))));

    [Fact]
    public void LegacySingleExitV1_IsRecognized()
        => Assert.True(RouteScriptBuilder.IsStrictlyOwnedScript(LegacySingleExitV1()));

    [Fact]
    public void LegacyDualExitV1_IsRecognized()
    {
        var legacy = new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.Direct))
            .Replace(RouteScriptBuilder.ManagedVersionHeader,
                RouteScriptBuilder.LegacyManagedVersionHeader, StringComparison.Ordinal);

        Assert.True(RouteScriptBuilder.IsStrictlyOwnedScript(legacy));
    }

    [Fact]
    public void LegacyV1_RegeneratesAsV2()
    {
        Assert.True(RouteScriptBuilder.IsStrictlyOwnedScript(LegacySingleExitV1()));
        var current = new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.Direct));

        Assert.Contains(RouteScriptBuilder.ManagedVersionHeader, current, StringComparison.Ordinal);
        Assert.DoesNotContain(RouteScriptBuilder.LegacyManagedVersionHeader, current, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownV1LikeUserScript_IsRejected()
        => Assert.False(RouteScriptBuilder.IsStrictlyOwnedScript(
            LegacySingleExitV1().Replace("  return config;", "  config.rules.push('MATCH,DIRECT');\n  return config;", StringComparison.Ordinal)));

    [Fact]
    public void CurrentV2_IsRecognized()
        => Assert.True(RouteScriptBuilder.IsStrictlyOwnedScript(
            new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.DialerProxy))));

    [Fact]
    public void MarkerInMiddle_IsNotOwned()
        => Assert.False(RouteScriptBuilder.IsStrictlyOwnedScript("function main(config) { return config; }\n// AIWORKSTATION MANAGED\n// VERSION: 1"));

    [Fact]
    public void ManagedHeaderWithUnknownExtraLogic_IsRejected()
    {
        var script = new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.Direct));
        Assert.False(RouteScriptBuilder.IsStrictlyOwnedScript(script + "\nunknownUserLogic();"));
    }

    [Fact]
    public void ManagedHeaderWithModifiedProxy_IsRejected()
    {
        var script = new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.Direct));
        var modified = script.Replace("\"port\":1080", "\"port\":1080,\"udp\":true", StringComparison.Ordinal);
        Assert.False(RouteScriptBuilder.IsStrictlyOwnedScript(modified));
    }

    [Fact]
    public void Utf8BomManagedHeader_IsAccepted()
        => Assert.True(RouteScriptBuilder.IsStrictlyOwnedScript("\uFEFF" + new RouteScriptBuilder().Build(Route("主策略", StaticTransportMode.Direct))));

    [Theory]
    [InlineData(RouteScriptBuilder.StaticGroupName)]
    [InlineData(RouteScriptBuilder.DirectStaticExitName)]
    [InlineData(RouteScriptBuilder.DialerStaticExitName)]
    public void ReservedFrontGroup_NeverCreatesCycle(string reserved)
    {
        var candidate = new RouteScriptBuilder().BuildRuntimeCandidate(BaseYaml, Route(reserved, StaticTransportMode.Direct));
        Assert.DoesNotContain("dialer-proxy", candidate);
    }

    [Fact]
    public void SafeFrontGroup_ComesFromCurrentRuntimeSelector()
    {
        var clash = Clash([
            new ProxySelection("主策略", "Taiwan 031") { Members = ["Taiwan 031"] },
            new ProxySelection(RouteScriptBuilder.StaticGroupName, RouteScriptBuilder.DirectStaticExitName)
            {
                Members = [RouteScriptBuilder.DirectStaticExitName]
            }
        ]);
        Assert.Equal("主策略", ClashVergeDetector.FindSafeDialerProxyGroup(clash, "主策略"));
        Assert.Equal("主策略", ClashVergeDetector.FindSafeDialerProxyGroup(clash, "不存在的 Profile"));
        var cyclic = Clash([new ProxySelection("主策略", "Taiwan 031") { Members = [RouteScriptBuilder.StaticGroupName] }]);
        Assert.Null(ClashVergeDetector.FindSafeDialerProxyGroup(cyclic, "主策略"));
    }

    [Fact]
    public void BaselineIssueIgnored_StillRequiresTemporaryRuntimeValidation()
    {
        var ignored = new MihomoValidationResult(true, 1, "基线问题", BaselineIssueIgnored: true);
        Assert.True(ApplyEngine.ShouldValidateTemporaryRuntime(ignored));
    }

    [Theory]
    [InlineData("198.18.0.1", true)]
    [InlineData("198.19.255.254", true)]
    [InlineData("198.20.0.1", false)]
    public void ClassifiesClashFakeIp(string value, bool expected)
        => Assert.Equal(expected, ProxyNodeInfo.IsClashFakeIp(IPAddress.Parse(value)));

    [Fact]
    public void PartialTraffic_RemainsSuccessful()
    {
        var observation = Correct("ChatGPT.exe");
        var result = RouteVerifier.Assess(["ChatGPT.exe", "codex.exe"],
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase) { ["ChatGPT.exe"] = observation },
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase), RouteScriptBuilder.DirectStaticExitName);
        Assert.True(result.Success);
        Assert.Contains(result.ApplicationResults!, item => item.ExecutableName == "codex.exe" && item.Status == ApplicationRouteStatus.NoTrafficObserved);
    }

    [Fact]
    public void WrongRoute_StillFails()
    {
        var wrong = new RouteObservation("codex.exe", "ProcessName", ["主策略"], null);
        var result = RouteVerifier.Assess(["codex.exe"],
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase) { ["codex.exe"] = wrong },
            RouteScriptBuilder.DirectStaticExitName);
        Assert.False(result.Success);
        Assert.Equal(FailureCode.ApplicationRouteMismatch, result.FailureCode);
    }

    [Fact]
    public void NoTraffic_ReturnsNotObservedResult()
    {
        var result = RouteVerifier.Assess(["ChatGPT.exe", "codex.exe"],
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RouteObservation>(StringComparer.OrdinalIgnoreCase), RouteScriptBuilder.DirectStaticExitName);
        Assert.True(result.Success);
        Assert.Equal(FailureCode.None, result.FailureCode);
        Assert.All(result.ApplicationResults!, item =>
            Assert.Equal(ApplicationRouteStatus.NoTrafficObserved, item.Status));
    }

    [Fact]
    public async Task ExplicitSelection_UsesUrlEncodedChineseGroupName()
    {
        var pipeName = "aiws-select-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        string? request = null;
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            request = await ReadHttpRequestAsync(server);
            await server.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            await server.FlushAsync();
        });
        await new MihomoNamedPipeClient(@"\\.\pipe\" + pipeName).SelectProxyAsync(
            RouteScriptBuilder.StaticGroupName, RouteScriptBuilder.DialerStaticExitName);
        await serverTask;
        var captured = Assert.IsType<string>(request);
        Assert.Contains("PUT /proxies/AI%E9%9D%99%E6%80%81%E9%93%BE HTTP/1.1", captured);
        using var body = System.Text.Json.JsonDocument.Parse(captured[(captured.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
        Assert.Equal(RouteScriptBuilder.DialerStaticExitName, body.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task DialerProxy_VerifiesActualExitIp()
    {
        await using var proxy = new SingleResponseProxy();
        proxy.Start(HttpStatusCode.OK, "203.0.113.77\n");
        var result = await new MihomoLocalProxyExitTester([new Uri("http://ip.test")])
            .TestAsync(proxy.Port, null, null, null);
        Assert.True(result.Success);
        Assert.Equal("203.0.113.77", result.ActualExitIp);
    }

    [Fact]
    public async Task DialerProxy_MissingLocalIngressIsConnectionFailure()
    {
        var result = await new MihomoLocalProxyExitTester([new Uri("http://ip.test")], TimeSpan.FromMilliseconds(100))
            .TestAsync(1, null, null, null);
        Assert.False(result.Success);
        Assert.Equal(FailureCode.StaticProxyConnectionFailed, result.FailureCode);
    }

    [Fact]
    public async Task DialerProxy_ExitIpMismatchFailsValidation()
    {
        await using var proxy = new SingleResponseProxy();
        proxy.Start(HttpStatusCode.OK, "203.0.113.77\n");
        var result = await new MihomoLocalProxyExitTester([new Uri("http://ip.test")])
            .TestAsync(proxy.Port, null, null, "203.0.113.88");
        Assert.False(result.Success);
        Assert.Equal(FailureCode.ExitIpMismatch, result.FailureCode);
    }

    [Fact]
    public async Task CorruptTransactionMarkerFailsClosedAndIsPreserved()
    {
        using var temp = new TempDirectory();
        var markerPath = temp.File("transaction.json");
        await File.WriteAllTextAsync(markerPath, "{ damaged");
        var markers = new TransactionMarkerService(markerPath);
        Assert.Equal(TransactionMarkerReadStatus.Corrupt, markers.ReadSafe().Status);
        Assert.False(await new RecoveryService(markers: markers).RecoverPendingAsync());
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task EmptyBoundScript_IsReusedWithoutProfilesMutation()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("profiles"));
        var profilesPath = temp.File("profiles.yaml");
        var scriptPath = temp.File("profiles/sEmpty0000001.js");
        await File.WriteAllTextAsync(profilesPath, "current: current\nitems:\n  - uid: current\n    type: remote\n    file: current.yaml\n    option:\n      script: sEmpty0000001\n  - uid: sEmpty0000001\n    type: script\n    file: sEmpty0000001.js\n");
        await File.WriteAllTextAsync(scriptPath, "function main(config, profileName) { return config; }");
        var subscription = new SubscriptionInfo("current", "主策略", "current.yaml", temp.File("profiles/current.yaml"),
            FileHash.Sha256(profilesPath), [], ExtensionOwnership.NoneOrEmpty, "sEmpty0000001", scriptPath, FileHash.Sha256(scriptPath));
        var plan = new ProfileBindingService().Prepare(Clash([], temp.Path), subscription);
        Assert.Equal("sEmpty0000001", plan.ScriptUid);
        Assert.Equal(scriptPath, plan.ScriptPath);
        Assert.False(plan.ProfilesChanged);
    }

    private static RouteConfiguration Route(string? dialerGroup, StaticTransportMode mode) => new([Target], new StaticExitConfig
    {
        Protocol = StaticProxyProtocol.Socks5,
        Server = "proxy.example",
        Port = 1080,
        Username = "fixture-user",
        Password = "fixture-password"
    }, mode == StaticTransportMode.Direct ? "203.0.113.1" : string.Empty, "主策略")
    {
        DialerProxyGroup = dialerGroup,
        TransportMode = mode
    };

    private static string LegacySingleExitV1() => """
// AIWORKSTATION MANAGED
// VERSION: 1
// 此文件由 AI WorkStation 完整维护，请勿手动修改。

function main(config, profileName) {
  config.proxies = Array.isArray(config.proxies) ? config.proxies : [];
  config['proxy-groups'] = Array.isArray(config['proxy-groups']) ? config['proxy-groups'] : [];
  config.rules = Array.isArray(config.rules) ? config.rules : [];
  const aiProxy = {"name":"AI静态出口","type":"socks5","server":"proxy.example","port":1080,"username":"fixture-user","password":"fixture-password"};
  const aiGroup = {"name":"AI静态链","type":"select","proxies":["AI静态出口"]};
  const aiRules = ["PROCESS-NAME,codex.exe,AI静态链"];
  const aiManagedNames = ["AI静态出口"];
  config.proxies = config.proxies.filter(p => p && !aiManagedNames.includes(p.name));
  config['proxy-groups'] = config['proxy-groups'].filter(g => g && g.name !== "AI静态链");
  config.rules = config.rules.filter(r => typeof r !== 'string' || !r.endsWith(',' + "AI静态链"));
  config.proxies.push(aiProxy);
  config['proxy-groups'].push(aiGroup);
  config.rules = aiRules.concat(config.rules);
  return config;
}
""";

    private static ClashInfo Clash(IReadOnlyList<ProxySelection> selections, string dataDirectory = @"C:\fixture")
        => new(new(1, null, @"C:\fixture\clash-verge.exe", "2.5.2"),
            new(2, null, @"C:\fixture\verge-mihomo.exe", "1"), dataDirectory,
            Path.Combine(dataDirectory, "profiles.yaml"), Path.Combine(dataDirectory, "clash-verge.yaml"),
            Path.Combine(dataDirectory, "profiles"), @"\\.\pipe\fixture", "rule", false, true, false, selections);

    private static RouteObservation Correct(string process)
        => new(process, "ProcessName", [RouteScriptBuilder.DirectStaticExitName, RouteScriptBuilder.StaticGroupName], null);

    private static YamlMappingNode FindNamed(string yamlText, string sequenceName, string name)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(yamlText));
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        var sequence = Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode(sequenceName)]);
        return sequence.Children.OfType<YamlMappingNode>().Single(item => Scalar(item, "name") == name);
    }

    private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
        => Assert.IsType<YamlSequenceNode>(mapping.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode mapping, string key)
        => Assert.IsType<YamlScalarNode>(mapping.Children[new YamlScalarNode(key)]).Value;

    private static async Task<string> ReadHttpRequestAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1024];
        var contentLength = 0;
        var headerLength = -1;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            var text = Encoding.UTF8.GetString(bytes.ToArray());
            if (headerLength < 0 && text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var index && index >= 0)
            {
                headerLength = index + 4;
                var lengthLine = text.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (lengthLine is not null) int.TryParse(lengthLine.Split(':', 2)[1].Trim(), out contentLength);
            }
            if (headerLength >= 0 && bytes.Count >= headerLength + contentLength) break;
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private const string BaseYaml = """
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
}
