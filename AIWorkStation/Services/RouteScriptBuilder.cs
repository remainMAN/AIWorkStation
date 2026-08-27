using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIWorkStation.Models;
using YamlDotNet.RepresentationModel;

namespace AIWorkStation.Services;

public sealed class RouteScriptBuilder
{
    private static readonly JsonSerializerOptions ScriptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public const string ManagedMarker = "AIWORKSTATION MANAGED";
    public const string ManagedHeader = "// AIWORKSTATION MANAGED";
    public const string ManagedVersionHeader = "// VERSION: 2";
    public const string LegacyManagedVersionHeader = "// VERSION: 1";
    public const string LegacyStaticExitName = "AI静态出口";
    public const string DirectStaticExitName = "AI静态出口-直连";
    public const string DialerStaticExitName = "AI静态出口-链式";
    public const string StaticExitName = DirectStaticExitName;
    public const string StaticGroupName = "AI静态链";

    public string Build(RouteConfiguration route)
    {
        route.StaticExit.Validate();
        if (route.Targets.Count == 0) throw new ArgumentException("至少需要一个目标程序。", nameof(route));
        var targets = route.Targets
            .GroupBy(target => target.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(target => target.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 同一份静态代理凭据生成两条逻辑路径；直连出口永远不携带 dialer-proxy。
        // 只有检测到当前运行态中可安全引用的主策略组时，才附加链式出口。
        var directProxy = CreateProxy(route, DirectStaticExitName);
        var dialerGroup = SafeDialerProxyGroup(route.DialerProxyGroup);
        if (route.TransportMode == StaticTransportMode.DialerProxy && dialerGroup is null)
            throw new InvalidOperationException("没有可安全使用的 Clash 前置策略组。");
        Dictionary<string, object?>? dialerProxy = null;
        if (dialerGroup is not null)
        {
            dialerProxy = CreateProxy(route, DialerStaticExitName);
            dialerProxy["dialer-proxy"] = dialerGroup;
        }

        var rules = new List<string>();
        foreach (var target in targets)
            rules.Add($"PROCESS-NAME,{target.ExecutableName},{StaticGroupName}");

        // Selector 的首项是重启后的持久回退，因此必须与本次最终选定的 transport 一致。
        string[] exits = dialerProxy is null
            ? [DirectStaticExitName]
            : route.TransportMode == StaticTransportMode.DialerProxy
                ? [DialerStaticExitName, DirectStaticExitName]
                : [DirectStaticExitName, DialerStaticExitName];

        var builder = new StringBuilder();
        builder.AppendLine(ManagedHeader);
        builder.AppendLine(ManagedVersionHeader);
        builder.AppendLine("// 此文件由 AI WorkStation 完整维护，请勿手动修改。");
        builder.AppendLine();
        builder.AppendLine("function main(config, profileName) {");
        builder.AppendLine("  config.proxies = Array.isArray(config.proxies) ? config.proxies : [];");
        builder.AppendLine("  config['proxy-groups'] = Array.isArray(config['proxy-groups']) ? config['proxy-groups'] : [];");
        builder.AppendLine("  config.rules = Array.isArray(config.rules) ? config.rules : [];");
        builder.Append("  const aiDirectProxy = ").Append(SerializeScriptValue(directProxy)).AppendLine(";");
        if (dialerProxy is not null) builder.Append("  const aiDialerProxy = ").Append(SerializeScriptValue(dialerProxy)).AppendLine(";");
        builder.Append("  const aiGroup = ").Append(SerializeScriptValue(new { name = StaticGroupName, type = "select", proxies = exits })).AppendLine(";");
        builder.Append("  const aiRules = ").Append(SerializeScriptValue(rules)).AppendLine(";");
        builder.AppendLine($"  const aiManagedNames = {SerializeScriptValue(new[] { LegacyStaticExitName, DirectStaticExitName, DialerStaticExitName })};");
        builder.AppendLine("  config.proxies = config.proxies.filter(p => p && !aiManagedNames.includes(p.name));");
        builder.AppendLine($"  config['proxy-groups'] = config['proxy-groups'].filter(g => g && g.name !== {SerializeScriptValue(StaticGroupName)});");
        builder.AppendLine($"  config.rules = config.rules.filter(r => typeof r !== 'string' || !r.endsWith(',' + {SerializeScriptValue(StaticGroupName)}));");
        builder.AppendLine("  config.proxies.push(aiDirectProxy);");
        if (dialerProxy is not null) builder.AppendLine("  config.proxies.push(aiDialerProxy);");
        builder.AppendLine("  config['proxy-groups'].push(aiGroup);");
        builder.AppendLine("  config.rules = aiRules.concat(config.rules);");
        builder.AppendLine("  return config;");
        builder.AppendLine("}");
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public string BuildRuntimeCandidate(
        string currentEffectiveYaml,
        RouteConfiguration route,
        IEnumerable<string>? validationDomains = null)
    {
        route.StaticExit.Validate();
        using var reader = new StringReader(currentEffectiveYaml);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("当前 Effective Config 根节点无效。");

        var proxies = EnsureSequence(root, "proxies");
        foreach (var name in new[] { LegacyStaticExitName, DirectStaticExitName, DialerStaticExitName }) RemoveNamed(proxies, name);
        proxies.Add(CreateYamlProxy(route, DirectStaticExitName));
        var dialerGroup = SafeDialerProxyGroup(route.DialerProxyGroup);
        if (route.TransportMode == StaticTransportMode.DialerProxy && dialerGroup is null)
            throw new InvalidOperationException("没有可安全使用的 Clash 前置策略组。");
        if (dialerGroup is not null)
        {
            var chained = CreateYamlProxy(route, DialerStaticExitName);
            chained.Add("dialer-proxy", dialerGroup);
            proxies.Add(chained);
        }

        var groups = EnsureSequence(root, "proxy-groups");
        RemoveNamed(groups, StaticGroupName);
        groups.Add(new YamlMappingNode
        {
            { "name", StaticGroupName },
            { "type", "select" },
            { "proxies", new YamlSequenceNode(dialerGroup is null
                ? [new YamlScalarNode(DirectStaticExitName)]
                : route.TransportMode == StaticTransportMode.DialerProxy
                    ? [new YamlScalarNode(DialerStaticExitName), new YamlScalarNode(DirectStaticExitName)]
                    : [new YamlScalarNode(DirectStaticExitName), new YamlScalarNode(DialerStaticExitName)]) }
        });

        var rules = EnsureSequence(root, "rules");
        var originalRules = rules.Children
            .Where(node => node is not YamlScalarNode scalar ||
                           scalar.Value is null ||
                           !scalar.Value.EndsWith("," + StaticGroupName, StringComparison.Ordinal))
            .ToArray();
        rules.Children.Clear();
        // 仅临时 Runtime 候选加入公网 IP 查询域名，最终 Extension 不持久化这些验证规则。
        foreach (var domain in (validationDomains ?? []).Where(IsSafeDomain).Distinct(StringComparer.OrdinalIgnoreCase))
            rules.Add($"DOMAIN,{domain},{StaticGroupName}");
        foreach (var target in route.Targets
                     .GroupBy(item => item.ExecutableName, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderBy(item => item.ExecutableName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase))
            rules.Add($"PROCESS-NAME,{target.ExecutableName},{StaticGroupName}");
        foreach (var originalRule in originalRules) rules.Add(originalRule);

        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static bool IsStrictlyOwnedScript(string content)
        => IsCurrentManagedScript(content) || IsLegacySingleExitV1(content);

    private static bool IsCurrentManagedScript(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        var index = 0;
        if (!Take(ManagedHeader) || !TakeVersion() ||
            !Take("// 此文件由 AI WorkStation 完整维护，请勿手动修改。") || !Take(string.Empty) ||
            !Take("function main(config, profileName) {") ||
            !Take("  config.proxies = Array.isArray(config.proxies) ? config.proxies : [];") ||
            !Take("  config['proxy-groups'] = Array.isArray(config['proxy-groups']) ? config['proxy-groups'] : [];") ||
            !Take("  config.rules = Array.isArray(config.rules) ? config.rules : [];")) return false;

        if (!TakeJson("  const aiDirectProxy = ", IsDirectProxy)) return false;
        var hasDialer = index < lines.Length && lines[index].StartsWith("  const aiDialerProxy = ", StringComparison.Ordinal);
        if (hasDialer && !TakeJson("  const aiDialerProxy = ", IsDialerProxy)) return false;
        if (!TakeJson("  const aiGroup = ", IsManagedGroup) ||
            !TakeJson("  const aiRules = ", IsManagedRules) ||
            !TakeJson("  const aiManagedNames = ", IsManagedNames) ||
            !Take("  config.proxies = config.proxies.filter(p => p && !aiManagedNames.includes(p.name));") ||
            !Take($"  config['proxy-groups'] = config['proxy-groups'].filter(g => g && g.name !== {SerializeScriptValue(StaticGroupName)});") ||
            !Take($"  config.rules = config.rules.filter(r => typeof r !== 'string' || !r.endsWith(',' + {SerializeScriptValue(StaticGroupName)}));") ||
            !Take("  config.proxies.push(aiDirectProxy);")) return false;
        if (hasDialer && !Take("  config.proxies.push(aiDialerProxy);")) return false;
        return Take("  config['proxy-groups'].push(aiGroup);") &&
               Take("  config.rules = aiRules.concat(config.rules);") &&
               Take("  return config;") && Take("}") && index == lines.Length;

        bool Take(string expected)
        {
            if (index >= lines.Length || !string.Equals(lines[index], expected, StringComparison.Ordinal)) return false;
            index++;
            return true;
        }

        bool TakeVersion()
        {
            if (index >= lines.Length ||
                lines[index] is not (ManagedVersionHeader or LegacyManagedVersionHeader)) return false;
            index++;
            return true;
        }

        bool TakeJson(string prefix, Func<JsonElement, bool> predicate)
        {
            if (index >= lines.Length || !lines[index].StartsWith(prefix, StringComparison.Ordinal) || !lines[index].EndsWith(';')) return false;
            var json = lines[index][prefix.Length..^1];
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!predicate(document.RootElement)) return false;
            }
            catch (JsonException) { return false; }
            index++;
            return true;
        }
    }

    private static bool IsLegacySingleExitV1(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        var index = 0;
        if (!Take(ManagedHeader) || !Take(LegacyManagedVersionHeader) ||
            !Take("// 此文件由 AI WorkStation 完整维护，请勿手动修改。") || !Take(string.Empty) ||
            !Take("function main(config, profileName) {") ||
            !Take("  config.proxies = Array.isArray(config.proxies) ? config.proxies : [];") ||
            !Take("  config['proxy-groups'] = Array.isArray(config['proxy-groups']) ? config['proxy-groups'] : [];") ||
            !Take("  config.rules = Array.isArray(config.rules) ? config.rules : [];") ||
            !TakeJson("  const aiProxy = ", IsLegacyProxy) ||
            !TakeJson("  const aiGroup = ", IsLegacyGroup) ||
            !TakeJson("  const aiRules = ", IsManagedRules) ||
            !TakeJson("  const aiManagedNames = ", IsLegacyManagedNames) ||
            !Take("  config.proxies = config.proxies.filter(p => p && !aiManagedNames.includes(p.name));") ||
            !Take($"  config['proxy-groups'] = config['proxy-groups'].filter(g => g && g.name !== {SerializeScriptValue(StaticGroupName)});") ||
            !Take($"  config.rules = config.rules.filter(r => typeof r !== 'string' || !r.endsWith(',' + {SerializeScriptValue(StaticGroupName)}));") ||
            !Take("  config.proxies.push(aiProxy);") ||
            !Take("  config['proxy-groups'].push(aiGroup);") ||
            !Take("  config.rules = aiRules.concat(config.rules);") ||
            !Take("  return config;") || !Take("}")) return false;
        return index == lines.Length;

        bool Take(string expected)
        {
            if (index >= lines.Length || !string.Equals(lines[index], expected, StringComparison.Ordinal)) return false;
            index++;
            return true;
        }

        bool TakeJson(string prefix, Func<JsonElement, bool> predicate)
        {
            if (index >= lines.Length || !lines[index].StartsWith(prefix, StringComparison.Ordinal) || !lines[index].EndsWith(';')) return false;
            try
            {
                using var document = JsonDocument.Parse(lines[index][prefix.Length..^1]);
                if (!predicate(document.RootElement)) return false;
            }
            catch (JsonException) { return false; }
            index++;
            return true;
        }
    }

    private static YamlSequenceNode EnsureSequence(YamlMappingNode root, string key)
    {
        var yamlKey = new YamlScalarNode(key);
        if (root.Children.TryGetValue(yamlKey, out var existing) && existing is YamlSequenceNode sequence) return sequence;
        var created = new YamlSequenceNode();
        root.Children[yamlKey] = created;
        return created;
    }

    private static void RemoveNamed(YamlSequenceNode sequence, string name)
    {
        var matches = sequence.Children.OfType<YamlMappingNode>()
            .Where(mapping => mapping.Children.TryGetValue(new YamlScalarNode("name"), out var value) &&
                              string.Equals((value as YamlScalarNode)?.Value, name, StringComparison.Ordinal))
            .Cast<YamlNode>()
            .ToArray();
        foreach (var match in matches) sequence.Children.Remove(match);
    }

    public static string SelectedExitName(RouteConfiguration route)
        => route.TransportMode == StaticTransportMode.DialerProxy ? DialerStaticExitName : DirectStaticExitName;

    private static Dictionary<string, object?> CreateProxy(RouteConfiguration route, string name)
    {
        var proxy = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["type"] = route.StaticExit.Protocol == StaticProxyProtocol.Socks5 ? "socks5" : "http",
            ["server"] = route.StaticExit.Server,
            ["port"] = route.StaticExit.Port
        };
        if (!string.IsNullOrEmpty(route.StaticExit.Username)) proxy["username"] = route.StaticExit.Username;
        if (!string.IsNullOrEmpty(route.StaticExit.Password)) proxy["password"] = route.StaticExit.Password;
        return proxy;
    }

    private static YamlMappingNode CreateYamlProxy(RouteConfiguration route, string name)
    {
        var proxy = new YamlMappingNode
        {
            { "name", name },
            { "type", route.StaticExit.Protocol == StaticProxyProtocol.Socks5 ? "socks5" : "http" },
            { "server", route.StaticExit.Server },
            { "port", route.StaticExit.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        if (!string.IsNullOrEmpty(route.StaticExit.Username)) proxy.Add("username", route.StaticExit.Username);
        if (!string.IsNullOrEmpty(route.StaticExit.Password)) proxy.Add("password", route.StaticExit.Password);
        return proxy;
    }

    private static string? SafeDialerProxyGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        if (group.Equals(StaticGroupName, StringComparison.Ordinal) ||
            group.Equals(LegacyStaticExitName, StringComparison.Ordinal) ||
            group.Equals(DirectStaticExitName, StringComparison.Ordinal) ||
            group.Equals(DialerStaticExitName, StringComparison.Ordinal))
            return null;
        return group;
    }

    private static bool IsSafeDomain(string value)
        => Uri.CheckHostName(value) == UriHostNameType.Dns && !value.Contains(',');

    private static string SerializeScriptValue<T>(T value)
        => JsonSerializer.Serialize(value, ScriptJsonOptions);

    private static bool IsDirectProxy(JsonElement element)
        => element.ValueKind == JsonValueKind.Object &&
           HasOnlyProperties(element, "name", "type", "server", "port", "username", "password") &&
           element.TryGetProperty("name", out var name) && name.GetString() == DirectStaticExitName &&
           IsValidProxyCore(element) &&
           !element.TryGetProperty("dialer-proxy", out _);

    private static bool IsLegacyProxy(JsonElement element)
        => element.ValueKind == JsonValueKind.Object &&
           HasOnlyProperties(element, "name", "type", "server", "port", "username", "password") &&
           element.TryGetProperty("name", out var name) && name.GetString() == LegacyStaticExitName &&
           IsValidProxyCore(element) &&
           !element.TryGetProperty("dialer-proxy", out _);

    private static bool IsDialerProxy(JsonElement element)
        => element.ValueKind == JsonValueKind.Object &&
           HasOnlyProperties(element, "name", "type", "server", "port", "username", "password", "dialer-proxy") &&
           element.TryGetProperty("name", out var name) && name.GetString() == DialerStaticExitName &&
           IsValidProxyCore(element) &&
           element.TryGetProperty("dialer-proxy", out var dialer) &&
           dialer.ValueKind == JsonValueKind.String &&
           SafeDialerProxyGroup(dialer.GetString()) is not null;

    private static bool IsManagedGroup(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(element, "name", "type", "proxies") ||
            !element.TryGetProperty("name", out var name) || name.GetString() != StaticGroupName ||
            !element.TryGetProperty("type", out var type) || type.GetString() != "select" ||
            !element.TryGetProperty("proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Array) return false;
        var values = proxies.EnumerateArray().Select(item => item.GetString()).ToArray();
        return values is [DirectStaticExitName] or
            [DirectStaticExitName, DialerStaticExitName] or
            [DialerStaticExitName, DirectStaticExitName];
    }

    private static bool IsLegacyGroup(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !HasOnlyProperties(element, "name", "type", "proxies") ||
            !element.TryGetProperty("name", out var name) || name.GetString() != StaticGroupName ||
            !element.TryGetProperty("type", out var type) || type.GetString() != "select" ||
            !element.TryGetProperty("proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Array) return false;
        return proxies.EnumerateArray().Select(item => item.GetString()).SequenceEqual([LegacyStaticExitName]);
    }

    private static bool IsManagedRules(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return false;
        var rules = element.EnumerateArray().Select(item => item.GetString()).ToArray();
        return rules.Length > 0 && rules.All(rule => rule is not null &&
            rule.Split(',') is ["PROCESS-NAME", var executable, StaticGroupName] &&
            !string.IsNullOrWhiteSpace(executable)) &&
            rules.Distinct(StringComparer.OrdinalIgnoreCase).Count() == rules.Length;
    }

    private static bool IsManagedNames(JsonElement element)
        => element.ValueKind == JsonValueKind.Array &&
           element.EnumerateArray().Select(item => item.GetString()).SequenceEqual(
               new[] { LegacyStaticExitName, DirectStaticExitName, DialerStaticExitName });

    private static bool IsLegacyManagedNames(JsonElement element)
        => element.ValueKind == JsonValueKind.Array &&
           element.EnumerateArray().Select(item => item.GetString()).SequenceEqual([LegacyStaticExitName]);

    private static bool IsValidProxyCore(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("socks5" or "http") ||
            !element.TryGetProperty("server", out var server) ||
            server.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(server.GetString()) ||
            !element.TryGetProperty("port", out var port) ||
            !port.TryGetInt32(out var numericPort) || numericPort is < 1 or > 65535)
            return false;
        return IsOptionalString(element, "username") && IsOptionalString(element, "password");
    }

    private static bool IsOptionalString(JsonElement element, string name)
        => !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.String;

    private static bool HasOnlyProperties(JsonElement element, params string[] allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.All(names.Contains) &&
               properties.Distinct(StringComparer.Ordinal).Count() == properties.Length;
    }
}
