using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using AIWorkStation.Models;

namespace AIWorkStation.Services;

public sealed record PackagedApplicationRecord(
    string? PackageName,
    string? PackageFamilyName,
    string? InstallLocation,
    string? ApplicationId,
    string? Executable,
    string? DisplayName,
    string? Publisher);

public sealed class PackagedApplicationSource : IAsyncApplicationSource
{
    public const string SourceName = "Windows 已安装应用";

    private readonly Func<CancellationToken, Task<IReadOnlyList<PackagedApplicationRecord>>> _readInventory;
    private readonly Func<string, bool> _fileExists;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private IReadOnlyList<ApplicationTarget>? _cachedApplications;

    public PackagedApplicationSource(
        Func<CancellationToken, Task<IReadOnlyList<PackagedApplicationRecord>>>? readInventory = null,
        Func<string, bool>? fileExists = null)
    {
        _readInventory = readInventory ?? new AppxInventoryReader().ReadAsync;
        _fileExists = fileExists ?? File.Exists;
    }

    // 兼容现有 Source 接口；ApplicationFinder 的正式路径始终调用可取消的异步方法。
    public IEnumerable<ApplicationTarget> FindAll()
        => FindAllAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<ApplicationTarget>> FindAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedApplications is not null) return _cachedApplications;

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedApplications is not null) return _cachedApplications;
            var inventory = await _readInventory(cancellationToken).ConfigureAwait(false);
            var candidates = new List<ApplicationTarget>();
            foreach (var candidate in ApplicationFinder.IsolateApplicationItems(
                         inventory,
                         ConvertToCandidate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.Add(candidate);
            }

            _cachedApplications = candidates.ToArray();
            return _cachedApplications;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    internal ApplicationTarget? ConvertToCandidate(PackagedApplicationRecord application)
    {
        try
        {
            var executable = (application.Executable ?? string.Empty)
                .Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            var executableName = Path.GetFileName(executable);
            if (string.IsNullOrWhiteSpace(executableName) ||
                !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

            var displayName = application.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName) ||
                displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                displayName = Path.GetFileNameWithoutExtension(executableName);

            return new ApplicationTarget(
                displayName,
                executableName,
                TryResolveExecutablePath(application.InstallLocation, executable),
                false,
                SourceName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 单个 Manifest 项损坏时只跳过该项，不能影响其他应用来源。
            return null;
        }
    }

    private string TryResolveExecutablePath(string? installLocation, string executable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installLocation) || Path.IsPathRooted(executable))
                return Path.IsPathRooted(executable) && _fileExists(executable)
                    ? Path.GetFullPath(executable)
                    : string.Empty;

            var installRoot = Path.GetFullPath(installLocation);
            var candidate = Path.GetFullPath(Path.Combine(installRoot, executable));
            var rootPrefix = installRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return _fileExists(candidate) ? candidate : string.Empty;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or
                                   ArgumentException or NotSupportedException or PathTooLongException)
        {
            // WindowsApps 路径无法验证时仍保留可信的 Manifest ExecutableName。
            return string.Empty;
        }
    }
}

internal sealed class AppxInventoryReader
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumOutputCharacters = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string InventoryScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $items = @()
        foreach ($package in @(Get-AppxPackage -ErrorAction Stop)) {
          try {
            $manifest = Get-AppxPackageManifest -Package $package -ErrorAction Stop
            foreach ($application in @($manifest.Package.Applications.Application)) {
              if ($null -eq $application) { continue }
              $displayName = [string]$application.VisualElements.DisplayName
              if ([string]::IsNullOrWhiteSpace($displayName)) { $displayName = [string]$package.Name }
              $items += [pscustomobject]@{
                PackageName = [string]$package.Name
                PackageFamilyName = [string]$package.PackageFamilyName
                InstallLocation = [string]$package.InstallLocation
                ApplicationId = [string]$application.Id
                Executable = [string]$application.Executable
                DisplayName = $displayName
                Publisher = [string]$package.PublisherDisplayName
              }
            }
          } catch {
            continue
          }
        }
        ConvertTo-Json -InputObject @($items) -Compress -Depth 4
        """;

    public async Task<IReadOnlyList<PackagedApplicationRecord>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(InventoryScript);

        using var process = Process.Start(startInfo)
            ?? throw new Win32Exception("无法启动 Windows PowerShell 读取应用包清单。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryStop(process);
            await ObserveExitAsync(process, outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Windows 打包应用查询超时。");
        }

        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false); // stderr 只作为内部技术诊断，不进入普通搜索结果。
        if (process.ExitCode != 0)
            throw new InvalidDataException("Windows 打包应用查询失败。");
        if (output.Length > MaximumOutputCharacters)
            throw new InvalidDataException("Windows 打包应用查询结果过大。");
        if (string.IsNullOrWhiteSpace(output)) return [];

        return JsonSerializer.Deserialize<List<PackagedApplicationRecord>>(
                   output.Trim().TrimStart('\uFEFF'), JsonOptions)
               ?? [];
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private static async Task ObserveExitAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException) { }
        if (outputTask.IsCompleted)
            try { _ = await outputTask.ConfigureAwait(false); } catch (IOException) { }
        if (errorTask.IsCompleted)
            try { _ = await errorTask.ConfigureAwait(false); } catch (IOException) { }
    }
}
