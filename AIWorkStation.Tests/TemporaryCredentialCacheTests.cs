using System.Text;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class TemporaryCredentialCacheTests
{
    private static readonly DateTimeOffset InitialUtc = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CredentialCache_RoundTripsForCurrentUser()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp, () => InitialUtc);

        await cache.SaveAsync(Credential());
        var loaded = Assert.IsType<TemporaryCredentialPayload>(await cache.LoadAsync());

        Assert.Equal(TemporaryCredentialCache.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(StaticProxyProtocol.Socks5, loaded.Protocol);
        Assert.Equal("proxy.test.invalid", loaded.Server);
        Assert.Equal(1080, loaded.Port);
        Assert.Equal("fixture-user", loaded.Username);
        Assert.Equal("fixture-password-9f8e7d", loaded.Password);
        Assert.Equal(InitialUtc, loaded.CreatedAtUtc);
        Assert.Equal(InitialUtc.AddHours(24), loaded.ExpiresAtUtc);
    }

    [Fact]
    public async Task CredentialCache_ExpiresAndDeletes()
    {
        using var temp = new TempDirectory();
        var now = InitialUtc;
        var cache = CreateCache(temp, () => now);
        await cache.SaveAsync(Credential());

        now = InitialUtc.Add(TemporaryCredentialCache.DefaultLifetime).AddSeconds(1);

        Assert.Null(await cache.LoadAsync());
        Assert.False(File.Exists(cache.CachePath));
    }

    [Fact]
    public async Task CredentialCache_CorruptFileIsDeleted()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp, () => InitialUtc);
        Directory.CreateDirectory(Path.GetDirectoryName(cache.CachePath)!);
        await File.WriteAllBytesAsync(cache.CachePath, [0x10, 0x20, 0x30, 0x40]);

        Assert.Null(await cache.LoadAsync());
        Assert.False(File.Exists(cache.CachePath));
    }

    [Fact]
    public async Task CredentialCache_ClearRemovesFile()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp, () => InitialUtc);
        await cache.SaveAsync(Credential());
        Assert.True(File.Exists(cache.CachePath));

        await cache.ClearAsync();

        Assert.False(File.Exists(cache.CachePath));
    }

    [Fact]
    public async Task CredentialCache_ClearReportsLockedFileFailure()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp, () => InitialUtc);
        await cache.SaveAsync(Credential());
        await using var locked = new FileStream(cache.CachePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var cleared = await cache.ClearAsync();

        Assert.False(cleared);
        Assert.True(File.Exists(cache.CachePath));
    }

    [Fact]
    public async Task CredentialCache_AuthenticationFailureClearsPassword()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp, () => InitialUtc);
        await cache.SaveAsync(Credential());

        await cache.ClearPasswordAsync();
        var loaded = Assert.IsType<TemporaryCredentialPayload>(await cache.LoadAsync());

        Assert.Equal(StaticProxyProtocol.Socks5, loaded.Protocol);
        Assert.Equal("proxy.test.invalid", loaded.Server);
        Assert.Equal(1080, loaded.Port);
        Assert.Equal("fixture-user", loaded.Username);
        Assert.Null(loaded.Password);
        Assert.Equal(InitialUtc, loaded.CreatedAtUtc);
        Assert.Equal(InitialUtc.AddHours(24), loaded.ExpiresAtUtc);
    }

    [Fact]
    public async Task CredentialCache_NeverWritesPlaintextPassword()
    {
        using var temp = new TempDirectory();
        var cache = CreateCache(temp, () => InitialUtc);
        var credential = Credential();

        await cache.SaveAsync(credential);
        var persisted = await File.ReadAllBytesAsync(cache.CachePath);
        var decoded = Encoding.UTF8.GetString(persisted);

        Assert.DoesNotContain(credential.Password!, decoded, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Server, decoded, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Username!, decoded, StringComparison.Ordinal);
    }

    private static TemporaryCredentialCache CreateCache(TempDirectory temp, Func<DateTimeOffset> utcNow)
        => new(temp.File(Path.Combine("credential-cache", "credential-cache.bin")), utcNow);

    private static StaticExitConfig Credential() => new()
    {
        Protocol = StaticProxyProtocol.Socks5,
        Server = "proxy.test.invalid",
        Port = 1080,
        Username = "fixture-user",
        Password = "fixture-password-9f8e7d"
    };
}
