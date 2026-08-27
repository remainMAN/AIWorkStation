using System.Text.Json;
using AIWorkStation.Models;
using Jint;

namespace AIWorkStation.Services;

public sealed class ScriptValidator
{
    public string Execute(string script, string inputYaml, string profileName)
    {
        var configJson = YamlValueConverter.ToJson(inputYaml);
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(3)).LimitRecursion(128));
        engine.Execute(script);
        engine.SetValue("__aiwsConfigJson", configJson);
        engine.SetValue("__aiwsProfileName", profileName);
        var result = engine.Evaluate("JSON.stringify(main(JSON.parse(__aiwsConfigJson), __aiwsProfileName))");
        if (!result.IsString()) throw new InvalidDataException("main(config, profileName) 没有返回配置对象。");
        return YamlValueConverter.JsonToYaml(result.AsString());
    }

    public void ValidateSemantics(string candidateYaml, IReadOnlyList<ApplicationTarget> targets, RouteConfiguration? route = null)
    {
        using var json = JsonDocument.Parse(YamlValueConverter.ToJson(candidateYaml));
        var root = json.RootElement;
        var proxies = RequireArray(root, "proxies");
        var groups = RequireArray(root, "proxy-groups");
        var rules = RequireArray(root, "rules");
        var direct = FindNamedObject(proxies, RouteScriptBuilder.DirectStaticExitName)
            ?? throw new InvalidDataException("候选配置缺少 AI静态出口-直连。");
        if (direct.TryGetProperty("dialer-proxy", out _)) throw new InvalidDataException("直连出口不得包含 dialer-proxy。");
        var managedGroup = FindNamedObject(groups, RouteScriptBuilder.StaticGroupName)
            ?? throw new InvalidDataException("候选配置缺少 AI静态链。");

        var chained = FindNamedObject(proxies, RouteScriptBuilder.DialerStaticExitName);
        if (chained is not null)
        {
            if (!chained.Value.TryGetProperty("dialer-proxy", out var dialer) || string.IsNullOrWhiteSpace(dialer.GetString()))
                throw new InvalidDataException("链式出口缺少安全前置策略组。");
            if (IsReserved(dialer.GetString()!)) throw new InvalidDataException("链式出口形成了代理循环。");
            if (!ContainsNamedObject(groups, dialer.GetString()!))
                throw new InvalidDataException("链式出口引用的前置策略组不在当前运行配置中。");
        }
        if (route?.DialerProxyGroup is not null)
        {
            if (chained is null || chained.Value.GetProperty("dialer-proxy").GetString() != route.DialerProxyGroup)
                throw new InvalidDataException("链式出口没有引用检测到的前置策略组。");
        }
        if (route?.TransportMode == StaticTransportMode.DialerProxy && chained is null)
            throw new InvalidDataException("当前选择链式连接，但候选配置缺少链式出口。");
        if (route is not null)
        {
            ValidateProxyDefinition(direct, route, expectedDialer: null);
            if (chained is not null) ValidateProxyDefinition(chained.Value, route, route.DialerProxyGroup);
            ValidateManagedGroupMembers(managedGroup, route);
        }

        var ruleStrings = rules.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray();
        foreach (var target in targets)
            if (!ruleStrings.Contains($"PROCESS-NAME,{target.ExecutableName},{RouteScriptBuilder.StaticGroupName}", StringComparer.Ordinal))
                throw new InvalidDataException($"缺少 {target.ExecutableName} 的进程规则。");
        var matchIndex = Array.FindIndex(ruleStrings, rule => rule.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase) || rule.Equals("MATCH", StringComparison.OrdinalIgnoreCase));
        var aiIndex = Array.FindIndex(ruleStrings, rule => rule.EndsWith("," + RouteScriptBuilder.StaticGroupName, StringComparison.Ordinal));
        if (matchIndex >= 0 && (aiIndex < 0 || aiIndex > matchIndex)) throw new InvalidDataException("目标程序规则位于 MATCH 之后。");
    }

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"候选配置的 {name} 字段无效。");
        return value;
    }

    private static bool ContainsNamedObject(JsonElement array, string name)
        => array.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("name", out var itemName) && itemName.GetString() == name);

    private static JsonElement? FindNamedObject(JsonElement array, string name)
    {
        foreach (var value in array.EnumerateArray())
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var itemName) && itemName.GetString() == name)
                return value;
        return null;
    }

    private static bool IsReserved(string name)
        => name is RouteScriptBuilder.StaticGroupName or RouteScriptBuilder.LegacyStaticExitName or
            RouteScriptBuilder.DirectStaticExitName or RouteScriptBuilder.DialerStaticExitName;

    private static void ValidateProxyDefinition(
        JsonElement proxy,
        RouteConfiguration route,
        string? expectedDialer)
    {
        var expectedType = route.StaticExit.Protocol == StaticProxyProtocol.Socks5 ? "socks5" : "http";
        if (!proxy.TryGetProperty("type", out var type) ||
            !string.Equals(type.GetString(), expectedType, StringComparison.OrdinalIgnoreCase) ||
            !proxy.TryGetProperty("server", out var server) ||
            !string.Equals(server.GetString(), route.StaticExit.Server, StringComparison.OrdinalIgnoreCase) ||
            !proxy.TryGetProperty("port", out var port) || !TryReadPort(port, out var numericPort) ||
            numericPort != route.StaticExit.Port)
            throw new InvalidDataException("受管静态代理定义与本次配置不一致。");

        var hasDialer = proxy.TryGetProperty("dialer-proxy", out var dialer);
        if (expectedDialer is null && hasDialer ||
            expectedDialer is not null && (!hasDialer || !string.Equals(dialer.GetString(), expectedDialer, StringComparison.Ordinal)))
            throw new InvalidDataException("受管静态代理的 dialer-proxy 与本次配置不一致。");
    }

    private static bool TryReadPort(JsonElement value, out int port)
        => value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out port)
            : int.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out port);

    private static void ValidateManagedGroupMembers(JsonElement group, RouteConfiguration route)
    {
        if (!group.TryGetProperty("proxies", out var members) || members.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AI静态链成员无效。");
        var actual = members.EnumerateArray().Select(item => item.GetString()).ToArray();
        var expected = string.IsNullOrWhiteSpace(route.DialerProxyGroup)
            ? new[] { RouteScriptBuilder.DirectStaticExitName }
            : route.TransportMode == StaticTransportMode.DialerProxy
                ? new[] { RouteScriptBuilder.DialerStaticExitName, RouteScriptBuilder.DirectStaticExitName }
                : new[] { RouteScriptBuilder.DirectStaticExitName, RouteScriptBuilder.DialerStaticExitName };
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("AI静态链成员顺序与最终连接方式不一致。");
    }
}
