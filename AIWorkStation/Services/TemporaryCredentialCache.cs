using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AIWorkStation.Models;

namespace AIWorkStation.Services;

public sealed record TemporaryCredentialPayload
{
    public required int SchemaVersion { get; init; }
    public required StaticProxyProtocol Protocol { get; init; }
    public required string Server { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public StaticExitConfig ToStaticExitConfig() => new()
    {
        Protocol = Protocol,
        Server = Server,
        Port = Port,
        Username = Username,
        Password = Password
    };
}

public sealed class TemporaryCredentialCache
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultLifetimeHours = 24;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(DefaultLifetimeHours);

    private readonly Func<DateTimeOffset> _utcNow;

    public TemporaryCredentialCache(string? cachePath = null, Func<DateTimeOffset>? utcNow = null)
    {
        CachePath = Path.GetFullPath(cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIWorkStation",
            "credential-cache.bin"));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string CachePath { get; }

    public async Task SaveAsync(StaticExitConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        if (!Enum.IsDefined(config.Protocol)) throw new ArgumentOutOfRangeException(nameof(config.Protocol));

        var now = _utcNow().ToUniversalTime();
        var payload = new TemporaryCredentialPayload
        {
            SchemaVersion = CurrentSchemaVersion,
            Protocol = config.Protocol,
            Server = config.Server,
            Port = config.Port,
            Username = string.IsNullOrEmpty(config.Username) ? null : config.Username,
            Password = string.IsNullOrEmpty(config.Password) ? null : config.Password,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(DefaultLifetime)
        };
        await WritePayloadAsync(payload, cancellationToken);
    }

    public async Task<TemporaryCredentialPayload?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CachePath)) return null;

        byte[]? encryptedBytes = null;
        byte[]? plaintextBytes = null;
        try
        {
            RestrictExistingStorageToCurrentUser();
            encryptedBytes = await File.ReadAllBytesAsync(CachePath, cancellationToken);
            plaintextBytes = ProtectedData.Unprotect(encryptedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var payload = JsonSerializer.Deserialize<TemporaryCredentialPayload>(plaintextBytes)
                ?? throw new InvalidDataException("凭证缓存内容为空。");
            ValidatePayload(payload);
            if (payload.ExpiresAtUtc <= _utcNow().ToUniversalTime())
            {
                _ = TryDeleteCache();
                return null;
            }
            return payload;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or
                                   JsonException or InvalidDataException or ArgumentException or SecurityException or
                                   IdentityNotMappedException or PlatformNotSupportedException)
        {
            _ = TryDeleteCache();
            return null;
        }
        finally
        {
            if (plaintextBytes is not null) CryptographicOperations.ZeroMemory(plaintextBytes);
            if (encryptedBytes is not null) CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    public Task<bool> ClearAsync() => Task.FromResult(TryDeleteCache());

    public async Task ClearPasswordAsync(CancellationToken cancellationToken = default)
    {
        var payload = await LoadAsync(cancellationToken);
        if (payload is null || payload.Password is null) return;
        await WritePayloadAsync(payload with { Password = null }, cancellationToken);
    }

    private async Task WritePayloadAsync(TemporaryCredentialPayload payload, CancellationToken cancellationToken)
    {
        byte[]? plaintextBytes = null;
        byte[]? encryptedBytes = null;
        try
        {
            plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            encryptedBytes = ProtectedData.Protect(plaintextBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            await WriteAtomicallyAsync(encryptedBytes, cancellationToken);
        }
        finally
        {
            if (plaintextBytes is not null) CryptographicOperations.ZeroMemory(plaintextBytes);
            if (encryptedBytes is not null) CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    private async Task WriteAtomicallyAsync(byte[] encryptedBytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(CachePath)
            ?? throw new InvalidOperationException("凭证缓存路径缺少目录。");
        Directory.CreateDirectory(directory);
        RestrictDirectoryToCurrentUser(directory);

        var temporaryPath = Path.Combine(directory, $".credential-cache-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encryptedBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            RestrictFileToCurrentUser(temporaryPath);
            if (File.Exists(CachePath)) File.Replace(temporaryPath, CachePath, null, ignoreMetadataErrors: true);
            else File.Move(temporaryPath, CachePath);
            RestrictFileToCurrentUser(CachePath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // 缓存写入失败由调用方显示非阻断提示；清理失败不能覆盖原始异常。
            }
        }
    }

    private void RestrictExistingStorageToCurrentUser()
    {
        var directory = Path.GetDirectoryName(CachePath)
            ?? throw new InvalidOperationException("凭证缓存路径缺少目录。");
        if (Directory.Exists(directory)) RestrictDirectoryToCurrentUser(directory);
        if (File.Exists(CachePath)) RestrictFileToCurrentUser(CachePath);
    }

    private static void RestrictDirectoryToCurrentUser(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUserSid(),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void RestrictFileToCurrentUser(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUserSid(),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static SecurityIdentifier CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
    }

    private static void ValidatePayload(TemporaryCredentialPayload payload)
    {
        if (payload.SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("凭证缓存版本不受支持。");
        if (!Enum.IsDefined(payload.Protocol)) throw new InvalidDataException("凭证缓存协议无效。");
        if (string.IsNullOrWhiteSpace(payload.Server) || payload.Port is < 1 or > 65535)
            throw new InvalidDataException("凭证缓存代理地址无效。");
        if (payload.CreatedAtUtc == default || payload.ExpiresAtUtc <= payload.CreatedAtUtc)
            throw new InvalidDataException("凭证缓存有效期无效。");
    }

    private bool TryDeleteCache()
    {
        try
        {
            if (File.Exists(CachePath)) File.Delete(CachePath);
            return !File.Exists(CachePath);
        }
        catch
        {
            // 缓存不可用时按“没有缓存”处理，不能阻止正式配置。
            return false;
        }
    }
}
