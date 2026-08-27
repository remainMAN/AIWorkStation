using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text.Json;
using AIWorkStation.Services;
using AIWorkStation.Views;

namespace AIWorkStation;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 当前 WPF 客户端在部分显卡/远控/合成环境中可能出现
        // Visual Tree 正常但窗口表面无法绘制的问题。
        // V1 使用软件渲染优先保证桌面工具的显示稳定性。
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
        if (SystemParameters.HighContrast)
            ApplyHighContrastResources();
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\AIWorkStation.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("AI WorkStation 已经在运行，请先使用已打开的窗口。", "AI WorkStation", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        if (e.Args.Contains("--application-discovery-smoke", StringComparer.OrdinalIgnoreCase))
        {
            await RunApplicationDiscoverySmokeAsync();
            Shutdown();
            return;
        }

        var uiSmoke = e.Args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase);
        if (!uiSmoke)
        {
            // 无论是否存在上次事务，启动最早阶段都先清理超过一小时的随机 Mihomo 候选文件。
            MihomoValidator.CleanupDefaultStaleCandidates();

            var markerService = new TransactionMarkerService();
            var markerRead = markerService.ReadSafe();
            if (markerRead.Status == TransactionMarkerReadStatus.Corrupt)
            {
                MessageBox.Show("检测到上一次配置记录损坏。为避免错误修改网络配置，本次不会继续自动应用。请重新检查环境或查看技术详情。",
                    "AI WorkStation", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }
            bool? recovery = markerRead.Marker is null
                ? null
                : await new RecoveryService(markers: markerService).RecoverAsync(markerRead.Marker);
            if (recovery == false)
            {
                MessageBox.Show("上一次配置没有完整结束，无法确认原配置已经恢复，请暂时不要继续操作。", "AI WorkStation", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }
        }
        if (e.Args.Contains("--host-report", StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = await new EnvironmentDetector().DetectAsync();
            var finder = new ApplicationFinder();
            var chatGptApps = await finder.FindAsync("ChatGPT");
            var codexApps = await finder.FindAsync("Codex");
            var chromeApps = await finder.FindAsync("Chrome");
            var openAiApps = new OpenAIApplicationMatcher().CreatePresetTargets(chatGptApps.Concat(codexApps));
            IReadOnlyList<Models.RouteObservation> routeObservations = [];
            bool? runtimeDeltaCandidateValid = null;
            bool? runtimeBaselineIssueIgnored = null;
            string? runtimeDeltaValidationDetail = null;
            if (snapshot.Clash is not null)
            {
                try { routeObservations = await new MihomoNamedPipeClient(snapshot.Clash.ControllerPipe).GetRouteObservationsAsync(); }
                catch (Exception ex) when (ex is IOException or TimeoutException) { }
                try
                {
                    var diagnosticExit = new Models.StaticExitConfig
                    {
                        Protocol = Models.StaticProxyProtocol.Socks5,
                        Server = "127.0.0.1",
                        Port = 9
                    };
                    var diagnosticTarget = new Models.ApplicationTarget(
                        "AIWS Host Diagnostic", "aiws-host-diagnostic.exe", string.Empty, false, "host-report");
                    var diagnosticRoute = new Models.RouteConfiguration(
                        [diagnosticTarget], diagnosticExit, "127.0.0.1", snapshot.Subscription?.Name ?? "host-report");
                    var baseline = await File.ReadAllTextAsync(snapshot.Clash.RuntimeConfigPath);
                    var candidate = new RouteScriptBuilder().BuildRuntimeCandidate(baseline, diagnosticRoute);
                    new ScriptValidator().ValidateSemantics(candidate, [diagnosticTarget]);
                    var validation = await new MihomoValidator().ValidateDeltaAsync(
                        snapshot.Clash.MihomoProcess.ExecutablePath,
                        snapshot.Clash.DataDirectory,
                        baseline,
                        candidate,
                        diagnosticExit,
                        [RouteScriptBuilder.StaticExitName, RouteScriptBuilder.StaticGroupName, diagnosticTarget.ExecutableName]);
                    runtimeDeltaCandidateValid = validation.Success;
                    runtimeBaselineIssueIgnored = validation.BaselineIssueIgnored;
                    runtimeDeltaValidationDetail = validation.SanitizedDetail;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    runtimeDeltaCandidateValid = false;
                    runtimeDeltaValidationDetail = ex.Message;
                }
            }
            var report = new
            {
                support = snapshot.Support.ToString(),
                reason = snapshot.ReasonZh,
                windows = snapshot.Machine.Edition,
                windowsVersion = snapshot.Machine.Version,
                build = snapshot.Machine.BuildNumber,
                architecture = snapshot.Machine.Architecture,
                timeZone = snapshot.Machine.TimeZoneId,
                utcOffset = snapshot.Machine.UtcOffset.ToString(),
                clashDetected = snapshot.Clash is not null,
                clashVersion = snapshot.Clash?.ClashProcess.Version,
                mihomoDetected = snapshot.Clash?.MihomoProcess is not null,
                profileDetected = snapshot.Subscription is not null,
                profile = snapshot.Subscription?.Name,
                subscriptionDetected = snapshot.Subscription is not null,
                nodesDetected = snapshot.Subscription?.Nodes.Count ?? 0,
                nodes = snapshot.Subscription?.Nodes.Take(5).Select(node => new { node.Name, node.Protocol, node.Server, node.ResolvedServerIp }),
                publicIpDetected = snapshot.CurrentPublicIp is not null,
                publicIp = snapshot.CurrentPublicIp,
                tun = snapshot.Clash?.TunEnabled,
                systemProxy = snapshot.Clash?.SystemProxyEnabled,
                proxySelections = snapshot.Clash?.ProxySelections,
                routeObservationCount = routeObservations.Count,
                routeProcessMetadataCount = routeObservations.Count(observation => !string.IsNullOrWhiteSpace(observation.Process)),
                routeChainMetadataCount = routeObservations.Count(observation => observation.Chains.Count > 0),
                runtimeDeltaCandidateValid,
                runtimeBaselineIssueIgnored,
                runtimeDeltaValidationDetail,
                applicationSearchWorking = chatGptApps.Count + codexApps.Count + chromeApps.Count > 0,
                chatGptDetected = openAiApps.Any(app => app.ExecutableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)),
                codexDetected = openAiApps.Any(app => app.ExecutableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)),
                customChromeDetected = chromeApps.Any(app => app.ExecutableName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase))
            };
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "AIWorkStation-host-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        if (uiSmoke)
        {
            window.ContentRendered += async (_, _) =>
            {
                var loaded = new List<string>();
                foreach (var (index, name) in new[] { (0, "Step 1"), (1, "Step 2"), (2, "Step 3"), (3, "Step 4") })
                {
                    window.ViewModel.CurrentStep = index;
                    window.UpdateLayout();
                    loaded.Add(name);
                    await Task.Delay(100);
                }
                window.ViewModel.CurrentStep = 0;
                window.UpdateLayout();
                var root = window.Content as FrameworkElement;
                var step1 = FindVisualDescendant<EnvironmentStep>(window);
                var dataContextPresent = window.DataContext is not null;
                var rootVisible = root is { IsVisible: true, Opacity: > 0, ActualWidth: > 0, ActualHeight: > 0 };
                var step1Visible = step1 is { IsVisible: true, Opacity: > 0, ActualWidth: > 0, ActualHeight: > 0 };
                var checkComputerTextVisible = FindVisualDescendants<TextBlock>(window)
                    .Any(text => text.IsVisible && text.Text.Contains("检查电脑", StringComparison.Ordinal));
                var blockingOverlayPresent = root is not null && FindVisualDescendants<FrameworkElement>(root)
                    .Any(element => Panel.GetZIndex(element) >= 100 &&
                                    element.IsVisible && element.Opacity >= 0.98 &&
                                    element.ActualWidth >= root.ActualWidth * 0.95 &&
                                    element.ActualHeight >= root.ActualHeight * 0.95);
                var progressLabels = new HashSet<string>(FindVisualDescendants<TextBlock>(window)
                    .Where(text => text.IsVisible)
                    .Select(text => text.Text), StringComparer.Ordinal);
                var fourStepNavigationVisible = new[] { "1  检查电脑", "2  配置分流", "3  确认并应用", "4  结果" }
                    .All(progressLabels.Contains);
                var primaryButtonVisible = FindVisualDescendants<Button>(window)
                    .Any(button => button.IsVisible && string.Equals(button.Content?.ToString(), "下一步", StringComparison.Ordinal));
                var automationPeer = new WindowAutomationPeer(window);
                var automationTreeNonEmpty = automationPeer.GetChildren()?.Count > 0;
                var workAreaCorrect = window.IsInsideCurrentMonitorWorkArea();
                var softwareRendering = RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly;
                var reportPath = Path.Combine(Path.GetTempPath(), "AIWorkStation-ui-smoke.json");
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                {
                    success = loaded.Count == 4 && dataContextPresent && rootVisible && step1Visible &&
                              checkComputerTextVisible && !blockingOverlayPresent && fourStepNavigationVisible &&
                              primaryButtonVisible && automationTreeNonEmpty && workAreaCorrect && softwareRendering,
                    loaded,
                    mainWindowCreated = true,
                    dataContextPresent,
                    processRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                    root = new { rootVisible, root?.Opacity, root?.ActualWidth, root?.ActualHeight },
                    step1 = new { step1Visible, checkComputerTextVisible, step1?.Opacity, step1?.ActualWidth, step1?.ActualHeight },
                    blockingOverlayPresent,
                    fourStepNavigationVisible,
                    primaryButtonVisible,
                    workAreaCorrect,
                    automationTreeNonEmpty
                }));
                window.Close();
            };
        }
        window.Show();
    }

    private void ApplyHighContrastResources()
    {
        Resources["BackgroundBrush"] = SystemColors.WindowBrush;
        Resources["SurfaceBrush"] = SystemColors.WindowBrush;
        Resources["InkBrush"] = SystemColors.WindowTextBrush;
        Resources["MutedBrush"] = SystemColors.WindowTextBrush;
        Resources["BorderBrush"] = SystemColors.WindowTextBrush;
        Resources["PrimaryBrush"] = SystemColors.HighlightBrush;
        Resources["PrimaryTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["BadgeTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["SelectedBackgroundBrush"] = SystemColors.HighlightBrush;
        Resources["InfoBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["InfoBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["WarningBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["WarningBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["SuccessBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["SuccessBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["SuccessBrush"] = SystemColors.WindowTextBrush;
        Resources["WarningBrush"] = SystemColors.WindowTextBrush;
        Resources["DangerBrush"] = SystemColors.WindowTextBrush;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualDescendants<T>(child)) yield return nested;
        }
    }

    private static async Task RunApplicationDiscoverySmokeAsync()
    {
        var packagedFinder = new ApplicationFinder([new PackagedApplicationSource()]);
        var packagedChatGpt = await packagedFinder.FindAsync("ChatGPT");
        var packagedCodex = await packagedFinder.FindAsync("codex");
        var finder = new ApplicationFinder();
        var manualChatGpt = await finder.FindAsync("ChatGPT");
        var preset = new OpenAIApplicationMatcher().CreatePresetTargets(
            manualChatGpt.Concat(await finder.FindAsync("codex")));
        var chatGptProcesses = System.Diagnostics.Process.GetProcessesByName("ChatGPT");
        var chatGptProcessCount = chatGptProcesses.Length;
        foreach (var process in chatGptProcesses) process.Dispose();

        var packageDetected = packagedChatGpt.Any(app =>
            app.ExecutableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase));
        var manualSearchPassed = manualChatGpt.Any(app =>
            app.ExecutableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase));
        var presetChatGptPassed = preset.Any(app =>
            app.ExecutableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase));
        var report = new
        {
            success = chatGptProcessCount == 0 && packageDetected && manualSearchPassed && presetChatGptPassed,
            chatGptProcessCount,
            chatGptPackageDetected = packageDetected,
            chatGptExecutableName = packagedChatGpt.FirstOrDefault(app =>
                app.ExecutableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))?.ExecutableName,
            manualSearchChatGptPassed = manualSearchPassed,
            openAiPresetChatGptPassed = presetChatGptPassed,
            packagedCodexDetected = packagedCodex.Any(app =>
                app.ExecutableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)),
            openAiPresetCodexDetected = preset.Any(app =>
                app.ExecutableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
        };
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "AIWorkStation-application-discovery-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
