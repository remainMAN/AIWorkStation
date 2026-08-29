using System.Text;
using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class WriteRecoveryAndMessageTests
{
    [Fact]
    public async Task BackupFailureBlocksWrite()
    {
        using var temp = new TempDirectory();
        var target = temp.File("locked.txt");
        await File.WriteAllTextAsync(target, "original");
        await using var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new BackupService(temp.File("backups")).BackupAsync([target]));
        Assert.Equal("original", Encoding.UTF8.GetString(await ReadLockedAsync(locked)));
    }

    [Fact]
    public async Task TargetHashChangedBlocksWrite()
    {
        using var temp = new TempDirectory();
        var target = temp.File("target.txt");
        await File.WriteAllTextAsync(target, "before");
        var hash = FileHash.Sha256(target);
        await File.WriteAllTextAsync(target, "external change");
        Assert.NotEqual(hash, FileHash.Sha256(target));
    }

    [Fact]
    public async Task AtomicWriteSucceeds()
    {
        using var temp = new TempDirectory();
        var target = temp.File("target.txt");
        await File.WriteAllTextAsync(target, "before");
        await new AtomicFileWriter().WriteAsync(target, Encoding.UTF8.GetBytes("after"));
        Assert.Equal("after", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task BackupsDoNotStorePlaintextCredentials()
    {
        using var temp = new TempDirectory();
        var target = temp.File("script.js");
        const string credential = "super-secret-password";
        await File.WriteAllTextAsync(target, credential);
        var backup = await new BackupService(temp.File("backups")).BackupAsync([target]);
        var encrypted = await File.ReadAllBytesAsync(backup.Manifest.Entries[0].BackupFile);
        Assert.DoesNotContain(credential, Encoding.UTF8.GetString(encrypted));
    }

    [Fact]
    public void CreatesCanonicalScriptUid()
    {
        var uid = ProfileBindingService.CreateScriptUid();
        Assert.Matches("^s[A-Za-z0-9]{11}$", uid);
    }

    [Fact]
    public async Task PostWriteVerifyFailureRestoresBackup()
    {
        using var temp = new TempDirectory();
        var target = temp.File("profiles.yaml");
        await File.WriteAllTextAsync(target, "old");
        var backup = await new BackupService(temp.File("backups")).BackupAsync([target]);
        await File.WriteAllTextAsync(target, "bad new");
        var markerService = new TransactionMarkerService(temp.File("transaction.json"));
        var marker = new TransactionMarker("writing", backup.Directory, [target], "clash-verge.exe", "runtime.yaml");
        await markerService.WriteAsync(marker);
        var recovered = await new RecoveryService(reloader: new FakeReloadService(true), markers: markerService).RecoverAsync(marker);
        Assert.True(recovered);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task ReloadFailureRestoresBackup()
    {
        using var temp = new TempDirectory();
        var target = temp.File("script.js");
        await File.WriteAllTextAsync(target, "old");
        var backup = await new BackupService(temp.File("backups")).BackupAsync([target]);
        await File.WriteAllTextAsync(target, "new");
        var marker = new TransactionMarker("writing", backup.Directory, [target], "clash-verge.exe", "runtime.yaml");
        var recovered = await new RecoveryService(reloader: new FakeReloadService(false), markers: new TransactionMarkerService(temp.File("transaction.json"))).RecoverAsync(marker);
        Assert.False(recovered);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RecoveryFailureReturnsCriticalError()
    {
        using var temp = new TempDirectory();
        var marker = new TransactionMarker("writing", temp.File("missing-backup"), [temp.File("x")], "clash.exe", "runtime.yaml");
        var recovered = await new RecoveryService(reloader: new FakeReloadService(true), markers: new TransactionMarkerService(temp.File("transaction.json"))).RecoverAsync(marker);
        Assert.False(recovered);
        Assert.Equal("无法确认原配置已恢复", new UserMessageMapper().Map(FailureCode.RecoveryFailed).TitleZh);
    }

    [Fact]
    public async Task StartupWithPendingMarkerRestoresBackup()
    {
        using var temp = new TempDirectory();
        var target = temp.File("profiles.yaml");
        await File.WriteAllTextAsync(target, "old");
        var backup = await new BackupService(temp.File("backups")).BackupAsync([target]);
        await File.WriteAllTextAsync(target, "interrupted");
        var markerService = new TransactionMarkerService(temp.File("transaction.json"));
        await markerService.WriteAsync(new("writing", backup.Directory, [target], "clash.exe", "runtime.yaml"));
        var result = await new RecoveryService(reloader: new FakeReloadService(true), markers: markerService).RecoverPendingAsync();
        Assert.True(result);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(markerService.MarkerPath));
    }

    [Fact]
    public async Task RecoveryRestoresOnlyTargetsCoveredByTransactionMarker()
    {
        using var temp = new TempDirectory();
        var script = temp.File("script.js");
        var profiles = temp.File("profiles.yaml");
        await File.WriteAllTextAsync(script, "old script");
        await File.WriteAllTextAsync(profiles, "old profiles");
        var backup = await new BackupService(temp.File("backups")).BackupAsync([script, profiles]);
        await File.WriteAllTextAsync(script, "aiws write");
        await File.WriteAllTextAsync(profiles, "external latest profiles");
        var markerService = new TransactionMarkerService(temp.File("transaction.json"));
        var marker = new TransactionMarker(
            "writing", backup.Directory, [script], "clash.exe", "runtime.yaml");
        await markerService.WriteAsync(marker);

        var recovered = await new RecoveryService(
            reloader: new FakeReloadService(true), markers: markerService).RecoverAsync(marker);

        Assert.True(recovered);
        Assert.Equal("old script", await File.ReadAllTextAsync(script));
        Assert.Equal("external latest profiles", await File.ReadAllTextAsync(profiles));
    }

    [Fact]
    public void EveryFailureCodeHasChineseMessage()
    {
        var mapper = new UserMessageMapper();
        foreach (var code in Enum.GetValues<FailureCode>())
        {
            var message = mapper.Map(code);
            Assert.NotEmpty(message.TitleZh);
            Assert.NotEmpty(message.MessageZh);
            Assert.NotEmpty(message.SuggestedActionZh);
        }
    }

    private static async Task<byte[]> ReadLockedAsync(FileStream stream)
    {
        stream.Position = 0;
        var bytes = new byte[stream.Length];
        _ = await stream.ReadAsync(bytes);
        return bytes;
    }
}
