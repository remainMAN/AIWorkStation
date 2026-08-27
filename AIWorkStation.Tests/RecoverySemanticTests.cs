using System.Text.Json;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class RecoverySemanticTests
{
    private const string ScriptUid = "sManaged00001";

    [Fact]
    public async Task Recovery_ManagedSemanticMismatch_StillFails()
    {
        using var temp = new TempDirectory();
        var paths = await CreateEnvironmentAsync(temp, ScriptUid);
        var baselineClient = new FakeRecoveryRuntimeClient { ManagedRulePayload = "codex.exe" };
        var baseline = await RecoveryService.CaptureBaselineAsync(paths.Profiles, paths.Extension, baselineClient);
        var backup = await new BackupService(temp.File("backups")).BackupAsync([paths.Extension], baseline);
        await File.WriteAllTextAsync(paths.Extension, "modified");

        var markers = new TransactionMarkerService(temp.File("transaction.json"));
        var marker = Marker(backup.Directory, paths.Extension, paths.Runtime);
        await markers.WriteAsync(marker);
        var differentRuntime = new FakeRecoveryRuntimeClient { ManagedRulePayload = "ChatGPT.exe" };

        var recovered = await new RecoveryService(
            reloader: new FakeReloadService(true),
            markers: markers,
            pipeFactory: _ => differentRuntime).RecoverAsync(marker);

        Assert.False(recovered);
        Assert.Equal("original extension", await File.ReadAllTextAsync(paths.Extension));
        Assert.Equal(baseline.ExtensionSha256, FileHash.Sha256(paths.Extension));
        Assert.True(File.Exists(markers.MarkerPath));
        Assert.True(Directory.Exists(backup.Directory));
    }

    [Fact]
    public async Task Recovery_ProfileBindingMismatch_Fails()
    {
        using var temp = new TempDirectory();
        var paths = await CreateEnvironmentAsync(temp, ScriptUid);
        var runtime = new FakeRecoveryRuntimeClient();
        var baseline = await RecoveryService.CaptureBaselineAsync(paths.Profiles, paths.Extension, runtime);
        var backup = await new BackupService(temp.File("backups")).BackupAsync([paths.Extension], baseline);
        await File.WriteAllTextAsync(paths.Extension, "modified");
        await WriteProfilesAsync(paths.Profiles, "sExternal0001");

        var markers = new TransactionMarkerService(temp.File("transaction.json"));
        var marker = Marker(backup.Directory, paths.Extension, paths.Runtime);
        await markers.WriteAsync(marker);
        var recovered = await new RecoveryService(
            reloader: new FakeReloadService(true),
            markers: markers,
            pipeFactory: _ => runtime).RecoverAsync(marker);

        Assert.False(recovered);
        Assert.Equal("original extension", await File.ReadAllTextAsync(paths.Extension));
        Assert.True(File.Exists(markers.MarkerPath));
        Assert.True(Directory.Exists(backup.Directory));
    }

    [Fact]
    public async Task Recovery_ManagedSemanticMatch_Passes()
    {
        using var temp = new TempDirectory();
        var paths = await CreateEnvironmentAsync(temp, ScriptUid);
        var runtime = new FakeRecoveryRuntimeClient();
        var baseline = await RecoveryService.CaptureBaselineAsync(paths.Profiles, paths.Extension, runtime);
        var backup = await new BackupService(temp.File("backups")).BackupAsync([paths.Extension], baseline);
        await File.WriteAllTextAsync(paths.Extension, "modified");

        var markers = new TransactionMarkerService(temp.File("transaction.json"));
        var marker = Marker(backup.Directory, paths.Extension, paths.Runtime);
        await markers.WriteAsync(marker);
        var recovered = await new RecoveryService(
            reloader: new FakeReloadService(true),
            markers: markers,
            pipeFactory: _ => runtime).RecoverAsync(marker);

        Assert.True(recovered);
        Assert.Equal("original extension", await File.ReadAllTextAsync(paths.Extension));
        Assert.False(File.Exists(markers.MarkerPath));
        Assert.False(Directory.Exists(backup.Directory));
        Assert.True(runtime.ConfigRequests > 0);
        Assert.True(runtime.ProxyRequests > 0);
        Assert.True(runtime.RuleRequests > 0);
    }

    [Fact]
    public async Task Recovery_RestoresPreviousAiwsTransportSelection()
    {
        using var temp = new TempDirectory();
        var paths = await CreateEnvironmentAsync(temp, ScriptUid);
        var runtime = new FakeRecoveryRuntimeClient { Selection = RouteScriptBuilder.DialerStaticExitName };
        var baseline = await RecoveryService.CaptureBaselineAsync(paths.Profiles, paths.Extension, runtime);
        Assert.Equal(RouteScriptBuilder.DialerStaticExitName, baseline.Runtime.ManagedGroupSelection);
        var backup = await new BackupService(temp.File("backups")).BackupAsync([paths.Extension], baseline);
        await File.WriteAllTextAsync(paths.Extension, "modified");
        runtime.Selection = RouteScriptBuilder.DirectStaticExitName;

        var markers = new TransactionMarkerService(temp.File("transaction.json"));
        var marker = Marker(backup.Directory, paths.Extension, paths.Runtime);
        await markers.WriteAsync(marker);
        var reloader = new CallbackReloadService(() => { });
        var recovered = await new RecoveryService(
            reloader: reloader,
            markers: markers,
            pipeFactory: _ => runtime).RecoverAsync(marker);

        Assert.True(recovered);
        Assert.Equal(RouteScriptBuilder.DialerStaticExitName, runtime.Selection);
        Assert.Equal(1, runtime.SelectionCalls);
        Assert.False(File.Exists(markers.MarkerPath));
        Assert.False(Directory.Exists(backup.Directory));
    }

    [Fact]
    public async Task Recovery_ManagedProxyDefinitionChanged_FailsAndKeepsRecoveryArtifacts()
    {
        using var temp = new TempDirectory();
        var paths = await CreateEnvironmentAsync(temp, ScriptUid);
        await File.WriteAllTextAsync(paths.Runtime, ManagedRuntimeYaml("198.51.100.10"));
        var runtime = new FakeRecoveryRuntimeClient();
        var baseline = await RecoveryService.CaptureBaselineAsync(
            paths.Profiles, paths.Extension, paths.Runtime, runtime);
        Assert.Equal(2, baseline.Runtime.ManagedProxyDefinitionHashes.Count);
        var backup = await new BackupService(temp.File("backups")).BackupAsync([paths.Extension], baseline);
        await File.WriteAllTextAsync(paths.Extension, "modified");
        await File.WriteAllTextAsync(paths.Runtime, ManagedRuntimeYaml("198.51.100.11"));

        var markers = new TransactionMarkerService(temp.File("transaction.json"));
        var marker = Marker(backup.Directory, paths.Extension, paths.Runtime);
        await markers.WriteAsync(marker);
        var recovered = await new RecoveryService(
            reloader: new FakeReloadService(true),
            markers: markers,
            pipeFactory: _ => runtime).RecoverAsync(marker);

        Assert.False(recovered);
        Assert.Equal("original extension", await File.ReadAllTextAsync(paths.Extension));
        Assert.True(File.Exists(markers.MarkerPath));
        Assert.True(Directory.Exists(backup.Directory));
    }

    [Fact]
    public async Task ManagedProxyDefinitionHash_IgnoresYamlPropertyOrderButDetectsValueChange()
    {
        using var temp = new TempDirectory();
        var firstPath = temp.File("first.yaml");
        var reorderedPath = temp.File("reordered.yaml");
        var changedPath = temp.File("changed.yaml");
        await File.WriteAllTextAsync(firstPath, ManagedRuntimeYaml("198.51.100.10"));
        await File.WriteAllTextAsync(reorderedPath, ManagedRuntimeYaml("198.51.100.10", reverseProperties: true));
        await File.WriteAllTextAsync(changedPath, ManagedRuntimeYaml("198.51.100.11"));

        var first = RecoveryService.CaptureManagedProxyDefinitionHashes(firstPath);
        var reordered = RecoveryService.CaptureManagedProxyDefinitionHashes(reorderedPath);
        var changed = RecoveryService.CaptureManagedProxyDefinitionHashes(changedPath);

        Assert.Equal(first.Keys.OrderBy(key => key), reordered.Keys.OrderBy(key => key));
        foreach (var key in first.Keys) Assert.Equal(first[key], reordered[key]);
        Assert.NotEqual(first[RouteScriptBuilder.DirectStaticExitName],
            changed[RouteScriptBuilder.DirectStaticExitName]);
    }

    [Fact]
    public async Task RuntimeSemanticBaseline_CapturesOnlyAiwsManagedRules()
    {
        var runtime = new FakeRecoveryRuntimeClient();
        var baseline = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(runtime);

        Assert.Equal(
            [RouteScriptBuilder.DirectStaticExitName, RouteScriptBuilder.DialerStaticExitName],
            baseline.ManagedProxyNames);
        Assert.True(baseline.ManagedGroupExists);
        Assert.Single(baseline.ManagedRules);
        Assert.DoesNotContain("unrelated.exe", baseline.ManagedRules[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeSemanticBaseline_ConfigPropertyOrderIsCanonicalized()
    {
        var firstRuntime = new FakeRecoveryRuntimeClient
        {
            ConfigsJson = """{"mode":"rule","mixed-port":7890,"nested":{"b":2,"a":1}}"""
        };
        var reorderedRuntime = new FakeRecoveryRuntimeClient
        {
            ConfigsJson = """{"nested":{"a":1,"b":2},"mixed-port":7890,"mode":"rule"}"""
        };

        var first = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(firstRuntime);
        var reordered = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(reorderedRuntime);

        Assert.Equal(first.ConfigsSha256, reordered.ConfigsSha256);
        Assert.True(first.SemanticallyEquals(reordered));
    }

    [Fact]
    public void Recovery_ConfigShaDifferenceAlone_DoesNotFail()
    {
        var before = new RuntimeSemanticBaseline(
            [RouteScriptBuilder.DirectStaticExitName], true,
            RouteScriptBuilder.DirectStaticExitName,
            [RouteScriptBuilder.DirectStaticExitName], ["managed-rule"])
        {
            ConfigsSha256 = "before",
            ManagedProxyDefinitionHashes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RouteScriptBuilder.DirectStaticExitName] = "same-definition"
            }
        };
        var after = before with { ConfigsSha256 = "after" };

        Assert.NotEqual(before.ConfigsSha256, after.ConfigsSha256);
        Assert.True(before.SemanticallyEquals(after));
    }

    [Fact]
    public async Task RuntimeProxiesWithoutServerPort_DoesNotProduceFakeDefinitionProof()
    {
        var baseline = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(
            new FakeRecoveryRuntimeClient());

        Assert.Empty(baseline.ManagedProxyDefinitionHashes);
    }

    [Fact]
    public async Task RuntimeProxiesWithoutCredentials_DoesNotProduceFakeCredentialProof()
    {
        var baseline = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(
            new FakeRecoveryRuntimeClient());

        Assert.Empty(baseline.ManagedProxyDefinitionHashes);
    }

    [Fact]
    public async Task TemporaryRestore_OriginalObjectsAbsent_RemovesCandidateObjects()
    {
        var before = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(
            new FakeRecoveryRuntimeClient { IncludeManagedObjects = false, IncludeManagedRule = false });
        var restored = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(
            new FakeRecoveryRuntimeClient { IncludeManagedObjects = false, IncludeManagedRule = false });
        var residue = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(
            new FakeRecoveryRuntimeClient());

        Assert.Empty(restored.ManagedProxyNames);
        Assert.False(restored.ManagedGroupExists);
        Assert.Empty(restored.ManagedRules);
        Assert.True(before.SemanticallyEquals(restored));
        Assert.False(before.SemanticallyEquals(residue));
    }

    [Fact]
    public async Task TemporaryRestore_GroupMembersRestored_Passes()
    {
        var before = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient());
        var after = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient());
        var mismatch = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient
        {
            GroupMembers = [RouteScriptBuilder.DirectStaticExitName]
        });

        Assert.True(before.SemanticallyEquals(after));
        Assert.False(before.SemanticallyEquals(mismatch));
    }

    [Fact]
    public async Task TemporaryRestore_GroupSelectionRestored_Passes()
    {
        var before = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient());
        var after = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient());
        var mismatch = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient
        {
            Selection = RouteScriptBuilder.DialerStaticExitName
        });

        Assert.True(before.SemanticallyEquals(after));
        Assert.False(before.SemanticallyEquals(mismatch));
    }

    [Fact]
    public async Task TemporaryRestore_RulesRestored_Passes()
    {
        var before = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient());
        var after = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient());
        var mismatch = await RecoveryService.CaptureRuntimeSemanticBaselineAsync(new FakeRecoveryRuntimeClient
        {
            ManagedRulePayload = "ChatGPT.exe"
        });

        Assert.True(before.SemanticallyEquals(after));
        Assert.False(before.SemanticallyEquals(mismatch));
    }

    [Fact]
    public async Task FakeRuntime_DoesNotExposeUnsupportedServerPortFields()
    {
        using var document = await new FakeRecoveryRuntimeClient().GetProxiesAsync();
        var serialized = document.RootElement.GetRawText();

        Assert.DoesNotContain("server", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("port", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupCancellation_CleansIncompleteWorkspace()
    {
        using var temp = new TempDirectory();
        var target = temp.File("target.txt");
        var backupRoot = temp.File("backups");
        await File.WriteAllTextAsync(target, "fixture");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackupService(backupRoot).BackupAsync([target], cancellation.Token));

        Assert.True(!Directory.Exists(backupRoot) || !Directory.EnumerateFileSystemEntries(backupRoot).Any());
    }

    private static async Task<(string Profiles, string Extension, string Runtime)> CreateEnvironmentAsync(
        TempDirectory temp,
        string scriptUid)
    {
        var profilesDirectory = temp.File("profiles");
        Directory.CreateDirectory(profilesDirectory);
        var profiles = temp.File("profiles.yaml");
        var extension = temp.File($"profiles/{scriptUid}.js");
        var runtime = temp.File("clash-verge.yaml");
        await WriteProfilesAsync(profiles, scriptUid);
        await File.WriteAllTextAsync(extension, "original extension");
        await File.WriteAllTextAsync(runtime, "external-controller-pipe: '\\\\.\\pipe\\recovery-fixture'\nmode: rule\n");
        return (profiles, extension, runtime);
    }

    private static Task WriteProfilesAsync(string path, string scriptUid)
        => File.WriteAllTextAsync(path, $$"""
            current: current
            items:
              - uid: current
                type: remote
                option:
                  script: {{scriptUid}}
              - uid: {{scriptUid}}
                type: script
                file: {{scriptUid}}.js
            """);

    private static string ManagedRuntimeYaml(string directServer, bool reverseProperties = false)
        => reverseProperties
            ? $$"""
                external-controller-pipe: '\\.\pipe\recovery-fixture'
                proxies:
                  - password: fixture-password
                    username: fixture-user
                    port: 1080
                    server: {{directServer}}
                    type: socks5
                    name: {{RouteScriptBuilder.DirectStaticExitName}}
                  - dialer-proxy: main
                    port: 1080
                    server: 198.51.100.20
                    type: socks5
                    name: {{RouteScriptBuilder.DialerStaticExitName}}
                """
            : $$"""
                external-controller-pipe: '\\.\pipe\recovery-fixture'
                proxies:
                  - name: {{RouteScriptBuilder.DirectStaticExitName}}
                    type: socks5
                    server: {{directServer}}
                    port: 1080
                    username: fixture-user
                    password: fixture-password
                  - name: {{RouteScriptBuilder.DialerStaticExitName}}
                    type: socks5
                    server: 198.51.100.20
                    port: 1080
                    dialer-proxy: main
                """;

    private static TransactionMarker Marker(string backupDirectory, string target, string runtimePath)
        => new("writing", backupDirectory, [target], "clash-verge.exe", runtimePath);

    private sealed class FakeRecoveryRuntimeClient : IMihomoApplyClient
    {
        public string Selection { get; set; } = RouteScriptBuilder.DirectStaticExitName;
        public string ManagedRulePayload { get; set; } = "codex.exe";
        public string ConfigsJson { get; set; } = "{\"mode\":\"rule\"}";
        public bool IncludeManagedObjects { get; set; } = true;
        public bool IncludeManagedRule { get; set; } = true;
        public IReadOnlyList<string> GroupMembers { get; set; } =
            [RouteScriptBuilder.DialerStaticExitName, RouteScriptBuilder.DirectStaticExitName];
        public int ConfigRequests { get; private set; }
        public int ProxyRequests { get; private set; }
        public int RuleRequests { get; private set; }
        public int SelectionCalls { get; private set; }

        public Task PutInlineConfigAsync(string yamlPayload, CancellationToken token = default)
            => Task.CompletedTask;

        public Task SelectProxyAsync(string groupName, string proxyName, CancellationToken token = default)
        {
            SelectionCalls++;
            Selection = proxyName;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProxySelection>> GetProxySelectionsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ProxySelection>>([
                new ProxySelection(RouteScriptBuilder.StaticGroupName, Selection)
            ]);

        public Task<IReadOnlyList<RouteObservation>> GetRouteObservationsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<RouteObservation>>([]);

        public Task<JsonDocument> GetConfigsAsync(CancellationToken token = default)
        {
            ConfigRequests++;
            return Task.FromResult(JsonDocument.Parse(ConfigsJson));
        }

        public Task<JsonDocument> GetProxiesAsync(CancellationToken token = default)
        {
            ProxyRequests++;
            var proxies = new Dictionary<string, object?>();
            if (IncludeManagedObjects)
            {
                // 真实 Mihomo /proxies 只保证可观测的对象、类型和 Selector 状态，
                // Fake 不再虚构 server、port 或 credential 字段。
                proxies[RouteScriptBuilder.DirectStaticExitName] = new
                {
                    type = "Socks5"
                };
                proxies[RouteScriptBuilder.DialerStaticExitName] = new
                {
                    type = "Socks5"
                };
                proxies[RouteScriptBuilder.StaticGroupName] = new
                {
                    type = "Selector",
                    now = Selection,
                    all = GroupMembers
                };
            }
            return Document(new { proxies });
        }

        public Task<JsonDocument> GetRulesAsync(CancellationToken token = default)
        {
            RuleRequests++;
            var rules = new List<object>();
            if (IncludeManagedRule)
                rules.Add(new { type = "ProcessName", payload = ManagedRulePayload, proxy = RouteScriptBuilder.StaticGroupName, size = -1 });
            rules.Add(new { type = "ProcessName", payload = "unrelated.exe", proxy = "DIRECT", size = -1 });
            return Document(new { rules });
        }

        public Task<int> GetProxyDelayAsync(string proxyName, CancellationToken token = default)
            => Task.FromResult(10);

        private static Task<JsonDocument> Document(object value)
            => Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(value)));
    }

    private sealed class CallbackReloadService(Action onRestart) : ClashReloadService
    {
        public override Task<bool> RestartAsync(string clashExecutable, string runtimeConfigPath, CancellationToken token = default)
        {
            onRestart();
            return Task.FromResult(true);
        }
    }
}
