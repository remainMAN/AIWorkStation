using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class CandidateCredentialSafetyTests
{
    [Fact]
    public void StaleCandidateFiles_AreCleanedAtStartup()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var stale = temp.File("candidate-stale.yaml");
        var fresh = temp.File("candidate-fresh.yaml");
        File.WriteAllText(stale, "password: fixture-stale");
        File.WriteAllText(fresh, "password: fixture-fresh");
        File.SetLastWriteTimeUtc(stale, now.UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime.AddMinutes(-10));

        _ = new MihomoValidator(temp.Path, () => now);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task CandidateFile_IsDeletedAfterValidationFailure()
    {
        using var temp = new TempDirectory();
        var validator = new MihomoValidator(temp.Path);

        var result = await validator.ValidateAsync(
            temp.File("missing-mihomo.exe"), temp.Path, CandidateYaml,
            Credential());

        Assert.False(result.Success);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "candidate-*.yaml"));
    }

    [Fact]
    public async Task CandidateDiagnostics_DoNotContainPassword()
    {
        using var temp = new TempDirectory();
        var config = Credential();
        var result = await new MihomoValidator(temp.Path).ValidateAsync(
            temp.File("missing-mihomo.exe"), temp.Path,
            CandidateYaml.Replace("fixture-password", config.Password, StringComparison.Ordinal),
            config);

        Assert.False(result.Success);
        Assert.DoesNotContain(config.Password!, result.SanitizedDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Username!, result.SanitizedDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Server, result.SanitizedDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate-", result.SanitizedDetail, StringComparison.OrdinalIgnoreCase);
    }

    private static StaticExitConfig Credential() => new()
    {
        Protocol = StaticProxyProtocol.Socks5,
        Server = "proxy.test.invalid",
        Port = 1080,
        Username = "fixture-user",
        Password = "fixture-password"
    };

    private const string CandidateYaml = """
        proxies:
          - name: AI静态出口-直连
            type: socks5
            server: proxy.test.invalid
            port: 1080
            username: fixture-user
            password: fixture-password
        proxy-groups:
          - name: AI静态链
            type: select
            proxies:
              - AI静态出口-直连
        rules:
          - PROCESS-NAME,codex.exe,AI静态链
        """;
}
