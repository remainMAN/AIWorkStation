using YamlDotNet.Serialization;

namespace AIWorkStation.Models;

public sealed class ProfilesDocument
{
    [YamlMember(Alias = "current")]
    public string? Current { get; set; }

    [YamlMember(Alias = "items")]
    public List<ProfileItem> Items { get; set; } = [];
}

public sealed class ProfileItem
{
    [YamlMember(Alias = "uid")]
    public string? Uid { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "file")]
    public string? File { get; set; }

    [YamlMember(Alias = "desc")]
    public string? Description { get; set; }

    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    [YamlMember(Alias = "updated")]
    public long? Updated { get; set; }

    [YamlMember(Alias = "option")]
    public ProfileOption? Option { get; set; }

    [YamlMember(Alias = "home")]
    public string? Home { get; set; }

    [YamlMember(Alias = "selected")]
    public List<Dictionary<string, string?>>? Selected { get; set; }

    [YamlMember(Alias = "extra")]
    public Dictionary<string, object?>? Extra { get; set; }
}

public sealed class ProfileOption
{
    [YamlMember(Alias = "user_agent")]
    public string? UserAgent { get; set; }
    [YamlMember(Alias = "with_proxy")]
    public bool? WithProxy { get; set; }
    [YamlMember(Alias = "self_proxy")]
    public bool? SelfProxy { get; set; }
    [YamlMember(Alias = "update_interval")]
    public long? UpdateInterval { get; set; }
    [YamlMember(Alias = "timeout_seconds")]
    public long? TimeoutSeconds { get; set; }
    [YamlMember(Alias = "allow_auto_update")]
    public bool? AllowAutoUpdate { get; set; }
    [YamlMember(Alias = "merge")]
    public string? Merge { get; set; }
    [YamlMember(Alias = "script")]
    public string? Script { get; set; }
    [YamlMember(Alias = "rules")]
    public string? Rules { get; set; }
    [YamlMember(Alias = "proxies")]
    public string? Proxies { get; set; }
    [YamlMember(Alias = "groups")]
    public string? Groups { get; set; }
}
