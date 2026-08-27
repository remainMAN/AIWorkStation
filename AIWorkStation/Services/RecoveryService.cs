using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIWorkStation.Models;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace AIWorkStation.Services;

public sealed class RecoveryService
{
    private readonly AtomicFileWriter _writer;
    private readonly ClashReloadService _reloader;
    private readonly TransactionMarkerService _markers;
    private readonly Func<string, IMihomoRuntimeClient> _pipeFactory;

    public RecoveryService(
        AtomicFileWriter? writer = null,
        ClashReloadService? reloader = null,
        TransactionMarkerService? markers = null,
        Func<string, IMihomoRuntimeClient>? pipeFactory = null)
    {
        _writer = writer ?? new AtomicFileWriter();
        _reloader = reloader ?? new ClashReloadService();
        _markers = markers ?? new TransactionMarkerService();
        _pipeFactory = pipeFactory ?? (pipePath => new MihomoNamedPipeClient(pipePath));
    }

    public static async Task<RecoveryBaseline> CaptureBaselineAsync(
        string profilesPath,
        string? extensionPath,
        IMihomoRuntimeClient runtimeClient,
        CancellationToken token = default)
        => await CaptureBaselineAsync(profilesPath, extensionPath, runtimeConfigPath: null, runtimeClient, token);

    public static async Task<RecoveryBaseline> CaptureBaselineAsync(
        string profilesPath,
        string? extensionPath,
        string? runtimeConfigPath,
        IMihomoRuntimeClient runtimeClient,
        CancellationToken token = default)
    {
        var profiles = ReadProfiles(profilesPath);
        var currentUid = profiles.Current;
        if (string.IsNullOrWhiteSpace(currentUid)) throw new InvalidDataException("当前 Profile UID 无效。");
        var current = profiles.Items.SingleOrDefault(item => string.Equals(item.Uid, currentUid, StringComparison.Ordinal))
            ?? throw new InvalidDataException("无法定位当前 Profile item。");
        var extensionHash = !string.IsNullOrWhiteSpace(extensionPath) && File.Exists(extensionPath)
            ? FileHash.Sha256(extensionPath)
            : null;
        var runtime = await CaptureRuntimeSemanticBaselineAsync(runtimeClient, token);
        if (!string.IsNullOrWhiteSpace(runtimeConfigPath) && File.Exists(runtimeConfigPath))
            runtime = runtime with
            {
                ManagedProxyDefinitionHashes = CaptureManagedProxyDefinitionHashes(runtimeConfigPath)
            };
        else
            runtime = runtime with
            {
                ManagedProxyDefinitionHashes = new Dictionary<string, string>(StringComparer.Ordinal)
            };
        return new(profilesPath, currentUid, current.Option?.Script, extensionPath, extensionHash, runtime);
    }

