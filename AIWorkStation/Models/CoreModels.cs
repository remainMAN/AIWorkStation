using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIWorkStation.Models;

public enum UiState { Checking, Ready, Applying, Succeeded, Failed, Recovering, RecoveryFailed }
public enum EnvironmentSupport { Supported, Unsupported }
public enum ExtensionOwnership { NoneOrEmpty, AIWorkStationManaged, UnknownUserLogic }
public enum StaticProxyProtocol { Socks5, Http }
public enum StaticTransportMode { Direct, DialerProxy }
public enum StaticTransportPreference { Auto, Direct, DialerProxy }
public enum ApplicationRouteStatus { Verified, NoTrafficObserved, WrongRoute }
public enum LatencyStatus { NotTested, Testing, Available, Timeout, Failed }

public enum FailureCode
{
    None,
    UnsupportedWindows,
    ClashNotFound,
    UnsupportedClashVersion,
    MihomoNotRunning,
    SubscriptionNotFound,
    UnknownCustomConfiguration,
    ApplicationNotFound,
    StaticProxyConnectionFailed,
    StaticProxyAuthenticationFailed,
    StaticProxyTimeout,
    OperationCancelled,
    ExitIpLookupFailed,
    ScriptBuildFailed,
    ScriptExecutionFailed,
    MihomoValidationFailed,
    TemporaryRuntimeLoadFailed,
    ApplicationTrafficNotObserved,
    ApplicationRouteMismatch,
    ExitIpMismatch,
    BackupFailed,
    TargetChanged,
    WriteFailed,
    ReloadFailed,
    PostWriteVerificationFailed,
    RecoveryFailed
}

public sealed record MachineInfo(
    string Edition,
    string Version,
    string BuildNumber,
    string Architecture,
    string TimeZoneDisplayName,
    string TimeZoneId,
    TimeSpan UtcOffset,
    bool IsSupported);

public sealed record ProcessInfo(int Pid, DateTime? StartTime, string ExecutablePath, string Version);

public sealed record ClashInfo(
    ProcessInfo ClashProcess,
    ProcessInfo MihomoProcess,
    string DataDirectory,
    string ProfilesPath,
    string RuntimeConfigPath,
    string ProfilesDirectory,
    string ControllerPipe,
    string Mode,
    bool TunEnabled,
    bool SystemProxyEnabled,
    bool SystemTunnelDetected,
    IReadOnlyList<ProxySelection> ProxySelections)
{
    public int? MixedPort { get; init; }
    public int? HttpPort { get; init; }
    public int? SocksPort { get; init; }
    public bool? StoreSelected { get; init; }
}

public sealed record ProxySelection(string GroupName, string CurrentSelection)
{
    public IReadOnlyList<string> Members { get; init; } = [];
    public string DisplayName => $"{GroupName} → {CurrentSelection}";
}

public sealed partial class ProxyNodeInfo : ObservableObject
{
    [ObservableProperty] private int? latencyMs;
    [ObservableProperty] private LatencyStatus latencyStatus = LatencyStatus.NotTested;
    [ObservableProperty] private DateTimeOffset? latencyTestedAt;

    public ProxyNodeInfo(string name, string protocol, string server, IReadOnlyList<IPAddress> resolvedAddresses)
    {
        Name = name;
        Protocol = protocol;
        Server = server;
        ResolvedAddresses = resolvedAddresses;
    }

    public string Name { get; }
    public string Protocol { get; }
    public string Server { get; }
    public IReadOnlyList<IPAddress> ResolvedAddresses { get; }

    public string Status => LatencyStatusDisplay;

    public string LatencyStatusDisplay => LatencyStatus switch
    {
        LatencyStatus.Testing => "测试中",
        LatencyStatus.Available => "可用",
        LatencyStatus.Timeout => "超时",
        LatencyStatus.Failed => "测试失败",
        _ => "未测试"
    };

    public string LatencyDisplay => LatencyStatus switch
    {
        LatencyStatus.Available when LatencyMs is > 0 => $"{LatencyMs} ms",
        LatencyStatus.Testing => "测试中",
        LatencyStatus.Timeout => "超时",
        LatencyStatus.Failed => "测试失败",
        _ => "未测试"
    };

    public string LatencyTestedAtDisplay => LatencyTestedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    public bool IsFakeIp => ResolvedAddresses.Any(IsClashFakeIp);

