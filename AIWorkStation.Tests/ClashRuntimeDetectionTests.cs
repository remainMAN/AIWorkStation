using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class ClashRuntimeDetectionTests
{
    [Fact]
    public void ProfileNamedSelector_IsStillPreferred()
    {
        using var temp = new TempDirectory();
        var runtime = Runtime(temp, """
rules:
  - MATCH,无关策略
""");
        var expected = new ProxySelection("主策略", "Taiwan 031") { Members = ["Taiwan 031", "Singapore 01"] };
        var clash = Clash([
            new ProxySelection("无关策略", "无关节点") { Members = ["无关节点"] },
            expected
        ], runtime);

        var selection = ClashVergeDetector.FindSafeDialerProxySelection(clash, "主策略");

        Assert.Same(expected, selection);
        Assert.Equal("Taiwan 031", selection!.CurrentSelection);
        Assert.Equal("主策略", ClashVergeDetector.FindSafeDialerProxyGroup(clash, "主策略"));
    }

    [Fact]
    public void NoSafeFrontSelector_ReturnsNullForDirectOnlyScope()
    {
        var clash = Clash([
            new ProxySelection(RouteScriptBuilder.StaticGroupName, RouteScriptBuilder.DirectStaticExitName)
            {
                Members = [RouteScriptBuilder.DirectStaticExitName]
            },
            new ProxySelection("主策略", "主策略") { Members = ["主策略"] }
        ]);

        Assert.Null(ClashVergeDetector.FindSafeDialerProxySelection(clash, "主策略"));
        Assert.Null(ClashVergeDetector.FindSafeDialerProxyGroup(clash, "主策略"));
    }

    [Fact]
    public void SafeFrontSelector_WithManagedMember_IsRejected()
    {
        var clash = Clash([
            new ProxySelection("主策略", "Taiwan 031")
            {
                Members = ["Taiwan 031", RouteScriptBuilder.StaticGroupName]
            }
        ]);

        Assert.Null(ClashVergeDetector.FindSafeDialerProxySelection(clash, "主策略"));
    }

    [Theory]
    [InlineData("未选择")]
    [InlineData("不在成员中的节点")]
    public void MissingNow_IsNotSafeFrontSelection(string current)
    {
        var clash = Clash([
            new ProxySelection("主策略", current) { Members = ["实际成员"] }
        ]);

        Assert.Null(ClashVergeDetector.FindSafeDialerProxySelection(clash, "主策略"));
    }

    [Fact]
    public void DifferentProfileAndMainSelector_UsesMatchTarget()
    {
        using var temp = new TempDirectory();
        var runtime = Runtime(temp, """
rules:
  - DOMAIN,example.com,自动选择
  - MATCH,节点选择,no-resolve
""");
        var expected = new ProxySelection("节点选择", "Taiwan 031") { Members = ["Taiwan 031"] };
        var clash = Clash([
            new ProxySelection("自动选择", "Hongkong 01") { Members = ["Hongkong 01"] },
            expected
        ], runtime);

        Assert.Same(expected, ClashVergeDetector.FindSafeDialerProxySelection(clash, "FlyintPro"));
    }

    [Fact]
    public void MostReferencedSafeSelector_IsSelected()
    {
        using var temp = new TempDirectory();
        var runtime = Runtime(temp, """
rules:
  - DOMAIN,one.example,节点选择
  - DOMAIN-SUFFIX,two.example,节点选择
  - DOMAIN,three.example,自动选择
""");
        var expected = new ProxySelection("节点选择", "Taiwan 031") { Members = ["Taiwan 031"] };
        var clash = Clash([
            new ProxySelection("自动选择", "Hongkong 01") { Members = ["Hongkong 01"] },
            expected
        ], runtime);

        Assert.Same(expected, ClashVergeDetector.FindSafeDialerProxySelection(clash, "FlyintPro"));
    }

    [Fact]
    public void AiwsManagedSelector_IsNeverUsedAsDialer()
    {
        var clash = Clash([
            new ProxySelection(RouteScriptBuilder.StaticGroupName, RouteScriptBuilder.DirectStaticExitName)
            {
                Members = [RouteScriptBuilder.DirectStaticExitName]
            }
        ]);

        Assert.Empty(ClashVergeDetector.FindSafeDialerProxySelections(clash, "FlyintPro"));
    }

    [Fact]
    public void SelfReferencingSelector_IsRejected()
    {
        var clash = Clash([
            new ProxySelection("节点选择", "Taiwan 031") { Members = ["Taiwan 031", "节点选择"] }
        ]);

        Assert.Empty(ClashVergeDetector.FindSafeDialerProxySelections(clash, "FlyintPro"));
    }

    [Fact]
    public void ValidSelectorWithDifferentName_StillEnablesDialer()
    {
        var expected = new ProxySelection("节点选择", "Taiwan 031") { Members = ["Taiwan 031"] };
        var clash = Clash([expected]);

        Assert.Same(expected, ClashVergeDetector.FindSafeDialerProxySelection(clash, "FlyintPro"));
    }

    [Fact]
    public void MultipleSafeSelectors_DoNotDisableDirect()
    {
        var clash = Clash([
            new ProxySelection("节点选择", "Taiwan 031") { Members = ["Taiwan 031"] },
            new ProxySelection("自动选择", "Hongkong 01") { Members = ["Hongkong 01"] }
        ]);

        Assert.NotNull(ClashVergeDetector.FindSafeDialerProxySelection(clash, "FlyintPro"));
        Assert.Equal(2, ClashVergeDetector.FindSafeDialerProxySelections(clash, "FlyintPro").Count);
    }

    [Fact]
    public void MultipleSafeSelectors_StillExposeDialerChoice()
    {
        var selections = new[]
        {
            new ProxySelection("节点选择", "Taiwan 031") { Members = ["Taiwan 031"] },
            new ProxySelection("自动选择", "Hongkong 01") { Members = ["Hongkong 01"] }
        };

        var choices = ClashVergeDetector.FindSafeDialerProxySelections(Clash(selections), "FlyintPro");

        Assert.Equal(["节点选择", "自动选择"], choices.Select(item => item.GroupName));
    }

    [Fact]
    public void NoSelector_DirectStillWorks()
    {
        var clash = Clash([]);

        Assert.Empty(ClashVergeDetector.FindSafeDialerProxySelections(clash, "FlyintPro"));
        Assert.Null(ClashVergeDetector.FindSafeDialerProxyGroup(clash, "FlyintPro"));
    }

    [Fact]
    public void RuntimeSettings_ReadsInboundPortsAndStoreSelected()
    {
        using var temp = new TempDirectory();
        var path = temp.File("clash-verge.yaml");
        File.WriteAllText(path, """
external-controller-pipe: \\.\pipe\verge-mihomo
mode: rule
mixed-port: 7897
http-port: 7898
socks-port: 7899
profile:
  store-selected: false
tun:
  enable: true
""");

        var settings = ClashVergeDetector.ReadRuntimeSettings(path);

        Assert.Equal(7897, settings.MixedPort);
        Assert.Equal(7898, settings.HttpPort);
        Assert.Equal(7899, settings.SocksPort);
        Assert.False(settings.StoreSelected);
        Assert.True(settings.TunEnabled);
    }

    [Fact]
    public void RuntimeSettings_UsesPortAsHttpPortFallback()
    {
        using var temp = new TempDirectory();
        var path = temp.File("clash-verge.yaml");
        File.WriteAllText(path, """
external-controller-pipe: \\.\pipe\verge-mihomo
port: 7890
profile:
  store-selected: true
""");

        var settings = ClashVergeDetector.ReadRuntimeSettings(path);

        Assert.Null(settings.MixedPort);
        Assert.Equal(7890, settings.HttpPort);
        Assert.Null(settings.SocksPort);
        Assert.True(settings.StoreSelected);
    }

    [Fact]
    public void ClashInfo_RuntimeFactsAreOptionalAndConstructorCompatible()
    {
        var originalShape = Clash([]);
        var enriched = originalShape with
        {
            MixedPort = 7897,
            HttpPort = 7898,
            SocksPort = 7899,
            StoreSelected = true
        };

        Assert.Null(originalShape.MixedPort);
        Assert.Equal(7897, enriched.MixedPort);
        Assert.True(enriched.StoreSelected);
    }

    private static string Runtime(TempDirectory temp, string yaml)
    {
        var path = temp.File("clash-verge.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static ClashInfo Clash(IReadOnlyList<ProxySelection> selections, string? runtimePath = null)
        => new(new ProcessInfo(1, null, @"C:\fixture\clash-verge.exe", "2.5.2"),
            new ProcessInfo(2, null, @"C:\fixture\verge-mihomo.exe", "1"), @"C:\fixture",
            @"C:\fixture\profiles.yaml", runtimePath ?? @"C:\fixture\clash-verge.yaml", @"C:\fixture\profiles",
            @"\\.\pipe\fixture", "rule", false, true, false, selections);
}
