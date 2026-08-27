using System.Diagnostics;
using AIWorkStation.Models;
using Microsoft.Win32;

namespace AIWorkStation.Services;

public interface IApplicationSource
{
    IEnumerable<ApplicationTarget> FindAll();
}

public interface IAsyncApplicationSource : IApplicationSource
{
    Task<IReadOnlyList<ApplicationTarget>> FindAllAsync(CancellationToken cancellationToken = default);
}

public sealed class ApplicationFinder
{
    private readonly IReadOnlyList<IApplicationSource> _sources;

    public ApplicationFinder(IEnumerable<IApplicationSource>? sources = null)
    {
        _sources = sources?.ToArray() ??
        [
            new RunningProcessApplicationSource(),
            new PackagedApplicationSource(),
            new AppPathsApplicationSource(),
            new StartMenuApplicationSource(),
            new UninstallApplicationSource()
        ];
    }

    public async Task<IReadOnlyList<ApplicationTarget>> FindAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var applications = new List<ApplicationTarget>();
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            applications.AddRange(await SafeFindAsync(source, cancellationToken));
        }

        var normalized = query.Trim();
        return applications
            .Where(app => normalized.Length == 0 || app.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                          app.ExecutableName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                          app.ExecutablePath.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Where(app => app.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            // 程序分流最终使用 PROCESS-NAME；同一 exe 的运行进程、Package 和快捷方式只能保留一个候选。
            .GroupBy(app => app.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(CandidatePriority).First())
            .OrderByDescending(app => app.RunningProcess)
            .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ApplicationTarget>> SafeFindAsync(
        IApplicationSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            return source is IAsyncApplicationSource asyncSource
                ? await asyncSource.FindAllAsync(cancellationToken)
                : await Task.Run(() => source.FindAll().ToArray(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) when (source is IAsyncApplicationSource) { return []; }
        catch (Exception ex) when (IsApplicationItemFailure(ex)) { return []; }
    }

    private static int CandidatePriority(ApplicationTarget application)
    {
        if (application.RunningProcess && !string.IsNullOrWhiteSpace(application.ExecutablePath)) return 500;
        if (application.Source == PackagedApplicationSource.SourceName) return 400;
        return application.Source switch
        {
            "App Paths" => 300,
            "开始菜单" => 200,
            "已安装程序" => 100,
            _ => string.IsNullOrWhiteSpace(application.ExecutablePath) ? 0 : 50
        };
    }

    public static ApplicationTarget? FromExecutable(string path, string displayName, bool running, string source)
    {
        var cleanPath = CleanExecutablePath(path);
        return File.Exists(cleanPath) && cleanPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new(displayName, Path.GetFileName(cleanPath), cleanPath, running, source)
            : null;
    }

    public static string CleanExecutablePath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith('"'))
        {
            var close = expanded.IndexOf('"', 1);
            if (close > 1) return expanded[1..close];
        }
        var exe = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? expanded[..(exe + 4)].Trim(' ', '"') : expanded.Trim(' ', '"');
    }

    public static ApplicationTarget FromManualExecutable(string path)
        => FromManualExecutable(
            path,
            File.Exists,
            candidate =>
            {
                using var stream = new FileStream(
                    candidate, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            });

    internal static ApplicationTarget FromManualExecutable(
        string path,
        Func<string, bool> fileExists,
        Action<string> assertReadable)
    {
        var candidate = Path.GetFullPath(path.Trim().Trim('"'));
        if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("所选程序不存在或不是 EXE。", candidate);
        // 先尝试只读打开，避免 File.Exists 在权限不足时返回 false 而误报成文件不存在。
        assertReadable(candidate);
        if (!fileExists(candidate))
            throw new FileNotFoundException("所选程序不存在或不是 EXE。", candidate);
        return new(
            Path.GetFileNameWithoutExtension(candidate),
            Path.GetFileName(candidate),
            candidate,
            false,
            "Manual");
    }

    // 单个注册表项、快捷方式或 Package 损坏时只跳过该项，保留同一来源中的其他正常程序。
    internal static IEnumerable<ApplicationTarget> IsolateApplicationItems<T>(
        IEnumerable<T> items,
        Func<T, ApplicationTarget?> convert)
    {
        foreach (var item in items)
        {
            ApplicationTarget? application;
            try { application = convert(item); }
            catch (Exception ex) when (IsApplicationItemFailure(ex)) { continue; }
            if (application is not null) yield return application;
        }
    }

    internal static bool IsApplicationItemFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception;
}

public sealed class RunningProcessApplicationSource : IApplicationSource
{
    public IEnumerable<ApplicationTarget> FindAll()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                string processName;
                try { processName = process.ProcessName; }
                catch { continue; }
                var app = FromProcessFacts(processName, path);
                if (app is not null) yield return app;
            }
        }
    }

    internal static ApplicationTarget? FromProcessFacts(string processName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var resolved = ApplicationFinder.FromExecutable(executablePath, processName, true, "正在运行");
            if (resolved is not null) return resolved;
        }

        // 运行中的 Codex / ChatGPT 即使无法读取 MainModule 路径，
        // 仍可根据进程名建立 PROCESS-NAME 分流目标。
        var cleanName = Path.GetFileName(processName.Trim());
        if (string.IsNullOrWhiteSpace(cleanName)) return null;
        var executableName = cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? cleanName
            : cleanName + ".exe";
        return new(processName, executableName, string.Empty, true, "正在运行");
    }
}

