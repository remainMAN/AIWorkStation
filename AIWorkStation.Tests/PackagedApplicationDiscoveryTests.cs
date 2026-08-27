using AIWorkStation.Models;
using AIWorkStation.Services;

namespace AIWorkStation.Tests;

public sealed class PackagedApplicationDiscoveryTests
{
    [Fact]
    public void RunningCodex_IsFoundEvenWhenMainModulePathUnavailable()
    {
        var app = RunningProcessApplicationSource.FromProcessFacts("codex", null);

        Assert.NotNull(app);
        Assert.Equal("codex.exe", app.ExecutableName);
        Assert.Equal(string.Empty, app.ExecutablePath);
        Assert.True(app.RunningProcess);
    }

    [Fact]
    public void RunningChatGPT_IsFoundEvenWhenMainModulePathUnavailable()
    {
        var app = RunningProcessApplicationSource.FromProcessFacts("ChatGPT", null);

        Assert.NotNull(app);
        Assert.Equal("ChatGPT.exe", app.ExecutableName);
        Assert.Equal(string.Empty, app.ExecutablePath);
        Assert.True(app.RunningProcess);
    }

    [Fact]
    public void CodexExecutableName_IsCodexExe()
        => Assert.Equal("codex.exe", RunningProcessApplicationSource.FromProcessFacts("codex", null)!.ExecutableName);

    [Fact]
    public void ChatGPTExecutableName_IsChatGPTExe()
        => Assert.Equal("ChatGPT.exe", RunningProcessApplicationSource.FromProcessFacts("ChatGPT", null)!.ExecutableName);

    [Fact]
    public async Task PackagedChatGPT_IsFoundWhenNotRunning()
    {
        var results = await Finder(Package("app/ChatGPT.exe", "ChatGPT")).FindAsync("ChatGPT");

        var app = Assert.Single(results);
        Assert.Equal("ChatGPT.exe", app.ExecutableName);
        Assert.False(app.RunningProcess);
        Assert.Equal(PackagedApplicationSource.SourceName, app.Source);
    }

    [Fact]
    public async Task PackagedCodex_IsFoundWhenNotRunning()
    {
        var results = await Finder(Package("bin/codex.exe", "Codex")).FindAsync("codex");

        Assert.Equal("codex.exe", Assert.Single(results).ExecutableName);
    }

    [Fact]
    public async Task PackagedApp_WithExecutableName_IsConvertedToCandidate()
    {
        using var temp = new TempDirectory();
        var install = temp.File("package");
        Directory.CreateDirectory(Path.Combine(install, "app"));
        var executable = Path.Combine(install, "app", "ChatGPT.exe");
        await File.WriteAllBytesAsync(executable, []);
        var source = Source(Package("app/ChatGPT.exe", "ChatGPT", install));

        var app = Assert.Single(await source.FindAllAsync());

        Assert.Equal("ChatGPT.exe", app.ExecutableName);
        Assert.Equal(executable, app.ExecutablePath);
        Assert.Equal("Windows 已安装应用", app.Source);
    }

    [Fact]
    public async Task PackagedApp_WithoutReadableInstallPath_IsStillFoundByExecutableName()
    {
        var source = Source(Package("app/ChatGPT.exe", "ChatGPT", @"C:\Program Files\WindowsApps\Denied"));

        var app = Assert.Single(await source.FindAllAsync());

        Assert.Equal("ChatGPT.exe", app.ExecutableName);
        Assert.Equal(string.Empty, app.ExecutablePath);
    }

    [Fact]
    public async Task RunningAndPackagedChatGPT_AreDeduplicated()
    {
        var running = new ApplicationTarget(
            "ChatGPT", "ChatGPT.exe", @"C:\Running\ChatGPT.exe", true, "正在运行");
        var finder = new ApplicationFinder([
            new FakeApplicationSource(running),
            Source(Package("app/chatgpt.exe", "ChatGPT"))
        ]);

        var app = Assert.Single(await finder.FindAsync("ChatGPT"));

        Assert.True(app.RunningProcess);
        Assert.Equal(running.ExecutablePath, app.ExecutablePath);
    }

    [Fact]
    public async Task RunningAndPackagedCodex_AreDeduplicated()
    {
        var running = RunningProcessApplicationSource.FromProcessFacts("codex", null)!;
        var finder = new ApplicationFinder([
            new FakeApplicationSource(running),
            Source(Package("bin/codex.exe", "Codex"))
        ]);

        var app = Assert.Single(await finder.FindAsync("codex"));

        Assert.Equal("codex.exe", app.ExecutableName);
        Assert.Equal(PackagedApplicationSource.SourceName, app.Source);
    }

    [Fact]
    public async Task SearchChatGPT_ReturnsPackagedChatGPT()
    {
        var results = await Finder(Package("app/ChatGPT.exe", "ChatGPT Desktop"))
            .FindAsync("ChatGPT");

        Assert.Contains(results, app => app.ExecutableName == "ChatGPT.exe");
    }

    [Fact]
    public async Task OpenAiPreset_FindsPackagedChatGPT()
    {
        var discovered = await Finder(Package("app/ChatGPT.exe", "ChatGPT")).FindAsync("ChatGPT");

        var preset = new OpenAIApplicationMatcher().Match(discovered);

        Assert.Equal("ChatGPT.exe", Assert.Single(preset).ExecutableName);
    }

