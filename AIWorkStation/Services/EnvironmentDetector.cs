using System.Runtime.InteropServices;
using System.Text.Json;
using AIWorkStation.Models;
using Microsoft.Win32;

namespace AIWorkStation.Services;

public sealed class EnvironmentDetector
{
    private readonly ClashVergeDetector _clashDetector;
    private readonly SubscriptionInspector _subscriptionInspector;
    private readonly PublicIpDetector _publicIpDetector;

    public EnvironmentDetector(
        ClashVergeDetector? clashDetector = null,
        SubscriptionInspector? subscriptionInspector = null,
        PublicIpDetector? publicIpDetector = null)
    {
        _clashDetector = clashDetector ?? new ClashVergeDetector();
        _subscriptionInspector = subscriptionInspector ?? new SubscriptionInspector();
        _publicIpDetector = publicIpDetector ?? new PublicIpDetector();
    }

    public async Task<EnvironmentSnapshot> DetectAsync(CancellationToken cancellationToken = default)
    {
        var machine = DetectMachine();
        if (!machine.IsSupported)
            return new(EnvironmentSupport.Unsupported, "当前版本仅支持 Windows 10 / 11 x64。", machine, null, null, await TryPublicIp(cancellationToken));

        try
        {
            var clash = await _clashDetector.DetectAsync(cancellationToken);
            var subscription = await _subscriptionInspector.InspectAsync(clash.DataDirectory, cancellationToken);
            if (subscription.ExtensionOwnership == ExtensionOwnership.UnknownUserLogic)
                return new(EnvironmentSupport.Unsupported, "检测到已有自定义网络配置，当前版本暂不支持自动配置。", machine, clash, subscription, await TryPublicIp(cancellationToken));
            var reason = DescribeSupportedEnvironment(clash.Mode);
            return new(EnvironmentSupport.Supported, reason, machine, clash, subscription, await TryPublicIp(cancellationToken));
        }
        catch (ClashDetectionException ex)
        {
            return new(EnvironmentSupport.Unsupported, new UserMessageMapper().Map(ex.FailureCode).MessageZh, machine, null, null, await TryPublicIp(cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                    JsonException or InvalidOperationException or ArgumentException or
                                    NotSupportedException or YamlDotNet.Core.YamlException)
        {
            return new(EnvironmentSupport.Unsupported, DescribeConfigReadFailure(ex), machine, null, null,
                await TryPublicIp(cancellationToken));
        }
    }

    public static MachineInfo DetectMachine()
    {
        var edition = RuntimeInformation.OSDescription;
        var displayVersion = Environment.OSVersion.Version.ToString();
        var build = Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            edition = Convert.ToString(key?.GetValue("ProductName")) ?? edition;
            displayVersion = Convert.ToString(key?.GetValue("DisplayVersion")) ?? Convert.ToString(key?.GetValue("ReleaseId")) ?? displayVersion;
            build = Convert.ToString(key?.GetValue("CurrentBuildNumber")) ?? build;
        }

        var architecture = RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" : RuntimeInformation.OSArchitecture.ToString();
        var buildNumber = int.TryParse(build, out var numericBuild) ? numericBuild : 0;
        var supported = OperatingSystem.IsWindows() && architecture == "x64" && buildNumber >= 10240;
        var zone = TimeZoneInfo.Local;
        return new(edition, displayVersion, build, architecture, zone.DisplayName, zone.Id, zone.GetUtcOffset(DateTimeOffset.Now), supported);
    }

    private async Task<string?> TryPublicIp(CancellationToken token)
    {
        try { return await _publicIpDetector.DetectAsync(cancellationToken: token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    internal static string DescribeConfigReadFailure(Exception exception)
        => exception switch
        {
            UnauthorizedAccessException => "AI WorkStation 无法读取 Clash 配置目录，没有进行修改。",
            JsonException => "Clash 运行状态返回了异常 JSON，AI WorkStation 没有进行修改。",
            YamlDotNet.Core.YamlException => "Clash 配置格式异常，AI WorkStation 没有进行修改。",
            InvalidOperationException when exception.Message.Contains("重复", StringComparison.Ordinal) =>
                "Clash 配置中存在重复项目，当前无法正确识别。",
            _ => $"无法读取当前 Clash 配置：{exception.Message}"
        };

    internal static string DescribeSupportedEnvironment(string mode)
        => mode.Equals("rule", StringComparison.OrdinalIgnoreCase)
            ? "当前电脑符合 Clash Verge Rev 2.5.2 标准环境。"
            : "当前电脑符合 Clash Verge Rev 2.5.2 标准环境。 当前 Clash 未处于规则模式；程序级分流通常需要规则模式才能按进程生效。";
}
