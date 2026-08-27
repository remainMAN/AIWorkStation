using AIWorkStation.Models;

namespace AIWorkStation.Services;

public sealed class OpenAIApplicationMatcher
{
    public static readonly IReadOnlySet<string> ExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ChatGPT.exe",
        "codex.exe"
    };

    public IReadOnlyList<ApplicationTarget> Match(IEnumerable<ApplicationTarget> applications)
        => applications.Where(app => ExecutableNames.Contains(app.ExecutableName))
            .GroupBy(app => app.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(app => app.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<ApplicationTarget> CreatePresetTargets(IEnumerable<ApplicationTarget> discovered)
    {
        var matches = Match(discovered);
        return new[]
        {
            Resolve(matches, "ChatGPT", "ChatGPT.exe"),
            Resolve(matches, "Codex", "codex.exe")
        };
    }

    private static ApplicationTarget Resolve(
        IReadOnlyList<ApplicationTarget> matches,
        string displayName,
        string executableName)
        => matches.FirstOrDefault(app =>
               app.ExecutableName.Equals(executableName, StringComparison.OrdinalIgnoreCase))
           ?? new ApplicationTarget(displayName, executableName, string.Empty, false, "OpenAI 预设");
}
