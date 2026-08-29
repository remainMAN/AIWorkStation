using System.Text;
using AIWorkStation.Services;
using YamlDotNet.RepresentationModel;

namespace AIWorkStation.Tests;

public sealed class ProfileBindingRaceTests
{
    [Fact]
    public void ShutdownOverwrite_NoScript_PreservesLatestFieldsAndPatchesOnce()
    {
        const string yaml = """
            current: Rabcdefghijk
            selected:
              - name: FlyintPro
                now: Hongkong 016
            items:
              - uid: Rabcdefghijk
                type: remote
                name: FlyintPro
                url: https://subscription.example/redacted
                file: Rabcdefghijk.yaml
                updated: 1787991001
                option:
                  merge: mMerge0000001
                  rules: rRules0000001
                  proxies: pProxy0000001
                  groups: gGroups000001
              - uid: mMerge0000001
                type: merge
                name: User Merge
                file: mMerge0000001.yaml
            """;

        AssertPatchIsFieldPreservingAndIdempotent(
            yaml,
            "sAiws0000001",
            root =>
            {
                var selected = Assert.IsType<YamlMappingNode>(Sequence(root, "selected").Children[0]);
                Assert.Equal("Hongkong 016", Scalar(selected, "now"));
                var current = Item(root, "Rabcdefghijk");
                Assert.Equal("https://subscription.example/redacted", Scalar(current, "url"));
                Assert.Equal("1787991001", Scalar(current, "updated"));
                var option = Mapping(current, "option");
                Assert.Equal("mMerge0000001", Scalar(option, "merge"));
                Assert.Equal("rRules0000001", Scalar(option, "rules"));
                Assert.Equal("pProxy0000001", Scalar(option, "proxies"));
                Assert.Equal("gGroups000001", Scalar(option, "groups"));
                Assert.Equal("User Merge", Scalar(Item(root, "mMerge0000001"), "name"));
            });
    }

    [Fact]
    public void ShutdownOverwrite_CanonicalEmptyScript_PreservesLatestFieldsAndPatchesOnce()
    {
        const string yaml = """
            current: Rabcdefghijk
            runtime:
              enable_tun_mode: true
              mixed_port: 7897
            items:
              - uid: Rabcdefghijk
                type: remote
                name: FlyintPro
                file: Rabcdefghijk.yaml
                updated: 1787992002
                option:
                  merge: mEmpty0000001
                  rules: rEmpty0000001
                  proxies: pEmpty0000001
                  groups: gEmpty0000001
              - uid: mEmpty0000001
                type: merge
                file: mEmpty0000001.yaml
              - uid: sEmpty0000001
                type: script
                name: Empty Script
                file: sEmpty0000001.js
                updated: 1787000000
              - uid: rEmpty0000001
                type: rules
                file: rEmpty0000001.yaml
              - uid: pEmpty0000001
                type: proxies
                file: pEmpty0000001.yaml
              - uid: gEmpty0000001
                type: groups
                file: gEmpty0000001.yaml
            """;

        AssertPatchIsFieldPreservingAndIdempotent(
            yaml,
            "sEmpty0000001",
            root =>
            {
                var runtime = Mapping(root, "runtime");
                Assert.Equal("true", Scalar(runtime, "enable_tun_mode"));
                Assert.Equal("7897", Scalar(runtime, "mixed_port"));
                var current = Item(root, "Rabcdefghijk");
                Assert.Equal("1787992002", Scalar(current, "updated"));
                var script = Item(root, "sEmpty0000001");
                Assert.Equal("Empty Script", Scalar(script, "name"));
                Assert.Equal("1787000000", Scalar(script, "updated"));
                Assert.Equal("rEmpty0000001", Scalar(Mapping(current, "option"), "rules"));
            });
    }

    [Fact]
    public void ShutdownOverwrite_AiwsV2_PreservesLatestFieldsAndPatchesOnce()
    {
        const string yaml = """
            current: Rabcdefghijk
            verge:
              last_profile_name: FlyintPro
              store_selected: true
            items:
              - uid: Rabcdefghijk
                type: remote
                name: FlyintPro
                file: Rabcdefghijk.yaml
                updated: 1787993003
                option:
                  merge: mUser00000001
              - uid: sPrevious0001
                type: script
                name: Previous User Script
                file: sPrevious0001.js
              - uid: sAiwsV200001
                type: script
                name: AI WorkStation
                file: sAiwsV200001.js
                updated: 1787003003
                aiws-version: 2
              - uid: mUser00000001
                type: merge
                name: User Merge
                file: mUser00000001.yaml
                custom-field: keep-me
            """;

        AssertPatchIsFieldPreservingAndIdempotent(
            yaml,
            "sAiwsV200001",
            root =>
            {
                var verge = Mapping(root, "verge");
                Assert.Equal("FlyintPro", Scalar(verge, "last_profile_name"));
                Assert.Equal("true", Scalar(verge, "store_selected"));
                Assert.Equal("Previous User Script", Scalar(Item(root, "sPrevious0001"), "name"));
                Assert.Equal("keep-me", Scalar(Item(root, "mUser00000001"), "custom-field"));
                var script = Item(root, "sAiwsV200001");
                Assert.Equal("2", Scalar(script, "aiws-version"));
                Assert.Equal("1787003003", Scalar(script, "updated"));
            });
    }