    public static async Task<RuntimeSemanticBaseline> CaptureRuntimeSemanticBaselineAsync(
        IMihomoRuntimeClient runtimeClient,
        CancellationToken token = default)
    {
        // Controller 可访问只是第一步；恢复判定还必须比较受管对象、选择和规则。
        using var configs = await runtimeClient.GetConfigsAsync(token);
        if (configs.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Mihomo /configs 响应无效。");

        using var proxiesDocument = await runtimeClient.GetProxiesAsync(token);
        if (!TryGetProperty(proxiesDocument.RootElement, "proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Mihomo /proxies 响应无效。");

        var managedProxyNames = new[]
            {
                RouteScriptBuilder.LegacyStaticExitName,
                RouteScriptBuilder.DirectStaticExitName,
                RouteScriptBuilder.DialerStaticExitName
            }
            .Where(name => HasNamedProperty(proxies, name))
            .ToArray();
        var groupExists = TryGetNamedProperty(proxies, RouteScriptBuilder.StaticGroupName, out var group) &&
                          group.ValueKind == JsonValueKind.Object;
        var selection = groupExists ? ReadString(group, "now") : null;
        var members = groupExists && TryGetProperty(group, "all", out var all) && all.ValueKind == JsonValueKind.Array
            ? all.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray()
            : [];

        using var rulesDocument = await runtimeClient.GetRulesAsync(token);
        if (!TryGetProperty(rulesDocument.RootElement, "rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Mihomo /rules 响应无效。");
        var managedRules = new List<string>();
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind == JsonValueKind.String)
            {
                var value = rule.GetString() ?? string.Empty;
                if (value.EndsWith("," + RouteScriptBuilder.StaticGroupName, StringComparison.Ordinal))
                    managedRules.Add(value);
                continue;
            }
            if (rule.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(rule, "proxy"), RouteScriptBuilder.StaticGroupName, StringComparison.Ordinal)) continue;
            managedRules.Add(JsonSerializer.Serialize(new[]
            {
                ReadJsonValue(rule, "type"),
                ReadJsonValue(rule, "payload"),
                RouteScriptBuilder.StaticGroupName
            }));
        }

        return new(managedProxyNames, groupExists, selection, members, managedRules)
        {
            // /configs 的规范 JSON 作为最小有效配置事实，与受管对象语义共同判断是否等价恢复。
            // Mihomo /proxies 不保证提供 server、port 或凭据，不能用缺失字段制造定义已恢复的假证明。
            // 持久化定义继续由 Runtime YAML / Extension Script 验证；这里仅比较 Controller 可稳定观察的对象语义。
            ConfigsSha256 = FileHash.Sha256(Encoding.UTF8.GetBytes(CanonicalJson(configs.RootElement)))
        };
    }

