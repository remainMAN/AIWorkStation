using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;
using AIWorkStation.Models;

namespace AIWorkStation.Services;

public sealed record MihomoValidationResult(bool Success, int ExitCode, string SanitizedDetail, bool BaselineIssueIgnored = false);

public class MihomoValidator
{
    public static void CleanupDefaultStaleCandidates() => new MihomoValidator().CleanupStaleCandidates();

    private readonly string _validationDirectory;
    private readonly Func<DateTimeOffset> _utcNow;

    public MihomoValidator(string? validationDirectory = null, Func<DateTimeOffset>? utcNow = null)
    {
        _validationDirectory = validationDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIWorkStation", "validation");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        CleanupStaleCandidates();
    }

    public virtual async Task<MihomoValidationResult> ValidateDeltaAsync(
        string mihomoPath,
        string dataDirectory,
        string effectiveBaselineYaml,
        string runtimeCandidateYaml,
        StaticExitConfig sensitiveConfig,
        IEnumerable<string> managedIdentifiers,
        CancellationToken cancellationToken = default)
    {
        var baseline = await ValidateAsync(mihomoPath, dataDirectory, effectiveBaselineYaml, sensitiveConfig, cancellationToken);
        var candidate = await ValidateAsync(mihomoPath, dataDirectory, runtimeCandidateYaml, sensitiveConfig, cancellationToken);
        return AssessDelta(baseline, candidate, managedIdentifiers);
    }

    public static MihomoValidationResult AssessDelta(
        MihomoValidationResult baseline,
        MihomoValidationResult candidate,
        IEnumerable<string> managedIdentifiers)
    {
        if (candidate.Success) return candidate;
        if (baseline.Success) return candidate;

        var managed = managedIdentifiers.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (managed.Any(value => candidate.SanitizedDetail.Contains(value, StringComparison.OrdinalIgnoreCase)))
            return candidate;

        var baselineErrors = ErrorSignatures(baseline.SanitizedDetail);
        var candidateErrors = ErrorSignatures(candidate.SanitizedDetail);
        // 用户订阅可能包含当前根本未使用的坏节点。
        // 这里只允许忽略运行基线已经存在的问题，候选配置新增的错误仍然必须阻止写入。
        if (baselineErrors.Count > 0 && candidateErrors.Count > 0 && candidateErrors.IsSubsetOf(baselineErrors))
            return new(true, candidate.ExitCode,
                "检测到订阅中存在部分异常节点，本次分流不会使用这些节点。", BaselineIssueIgnored: true);
        return candidate;
    }

    public virtual async Task<MihomoValidationResult> ValidateAsync(
        string mihomoPath,
        string dataDirectory,
        string candidateYaml,
        StaticExitConfig sensitiveConfig,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_validationDirectory);
        RestrictToCurrentUser(_validationDirectory);
        var candidatePath = Path.Combine(_validationDirectory, $"candidate-{Guid.NewGuid():N}.yaml");
        try
        {
            // Mihomo -t 只能接收文件；候选短期明文落盘，但使用随机名、用户 ACL、WriteThrough，并在 finally 删除。
            var bytes = new UTF8Encoding(false).GetBytes(candidateYaml);
            try
            {
                await using var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            var start = new ProcessStartInfo
            {
                FileName = mihomoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dataDirectory
            };
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(dataDirectory);
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(candidatePath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 verge-mihomo 验证进程。");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                return new(false, -1, "Mihomo 离线验证超时。");
            }
            var detail = Sanitize((await stdout) + Environment.NewLine + (await stderr), sensitiveConfig, dataDirectory, candidatePath);
            return new(process.ExitCode == 0, process.ExitCode, detail.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, -1, Sanitize(ex.Message, sensitiveConfig, dataDirectory, candidatePath));
        }
        finally
        {
            try { if (File.Exists(candidatePath)) File.Delete(candidatePath); } catch { }
        }
    }

    public void CleanupStaleCandidates()
    {
        try
        {
            if (!Directory.Exists(_validationDirectory)) return;
            var cutoff = _utcNow().UtcDateTime - TimeSpan.FromHours(1);
            foreach (var path in Directory.EnumerateFiles(_validationDirectory, "candidate-*.yaml", SearchOption.TopDirectoryOnly))
            {
                try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Sanitize(string value, StaticExitConfig config, params string[] relevantPaths)
    {
        if (!string.IsNullOrEmpty(config.Password)) value = value.Replace(config.Password, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(config.Username)) value = value.Replace(config.Username, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(config.Server)) value = value.Replace(config.Server, "***", StringComparison.OrdinalIgnoreCase);
        foreach (var path in relevantPaths) value = value.Replace(path, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        return value.Length <= 4000 ? value : value[..4000];
    }

    private static HashSet<string> ErrorSignatures(string detail)
    {
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in detail.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (!Regex.IsMatch(line, "invalid|error|failed|timeout", RegexOptions.IgnoreCase)) continue;
            var normalized = Regex.Replace(line, @"candidate-[a-f0-9]+\.yaml", "candidate.yaml", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "time=\"[^\"]+\"", "time=<timestamp>", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"proxy\s+\d+", "proxy #", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\b\d+(?:\.\d+)?\s*(?:ms|s)\b", "<duration>", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\[[^\]]+\]", string.Empty);
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            if (normalized.Length > 0) signatures.Add(normalized);
        }
        return signatures;
    }

    private static void RestrictToCurrentUser(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("无法确定候选目录的当前 Windows 用户。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
