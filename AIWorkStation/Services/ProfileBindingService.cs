using System.Security.Cryptography;
using System.Text;
using AIWorkStation.Models;
using YamlDotNet.RepresentationModel;

namespace AIWorkStation.Services;

public sealed record ProfileBindingPlan(string ScriptUid, string ScriptPath, byte[]? UpdatedProfilesBytes)
{
    public bool ProfilesChanged => UpdatedProfilesBytes is not null;
}

public sealed class ProfileBindingTargetChangedException(string message) : IOException(message);

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

    public byte[]? PatchLatestProfiles(
        string profilesPath,
        string expectedCurrentUid,
        ProfileBindingPlan binding)
    {
        using var reader = File.OpenText(profilesPath);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("profiles.yaml 根节点无效。");
        if (Scalar(root, "current") != expectedCurrentUid)
            throw new ProfileBindingTargetChangedException("Clash 退出后 Current Profile UID 已变化。");
        if (!root.Children.TryGetValue(new YamlScalarNode("items"), out var itemsNode) || itemsNode is not YamlSequenceNode items)
            throw new InvalidDataException("profiles.yaml.items 无效。");

        var currentItems = items.Children.OfType<YamlMappingNode>()
            .Where(item => Scalar(item, "uid") == expectedCurrentUid)
            .ToArray();
        if (currentItems.Length != 1)
            throw new InvalidDataException("Clash 退出后无法唯一定位当前 Profile item。");

        var changed = false;
        var current = currentItems[0];
        if (!current.Children.TryGetValue(new YamlScalarNode("option"), out var optionNode) ||
            optionNode is not YamlMappingNode option)
        {
            option = new YamlMappingNode();
            current.Children[new YamlScalarNode("option")] = option;
            changed = true;
        }
        var currentScriptUid = Scalar(option, "script");
        if (!string.IsNullOrWhiteSpace(currentScriptUid) &&
            !string.Equals(currentScriptUid, binding.ScriptUid, StringComparison.Ordinal))
            throw new ProfileBindingTargetChangedException("Clash 退出时当前 Profile 的 Script 绑定已被其他程序替换。");
        if (currentScriptUid != binding.ScriptUid)
        {
            option.Children[new YamlScalarNode("script")] = new YamlScalarNode(binding.ScriptUid);
            changed = true;
        }

        var scriptItems = items.Children.OfType<YamlMappingNode>()
            .Where(item => Scalar(item, "uid") == binding.ScriptUid)
            .ToArray();
        if (scriptItems.Length > 1)
            throw new InvalidDataException("profiles.yaml 中存在重复的 AI WorkStation Script UID。");

        var expectedFile = Path.GetFileName(binding.ScriptPath);
        if (scriptItems.Length == 0)
        {
            items.Add(new YamlMappingNode
            {
                { "uid", binding.ScriptUid },
                { "type", "script" },
                { "name", "AI WorkStation" },
                { "file", expectedFile },
                { "updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) }
            });
            changed = true;
        }
        else
        {
            var scriptItem = scriptItems[0];
            if (!string.Equals(Scalar(scriptItem, "type"), "script", StringComparison.OrdinalIgnoreCase))
                throw new ProfileBindingTargetChangedException("AI WorkStation Script UID 被非 Script 项占用。");
            var existingFile = Scalar(scriptItem, "file");
            if (!string.IsNullOrWhiteSpace(existingFile) &&
                !string.Equals(existingFile, expectedFile, StringComparison.OrdinalIgnoreCase))
                throw new ProfileBindingTargetChangedException("AI WorkStation Script UID 指向了其他文件。");
            if (!string.Equals(existingFile, expectedFile, StringComparison.OrdinalIgnoreCase))
            {
                scriptItem.Children[new YamlScalarNode("file")] = new YamlScalarNode(expectedFile);
                changed = true;
            }
        }

        if (!changed) return null;
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return new UTF8Encoding(false).GetBytes(writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
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
