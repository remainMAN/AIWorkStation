using System.Diagnostics;

namespace AIWorkStation.Services;

public class ClashReloadService
{
    public virtual Task<bool> RestartAsync(
        string clashExecutable,
        string runtimeConfigPath,
        CancellationToken token = default)
        => RestartCoreAsync(clashExecutable, runtimeConfigPath, afterProcessesStopped: null, token);

    public virtual Task<bool> RestartAsync(
        string clashExecutable,
        string runtimeConfigPath,
        Func<CancellationToken, Task> afterProcessesStopped,
        CancellationToken token = default)
        => RestartCoreAsync(clashExecutable, runtimeConfigPath, afterProcessesStopped, token);

    private static async Task<bool> RestartCoreAsync(
        string clashExecutable,
        string runtimeConfigPath,
        Func<CancellationToken, Task>? afterProcessesStopped,
        CancellationToken token)
    {
        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(clashExecutable));
        if (installDirectory is null || !File.Exists(clashExecutable)) return false;
        await StopMatchingProcessesAsync("clash-verge", installDirectory, graceful: true, token);
        await StopMatchingProcessesAsync("verge-mihomo", installDirectory, graceful: false, token);
        if (afterProcessesStopped is not null) await afterProcessesStopped(token);

        Process.Start(new ProcessStartInfo
        {
            FileName = clashExecutable,
            WorkingDirectory = installDirectory,
            UseShellExecute = true
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var clash = ClashVergeDetector.FindSingleProcess("clash-verge");
            var mihomo = ClashVergeDetector.FindSingleProcess("verge-mihomo");
            if (clash is not null && mihomo is not null && File.Exists(runtimeConfigPath) &&
                string.Equals(Path.GetDirectoryName(clash.ExecutablePath), installDirectory, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(mihomo.ExecutablePath), installDirectory, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // 文件时间变化不能证明运行态已恢复；必须重新读取 Runtime 并确认 Named Pipe Controller 可访问。
                    var settings = ClashVergeDetector.ReadRuntimeSettings(runtimeConfigPath);
                    using var config = await new MihomoNamedPipeClient(settings.ControllerPipe, TimeSpan.FromSeconds(2)).GetConfigsAsync(token);
                    return config.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
                }
                catch (Exception ex) when (
                    MihomoRuntimeConvergence.IsTransient(ex, token, includeInvalidRuntimeSample: true) ||
                    ex is ArgumentException or YamlDotNet.Core.YamlException)
                {
                    if (token.IsCancellationRequested) throw;
                }
            }
            await Task.Delay(500, token);
        }
        return false;
    }

    private static async Task StopMatchingProcessesAsync(string name, string installDirectory, bool graceful, CancellationToken token)
    {
        var matches = Process.GetProcessesByName(name).Where(process => IsInDirectory(process, installDirectory)).ToArray();
        foreach (var process in matches)
        {
            using (process)
            {
                try { if (graceful) process.CloseMainWindow(); } catch { }
            }
        }
        var gracefulDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(graceful ? 5 : 0);
        while (DateTime.UtcNow < gracefulDeadline && Process.GetProcessesByName(name).Any(process => IsInDirectoryAndDispose(process, installDirectory)))
            await Task.Delay(250, token);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (!IsInDirectory(process, installDirectory)) continue;
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(token); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        if (Process.GetProcessesByName(name).Any(process => IsInDirectoryAndDispose(process, installDirectory)))
            throw new IOException($"{name} 未能完全停止，已取消写入停机后的 profiles.yaml 补丁。");
    }

    private static bool IsInDirectoryAndDispose(Process process, string directory)
    {
        using (process) return IsInDirectory(process, directory);
    }

    private static bool IsInDirectory(Process process, string directory)
    {
        try { return string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), directory, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
