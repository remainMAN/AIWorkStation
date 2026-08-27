using AIWorkStation.Models;
using AIWorkStation.Services;
using AIWorkStation.ViewModels;

namespace AIWorkStation.Tests;

public sealed class FinalSmallFixApplicationTests
{
    [Fact]
    public async Task ManualExeSelection_CreatesApplicationTarget()
    {
        using var temp = new TempDirectory();
        var path = temp.File("MyApp.exe");
        await File.WriteAllTextAsync(path, "fixture executable");

        var target = ApplicationFinder.FromManualExecutable(path);

        Assert.Equal("MyApp", target.DisplayName);
        Assert.Equal(Path.GetFullPath(path), target.ExecutablePath);
        Assert.Equal("Manual", target.Source);
    }

    [Fact]
    public async Task ManualExeSelection_UsesExecutableName()
    {
        using var temp = new TempDirectory();
        var path = temp.File("PortableTool.EXE");
        await File.WriteAllTextAsync(path, "fixture executable");

        var target = ApplicationFinder.FromManualExecutable(path);

        Assert.Equal("PortableTool.EXE", target.ExecutableName);
    }

    [Fact]
    public async Task ManualExeSelection_DeduplicatesSameExecutableName()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("A"));
        Directory.CreateDirectory(temp.File("B"));
        var first = temp.File("A/foo.exe");
        var second = temp.File("B/FOO.exe");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var paths = new Queue<string>([first, second]);
        var viewModel = new MainViewModel(selectExecutable: () => paths.Dequeue());

        viewModel.BrowseExecutableCommand.Execute(null);
        viewModel.BrowseExecutableCommand.Execute(null);

        Assert.Single(viewModel.SelectedTargets);
        Assert.Contains("同名程序", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualExeSelection_CancelDoesNothing()
    {
        var viewModel = new MainViewModel(selectExecutable: () => null);
        var status = viewModel.StatusText;

        viewModel.BrowseExecutableCommand.Execute(null);

        Assert.Empty(viewModel.SelectedTargets);
        Assert.Equal(status, viewModel.StatusText);
    }

    [Fact]
    public void ManualExeSelection_InvalidFileShowsError()
    {
        var viewModel = new MainViewModel(selectExecutable: () => @"C:\missing\missing.exe");

        viewModel.BrowseExecutableCommand.Execute(null);

        Assert.Empty(viewModel.SelectedTargets);
        Assert.Equal("无法找到所选程序，请重新选择。", viewModel.StatusText);
    }

    [Fact]
    public void ManualExeSelection_UnreadableFileShowsError()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            ApplicationFinder.FromManualExecutable(
                @"C:\Apps\Private.exe", _ => true,
                _ => throw new UnauthorizedAccessException("fixture")));
        var viewModel = new MainViewModel(
            selectExecutable: () => @"C:\Apps\Private.exe",
            createManualApplication: _ => throw new UnauthorizedAccessException("fixture"));

        viewModel.BrowseExecutableCommand.Execute(null);

        Assert.Empty(viewModel.SelectedTargets);
        Assert.Equal("无法读取所选程序，请选择其他程序。", viewModel.StatusText);
    }

    [Fact]
    public void OneBrokenUninstallEntry_DoesNotDiscardOtherEntries()
        => AssertItemIsolation(new UnauthorizedAccessException("fixture registry item"));

    [Fact]
    public void OneBrokenShortcut_DoesNotDiscardOtherShortcuts()
        => AssertItemIsolation(new IOException("fixture shortcut"));

    [Fact]
    public void OneBrokenPackageItem_DoesNotDiscardOtherPackages()
        => AssertItemIsolation(new ArgumentException("fixture package item"));

    [Fact]
    public async Task ApplicationSourceFailure_DoesNotCrashFinder()
    {
        var expected = Target("Working.exe");
        var finder = new ApplicationFinder([
            new ThrowingSource(),
            new FixedSource(expected)
        ]);

        var results = await finder.FindAsync(string.Empty);

        Assert.Contains(results, item => item.ExecutableName == expected.ExecutableName);
    }

    private static void AssertItemIsolation(Exception failure)
    {
        var items = ApplicationFinder.IsolateApplicationItems(
                new[] { "A.exe", "broken.exe", "C.exe" },
                name => name == "broken.exe" ? throw failure : Target(name))
            .ToArray();

        Assert.Equal(["A.exe", "C.exe"], items.Select(item => item.ExecutableName));
    }

    private static ApplicationTarget Target(string executableName)
        => new(Path.GetFileNameWithoutExtension(executableName), executableName,
            @"C:\Apps\" + executableName, false, "fixture");

    private sealed class ThrowingSource : IApplicationSource
    {
        public IEnumerable<ApplicationTarget> FindAll()
            => throw new InvalidOperationException("fixture source");
    }

    private sealed class FixedSource(ApplicationTarget target) : IApplicationSource
    {
        public IEnumerable<ApplicationTarget> FindAll() => [target];
    }
}