    public async Task<bool> RecoverAsync(TransactionMarker marker, CancellationToken token = default)
    {
        try
        {
            var manifest = BackupService.ReadManifest(marker.BackupDirectory);
            foreach (var entry in manifest.Entries)
            {
                if (entry.Existed)
                {
                    var encrypted = await File.ReadAllBytesAsync(entry.BackupFile, token);
                    byte[]? bytes = null;
                    try
                    {
                        bytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
                        await _writer.WriteAsync(entry.TargetPath, bytes, token);
                    }
                    finally
                    {
                        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                    }
                }
                else if (File.Exists(entry.TargetPath)) File.Delete(entry.TargetPath);
            }
            foreach (var entry in manifest.Entries)
            {
                if (entry.Existed && (!File.Exists(entry.TargetPath) || FileHash.Sha256(entry.TargetPath) != entry.OriginalSha256)) return false;
                if (!entry.Existed && File.Exists(entry.TargetPath)) return false;
            }
            // 文件恢复成功并不代表 Mihomo 运行态已经恢复。
            // Reload 只有在进程、Runtime 和 Controller 均可访问时才返回成功。
            if (!await _reloader.RestartAsync(marker.ClashExecutable, marker.RuntimeConfigPath, token)) return false;
            if (manifest.RecoveryBaseline is not null &&
                !await VerifyRecoveryBaselineAsync(marker, manifest.RecoveryBaseline, token)) return false;

            // 先消除事务 Marker，再清理可孤立重试的加密备份，避免 Marker 指向已删除 workspace。
            _markers.Delete();
            if (File.Exists(_markers.MarkerPath)) return false;
            TryDeleteDirectory(marker.BackupDirectory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                    InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or
                                    CryptographicException or JsonException or TimeoutException or OperationCanceledException or
                                    YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    public async Task<bool?> RecoverPendingAsync(CancellationToken token = default)
    {
        var read = _markers.ReadSafe();
        if (read.Status == TransactionMarkerReadStatus.None) return null;
        if (read.Status == TransactionMarkerReadStatus.Corrupt || read.Marker is null) return false;
        return await RecoverAsync(read.Marker, token);
    }

    private async Task<bool> VerifyRecoveryBaselineAsync(
        TransactionMarker marker,
        RecoveryBaseline baseline,
        CancellationToken token)
    {
        if (!File.Exists(baseline.ProfilesPath)) return false;
        var profiles = ReadProfiles(baseline.ProfilesPath);
        if (!string.Equals(profiles.Current, baseline.CurrentProfileUid, StringComparison.Ordinal)) return false;
        var current = profiles.Items.SingleOrDefault(item => string.Equals(item.Uid, baseline.CurrentProfileUid, StringComparison.Ordinal));
        if (!string.Equals(current?.Option?.Script, baseline.ScriptUid, StringComparison.Ordinal)) return false;

        if (baseline.ExtensionPath is null)
        {
            if (baseline.ExtensionSha256 is not null) return false;
        }
        else if (baseline.ExtensionSha256 is null)
        {
            if (File.Exists(baseline.ExtensionPath)) return false;
        }
        else if (!File.Exists(baseline.ExtensionPath) ||
                 !string.Equals(FileHash.Sha256(baseline.ExtensionPath), baseline.ExtensionSha256, StringComparison.Ordinal)) return false;

        var settings = ClashVergeDetector.ReadRuntimeSettings(marker.RuntimeConfigPath);
        var client = _pipeFactory(settings.ControllerPipe);
        if (baseline.Runtime.ManagedGroupExists &&
            !string.IsNullOrWhiteSpace(baseline.Runtime.ManagedGroupSelection))
        {
            if (client is not IMihomoApplyClient controller) return false;
            // store-selected 可能保留失败 Apply 的新选择；Recovery 必须主动选回旧 transport。
            await controller.SelectProxyAsync(
                RouteScriptBuilder.StaticGroupName,
                baseline.Runtime.ManagedGroupSelection,
                token);
        }
        var currentRuntime = await CaptureRuntimeSemanticBaselineAsync(client, token);
        currentRuntime = currentRuntime with
        {
            ManagedProxyDefinitionHashes = CaptureManagedProxyDefinitionHashes(marker.RuntimeConfigPath)
        };
        return baseline.Runtime.SemanticallyEquals(currentRuntime);
    }

    internal static IReadOnlyDictionary<string, string> CaptureManagedProxyDefinitionHashes(string runtimeConfigPath)
    {
        return CaptureManagedProxyDefinitionHashesFromYaml(File.ReadAllText(runtimeConfigPath));
    }

    internal static IReadOnlyDictionary<string, string> CaptureManagedProxyDefinitionHashesFromYaml(string runtimeYaml)
    {
        using var reader = new StringReader(runtimeYaml);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("Mihomo Runtime YAML 无效。");
        if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxiesNode) ||
            proxiesNode is not YamlSequenceNode proxies)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var managed = new HashSet<string>(StringComparer.Ordinal)
        {
            RouteScriptBuilder.LegacyStaticExitName,
            RouteScriptBuilder.DirectStaticExitName,
            RouteScriptBuilder.DialerStaticExitName
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in proxies.Children.OfType<YamlMappingNode>())
        {
            if (!mapping.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode) ||
                nameNode is not YamlScalarNode name || name.Value is null || !managed.Contains(name.Value)) continue;
            // Manifest 只保存定义的 SHA-256，不保存 server/username/password 原值。
            result[name.Value] = FileHash.Sha256(Encoding.UTF8.GetBytes(CanonicalYaml(mapping)));
        }
        return result;
    }

    private static ProfilesDocument ReadProfiles(string profilesPath)
        => new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
               .Deserialize<ProfilesDocument>(File.ReadAllText(profilesPath))
           ?? throw new InvalidDataException("profiles.yaml 无效。");

    private static bool TryGetNamedProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.Ordinal))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static bool HasNamedProperty(JsonElement element, string name)
        => TryGetNamedProperty(element, name, out _);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadJsonValue(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
    }

    private static string CanonicalJson(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };

    private static string CanonicalYaml(YamlNode node)
        => node switch
        {
            YamlScalarNode scalar => JsonSerializer.Serialize(scalar.Value ?? string.Empty),
            YamlSequenceNode sequence => "[" + string.Join(",", sequence.Children.Select(CanonicalYaml)) + "]",
            YamlMappingNode mapping => "{" + string.Join(",", mapping.Children
                .Select(item => new
                {
                    Key = (item.Key as YamlScalarNode)?.Value ?? CanonicalYaml(item.Key),
                    Value = item.Value
                })
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => JsonSerializer.Serialize(item.Key) + ":" + CanonicalYaml(item.Value))) + "}",
            _ => node.ToString()
        };

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
