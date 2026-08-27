using System.Diagnostics;
using System.Net.NetworkInformation;
using AIWorkStation.Models;
using Microsoft.Win32;
using YamlDotNet.RepresentationModel;

namespace AIWorkStation.Services;

public sealed class ClashVergeDetector
{
    public const string SupportedVersion = "2.5.2";
    public const string AppDataFolderName = "io.github.clash-verge-rev.clash-verge-rev";

    public async Task<ClashInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        var clash = FindSingleProcess("clash-verge") ?? throw new ClashDetectionException(FailureCode.ClashNotFound, "未发现 clash-verge 进程。");
        if (!VersionMatches(clash.Version, SupportedVersion))
            throw new ClashDetectionException(FailureCode.UnsupportedClashVersion, $"文件版本为 {clash.Version}。");

        var mihomo = FindSingleProcess("verge-mihomo") ?? throw new ClashDetectionException(FailureCode.MihomoNotRunning, "未发现 verge-mihomo 进程。");
        var clashDirectory = Path.GetDirectoryName(clash.ExecutablePath);
        var mihomoDirectory = Path.GetDirectoryName(mihomo.ExecutablePath);
        if (!string.Equals(clashDirectory, mihomoDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ClashDetectionException(FailureCode.MihomoNotRunning, "verge-mihomo 与 clash-verge 不在同一安装目录。");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDirectory = Path.Combine(appData, AppDataFolderName);
        var profilesPath = Path.Combine(dataDirectory, "profiles.yaml");
        var runtimePath = Path.Combine(dataDirectory, "clash-verge.yaml");
        var profilesDirectory = Path.Combine(dataDirectory, "profiles");
        if (!File.Exists(profilesPath) || !File.Exists(runtimePath) || !Directory.Exists(profilesDirectory))
            throw new ClashDetectionException(FailureCode.SubscriptionNotFound, "Clash Verge 数据目录不完整。");

        var runtime = ReadRuntimeSettings(runtimePath);
        if (string.IsNullOrWhiteSpace(runtime.ControllerPipe))
            throw new ClashDetectionException(FailureCode.MihomoNotRunning, "clash-verge.yaml 未声明 external-controller-pipe。");

        IReadOnlyList<ProxySelection> selections = [];
        try { selections = await new MihomoNamedPipeClient(runtime.ControllerPipe).GetProxySelectionsAsync(cancellationToken); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }

        return new ClashInfo(
            clash, mihomo, dataDirectory, profilesPath, runtimePath, profilesDirectory,
            runtime.ControllerPipe, runtime.Mode, runtime.TunEnabled, IsSystemProxyEnabled(),
            DetectSystemTunnel(), selections)
        {
            MixedPort = runtime.MixedPort,
            HttpPort = runtime.HttpPort,
            SocksPort = runtime.SocksPort,
            StoreSelected = runtime.StoreSelected
        };
    }

    public static ProcessInfo? FindSingleProcess(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName).OrderByDescending(p => SafeStartTime(p)))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    var version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
                    return new ProcessInfo(process.Id, SafeStartTime(process), path, version);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return null;
    }

    public static bool VersionMatches(string actual, string supported)
        => actual.Equals(supported, StringComparison.OrdinalIgnoreCase) || actual.StartsWith(supported + ".", StringComparison.OrdinalIgnoreCase) || actual.StartsWith(supported + "-", StringComparison.OrdinalIgnoreCase);

    public static string? FindSafeDialerProxyGroup(ClashInfo clash, string currentProfileName)
        => FindSafeDialerProxySelection(clash, currentProfileName)?.GroupName;

    public static ProxySelection? FindSafeDialerProxySelection(ClashInfo clash, string currentProfileName)
        => FindSafeDialerProxySelections(clash, currentProfileName).FirstOrDefault();

    public static IReadOnlyList<ProxySelection> FindSafeDialerProxySelections(ClashInfo clash, string currentProfileName)
    {
        // Named Pipe 返回的 Selector 才是当前运行态真实存在的策略组。
        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            RouteScriptBuilder.StaticGroupName,
            RouteScriptBuilder.DirectStaticExitName,
            RouteScriptBuilder.DialerStaticExitName,
            RouteScriptBuilder.LegacyStaticExitName
        };
        var safeSelectors = clash.ProxySelections
            .Where(item => !reserved.Contains(item.GroupName) &&
                           !string.IsNullOrWhiteSpace(item.CurrentSelection) &&
                           !item.CurrentSelection.Equals("未选择", StringComparison.Ordinal) &&
                           !reserved.Contains(item.CurrentSelection) &&
                           !item.CurrentSelection.Equals(item.GroupName, StringComparison.Ordinal) &&
                           item.Members.Contains(item.CurrentSelection, StringComparer.Ordinal) &&
                           !item.Members.Any(reserved.Contains) &&
                           !item.Members.Contains(item.GroupName, StringComparer.Ordinal))
            .GroupBy(item => item.GroupName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (safeSelectors.Length == 0) return [];

        var rules = ReadRuntimeRules(clash.RuntimeConfigPath);
        var matchPolicy = rules
            .Select(SplitRule)
            .Where(parts => parts.Length >= 2 && parts[0].Equals("MATCH", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .LastOrDefault();
        var references = safeSelectors.ToDictionary(
            selector => selector.GroupName,
            selector => rules.Count(rule => SplitRule(rule).Skip(1)
                .Contains(selector.GroupName, StringComparer.Ordinal)),
            StringComparer.Ordinal);

        return safeSelectors
            .Select((selector, index) => new { selector, index })
            .OrderByDescending(item => item.selector.GroupName.Equals(currentProfileName, StringComparison.Ordinal))
            .ThenByDescending(item => item.selector.GroupName.Equals(matchPolicy, StringComparison.Ordinal))
            .ThenByDescending(item => references[item.selector.GroupName])
            .ThenBy(item => item.index)
            .Select(item => item.selector)
            .ToArray();
    }

    internal static IReadOnlyList<string> ReadRuntimeRules(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
                !root.Children.TryGetValue(new YamlScalarNode("rules"), out var rulesNode) ||
                rulesNode is not YamlSequenceNode rules) return [];
            return rules.Children.OfType<YamlScalarNode>()
                .Select(rule => rule.Value)
                .Where(rule => !string.IsNullOrWhiteSpace(rule))
                .Cast<string>()
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return [];
        }
    }

    private static string[] SplitRule(string rule)
        => rule.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static RuntimeSettings ReadRuntimeSettings(string path)
    {
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root) throw new InvalidDataException("clash-verge.yaml 无效。");
        var pipe = Scalar(root, "external-controller-pipe") ?? string.Empty;
        var mode = Scalar(root, "mode") ?? "unknown";
        var tun = root.Children.TryGetValue(new YamlScalarNode("tun"), out var tunNode) && tunNode is YamlMappingNode tunMap &&
                  bool.TryParse(Scalar(tunMap, "enable"), out var enabled) && enabled;
        // 本地入站与 store-selected 仅作为链式验证和技术详情的只读事实，不改写用户全局配置。
        var profile = root.Children.TryGetValue(new YamlScalarNode("profile"), out var profileNode) &&
                      profileNode is YamlMappingNode profileMap
            ? profileMap
            : null;
        return new(pipe, mode, tun)
        {
            MixedPort = ReadPort(root, "mixed-port"),
            HttpPort = ReadPort(root, "http-port") ?? ReadPort(root, "port"),
            SocksPort = ReadPort(root, "socks-port"),
            StoreSelected = profile is null ? null : ReadBoolean(profile, "store-selected")
        };
    }

    private static bool IsSystemProxyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return key?.GetValue("ProxyEnable") is int enabled && enabled != 0;
    }

    private static bool DetectSystemTunnel()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel);
        }
        catch (NetworkInformationException) { return false; }
    }

    private static DateTime? SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }

    private static string? Scalar(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    private static int? ReadPort(YamlMappingNode mapping, string key)
        => int.TryParse(Scalar(mapping, key), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : null;

    private static bool? ReadBoolean(YamlMappingNode mapping, string key)
        => bool.TryParse(Scalar(mapping, key), out var value) ? value : null;

    public sealed record RuntimeSettings(string ControllerPipe, string Mode, bool TunEnabled)
    {
        public int? MixedPort { get; init; }
        public int? HttpPort { get; init; }
        public int? SocksPort { get; init; }
        public bool? StoreSelected { get; init; }
    }
}

public sealed class ClashDetectionException(FailureCode code, string message) : Exception(message)
{
    public FailureCode FailureCode { get; } = code;
}