    [Fact]
    public void ShutdownOverwrite_CurrentUidChanged_IsRejected()
    {
        using var temp = new TempDirectory();
        var profilesPath = temp.File("profiles.yaml");
        File.WriteAllText(profilesPath, """
            current: Rnewprofile01
            items:
              - uid: Rnewprofile01
                type: remote
                name: New Profile
                file: Rnewprofile01.yaml
            """);

        var error = Assert.Throws<ProfileBindingTargetChangedException>(() =>
            new ProfileBindingService().PatchLatestProfiles(
                profilesPath,
                "Rabcdefghijk",
                Binding(temp, "sAiws0000001")));

        Assert.Contains("Current Profile UID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownOverwrite_DuplicateAiwsScriptUid_IsRejected()
    {
        using var temp = new TempDirectory();
        var profilesPath = temp.File("profiles.yaml");
        File.WriteAllText(profilesPath, """
            current: Rabcdefghijk
            items:
              - uid: Rabcdefghijk
                type: remote
                name: FlyintPro
                file: Rabcdefghijk.yaml
                option: {}
              - uid: sAiws0000001
                type: script
                name: AI WorkStation
                file: sAiws0000001.js
              - uid: sAiws0000001
                type: script
                name: Duplicate AI WorkStation
                file: sAiws0000001.js
            """);

        var error = Assert.Throws<InvalidDataException>(() =>
            new ProfileBindingService().PatchLatestProfiles(
                profilesPath,
                "Rabcdefghijk",
                Binding(temp, "sAiws0000001")));

        Assert.Contains("重复", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownOverwrite_AiwsScriptUidOccupiedByNonScript_IsTargetChanged()
    {
        using var temp = new TempDirectory();
        var profilesPath = temp.File("profiles.yaml");
        File.WriteAllText(profilesPath, """
            current: Rabcdefghijk
            items:
              - uid: Rabcdefghijk
                type: remote
                name: FlyintPro
                file: Rabcdefghijk.yaml
                option: {}
              - uid: sAiws0000001
                type: merge
                name: External Item
                file: sAiws0000001.js
            """);

        var error = Assert.Throws<ProfileBindingTargetChangedException>(() =>
            new ProfileBindingService().PatchLatestProfiles(
                profilesPath,
                "Rabcdefghijk",
                Binding(temp, "sAiws0000001")));

        Assert.Contains("非 Script 项占用", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownOverwrite_AiwsScriptUidPointsToOtherFile_IsTargetChanged()
    {
        using var temp = new TempDirectory();
        var profilesPath = temp.File("profiles.yaml");
        File.WriteAllText(profilesPath, """
            current: Rabcdefghijk
            items:
              - uid: Rabcdefghijk
                type: remote
                name: FlyintPro
                file: Rabcdefghijk.yaml
                option: {}
              - uid: sAiws0000001
                type: script
                name: Externally Replaced Script
                file: sExternal00001.js
            """);

        var error = Assert.Throws<ProfileBindingTargetChangedException>(() =>
            new ProfileBindingService().PatchLatestProfiles(
                profilesPath,
                "Rabcdefghijk",
                Binding(temp, "sAiws0000001")));

        Assert.Contains("指向了其他文件", error.Message, StringComparison.Ordinal);
    }

    private static void AssertPatchIsFieldPreservingAndIdempotent(
        string latestProfilesYaml,
        string scriptUid,
        Action<YamlMappingNode> assertPreservedFields)
    {
        using var temp = new TempDirectory();
        var profilesPath = temp.File("profiles.yaml");
        File.WriteAllText(profilesPath, latestProfilesYaml, new UTF8Encoding(false));
        var service = new ProfileBindingService();
        var binding = Binding(temp, scriptUid);

        var patched = service.PatchLatestProfiles(profilesPath, "Rabcdefghijk", binding);

        Assert.NotNull(patched);
        File.WriteAllBytes(profilesPath, patched);
        var root = ParseRoot(File.ReadAllText(profilesPath));
        assertPreservedFields(root);
        var current = Item(root, "Rabcdefghijk");
        Assert.Equal(scriptUid, Scalar(Mapping(current, "option"), "script"));
        Assert.Single(Items(root), item => Scalar(item, "uid") == scriptUid);
        Assert.Equal("script", Scalar(Item(root, scriptUid), "type"));
        Assert.Equal(scriptUid + ".js", Scalar(Item(root, scriptUid), "file"));

        Assert.Null(service.PatchLatestProfiles(profilesPath, "Rabcdefghijk", binding));
    }

    private static ProfileBindingPlan Binding(TempDirectory temp, string scriptUid)
        => new(scriptUid, temp.File(Path.Combine("profiles", scriptUid + ".js")), null);

    private static YamlMappingNode ParseRoot(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        return Assert.IsType<YamlMappingNode>(yaml.Documents.Single().RootNode);
    }

    private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
        => Assert.IsType<YamlSequenceNode>(mapping.Children[new YamlScalarNode(key)]);

    private static IEnumerable<YamlMappingNode> Items(YamlMappingNode root)
        => Sequence(root, "items").Children.Select(Assert.IsType<YamlMappingNode>);

    private static YamlMappingNode Item(YamlMappingNode root, string uid)
        => Items(root).Single(item => Scalar(item, "uid") == uid);

    private static YamlMappingNode Mapping(YamlMappingNode mapping, string key)
        => Assert.IsType<YamlMappingNode>(mapping.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode mapping, string key)
        => Assert.IsType<YamlScalarNode>(mapping.Children[new YamlScalarNode(key)]).Value;
}