    public string ResolvedServerIp => ResolvedAddresses.Count == 0
        ? "解析失败"
        : IsFakeIp
            ? $"解析结果：{string.Join(", ", ResolvedAddresses.Select(ip => ip.ToString()))} · Clash Fake-IP（非真实服务器 IP）"
            : string.Join(", ", ResolvedAddresses.Select(ip => ip.ToString()));

    public static bool IsClashFakeIp(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    partial void OnLatencyMsChanged(int? value) => OnPropertyChanged(nameof(LatencyDisplay));

    partial void OnLatencyStatusChanged(LatencyStatus value)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(LatencyStatusDisplay));
        OnPropertyChanged(nameof(LatencyDisplay));
    }

    partial void OnLatencyTestedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LatencyTestedAtDisplay));
}

public sealed record SubscriptionInfo(
    string Uid,
    string Name,
    string FileName,
    string FilePath,
    string ProfilesHash,
    IReadOnlyList<ProxyNodeInfo> Nodes,
    ExtensionOwnership ExtensionOwnership,
    string? ScriptUid,
    string? ScriptPath,
    string? ScriptHash);

public sealed class StaticExitConfig
{
    public StaticProxyProtocol Protocol { get; init; }
    public required string Server { get; init; }
    public int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server)) throw new ArgumentException("代理服务器不能为空。");
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port), "代理端口无效。");
    }
}

public sealed record StaticExitTestResult(bool Success, string? ActualExitIp, FailureCode FailureCode, string? SanitizedDetail);

public sealed record ApplicationTarget(
    string DisplayName,
    string ExecutableName,
    string ExecutablePath,
    bool RunningProcess,
    string Source);

public sealed record RouteConfiguration(
    IReadOnlyList<ApplicationTarget> Targets,
    StaticExitConfig StaticExit,
    string ActualExitIp,
    string ProfileName)
{
    public StaticTransportMode TransportMode { get; init; } = StaticTransportMode.Direct;
    public StaticTransportPreference TransportPreference { get; init; } = StaticTransportPreference.Auto;
    public string? DialerProxyGroup { get; init; }
}

public sealed record EnvironmentSnapshot(
    EnvironmentSupport Support,
    string ReasonZh,
    MachineInfo Machine,
    ClashInfo? Clash,
    SubscriptionInfo? Subscription,
    string? CurrentPublicIp);

public sealed record ApplyResult(
    bool Success,
    FailureCode FailureCode,
    string Stage,
    string SanitizedDetail,
    bool FilesModified,
    bool RecoveryAttempted,
    bool RecoverySucceeded,
    string? ActualExitIp)
{
    public StaticTransportMode TransportMode { get; init; } = StaticTransportMode.Direct;
    public bool NoChangesRequired { get; init; }
    public IReadOnlyList<ApplicationRouteResult> ApplicationResults { get; init; } = [];
    public bool RouteVerificationNotObserved =>
        ApplicationResults.Count > 0 &&
        ApplicationResults.All(result => result.Status == ApplicationRouteStatus.NoTrafficObserved);

    public static ApplyResult Ok(
        string? exitIp,
        StaticTransportMode transportMode = StaticTransportMode.Direct,
        bool filesModified = true,
        bool noChangesRequired = false,
        string detail = "",
        IReadOnlyList<ApplicationRouteResult>? applicationResults = null)
        => new(true, FailureCode.None, "Verify",
            noChangesRequired ? "当前配置已经是最新状态。" : detail,
            filesModified, false, false, exitIp)
        {
            TransportMode = transportMode,
            NoChangesRequired = noChangesRequired,
            ApplicationResults = applicationResults ?? []
        };
    public static ApplyResult Fail(FailureCode code, string stage, string detail, bool modified = false, bool recoveryAttempted = false, bool recoverySucceeded = false)
        => new(false, code, stage, detail, modified, recoveryAttempted, recoverySucceeded, null);
}

public sealed record UserMessage(string TitleZh, string MessageZh, string SuggestedActionZh);

public sealed record FileFingerprint(string Path, string Sha256);

public sealed record ApplyContext(
    EnvironmentSnapshot Environment,
    RouteConfiguration Route,
    FileFingerprint ProfilesFingerprint,
    FileFingerprint? ScriptFingerprint);

public sealed record RouteObservation(
    string Process,
    string Rule,
    IReadOnlyList<string> Chains,
    string? RemoteAddress)
{
    public string? ConnectionId { get; init; }
}

public sealed record ApplicationRouteResult(string ExecutableName, ApplicationRouteStatus Status, RouteObservation? Observation);
