using System.Security.Cryptography;
using System.Text;
using AIWorkStation.Models;
using YamlDotNet.RepresentationModel;

namespace AIWorkStation.Services;

public sealed record ProfileBindingPlan(string ScriptUid, string ScriptPath, byte[]? UpdatedProfilesBytes)
{
    public bool ProfilesChanged => UpdatedProfilesBytes is not null;
}

public sealed class ProfileBindingService
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public ProfileBindingPlan Prepare(ClashInfo clash, SubscriptionInfo subscription)
    {
        if (subscription.ExtensionOwnership == ExtensionOwnership.AIWorkStationManaged &&
            !string.IsNullOrWhiteSpace(subscription.ScriptUid) && !string.IsNullOrWhiteSpace(subscription.ScriptPath))
            return new(subscription.ScriptUid, subscription.ScriptPath, null);

        // 当前 Profile 已绑定标准空脚本时直接接管该文件，避免制造第二个 UID 和遗留空项。
        if (subscription.ExtensionOwnership == ExtensionOwnership.NoneOrEmpty &&
            !string.IsNullOrWhiteSpace(subscription.ScriptUid) && !string.IsNullOrWhiteSpace(subscription.ScriptPath))
            return new(subscription.ScriptUid, subscription.ScriptPath, null);

        if (subscription.ExtensionOwnership == ExtensionOwnership.UnknownUserLogic)
            throw new InvalidOperationException("未知自定义 Extension 不允许绑定。");

        var uid = CreateScriptUid();
        var scriptPath = SubscriptionInspector.SafeProfilePath(clash.ProfilesDirectory, uid + ".js");
        using var reader = File.OpenText(clash.ProfilesPath);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("profiles.yaml 根节点无效。");
        if (!root.Children.TryGetValue(new YamlScalarNode("items"), out var itemsNode) || itemsNode is not YamlSequenceNode items)
            throw new InvalidDataException("profiles.yaml.items 无效。");

        var current = items.Children.OfType<YamlMappingNode>().SingleOrDefault(item => Scalar(item, "uid") == subscription.Uid)
            ?? throw new InvalidDataException("无法定位当前 Profile item。");
        YamlMappingNode option;
        if (!current.Children.TryGetValue(new YamlScalarNode("option"), out var optionNode) || optionNode is not YamlMappingNode existingOption)
        {
            option = new YamlMappingNode();
            current.Children[new YamlScalarNode("option")] = option;
        }
        else option = existingOption;
        option.Children[new YamlScalarNode("script")] = new YamlScalarNode(uid);

        var scriptItem = new YamlMappingNode
        {
            { "uid", uid },
            { "type", "script" },
            { "name", "AI WorkStation" },
            { "file", uid + ".js" },
            { "updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        items.Add(scriptItem);

        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return new(uid, scriptPath, new UTF8Encoding(false).GetBytes(writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    public static string CreateScriptUid()
    {
        Span<byte> bytes = stackalloc byte[11];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[12];
        chars[0] = 's';
        for (var index = 0; index < bytes.Length; index++) chars[index + 1] = Alphabet[bytes[index] % Alphabet.Length];
        return new string(chars);
    }

    private static string? Scalar(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;
}