public sealed class AppPathsApplicationSource : IApplicationSource
{
    public IEnumerable<ApplicationTarget> FindAll()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null) continue;
            foreach (var app in ApplicationFinder.IsolateApplicationItems(
                         key.GetSubKeyNames(),
                         name =>
                         {
                             using var item = key.OpenSubKey(name);
                             var path = Convert.ToString(item?.GetValue(null));
                             return path is null
                                 ? null
                                 : ApplicationFinder.FromExecutable(
                                     path, Path.GetFileNameWithoutExtension(name), false, "App Paths");
                         }))
                yield return app;
        }
    }
}

public sealed class UninstallApplicationSource : IApplicationSource
{
    public IEnumerable<ApplicationTarget> FindAll()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is null) continue;
            foreach (var app in ApplicationFinder.IsolateApplicationItems(
                         key.GetSubKeyNames(),
                         childName =>
                         {
                             using var item = key.OpenSubKey(childName);
                             var displayName = Convert.ToString(item?.GetValue("DisplayName"));
                             var icon = Convert.ToString(item?.GetValue("DisplayIcon"));
                             var install = Convert.ToString(item?.GetValue("InstallLocation"));
                             ApplicationTarget? candidate = null;
                             if (!string.IsNullOrWhiteSpace(icon))
                                 candidate = ApplicationFinder.FromExecutable(
                                     icon, displayName ?? childName, false, "已安装程序");
                             if (candidate is null && !string.IsNullOrWhiteSpace(install) && Directory.Exists(install))
                             {
                                 var probable = Directory.EnumerateFiles(
                                     install, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                                 if (probable is not null)
                                     candidate = ApplicationFinder.FromExecutable(
                                         probable, displayName ?? childName, false, "已安装程序");
                             }
                             return candidate;
                         }))
                yield return app;
        }
    }
}

public sealed class StartMenuApplicationSource : IApplicationSource
{
    public IEnumerable<ApplicationTarget> FindAll()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            string[] shortcuts;
            try { shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToArray(); }
            catch (Exception ex) when (ApplicationFinder.IsApplicationItemFailure(ex)) { continue; }
            foreach (var app in ApplicationFinder.IsolateApplicationItems(
                         shortcuts,
                         shortcut =>
                         {
                             var target = ResolveShortcut(shortcut);
                             return target is null
                                 ? null
                                 : ApplicationFinder.FromExecutable(
                                     target, Path.GetFileNameWithoutExtension(shortcut), false, "开始菜单");
                         }))
                yield return app;
        }
    }

    private static string? ResolveShortcut(string shortcut)
    {
        Type? type = null;
        object? shell = null;
        try
        {
            type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            dynamic dynamicShell = shell!;
            dynamic link = dynamicShell.CreateShortcut(shortcut);
            return (string?)link.TargetPath;
        }
        catch { return null; }
        finally { if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
