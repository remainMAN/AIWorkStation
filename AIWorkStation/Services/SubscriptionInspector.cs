using System.Net;
using System.Net.Sockets;
using AIWorkStation.Models;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace AIWorkStation.Services;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class SubscriptionInspector
{
    private static readonly HashSet<string> KnownProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "ss", "ssr", "vmess", "vless", "trojan", "socks5", "http", "hysteria2", "tuic"
    };

    private readonly IDnsResolver _dnsResolver;
    private readonly int _maxDnsConcurrency;
    private readonly TimeSpan _dnsTimeout;
    private readonly IDeserializer _deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public SubscriptionInspector(IDnsResolver? dnsResolver = null, int maxDnsConcurrency = 8, TimeSpan? dnsTimeout = null)
    {
        _dnsResolver = dnsResolver ?? new SystemDnsResolver();
        _maxDnsConcurrency = Math.Max(1, maxDnsConcurrency);
        _dnsTimeout = dnsTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<SubscriptionInfo> InspectAsync(string dataDirectory, CancellationToken cancellationToken = default)
    {
        var profilesPath = Path.Combine(dataDirectory, "profiles.yaml");
        if (!File.Exists(profilesPath)) throw new FileNotFoundException("profiles.yaml 不存在。", profilesPath);

        ProfilesDocument profiles;
        using (var reader = File.OpenText(profilesPath))
            profiles = _deserializer.Deserialize<ProfilesDocument>(reader) ?? new ProfilesDocument();

        if (string.IsNullOrWhiteSpace(profiles.Current)) throw new InvalidDataException("profiles.yaml.current 为空。");
        var duplicateUid = profiles.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Uid))
            .GroupBy(item => item.Uid!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateUid is not null)
            throw new InvalidOperationException("Clash 配置中存在重复 UID，当前无法正确识别。");
        var current = profiles.Items.SingleOrDefault(item => string.Equals(item.Uid, profiles.Current, StringComparison.Ordinal));
        if (current is null || string.IsNullOrWhiteSpace(current.File))
            throw new InvalidDataException("无法从 current UID 定位当前 Profile item。");

        var profilesDirectory = Path.Combine(dataDirectory, "profiles");
        var profilePath = SafeProfilePath(profilesDirectory, current.File);
        if (!File.Exists(profilePath)) throw new FileNotFoundException("当前订阅文件不存在。", profilePath);

        var ownership = InspectExtensionOwnership(dataDirectory, profiles, current, out var scriptUid, out var scriptPath);
        var nodes = await ReadNodesAsync(profilePath, cancellationToken);
        return new SubscriptionInfo(
            current.Uid!,
            string.IsNullOrWhiteSpace(current.Name) ? current.Uid! : current.Name,
            current.File,
            profilePath,
            FileHash.Sha256(profilesPath),
            nodes,
            ownership,
            scriptUid,
            scriptPath,
            scriptPath is not null && File.Exists(scriptPath) ? FileHash.Sha256(scriptPath) : null);
    }

    public async Task<IReadOnlyList<ProxyNodeInfo>> ReadNodesAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        using var input = File.OpenText(profilePath);
        var yaml = new YamlStream();
        yaml.Load(input);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("订阅 YAML 根节点无效。");
        if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxiesNode) || proxiesNode is not YamlSequenceNode proxies)
            return [];

        var raw = new List<(string Name, string Protocol, string Server)>();
        foreach (var entry in proxies.Children.OfType<YamlMappingNode>())
        {
            var name = Scalar(entry, "name") ?? "未命名节点";
            var type = Scalar(entry, "type") ?? "unknown";
            var server = Scalar(entry, "server") ?? string.Empty;
            raw.Add((name, KnownProtocols.Contains(type) ? type.ToLowerInvariant() : "Unknown", server));
        }

        using var concurrency = new SemaphoreSlim(_maxDnsConcurrency);
        var tasks = raw.Select(async node =>
        {
            var addresses = await ResolveServerAsync(node.Server, concurrency, cancellationToken);
            return new ProxyNodeInfo(node.Name, node.Protocol, node.Server, addresses);
        });
        return await Task.WhenAll(tasks);
    }

    public static string SafeProfilePath(string profilesDirectory, string fileName)
    {
        var root = Path.GetFullPath(profilesDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(profilesDirectory, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Profile 文件路径越界。");
        return path;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveServerAsync(string server, SemaphoreSlim concurrency, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(server, out var address)) return [address];
        if (string.IsNullOrWhiteSpace(server)) return [];
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_dnsTimeout);
            try { return await _dnsResolver.ResolveAsync(server, timeout.Token); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return []; }
        }
        finally { concurrency.Release(); }
    }

    private static ExtensionOwnership InspectExtensionOwnership(
        string dataDirectory,
        ProfilesDocument profiles,
        ProfileItem current,
        out string? scriptUid,
        out string? scriptPath)
    {
        scriptUid = current.Option?.Script;
        scriptPath = null;

        var references = new (string? Uid, string Type)[]
        {
            (current.Option?.Merge, "merge"),
            (current.Option?.Script, "script"),
            (current.Option?.Rules, "rules"),
            (current.Option?.Proxies, "proxies"),
            (current.Option?.Groups, "groups")
        };
        // AI WorkStation 只管理当前活动 Profile 真正引用的 Extension。
        // 其他未使用 Profile 的自定义脚本不能阻塞当前配置。
        foreach (var reference in references.Where(reference => HasReference(reference.Uid)))
        {
            var item = profiles.Items.SingleOrDefault(candidate =>
                string.Equals(candidate.Uid, reference.Uid, StringComparison.Ordinal) &&
                string.Equals(candidate.Type, reference.Type, StringComparison.OrdinalIgnoreCase));
            if (item is null || string.IsNullOrWhiteSpace(item.File)) return ExtensionOwnership.UnknownUserLogic;
            var path = SafeProfilePath(Path.Combine(dataDirectory, "profiles"), item.File);
            if (!File.Exists(path)) return ExtensionOwnership.UnknownUserLogic;
            if (reference.Type.Equals("script", StringComparison.OrdinalIgnoreCase))
                scriptPath = path;
            else if (!IsCanonicalEmptyExtension(path, File.ReadAllText(path)))
                return ExtensionOwnership.UnknownUserLogic;
        }

        foreach (var global in new[] { Path.Combine(dataDirectory, "Merge.yaml"), Path.Combine(dataDirectory, "Script.js") })
        {
            if (File.Exists(global) && !IsCanonicalEmptyExtension(global, File.ReadAllText(global)))
                return ExtensionOwnership.UnknownUserLogic;
        }

        if (!HasReference(scriptUid)) return ExtensionOwnership.NoneOrEmpty;
        if (scriptPath is null) return ExtensionOwnership.UnknownUserLogic;
        var script = File.ReadAllText(scriptPath);
        // marker 只能出现在规范头部；结构不完全匹配时按未知用户逻辑处理，绝不覆盖。
        if (RouteScriptBuilder.IsStrictlyOwnedScript(script)) return ExtensionOwnership.AIWorkStationManaged;
        return IsCanonicalEmptyExtension(scriptPath, script) ? ExtensionOwnership.NoneOrEmpty : ExtensionOwnership.UnknownUserLogic;
    }

    private static bool HasReference(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool IsCanonicalEmptyExtension(string path, string content)
    {
        var lines = content.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("//", StringComparison.Ordinal))
            .ToArray();
        if (Path.GetExtension(path).Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            var code = string.Join("", lines).Replace(" ", string.Empty, StringComparison.Ordinal);
            return code is "" or
                "functionmain(config,profileName){returnconfig;}" or
                "functionmain(config){returnconfig;}" or
                "functionmain(config,profileName){}" or
                "functionmain(config){}";
        }
        if (lines.Length == 0) return true;
        var semantic = string.Join("", lines).Replace(" ", string.Empty, StringComparison.Ordinal);
        return semantic is "prepend:[]append:[]delete:[]" or "profile:store-selected:true";
    }

    private static string? Scalar(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;
}
