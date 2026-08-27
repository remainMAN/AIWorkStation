using System.Text.Json;
using System.Security.Cryptography;

namespace AIWorkStation.Services;

public sealed record BackupEntry(string TargetPath, string BackupFile, bool Existed, string? OriginalSha256);
public sealed record RuntimeSemanticBaseline(
    IReadOnlyList<string> ManagedProxyNames,
    bool ManagedGroupExists,
    string? ManagedGroupSelection,
    IReadOnlyList<string> ManagedGroupMembers,
    IReadOnlyList<string> ManagedRules)
{
    public string ConfigsSha256 { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> ManagedProxyDefinitionHashes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool SemanticallyEquals(RuntimeSemanticBaseline other)
        => ManagedGroupExists == other.ManagedGroupExists &&
           string.Equals(ManagedGroupSelection, other.ManagedGroupSelection, StringComparison.Ordinal) &&
           ManagedProxyNames.SequenceEqual(other.ManagedProxyNames, StringComparer.Ordinal) &&
           ManagedGroupMembers.SequenceEqual(other.ManagedGroupMembers, StringComparer.Ordinal) &&
           ManagedRules.SequenceEqual(other.ManagedRules, StringComparer.Ordinal) &&
           DictionaryEquals(ManagedProxyDefinitionHashes, other.ManagedProxyDefinitionHashes);

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(item =>
            right.TryGetValue(item.Key, out var value) &&
            string.Equals(item.Value, value, StringComparison.Ordinal));
}

public sealed record RecoveryBaseline(
    string ProfilesPath,
    string CurrentProfileUid,
    string? ScriptUid,
    string? ExtensionPath,
    string? ExtensionSha256,
    RuntimeSemanticBaseline Runtime);

public sealed record BackupManifest(IReadOnlyList<BackupEntry> Entries, RecoveryBaseline? RecoveryBaseline = null);

public sealed class BackupService
{
    private readonly string _backupRoot;

    public BackupService(string? backupRoot = null)
    {
        _backupRoot = backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkStation", "backups");
    }

    public async Task<(string Directory, BackupManifest Manifest)> BackupAsync(IEnumerable<string> targetPaths, CancellationToken token = default)
        => await BackupAsync(targetPaths, recoveryBaseline: null, token);

    public async Task<(string Directory, BackupManifest Manifest)> BackupAsync(
        IEnumerable<string> targetPaths,
        RecoveryBaseline? recoveryBaseline,
        CancellationToken token = default)
    {
        var directory = Path.Combine(_backupRoot, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var completed = false;
        try
        {
            var entries = new List<BackupEntry>();
            var index = 0;
            foreach (var target in targetPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var backupFile = Path.Combine(directory, $"{index++:D3}.bin");
                if (File.Exists(target))
                {
                    byte[]? bytes = null;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(target, token);
                        // Extension 必须包含代理凭据；备份使用当前 Windows 用户 DPAPI 加密，避免落盘明文副本。
                        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                        await File.WriteAllBytesAsync(backupFile, encrypted, token);
                        entries.Add(new(target, backupFile, true, FileHash.Sha256(bytes)));
                    }
                    finally
                    {
                        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                    }
                }
                else entries.Add(new(target, backupFile, false, null));
            }
            var manifest = new BackupManifest(entries, recoveryBaseline);
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest), token);
            completed = true;
            return (directory, manifest);
        }
        finally
        {
            // 未完成的 workspace 不能留给启动恢复逻辑误判为可用备份。
            if (!completed) TryDeleteDirectory(directory);
        }
    }

    public static BackupManifest ReadManifest(string backupDirectory)
    {
        var path = Path.Combine(backupDirectory, "manifest.json");
        return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("备份清单无效。");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