    [Fact]
    public async Task OpenAiPreset_FindsPackagedChatGPTAndCodex()
    {
        var finder = Finder(
            Package("app/ChatGPT.exe", "ChatGPT"),
            Package("bin/codex.exe", "Codex"));
        var discovered = (await finder.FindAsync("ChatGPT"))
            .Concat(await finder.FindAsync("codex"));

        var preset = new OpenAIApplicationMatcher().Match(discovered);

        Assert.Equal(2, preset.Count);
        Assert.Contains(preset, app => app.ExecutableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preset, app => app.ExecutableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OneMissingOpenAiApp_DoesNotHideTheOther()
    {
        var discovered = await Finder(Package("app/ChatGPT.exe", "ChatGPT")).FindAsync(string.Empty);

        var preset = new OpenAIApplicationMatcher().Match(discovered);

        Assert.Single(preset);
        Assert.Equal("ChatGPT.exe", preset[0].ExecutableName);
        Assert.DoesNotContain(preset, app => app.ExecutableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PackagedApplicationSourceFailure_DoesNotBlockExistingSources()
    {
        var packaged = new PackagedApplicationSource(
            _ => throw new IOException("fixture inventory failure"));
        var chrome = new ApplicationTarget(
            "Chrome", "chrome.exe", @"C:\Apps\chrome.exe", false, "App Paths");
        var finder = new ApplicationFinder([packaged, new FakeApplicationSource(chrome)]);

        var results = await finder.FindAsync("Chrome");

        Assert.Equal("chrome.exe", Assert.Single(results).ExecutableName);
    }

    [Fact]
    public async Task MalformedPackageManifest_IsSkippedWithoutCrash()
    {
        var source = Source(
            Package(string.Empty, "Broken"),
            Package("not-an-executable", "Broken"),
            Package("app/ChatGPT.exe", "ChatGPT"));

        var results = await source.FindAllAsync();

        Assert.Equal("ChatGPT.exe", Assert.Single(results).ExecutableName);
    }

    [Fact]
    public async Task WindowsAppsAccessDenied_DoesNotCrash()
    {
        var source = new PackagedApplicationSource(
            _ => Task.FromResult<IReadOnlyList<PackagedApplicationRecord>>([
                Package("app/ChatGPT.exe", "ChatGPT", @"C:\Program Files\WindowsApps\Denied")
            ]),
            _ => throw new UnauthorizedAccessException("fixture access denied"));

        var app = Assert.Single(await source.FindAllAsync());

        Assert.Equal("ChatGPT.exe", app.ExecutableName);
        Assert.Equal(string.Empty, app.ExecutablePath);
    }

    [Fact]
    public async Task ExecutableNameComparison_IsCaseInsensitive()
    {
        var results = await Finder(
            Package("app/ChatGPT.exe", "ChatGPT"),
            Package("other/chatgpt.exe", "CHATGPT"))
            .FindAsync("chatgpt");

        Assert.Single(results);
    }

    [Theory]
    [InlineData("Chrome", "chrome.exe")]
    [InlineData("微信", "WeChat.exe")]
    [InlineData("Win32 Tool", "tool.exe")]
    [InlineData("Custom Local", "custom.exe")]
    public async Task ExistingWin32Discovery_RemainsAvailable(string displayName, string executableName)
    {
        var target = new ApplicationTarget(
            displayName, executableName, $@"C:\Apps\{executableName}", false, "App Paths");
        var finder = new ApplicationFinder([new FakeApplicationSource(target)]);

        var results = await finder.FindAsync(displayName);

        Assert.Equal(executableName, Assert.Single(results).ExecutableName);
    }

    [Fact]
    public async Task PackagedCandidate_IsPreferredOverAppPaths()
    {
        var appPaths = new ApplicationTarget(
            "ChatGPT", "ChatGPT.exe", @"C:\AppPaths\ChatGPT.exe", false, "App Paths");
        var finder = new ApplicationFinder([
            new FakeApplicationSource(appPaths),
            Source(Package("app/ChatGPT.exe", "ChatGPT"))
        ]);

        var app = Assert.Single(await finder.FindAsync("ChatGPT"));

        Assert.Equal(PackagedApplicationSource.SourceName, app.Source);
    }

    [Fact]
    public async Task PackagedApplicationSource_SupportsCancellation()
    {
        var source = new PackagedApplicationSource(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ApplicationFinder([source]).FindAsync("ChatGPT", cancellation.Token));
    }

    [Fact]
    public async Task PackagedApplicationSource_CachesInventoryWithinSession()
    {
        var reads = 0;
        var source = new PackagedApplicationSource(_ =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<PackagedApplicationRecord>>([
                Package("app/ChatGPT.exe", "ChatGPT")
            ]);
        });
        var finder = new ApplicationFinder([source]);

        await finder.FindAsync("ChatGPT");
        await finder.FindAsync("codex");

        Assert.Equal(1, reads);
    }

    private static ApplicationFinder Finder(params PackagedApplicationRecord[] applications)
        => new([Source(applications)]);

    private static PackagedApplicationSource Source(params PackagedApplicationRecord[] applications)
        => new(_ => Task.FromResult<IReadOnlyList<PackagedApplicationRecord>>(applications));

    private static PackagedApplicationRecord Package(
        string executable,
        string displayName,
        string? installLocation = null)
        => new(
            "OpenAI.TestPackage",
            "OpenAI.TestPackage_fixture",
            installLocation,
            "App",
            executable,
            displayName,
            "OpenAI");
}
